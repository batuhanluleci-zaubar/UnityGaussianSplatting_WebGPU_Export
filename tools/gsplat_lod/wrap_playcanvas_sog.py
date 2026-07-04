#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Convert a PlayCanvas / splat-transform monolithic .sog ZIP into Streamed SOG layout
(lod-meta.json + per-leaf chunk dirs) without re-encoding splats.

The input .sog is a ZIP containing meta.json + WebP planes at splat count width.
This tool:
  1. Decodes centroid positions for a spatial KD-tree split (same stop rule as sog_baker).
  2. Gathers per-splat WebP rows into leaf chunks (lossless — no re-quantisation).
  3. Rewrites per-chunk meta.json bounds/count; copies shN centroids atlas verbatim.

Usage:
  python3 wrap_playcanvas_sog.py "Assets/Festsaal 10m bereinigt.sog" \\
      -o projects/GaussianExample-URP/Assets/StreamingAssets/gsplat_lod/festsaal_sog

For multi-LOD voxel merges, bake from SPZ with sog_baker.py instead.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import tempfile
import time
import zipfile
from pathlib import Path

import numpy as np

from streamed_sog import _build_tree, _iter_leaves

try:
    from PIL import Image, features
except ImportError:
    print("Install Pillow: pip install Pillow", file=sys.stderr)
    sys.exit(2)

if not features.check("webp"):
    print("Pillow needs WebP support", file=sys.stderr)
    sys.exit(3)


def log_transform(x: float) -> float:
    return math.copysign(math.log1p(abs(x)), x)


def inv_log_transform(lv: float) -> float:
    return math.copysign(math.expm1(abs(lv)), lv)


def load_rgba_webp(path: Path, expected_count: int | None = None) -> np.ndarray:
    """Load a SOG WebP plane. PlayCanvas stores splats in a 2D atlas (row-major)."""
    img = Image.open(path).convert("RGBA")
    w, h = img.size
    flat = np.frombuffer(img.tobytes(), dtype=np.uint8).reshape(w * h, 4)
    if expected_count is not None:
        if flat.shape[0] < expected_count:
            raise ValueError(f"{path}: atlas {w}x{h}={flat.shape[0]} < count {expected_count}")
        return flat[:expected_count]
    return flat


def save_rgba_webp(path: Path, rows: np.ndarray) -> None:
    """Write splats as a row-major 2D atlas (WebP max dimension is 16383)."""
    n = rows.shape[0]
    side = max(1, int(math.ceil(math.sqrt(n))))
    grid = np.zeros((side, side, 4), dtype=np.uint8)
    grid.reshape(-1, 4)[:n] = rows
    Image.fromarray(grid, mode="RGBA").save(
        str(path), format="WEBP", lossless=True, quality=100, method=6)


def decode_positions(means_l: np.ndarray, means_u: np.ndarray,
                     mins: tuple[float, float, float],
                     maxs: tuple[float, float, float]) -> np.ndarray:
    mins_a = np.array(mins, dtype=np.float64)
    maxs_a = np.array(maxs, dtype=np.float64)
    denom = np.maximum(maxs_a - mins_a, 1e-6)
    lo = means_l[:, :3].astype(np.uint16)
    hi = means_u[:, :3].astype(np.uint16)
    q = lo | (hi << 8)
    t = q.astype(np.float64) / 65535.0
    log_v = mins_a + t * denom
    return np.sign(log_v) * np.expm1(np.abs(log_v))


def chunk_log_bounds(pos: np.ndarray) -> tuple[tuple[float, float, float], tuple[float, float, float]]:
    logged = np.sign(pos) * np.log1p(np.abs(pos))
    mins = tuple(float(logged[:, a].min()) for a in range(3))
    maxs = tuple(float(logged[:, a].max()) for a in range(3))
    maxs = tuple(maxs[a] if maxs[a] > mins[a] else mins[a] + 1e-4 for a in range(3))
    return mins, maxs


def decode_scales_lin(scales: np.ndarray, codebook: list[float]) -> np.ndarray:
    cb = np.array(codebook, dtype=np.float64)
    idx = scales[:, 0].astype(np.intp)
    log_s = cb[idx]
    s = np.exp(log_s)
    return np.stack([s, s, s], axis=1).astype(np.float32)


def tight_leaf_bound(pos: np.ndarray, scales_lin: np.ndarray,
                     extra_scale: float = 2.0) -> tuple[list[float], list[float]]:
    lo = np.percentile(pos, 0.5, axis=0)
    hi = np.percentile(pos, 99.5, axis=0)
    margin = float(np.percentile(scales_lin.max(axis=1), 95.0)) * extra_scale
    return (lo - margin).tolist(), (hi + margin).tolist()


def bound_to_log(bmin, bmax):
    """Tree bounds in lod-meta.json must be log-space (runtime InvLogTransform)."""
    bmin = list(bmin.tolist() if hasattr(bmin, "tolist") else bmin)
    bmax = list(bmax.tolist() if hasattr(bmax, "tolist") else bmax)
    return (
        [round(log_transform(float(bmin[i])), 5) for i in range(3)],
        [round(log_transform(float(bmax[i])), 5) for i in range(3)],
    )


def meta_node(node, filenames: list[str]):
    lo, hi = bound_to_log(node.bmin, node.bmax)
    bound = {"min": lo, "max": hi}
    if node.left is not None:
        return {"bound": bound, "children": [meta_node(node.left, filenames), meta_node(node.right, filenames)]}
    lods = {str(k): {"file": v["file"], "offset": 0, "count": v["count"]}
            for k, v in (node.lods or {}).items()}
    return {"bound": bound, "lods": lods}


def emit_leaf_chunk(out_dir: Path, meta_root: dict, planes: dict[str, np.ndarray],
                    indices: np.ndarray, centroids_src: Path | None) -> int:
    n = int(indices.shape[0])
    if n == 0:
        return 0
    out_dir.mkdir(parents=True, exist_ok=True)

    means_l = planes["means_l"][indices]
    means_u = planes["means_u"][indices]
    pos = decode_positions(means_l, means_u,
                           tuple(meta_root["means"]["mins"]),
                           tuple(meta_root["means"]["maxs"]))
    mins, maxs = chunk_log_bounds(pos)

    save_rgba_webp(out_dir / "means_l.webp", means_l)
    save_rgba_webp(out_dir / "means_u.webp", means_u)
    save_rgba_webp(out_dir / "scales.webp", planes["scales"][indices])
    save_rgba_webp(out_dir / "quats.webp", planes["quats"][indices])
    save_rgba_webp(out_dir / "sh0.webp", planes["sh0"][indices])

    chunk_meta = {
        "version": meta_root.get("version", 2),
        "count": n,
        "means": {"mins": list(mins), "maxs": list(maxs), "files": ["means_l.webp", "means_u.webp"]},
        "scales": {"codebook": meta_root["scales"]["codebook"], "files": ["scales.webp"]},
        "quats": {"files": ["quats.webp"]},
        "sh0": {"codebook": meta_root["sh0"]["codebook"], "files": ["sh0.webp"]},
    }

    if "shN" in meta_root:
        save_rgba_webp(out_dir / "shN_labels.webp", planes["shN_labels"][indices])
        if centroids_src is not None:
            dst = out_dir / "shN_centroids.webp"
            try:
                os.link(centroids_src, dst)
            except OSError:
                shutil.copy2(centroids_src, dst)
        else:
            save_rgba_webp(out_dir / "shN_centroids.webp", planes["shN_centroids"])
        chunk_meta["shN"] = {
            "count": meta_root["shN"]["count"],
            "bands": meta_root["shN"]["bands"],
            "codebook": meta_root["shN"]["codebook"],
            "files": ["shN_labels.webp", "shN_centroids.webp"],
        }

    (out_dir / "meta.json").write_text(json.dumps(chunk_meta, indent=2))
    return n


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="PlayCanvas monolithic .sog ZIP")
    ap.add_argument("-o", "--out", required=True, help="Output streamed SOG directory")
    ap.add_argument("--lod-chunk-count", type=int, default=96,
                    help="max splats per leaf in thousands (default 96 -> 98304)")
    ap.add_argument("--lod-chunk-extent", type=float, default=5.0, help="max leaf AABB extent in metres")
    ap.add_argument("--min-leaf", type=int, default=512)
    args = ap.parse_args()

    t0 = time.time()
    src = Path(args.input).resolve()
    out = Path(args.out).resolve()
    if not src.is_file():
        print(f"Input not found: {src}", file=sys.stderr)
        return 1

    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)

    with tempfile.TemporaryDirectory(prefix="wrap_sog_") as tmp:
        tmp_dir = Path(tmp)
        with zipfile.ZipFile(src, "r") as zf:
            zf.extractall(tmp_dir)

        meta_path = tmp_dir / "meta.json"
        if not meta_path.is_file():
            print("ZIP has no meta.json — not a PlayCanvas SOG", file=sys.stderr)
            return 1
        meta_root = json.loads(meta_path.read_text())
        count = int(meta_root["count"])
        print(f"Read meta: {count:,} splats")

        planes: dict[str, np.ndarray] = {}
        for key, fname in [
            ("means_l", "means_l.webp"),
            ("means_u", "means_u.webp"),
            ("scales", "scales.webp"),
            ("quats", "quats.webp"),
            ("sh0", "sh0.webp"),
        ]:
            p = tmp_dir / fname
            if not p.is_file():
                print(f"Missing {fname}", file=sys.stderr)
                return 1
            arr = load_rgba_webp(p, count)
            planes[key] = arr
            print(f"  loaded {fname} ({arr.shape[0]:,} splats from atlas)")

        centroids_src: Path | None = None
        if "shN" in meta_root:
            labels_path = tmp_dir / "shN_labels.webp"
            centroids_path = tmp_dir / "shN_centroids.webp"
            if not labels_path.is_file() or not centroids_path.is_file():
                print("Missing shN webp files", file=sys.stderr)
                return 1
            planes["shN_labels"] = load_rgba_webp(labels_path, count)
            centroids_src = centroids_path
            print(f"  loaded shN_labels ({count:,} splats); centroids copied verbatim")

        print("Decoding positions for KD-tree…")
        pos = decode_positions(planes["means_l"], planes["means_u"],
                               tuple(meta_root["means"]["mins"]),
                               tuple(meta_root["means"]["maxs"]))
        scales_lin = decode_scales_lin(planes["scales"], meta_root["scales"]["codebook"])

        splat_cap = args.lod_chunk_count * 1024
        print(f"KD-tree: cap={splat_cap:,}, extent={args.lod_chunk_extent}m")
        tree = _build_tree(pos.astype(np.float64), np.arange(count, dtype=np.int64),
                           splat_cap, args.lod_chunk_extent, args.min_leaf)
        leaves = list(_iter_leaves(tree))
        sizes = [leaf.indices.shape[0] for leaf in leaves]
        print(f"Split into {len(leaves)} leaves: min={min(sizes):,} max={max(sizes):,} median={sorted(sizes)[len(sizes)//2]:,}")

        filenames: list[str] = []
        total = 0
        for li, leaf in enumerate(leaves):
            bmin, bmax = tight_leaf_bound(pos[leaf.indices], scales_lin[leaf.indices])
            leaf.bmin = np.array(bmin, dtype=np.float64)
            leaf.bmax = np.array(bmax, dtype=np.float64)
            name = f"chunk_{li}"
            filenames.append(name)
            n = emit_leaf_chunk(out / name, meta_root, planes, leaf.indices, centroids_src)
            leaf.lods = {0: {"file": li, "offset": 0, "count": n}}
            total += n
            print(f"  leaf {li}: {n:,} splats  extent={leaf.largest_dim():.2f}m")

        lod_meta = {
            "version": 1,
            "asset": {"generator": f"wrap_playcanvas_sog.py (from {src.name})"},
            "count": int(total),
            "counts": [int(total)],
            "lodLevels": 1,
            "lodChunkCount": args.lod_chunk_count,
            "lodChunkExtent": args.lod_chunk_extent,
            "environment": None,
            "filenames": filenames,
            "tree": meta_node(tree, filenames),
        }
        (out / "lod-meta.json").write_text(json.dumps(lod_meta, indent=2))

    print(f"Done: {len(leaves)} chunks, {total:,} splats -> {out}/lod-meta.json ({time.time()-t0:.1f}s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
