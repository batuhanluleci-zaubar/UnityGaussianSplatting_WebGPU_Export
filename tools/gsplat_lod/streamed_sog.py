#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Streamed multi-LOD baker — SPZ variant of splat-transform's Streamed SOG.

Mirrors playcanvas/splat-transform's write-lod.ts logical design:
  * a spatial KD-tree over the scene centroids with the DUAL stop rule:
      split while (node.count > lodChunkCount*1024) OR (node.aabb.largestDim() > lodChunkExtent)
    stop (become a manifest leaf) when BOTH thresholds are satisfied
  * per-leaf tight world AABB from the actual Gaussian ellipsoids (calcBound)
  * each leaf carries K independent LOD copies (LOD0 = raw splats, LOD>=1 = voxel-merged)
  * lod-meta.json manifest with field names matching splat-transform's LodMeta:
      { version, count, counts[per-LOD], lodLevels, filenames[], tree: MetaNode }
      MetaNode = { bound:{min,max}, children?:[l,r], lods?:{L:{file,offset,count}} }

Differences from splat-transform's stock output (deliberate, documented in STREAMED_LOD_DESIGN.md):
  * Files are SPZ, not SOG WebP (Adreno WebP decode is pure cost).
  * NO leaf packing: each (leaf,LOD) is its own file (offset always 0). splat-transform packs
    to reduce HTTP round-trips; we stream from local disk via Addressables so packing is pure
    complexity.

  python3 streamed_sog.py in.spz -o out/streamed --lod-chunk-count 128 --lod-chunk-extent 6 \
      --levels 5 --raw-lod0 --prune-opacity 0.05
"""
import argparse
import json
import os
import time

import numpy as np

from spz_reader import read_spz
from spz_writer import write_spz
from merge import voxel_merge


# --- Spatial KD-tree with dual stop rule (mirrors write-lod.ts:209) --------------------------

class _Node:
    __slots__ = ("indices", "bmin", "bmax", "left", "right", "lods")
    def __init__(self, indices, bmin, bmax):
        self.indices = indices          # np.ndarray[int] for leaves, or None once split
        self.bmin = bmin                # np.ndarray[3] float64
        self.bmax = bmax
        self.left = None
        self.right = None
        self.lods = None                # dict[level -> {"file":int, "offset":int, "count":int}]

    def largest_dim(self):
        return float((self.bmax - self.bmin).max())


def _build_tree(pos, indices, splat_cap, extent_cap, min_leaf=512):
    """Median-split KD-tree over centroids. Stop when both (count <= cap) AND (extent <= cap),
    OR when the node has fewer than `min_leaf` splats (protects against per-outlier explosion
    when a source capture has spatially isolated giant-scale floaters)."""
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
        # degenerate (all equal) — hard split at midpoint by order
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
        yield node; return
    yield from _iter_leaves(node.left)
    yield from _iter_leaves(node.right)


def _tight_leaf_bound(d, indices, extra_scale=2.0):
    """splat-transform's calcBound spirit: robust position bounds + a scale-based margin so the
    box conservatively encloses the ELLIPSOIDS, not just their centres."""
    p = d["positions"][indices].astype(np.float64)
    lo = np.percentile(p, 0.5, axis=0)
    hi = np.percentile(p, 99.5, axis=0)
    margin = float(np.percentile(d["scales_lin"][indices], 95.0)) * extra_scale
    return (lo - margin).tolist(), (hi + margin).tolist()


# --- LOD emission ----------------------------------------------------------------------------

def _subset(d, indices):
    out = {}
    N = d["positions"].shape[0]
    for k, v in d.items():
        out[k] = v[indices] if isinstance(v, np.ndarray) and v.ndim >= 1 and v.shape[0] == N else v
    return out


def _prune(sub, prune_opacity, prune_min_scale, prune_max_scale=0.0):
    if prune_opacity <= 0 and prune_min_scale <= 0 and prune_max_scale <= 0:
        return sub
    keep = np.ones(sub["positions"].shape[0], bool)
    if prune_opacity > 0:
        keep &= sub["opacity"] >= prune_opacity
    if prune_min_scale > 0:
        keep &= sub["scales_lin"].max(axis=1) >= prune_min_scale
    if prune_max_scale > 0:
        keep &= sub["scales_lin"].max(axis=1) <= prune_max_scale
    if keep.all():
        return sub
    out = {}
    for k, v in sub.items():
        out[k] = v[keep] if isinstance(v, np.ndarray) and v.ndim >= 1 and v.shape[0] == keep.size else v
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input .spz")
    ap.add_argument("-o", "--out", default="out/streamed", help="output directory")
    ap.add_argument("--lod-chunk-count", type=int, default=128,
                    help="approx max Gaussians per chunk, in THOUSANDS (splat-transform default 512; 64-128 for XR)")
    ap.add_argument("--lod-chunk-extent", type=float, default=6.0,
                    help="approx max chunk size in world units (splat-transform default 16; 4-8 for XR)")
    ap.add_argument("--levels", type=int, default=5, help="LOD levels (LOD0 fine .. LODk coarse)")
    ap.add_argument("--voxel", type=float, default=0.05, help="voxel size for LOD1 (LOD0 raw when --raw-lod0)")
    ap.add_argument("--lod-mult", type=float, default=2.0, help="voxel growth per LOD level")
    ap.add_argument("--op-boost", type=float, default=1.3, help="opacity boost so coarse levels read solid")
    ap.add_argument("--prune-opacity", type=float, default=0.0, help="drop splats below this opacity")
    ap.add_argument("--prune-min-scale", type=float, default=0.0, help="drop splats whose largest axis scale is below this")
    ap.add_argument("--prune-max-scale", type=float, default=0.0,
                    help="drop giant-scale floaters (e.g. --prune-max-scale 3.0 kills anything > 3m). Essential for "
                         "noisy captures: spatially isolated giant splats would otherwise become their own KD leaves.")
    ap.add_argument("--min-leaf", type=int, default=512,
                    help="never split a subtree below this many splats (prevents per-outlier explosion). Default: 512")
    ap.add_argument("--raw-lod0", action="store_true",
                    help="LOD0 = RAW splats of the chunk (no merge, matches splat-transform's --decimate -F 100% top LOD)")
    args = ap.parse_args()

    t0 = time.time()
    d = read_spz(args.input)
    n0 = d["positions"].shape[0]
    scene = os.path.splitext(os.path.basename(args.input))[0].replace(" ", "_")
    os.makedirs(args.out, exist_ok=True)
    print(f"read {n0:,} splats (sh_degree={d['sh_degree']}) in {time.time()-t0:.1f}s")

    # global prune before tree build (splat-transform applies filters up-front too)
    d = _prune(d, args.prune_opacity, args.prune_min_scale, args.prune_max_scale)
    n1 = d["positions"].shape[0]
    if n1 != n0:
        print(f"pruned {n0-n1:,} floaters ({100*(n0-n1)/n0:.1f}%) -> {n1:,}")

    splat_cap = args.lod_chunk_count * 1024
    print(f"KD-tree stop rule: count <= {splat_cap:,} AND extent <= {args.lod_chunk_extent}m")
    tree = _build_tree(d["positions"].astype(np.float64), np.arange(n1), splat_cap, args.lod_chunk_extent, args.min_leaf)
    leaves = list(_iter_leaves(tree))
    sizes = [l.indices.shape[0] for l in leaves]
    ext = [l.largest_dim() for l in leaves]
    print(f"KD-split into {len(leaves)} leaves: sizes min={min(sizes):,} max={max(sizes):,} median={sorted(sizes)[len(sizes)//2]:,}")
    print(f"                       extents (m): min={min(ext):.2f} max={max(ext):.2f}")

    # emit per-(leaf, level) SPZ files + populate leaf.lods[level] = {file, offset, count}
    filenames = []
    counts = [0] * args.levels
    for leaf_i, leaf in enumerate(leaves):
        leaf.lods = {}
        sub = _subset(d, leaf.indices)
        for lvl in range(args.levels):
            fname = f"{lvl}_{leaf_i}.spz"
            path = os.path.join(args.out, fname)
            if lvl == 0 and args.raw_lod0:
                m = sub                                # raw
                voxel = 0.0
            else:
                vox = args.voxel * (args.lod_mult ** max(0, lvl - (1 if args.raw_lod0 else 0)))
                m = voxel_merge(sub, vox, op_boost=args.op_boost)
                voxel = vox
            write_spz(path, m["positions"], m["scales_lin"], m["quats"],
                      m["opacity"], m["dc"], m["sh"], sh_degree=d.get("sh_degree", 3))
            file_i = len(filenames); filenames.append(fname)
            n = int(m["positions"].shape[0])
            counts[lvl] += n
            leaf.lods[lvl] = {"file": file_i, "offset": 0, "count": n, "voxel": round(voxel, 5)}
        bmin, bmax = _tight_leaf_bound(d, leaf.indices)
        leaf.bmin = np.array(bmin); leaf.bmax = np.array(bmax)
        print(f"  leaf {leaf_i}: {leaf.indices.shape[0]:,} splats  extent={leaf.largest_dim():.1f}m  " +
              " ".join(f"L{L}={leaf.lods[L]['count']:,}" for L in range(args.levels)))

    def to_meta(node):
        bound = {"min": [round(x, 5) for x in node.bmin.tolist()],
                 "max": [round(x, 5) for x in node.bmax.tolist()]}
        if node.left is not None:
            return {"bound": bound, "children": [to_meta(node.left), to_meta(node.right)]}
        return {"bound": bound, "lods": {str(L): v for L, v in node.lods.items()}}

    manifest = {
        "version": 1,
        "asset": {"generator": f"streamed_sog.py v1 (splat-transform parity, SPZ variant)"},
        "count": int(n1),
        "counts": counts,
        "lodLevels": args.levels,
        "lodChunkCount": args.lod_chunk_count,
        "lodChunkExtent": args.lod_chunk_extent,
        "filenames": filenames,
        "tree": to_meta(tree),
    }
    with open(os.path.join(args.out, "lod-meta.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    total_bytes = sum(os.path.getsize(os.path.join(args.out, fn)) for fn in filenames)
    print(f"\nwrote {len(filenames)} SPZ files ({total_bytes/1e6:.0f} MB total) + lod-meta.json")
    print(f"totals per LOD: {counts}  (source {n1:,})")
    print(f"done in {time.time()-t0:.1f}s -> {args.out}")


if __name__ == "__main__":
    main()
