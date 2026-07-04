#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
SuperSplat Streamed SOG baker — KD-tree + multi-LOD + WebP SOG + lod-meta.json.

Desktop 250K profile defaults: 96K splat cap, 5m extent, 5 LOD, raw LOD0.

  python3 sog_baker.py input.spz -o out/festsaal_sog --raw-lod0

For full Festsaal bakes, copy output to:
  projects/GaussianExample-URP/Assets/StreamingAssets/gsplat_lod/festsaal_sog/
"""
from __future__ import annotations

import argparse
import json
import math
import os
import shutil
import sys
import time
from pathlib import Path

import numpy as np

from streamed_sog import _build_tree, _iter_leaves, _prune, _subset, _tight_leaf_bound
from chunk_lod import apply_prune_mask, prune_subset
from spz_reader import read_spz
from merge import voxel_merge

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


def bound_to_log(bmin, bmax):
    """Tree bounds in lod-meta.json must be log-space (runtime InvLogTransform)."""
    bmin = list(bmin.tolist() if hasattr(bmin, "tolist") else bmin)
    bmax = list(bmax.tolist() if hasattr(bmax, "tolist") else bmax)
    return (
        [log_transform(float(bmin[i])) for i in range(3)],
        [log_transform(float(bmax[i])) for i in range(3)],
    )


def encode_positions_split(positions, mins, maxs):
    low_px, high_px = [], []
    for p in positions:
        lr, hr = [0, 0, 0, 255], [0, 0, 0, 255]
        for axis in range(3):
            lv = log_transform(float(p[axis]))
            denom = max(maxs[axis] - mins[axis], 1e-6)
            t = max(0.0, min(1.0, (lv - mins[axis]) / denom))
            q = int(round(t * 65535))
            lr[axis] = q & 0xFF
            hr[axis] = (q >> 8) & 0xFF
        low_px.append(tuple(lr))
        high_px.append(tuple(hr))
    return low_px, high_px


def scales_codebook():
    lo, hi = math.log(0.001), math.log(2.0)
    return [lo + (hi - lo) * (i / 255.0) for i in range(256)]


def scales_codebook_from_data(scales_lin: np.ndarray) -> list[float]:
    """Dataset log-scale range (PlayCanvas uses k-means; uniform bins are OK when wide enough)."""
    ls = np.log(np.maximum(scales_lin.astype(np.float64), 1e-6))
    lo = float(np.percentile(ls, 0.5))
    lo = min(lo, math.log(1e-4))  # avoid overly narrow codebook floor
    hi = float(np.percentile(ls, 99.95))
    if hi <= lo:
        hi = lo + 1.0
    return [lo + (hi - lo) * (i / 255.0) for i in range(256)]


def sh0_codebook():
    return [-3.0 + (6.0 * i / 255.0) for i in range(256)]


def shn_codebook():
    """Match SPZ higher-SH decode: (byte - 128) / 128."""
    return [(i - 128) / 128.0 for i in range(256)]


def sh_bands_for_degree(sh_degree: int) -> int:
    if sh_degree <= 0:
        return 0
    if sh_degree == 1:
        return 1
    if sh_degree == 2:
        return 2
    return 3


def sh_coeff_count(bands: int) -> int:
    return {1: 3, 2: 8, 3: 15}.get(bands, 0)


def quantize_sh_row(sh_row: np.ndarray, codebook: list[float]) -> np.ndarray:
    out = np.empty(sh_row.shape[0], dtype=np.uint8)
    for i, v in enumerate(sh_row):
        out[i] = nearest_idx(float(v), codebook)
    return out


def nearest_labels_batched(q: np.ndarray, centroids: np.ndarray, batch: int = 128) -> np.ndarray:
    """Memory-safe nearest-centroid lookup (avoids N x C x D dense distance matrix)."""
    n = q.shape[0]
    labels = np.empty(n, dtype=np.uint32)
    c = centroids.astype(np.int16, copy=False)
    for start in range(0, n, batch):
        end = min(start + batch, n)
        bq = q[start:end].astype(np.int16, copy=False)
        diff = bq[:, None, :] - c[None, :, :]
        dist = np.sum(diff.astype(np.int32) * diff.astype(np.int32), axis=2)
        labels[start:end] = np.argmin(dist, axis=1)
    return labels


def build_shn_vq(sh: np.ndarray, bands: int, max_centroids: int = 65536):
    """Global VQ over all splats; streaming dedup — bounded RAM."""
    sh_coeffs = sh_coeff_count(bands)
    if sh_coeffs == 0 or sh.shape[0] == 0:
        return None

    flat = sh[:, : sh_coeffs * 3].astype(np.float32, copy=False)
    codebook = shn_codebook()
    q = np.clip(np.round(flat * 128.0 + 128.0), 0, 255).astype(np.uint8)

    pattern_to_label: dict[bytes, int] = {}
    centroids_list: list[np.ndarray] = []
    labels = np.empty(q.shape[0], dtype=np.uint32)
    overflow_rows: list[int] = []

    for i in range(q.shape[0]):
        if i > 0 and i % 50000 == 0:
            print(f"  shN dedup: {i:,}/{q.shape[0]:,} splats, {len(centroids_list):,} centroids...")
        key = q[i].tobytes()
        label = pattern_to_label.get(key)
        if label is not None:
            labels[i] = label
        elif len(centroids_list) < max_centroids:
            label = len(centroids_list)
            pattern_to_label[key] = label
            centroids_list.append(q[i])
            labels[i] = label
        else:
            overflow_rows.append(i)

    centroids = np.stack(centroids_list) if centroids_list else np.zeros((0, sh_coeffs * 3), dtype=np.uint8)
    if overflow_rows:
        print(f"  shN mapping {len(overflow_rows):,} overflow splats to {centroids.shape[0]:,} centroids...")
        overflow_q = q[np.array(overflow_rows, dtype=np.int64)]
        mapped = nearest_labels_batched(overflow_q, centroids)
        for j, i in enumerate(overflow_rows):
            labels[i] = mapped[j]

    return labels, centroids, codebook, sh_coeffs


def build_shn_centroids_atlas(centroids: np.ndarray, sh_coeffs: int) -> np.ndarray:
    """RGBA atlas: one pixel per SH coeff, RGB = quantised triplet (SuperSplat SOG v2)."""
    width = 64 * sh_coeffs
    count = centroids.shape[0]
    height = max(1, (count + 63) // 64)
    atlas = np.zeros((height, width, 4), dtype=np.uint8)
    for label, pattern in enumerate(centroids):
        u = (label % 64) * sh_coeffs
        v = label // 64
        for c in range(sh_coeffs):
            col = u + c
            atlas[v, col, 0] = pattern[c * 3 + 0]
            atlas[v, col, 1] = pattern[c * 3 + 1]
            atlas[v, col, 2] = pattern[c * 3 + 2]
            atlas[v, col, 3] = 255
    return atlas


def write_shn_centroids_webp(path: Path, atlas_rgba: np.ndarray) -> None:
    h, w, _ = atlas_rgba.shape
    img = Image.fromarray(atlas_rgba, mode="RGBA")
    img.save(str(path), format="WEBP", lossless=True, quality=100, method=6)


def write_shn_labels_webp(path: Path, labels: np.ndarray) -> None:
    lo = (labels & 0xFF).astype(np.uint8)
    hi = ((labels >> 8) & 0xFF).astype(np.uint8)
    px = [(int(lo[i]), int(hi[i]), 0, 255) for i in range(labels.shape[0])]
    img = Image.new("RGBA", (len(px), 1))
    img.putdata(px)
    img.save(str(path), format="WEBP", lossless=True, quality=100, method=6)


SH_C0 = 0.28209479177387814
INV_NORM = 1.0 / math.sqrt(2.0)
_QUAT_ENC_ORDER = {0: (1, 2, 3), 1: (0, 2, 3), 2: (0, 1, 3), 3: (0, 1, 2)}


def nearest_idx(val, cb):
    return min(range(256), key=lambda i: abs(cb[i] - val))


def encode_sh0_index(dc_val: float, hcb: list[float]) -> int:
    """Match DecodeSh0Job: codebook[idx] is f_dc; decode applies 0.5 + codebook[idx] * SH_C0."""
    return min(range(256), key=lambda i: abs(hcb[i] - dc_val))


def pack_smallest_three_quat(x: float, y: float, z: float, w: float) -> tuple[int, int, int, int]:
    q = [x, y, z, w]
    largest = max(range(4), key=lambda i: abs(q[i]))
    if q[largest] < 0.0:
        q = [-v for v in q]
    out = []
    for i in _QUAT_ENC_ORDER[largest]:
        v = max(-1.0, min(1.0, q[i] / INV_NORM))
        out.append(int(round((v + 1.0) * 0.5 * 255.0)))
    return (out[0], out[1], out[2], 252 + largest)


def assign_labels_to_sh(sh: np.ndarray, centroids: np.ndarray, codebook: list[float]) -> np.ndarray:
    """Map each row of sh to nearest global centroid label."""
    sh_coeffs = centroids.shape[1] // 3
    flat = sh[:, : sh_coeffs * 3].astype(np.float32, copy=False)
    q = np.clip(np.round(flat * 128.0 + 128.0), 0, 255).astype(np.uint8)
    return nearest_labels_batched(q, centroids)


def emit_chunk(out_dir: Path, sub: dict, shn_ctx: dict | None = None, shn_labels: np.ndarray | None = None,
               scales_cb: list[float] | None = None) -> int:
    n = sub["positions"].shape[0]
    if n == 0:
        return 0
    out_dir.mkdir(parents=True, exist_ok=True)

    pos = sub["positions"]
    scales_lin = sub["scales_lin"]
    opacity = sub["opacity"]
    quats = sub["quats"]
    colors = sub.get("dc", np.zeros((n, 3), dtype=np.float32))

    logged = np.array([[log_transform(float(p[a])) for a in range(3)] for p in pos])
    mins = tuple(float(logged[:, a].min()) for a in range(3))
    maxs = tuple(float(logged[:, a].max()) for a in range(3))
    maxs = tuple(maxs[a] if maxs[a] > mins[a] else mins[a] + 1e-4 for a in range(3))

    means_l, means_u = encode_positions_split(pos, mins, maxs)
    scb = scales_cb if scales_cb is not None else scales_codebook()
    hcb = sh0_codebook()

    scales_px, quats_px, sh0_px = [], [], []
    for i in range(n):
        sl = np.array(scales_lin[i], dtype=np.float64)
        mx = float(np.max(sl))
        mn = max(float(np.min(sl)), 1e-6)
        if mx / mn > 8.0:
            sl = np.maximum(sl, mx / 8.0)
        sx = nearest_idx(math.log(max(float(sl[0]), 1e-6)), scb)
        sy = nearest_idx(math.log(max(float(sl[1]), 1e-6)), scb)
        sz = nearest_idx(math.log(max(float(sl[2]), 1e-6)), scb)
        # Alpha must be 255: Pillow lossless WebP zeroes RGB when A=0 on decode.
        scales_px.append((sx, sy, sz, 255))
        q = quats[i]
        quats_px.append(pack_smallest_three_quat(float(q[0]), float(q[1]), float(q[2]), float(q[3])))
        ri = encode_sh0_index(float(colors[i, 0]), hcb)
        gi = encode_sh0_index(float(colors[i, 1]), hcb)
        bi = encode_sh0_index(float(colors[i, 2]), hcb)
        a = int(max(0, min(255, opacity[i] * 255)))
        sh0_px.append((ri, gi, bi, a))

    def write_webp(name, px):
        img = Image.new("RGBA", (len(px), 1))
        img.putdata(px)
        img.save(str(out_dir / name), format="WEBP", lossless=True, quality=100, method=6)

    write_webp("means_l.webp", means_l)
    write_webp("means_u.webp", means_u)
    write_webp("scales.webp", scales_px)
    write_webp("quats.webp", quats_px)
    write_webp("sh0.webp", sh0_px)

    meta = {
        "version": 2,
        "count": n,
        "means": {
            "mins": list(mins),
            "maxs": list(maxs),
            "files": ["means_l.webp", "means_u.webp"],
        },
        "scales": {
            "codebook": scb,
            "files": ["scales.webp"],
        },
        "quats": {
            "files": ["quats.webp"],
        },
        "sh0": {
            "codebook": hcb,
            "files": ["sh0.webp"],
        },
    }

    if shn_ctx is not None and shn_labels is not None and shn_ctx.get("bands", 0) > 0:
        write_shn_labels_webp(out_dir / "shN_labels.webp", shn_labels)
        centroids_src = shn_ctx["centroids_path"]
        dst = out_dir / "shN_centroids.webp"
        try:
            os.link(centroids_src, dst)
        except OSError:
            shutil.copy2(centroids_src, dst)
        meta["shN"] = {
            "count": int(shn_ctx["centroid_count"]),
            "bands": int(shn_ctx["bands"]),
            "codebook": shn_ctx["codebook"],
            "files": ["shN_labels.webp", "shN_centroids.webp"],
        }

    (out_dir / "meta.json").write_text(json.dumps(meta, indent=2))
    return n


def meta_node(node, filenames):
    lo, hi = bound_to_log(node.bmin, node.bmax)
    bound = {"min": lo, "max": hi}
    if node.left is None:
        lods = {str(k): {"file": v["file"], "offset": 0, "count": v["count"]}
                for k, v in (node.lods or {}).items()}
        return {"bound": bound, "lods": lods}
    return {"bound": bound,
            "children": [meta_node(node.left, filenames), meta_node(node.right, filenames)]}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("input")
    ap.add_argument("-o", "--out", default="out/festsaal_sog")
    ap.add_argument("--lod-chunk-count", type=int, default=96)
    ap.add_argument("--lod-chunk-extent", type=float, default=5.0)
    ap.add_argument("--levels", type=int, default=5)
    ap.add_argument("--voxel", type=float, default=0.05)
    ap.add_argument("--lod-mult", type=float, default=2.0)
    ap.add_argument("--prune-opacity", type=float, default=0.04,
                    help="opacity prune for merged LOD1+ (ignored on LOD0 when --raw-lod0)")
    ap.add_argument("--prune-min-scale", type=float, default=0.0)
    ap.add_argument("--prune-max-scale", type=float, default=0.0,
                    help="drop giant-scale floaters in merged LOD1+ (e.g. 0.3)")
    ap.add_argument("--prune-aspect-ratio", type=float, default=0.0,
                    help="needle prune for merged LOD1+ only when --raw-lod0 (default 0)")
    ap.add_argument("--lod0-prune-opacity", type=float, default=0.0,
                    help="LOD0-only opacity prune when --raw-lod0 (default 0 = keep all)")
    ap.add_argument("--lod0-prune-min-scale", type=float, default=0.0)
    ap.add_argument("--lod0-prune-max-scale", type=float, default=0.0)
    ap.add_argument("--lod0-prune-aspect-ratio", type=float, default=0.0,
                    help="LOD0-only aspect prune when --raw-lod0 (default 0 = keep all source splats)")
    ap.add_argument("--raw-lod0", action="store_true",
                    help="LOD0 = raw leaf splats (no merge). Prune only via --lod0-prune-* (default: none).")
    ap.add_argument("--skip-shn", action="store_true", help="Skip shN VQ (lower RAM, DC-only rendering)")
    ap.add_argument("--shn-max-centroids", type=int, default=65536)
    args = ap.parse_args()

    t0 = time.time()
    d = read_spz(args.input)
    source_count = int(d["positions"].shape[0])
    out = Path(args.out)
    if out.exists():
        shutil.rmtree(out)
    out.mkdir(parents=True)
    print(f"read {source_count:,} splats in {time.time()-t0:.1f}s")

    if args.raw_lod0:
        # LOD0 keeps the full source; prune only via --lod0-prune-* per leaf.
        # Merged LOD1+ prune runs inside voxel_merge below.
        print("raw LOD0: skipping global prune (LOD0 splat count should match source)")
    else:
        d = _prune(d, args.prune_opacity, args.prune_min_scale, args.prune_max_scale)
        keep = apply_prune_mask(
            d,
            prune_opacity=0.0,
            prune_min_scale=0.0,
            prune_max_scale=args.prune_max_scale,
            prune_aspect_ratio=args.prune_aspect_ratio,
        )
        if not keep.all():
            n0 = d["positions"].shape[0]
            d = prune_subset(d, keep)
            print(f"global prune: {n0:,} -> {d['positions'].shape[0]:,} splats")
    scales_cb = scales_codebook_from_data(d["scales_lin"])
    print(f"scale codebook log range [{scales_cb[0]:.3f}, {scales_cb[-1]:.3f}] "
          f"(linear ~{math.exp(scales_cb[0]):.4g} .. {math.exp(scales_cb[-1]):.4g})")

    shn_ctx = None
    bands = sh_bands_for_degree(int(d.get("sh_degree", 0)))
    if (not args.skip_shn and bands > 0 and d.get("sh") is not None
            and d["sh"].shape[0] > 0 and d["sh"].shape[1] > 0):
        print(f"Building shN VQ (bands={bands}, max_centroids={args.shn_max_centroids})...")
        vq = build_shn_vq(d["sh"], bands, max_centroids=args.shn_max_centroids)
        if vq is not None:
            labels, centroids, shn_cb, sh_coeffs = vq
            atlas = build_shn_centroids_atlas(centroids, sh_coeffs)
            centroids_path = out / "_shN_centroids_shared.webp"
            write_shn_centroids_webp(centroids_path, atlas)
            shn_ctx = {
                "labels": labels,
                "centroids": centroids,
                "centroids_path": str(centroids_path.resolve()),
                "centroid_count": int(centroids.shape[0]),
                "bands": bands,
                "codebook": shn_cb,
            }
            print(f"  shN: {centroids.shape[0]:,} centroids, atlas {atlas.shape[1]}x{atlas.shape[0]}")

    cap = args.lod_chunk_count * 1024
    tree = _build_tree(d["positions"].astype(np.float64), np.arange(d["positions"].shape[0]),
                       cap, args.lod_chunk_extent, 512)
    leaves = list(_iter_leaves(tree))
    filenames, counts = [], [0] * args.levels

    for li, leaf in enumerate(leaves):
        leaf.lods = {}
        sub = _subset(d, leaf.indices)
        bmin, bmax = _tight_leaf_bound(d, leaf.indices)
        leaf.bmin, leaf.bmax = np.array(bmin), np.array(bmax)
        for lvl in range(args.levels):
            if lvl == 0 and args.raw_lod0:
                keep = apply_prune_mask(
                    sub,
                    prune_opacity=args.lod0_prune_opacity,
                    prune_min_scale=args.lod0_prune_min_scale,
                    prune_max_scale=args.lod0_prune_max_scale,
                    prune_aspect_ratio=args.lod0_prune_aspect_ratio,
                )
                lod_sub = sub if keep.all() else prune_subset(sub, keep)
            else:
                vx = args.voxel * (args.lod_mult ** max(0, lvl - (1 if args.raw_lod0 else 0)))
                lod_sub = voxel_merge(
                    sub,
                    voxel_size=vx,
                    prune_opacity=args.prune_opacity,
                    prune_min_scale=args.prune_min_scale,
                    prune_max_scale=args.prune_max_scale,
                    prune_aspect_ratio=args.prune_aspect_ratio,
                    op_boost=1.3,
                )
            name = f"{li}_{lvl}"
            filenames.append(name)
            fi = len(filenames) - 1
            shn_labels = None
            if shn_ctx is not None:
                if lvl == 0 and args.raw_lod0:
                    shn_labels = shn_ctx["labels"][leaf.indices]
                elif lod_sub["sh"].shape[0] > 0:
                    shn_labels = assign_labels_to_sh(
                        lod_sub["sh"], shn_ctx["centroids"], shn_ctx["codebook"])
            n = emit_chunk(out / name, lod_sub, shn_ctx, shn_labels, scales_cb=scales_cb)
            leaf.lods[lvl] = {"file": fi, "offset": 0, "count": n}
            counts[lvl] += n

    lod_meta = {
        "version": 1,
        "count": int(counts[0]),
        "counts": counts,
        "lodLevels": args.levels,
        "sourceSplatCount": source_count,
        "environment": None,
        "filenames": filenames,
        "tree": meta_node(tree, filenames),
    }
    (out / "lod-meta.json").write_text(json.dumps(lod_meta, indent=2))
    print(f"LOD splat totals: {counts}")
    if args.raw_lod0 and counts[0] != source_count:
        pct = 100.0 * counts[0] / max(source_count, 1)
        print(f"WARNING: LOD0 {counts[0]:,} != source {source_count:,} ({pct:.1f}%) — check --lod0-prune-* flags")
    elif args.raw_lod0:
        print(f"LOD0 matches source: {counts[0]:,} splats")
    print(f"Done: {len(leaves)} leaves -> {out}/lod-meta.json ({time.time()-t0:.1f}s)")


if __name__ == "__main__":
    main()
