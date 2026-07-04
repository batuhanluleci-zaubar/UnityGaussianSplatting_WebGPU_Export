#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Streamed multi-LOD baker — SPZ variant of splat-transform's Streamed SOG.

Emits:
  * per-(leaf, LOD) .spz files
  * manifest.json  — Unity LodManifest v2 (flat chunks[] + filenames[]) for Addressables SPZ streaming
  * lod-meta.json  — tree layout (splat-transform parity, optional tooling)

  python3 streamed_sog.py in.spz -o out/streamed --lod-chunk-count 96 --lod-chunk-extent 5 \
      --levels 5 --raw-lod0 --prune-opacity 0.04 --prune-max-scale 0.3 --prune-aspect-ratio 20
"""
import argparse
import json
import math
import os
import time

import numpy as np

from chunk_lod import apply_prune_mask, chunk_aabb, prune_subset
from spz_reader import read_spz
from spz_writer import write_spz
from merge import voxel_merge


def log_transform(x: float) -> float:
    return math.copysign(math.log1p(abs(x)), x)


def bound_to_log(bmin, bmax):
    """Tree bounds in lod-meta.json must be log-space (runtime InvLogTransform)."""
    bmin = list(bmin.tolist() if hasattr(bmin, "tolist") else bmin)
    bmax = list(bmax.tolist() if hasattr(bmax, "tolist") else bmax)
    return (
        [round(log_transform(float(bmin[i])), 5) for i in range(3)],
        [round(log_transform(float(bmax[i])), 5) for i in range(3)],
    )


class _Node:
    __slots__ = ("indices", "bmin", "bmax", "left", "right", "lods")
    def __init__(self, indices, bmin, bmax):
        self.indices = indices
        self.bmin = bmin
        self.bmax = bmax
        self.left = None
        self.right = None
        self.lods = None

    def largest_dim(self):
        return float((self.bmax - self.bmin).max())


def _build_tree(pos, indices, splat_cap, extent_cap, min_leaf=512):
    p = pos[indices]
    bmin = p.min(axis=0)
    bmax = p.max(axis=0)
    node = _Node(indices, bmin, bmax)
    if indices.shape[0] <= min_leaf:
        return node
    if indices.shape[0] <= splat_cap and node.largest_dim() <= extent_cap:
        return node
    axis = int(np.argmax(bmax - bmin))
    med = float(np.median(p[:, axis]))
    left_mask = p[:, axis] <= med
    left_idx = indices[left_mask]
    right_idx = indices[~left_mask]
    if left_idx.shape[0] == 0 or right_idx.shape[0] == 0:
        order = np.argsort(p[:, axis])
        half = indices.shape[0] // 2
        left_idx = indices[order[:half]]
        right_idx = indices[order[half:]]
    node.indices = None
    node.left = _build_tree(pos, left_idx, splat_cap, extent_cap)
    node.right = _build_tree(pos, right_idx, splat_cap, extent_cap)
    node.bmin = np.minimum(node.left.bmin, node.right.bmin)
    node.bmax = np.maximum(node.left.bmax, node.right.bmax)
    return node


def _iter_leaves(node):
    if node.left is None:
        yield node
        return
    yield from _iter_leaves(node.left)
    yield from _iter_leaves(node.right)


def _tight_leaf_bound(d, indices, extra_scale=2.0):
    p = d["positions"][indices].astype(np.float64)
    lo = np.percentile(p, 0.5, axis=0)
    hi = np.percentile(p, 99.5, axis=0)
    margin = float(np.percentile(d["scales_lin"][indices], 95.0)) * extra_scale
    return (lo - margin).tolist(), (hi + margin).tolist()


def _subset(d, indices):
    out = {}
    N = d["positions"].shape[0]
    for k, v in d.items():
        out[k] = v[indices] if isinstance(v, np.ndarray) and v.ndim >= 1 and v.shape[0] == N else v
    return out


def _prune(d, prune_opacity=0.0, prune_min_scale=0.0, prune_max_scale=0.0):
    """Opacity / scale prune (sog_baker compatibility). Aspect prune is applied separately."""
    keep = apply_prune_mask(
        d,
        prune_opacity=prune_opacity,
        prune_min_scale=prune_min_scale,
        prune_max_scale=prune_max_scale,
    )
    if keep.all():
        return d
    return prune_subset(d, keep)


def _global_prune(d, prune_opacity, prune_min_scale, prune_max_scale, prune_aspect_ratio):
    keep = apply_prune_mask(
        d,
        prune_opacity=prune_opacity,
        prune_min_scale=prune_min_scale,
        prune_max_scale=prune_max_scale,
        prune_aspect_ratio=prune_aspect_ratio,
    )
    if keep.all():
        return d
    return prune_subset(d, keep)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input .spz")
    ap.add_argument("-o", "--out", default="out/streamed", help="output directory")
    ap.add_argument("--lod-chunk-count", type=int, default=128)
    ap.add_argument("--lod-chunk-extent", type=float, default=6.0)
    ap.add_argument("--levels", type=int, default=5)
    ap.add_argument("--voxel", type=float, default=0.05)
    ap.add_argument("--lod-mult", type=float, default=2.0)
    ap.add_argument("--op-boost", type=float, default=1.3)
    ap.add_argument("--prune-opacity", type=float, default=0.0)
    ap.add_argument("--prune-min-scale", type=float, default=0.0)
    ap.add_argument("--prune-max-scale", type=float, default=0.0)
    ap.add_argument("--prune-aspect-ratio", type=float, default=0.0,
                    help="drop needle splats (max/min axis ratio) before tree build and LOD0")
    ap.add_argument("--lod0-prune-aspect-ratio", type=float, default=0.0,
                    help="LOD0-only aspect prune (defaults to --prune-aspect-ratio when 0)")
    ap.add_argument("--min-leaf", type=int, default=512)
    ap.add_argument("--raw-lod0", action="store_true")
    ap.add_argument("--k-sigma", type=float, default=3.0, help="ellipsoid bound k-sigma for manifest v2")
    args = ap.parse_args()

    lod0_ar = args.lod0_prune_aspect_ratio if args.lod0_prune_aspect_ratio > 0 else args.prune_aspect_ratio

    t0 = time.time()
    d = read_spz(args.input)
    n0 = d["positions"].shape[0]
    scene = os.path.splitext(os.path.basename(args.input))[0].replace(" ", "_")
    os.makedirs(args.out, exist_ok=True)
    print(f"read {n0:,} splats (sh_degree={d['sh_degree']}) in {time.time()-t0:.1f}s")

    if args.raw_lod0:
        print("raw LOD0: skipping global prune (LOD0 splat count should match source)")
    else:
        d = _global_prune(d, args.prune_opacity, args.prune_min_scale, args.prune_max_scale, args.prune_aspect_ratio)
        n1 = d["positions"].shape[0]
        if n1 != n0:
            print(f"global prune: {n0:,} -> {n1:,} ({100*(n0-n1)/n0:.1f}% removed)")

    n_work = d["positions"].shape[0]
    splat_cap = args.lod_chunk_count * 1024
    print(f"KD-tree stop rule: count <= {splat_cap:,} AND extent <= {args.lod_chunk_extent}m")
    tree = _build_tree(d["positions"].astype(np.float64), np.arange(n_work), splat_cap, args.lod_chunk_extent, args.min_leaf)
    leaves = list(_iter_leaves(tree))
    sizes = [l.indices.shape[0] for l in leaves]
    ext = [l.largest_dim() for l in leaves]
    print(f"KD-split into {len(leaves)} leaves: sizes min={min(sizes):,} max={max(sizes):,} median={sorted(sizes)[len(sizes)//2]:,}")
    print(f"                       extents (m): min={min(ext):.2f} max={max(ext):.2f}")

    filenames = []
    counts = [0] * args.levels
    chunks = []

    for leaf_i, leaf in enumerate(leaves):
        leaf.lods = {}
        sub = _subset(d, leaf.indices)
        bmin, bmax, bminE, bmaxE = chunk_aabb(sub, k_sigma=args.k_sigma)
        centre = [(a + b) * 0.5 for a, b in zip(bmin, bmax)]
        entry = {
            "id": leaf_i,
            "boundMin": bmin,
            "boundMax": bmax,
            "boundMinEllipsoid": bminE,
            "boundMaxEllipsoid": bmaxE,
            "centre": centre,
            "lods": [],
        }

        for lvl in range(args.levels):
            spz_name = f"{lvl}_{leaf_i}.spz"
            path = os.path.join(args.out, spz_name)
            if lvl == 0 and args.raw_lod0:
                keep = apply_prune_mask(
                    sub,
                    prune_opacity=0.0,
                    prune_min_scale=0.0,
                    prune_max_scale=0.0,
                    prune_aspect_ratio=lod0_ar,
                )
                m = prune_subset(sub, keep)
                voxel = 0.0
            else:
                vox = args.voxel * (args.lod_mult ** max(0, lvl - (1 if args.raw_lod0 else 0)))
                m = voxel_merge(
                    sub, vox,
                    prune_opacity=args.prune_opacity,
                    prune_min_scale=args.prune_min_scale,
                    prune_max_scale=args.prune_max_scale,
                    prune_aspect_ratio=args.prune_aspect_ratio,
                    op_boost=args.op_boost,
                )
                voxel = vox

            write_spz(path, m["positions"], m["scales_lin"], m["quats"],
                      m["opacity"], m["dc"], m["sh"], sh_degree=d.get("sh_degree", 3))
            file_i = len(filenames)
            filenames.append(spz_name)
            n = int(m["positions"].shape[0])
            counts[lvl] += n
            leaf.lods[lvl] = {"file": file_i, "offset": 0, "count": n, "voxel": round(voxel, 5)}
            entry["lods"].append({
                "level": lvl,
                "file": spz_name,
                "fileIdx": file_i,
                "voxel": round(voxel, 5),
                "splatCount": n,
            })

        bmin_t, bmax_t = _tight_leaf_bound(d, leaf.indices)
        leaf.bmin = np.array(bmin_t)
        leaf.bmax = np.array(bmax_t)
        chunks.append(entry)
        print(f"  leaf {leaf_i}: {leaf.indices.shape[0]:,} splats  extent={leaf.largest_dim():.1f}m  " +
              " ".join(f"L{L}={leaf.lods[L]['count']:,}" for L in range(args.levels)))

    def to_meta(node):
        lo, hi = bound_to_log(node.bmin, node.bmax)
        bound = {"min": lo, "max": hi}
        if node.left is not None:
            return {"bound": bound, "children": [to_meta(node.left), to_meta(node.right)]}
        return {"bound": bound, "lods": {str(L): v for L, v in node.lods.items()}}

    tree_manifest = {
        "version": 1,
        "asset": {"generator": "streamed_sog.py v2 (SPZ variant)"},
        "count": int(n1),
        "counts": counts,
        "lodLevels": args.levels,
        "lodChunkCount": args.lod_chunk_count,
        "lodChunkExtent": args.lod_chunk_extent,
        "filenames": filenames,
        "tree": to_meta(tree),
    }
    with open(os.path.join(args.out, "lod-meta.json"), "w") as f:
        json.dump(tree_manifest, f, indent=2)

    unity_manifest = {
        "version": 2,
        "scene": scene,
        "chunkCount": len(leaves),
        "lodLevels": args.levels,
        "voxel0": args.voxel,
        "lodMult": args.lod_mult,
        "sourceSplatCount": int(n0),
        "lod0PruneRemoved": int(n0 - n1),
        "totalSplatsByLod": counts,
        "generator": "streamed_sog.py v2",
        "filenames": filenames,
        "bounds": {"kind": "ellipsoidExtent", "kSigma": float(args.k_sigma)},
        "chunks": chunks,
    }
    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(unity_manifest, f, indent=2)

    total_bytes = sum(os.path.getsize(os.path.join(args.out, fn)) for fn in filenames)
    print(f"\nwrote {len(filenames)} SPZ files ({total_bytes/1e6:.0f} MB total)")
    print(f"manifest.json + lod-meta.json  totals per LOD: {counts}  (source {n1:,})")
    print(f"done in {time.time()-t0:.1f}s -> {args.out}")


if __name__ == "__main__":
    main()
