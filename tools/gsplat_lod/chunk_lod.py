#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Chunked multi-LOD baker (spike for the Streamed-SOG design).

Splits a .spz scene into spatial CHUNKS (median-split KD-tree), and for each chunk
emits K INDEPENDENT LOD levels (LOD0 = fine merge, LODk = progressively coarser merge)
plus a manifest.json. This is the offline half of STREAMED_LOD_DESIGN.md:
  - LODs are independent complete copies of the chunk (swap, not accumulate).
  - Each chunk carries a tight world AABB for runtime frustum-cull + screen-error LOD.

  python3 chunk_lod.py "Festsaal 10m bereinigt.spz" -o out/spike --chunks 4 --levels 2 --voxel 0.05

Output: out/spike/<scene>_c<ID>_lod<L>.ply  +  out/spike/manifest.json
Bake each .ply with the Unity Gaussian Splat Creator; the runtime probe reads manifest.json.
"""
import argparse
import json
import os
import time

import numpy as np

from spz_reader import read_spz
from merge import voxel_merge
from ply_writer import write_ply


def kd_split(pos, target_chunks):
    """Median-split KD-tree over centroids -> list of index arrays (~target_chunks leaves).
    Splits the largest-count leaf on its longest axis at the median until we have enough."""
    leaves = [np.arange(pos.shape[0])]
    while len(leaves) < target_chunks:
        # pick the leaf with the most points to split next
        i = max(range(len(leaves)), key=lambda k: leaves[k].shape[0])
        idx = leaves[i]
        if idx.shape[0] < 2:
            break
        p = pos[idx]
        axis = int(np.argmax(p.max(axis=0) - p.min(axis=0)))  # longest extent axis
        med = np.median(p[:, axis])
        left = idx[p[:, axis] <= med]
        right = idx[p[:, axis] > med]
        if left.shape[0] == 0 or right.shape[0] == 0:  # degenerate (all equal) -> hard split
            half = idx.shape[0] // 2
            order = np.argsort(p[:, axis])
            left, right = idx[order[:half]], idx[order[half:]]
        leaves[i] = left
        leaves.append(right)
    return leaves


def subset(d, idx):
    """Subset the splat dict by index array (scalars like sh_degree pass through)."""
    out = {}
    for k, v in d.items():
        out[k] = v[idx] if isinstance(v, np.ndarray) and v.ndim >= 1 and v.shape[0] == d["positions"].shape[0] else v
    return out


def chunk_aabb(sub):
    """Robust world AABB: percentile-trimmed position bounds (a few giant outlier splats
    otherwise inflate the box to uselessness — the known outlier-bounds trap) expanded by a
    percentile scale margin. The chunk still RENDERS all its splats; this box is only the
    metadata used for frustum-cull + distance/screen-error LOD selection."""
    p = sub["positions"].astype(np.float64)
    lo = np.percentile(p, 0.5, axis=0)
    hi = np.percentile(p, 99.5, axis=0)
    margin = float(np.percentile(sub["scales_lin"], 95.0)) * 2.0  # robust ~2 sigma, outlier-proof
    return (lo - margin).tolist(), (hi + margin).tolist()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input .spz")
    ap.add_argument("-o", "--out", default="out/spike", help="output directory")
    ap.add_argument("--chunks", type=int, default=4, help="approx number of spatial chunks")
    ap.add_argument("--levels", type=int, default=2, help="LOD levels per chunk (LOD0 fine .. LODk coarse)")
    ap.add_argument("--voxel", type=float, default=0.05, help="voxel size for LOD0 (each level multiplies by --lod-mult)")
    ap.add_argument("--lod-mult", type=float, default=2.5, help="voxel growth per LOD level")
    ap.add_argument("--op-boost", type=float, default=1.3, help="opacity boost so coarse levels read solid")
    ap.add_argument("--prune-opacity", type=float, default=0.0, help="prune splats with opacity below this before merging (kills floaters/streaks)")
    ap.add_argument("--prune-min-scale", type=float, default=0.0, help="prune splats whose largest axis scale is below this")
    ap.add_argument("--no-env", dest="env", action="store_false", help="skip the always-resident coarse whole-scene env asset")
    ap.set_defaults(env=True)
    args = ap.parse_args()

    t0 = time.time()
    d = read_spz(args.input)
    n0 = d["positions"].shape[0]
    scene = os.path.splitext(os.path.basename(args.input))[0].replace(" ", "_")
    os.makedirs(args.out, exist_ok=True)
    print(f"read {n0:,} splats (sh_degree={d['sh_degree']}) in {time.time()-t0:.1f}s -> scene '{scene}'")

    leaves = kd_split(d["positions"].astype(np.float64), args.chunks)
    print(f"KD-split into {len(leaves)} chunks: sizes {[l.shape[0] for l in leaves]}")

    manifest = {"version": 1, "scene": scene, "chunkCount": len(leaves),
                "lodLevels": args.levels, "voxel0": args.voxel, "lodMult": args.lod_mult,
                "chunks": []}
    total_by_lod = [0] * args.levels

    for cid, idx in enumerate(leaves):
        sub = subset(d, idx)
        bmin, bmax = chunk_aabb(sub)
        centre = [(a + b) * 0.5 for a, b in zip(bmin, bmax)]
        entry = {"id": cid, "boundMin": bmin, "boundMax": bmax, "centre": centre, "lods": []}
        for lvl in range(args.levels):
            vox = args.voxel * (args.lod_mult ** lvl)
            m = voxel_merge(sub, vox, args.prune_opacity, args.prune_min_scale, op_boost=args.op_boost)
            n1 = m["positions"].shape[0]
            total_by_lod[lvl] += n1
            fname = f"{scene}_c{cid}_lod{lvl}.ply"
            write_ply(os.path.join(args.out, fname), m["positions"], m["scales_lin"],
                      m["quats"], m["opacity"], m["dc"], m["sh"])
            entry["lods"].append({"level": lvl, "file": fname, "voxel": round(vox, 5), "splatCount": int(n1)})
            print(f"  c{cid} lod{lvl}: voxel={vox:.4f}  {idx.shape[0]:,} -> {n1:,} splats -> {fname}")
        manifest["chunks"].append(entry)

    manifest["totalSplatsByLod"] = total_by_lod

    # Always-resident coarse env/background (splat-transform's --lod -1): the whole scene at
    # one step coarser than the coarsest chunk level, never culled/evicted so the far field is
    # never empty during streaming (Phase 2) and there is a stable floor image (Phase 1).
    if args.env:
        vox_env = args.voxel * (args.lod_mult ** args.levels)
        me = voxel_merge(d, vox_env, op_boost=max(args.op_boost, 1.4))
        ne = me["positions"].shape[0]
        envname = f"{scene}_env.ply"
        write_ply(os.path.join(args.out, envname), me["positions"], me["scales_lin"],
                  me["quats"], me["opacity"], me["dc"], me["sh"])
        manifest["envFile"] = envname
        manifest["envSplatCount"] = int(ne)
        print(f"  env: voxel={vox_env:.4f}  {n0:,} -> {ne:,} splats -> {envname}")

    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    print(f"\nmanifest.json written. total by LOD: {total_by_lod} (full={n0:,})")
    print(f"done in {time.time()-t0:.1f}s -> {args.out}")


if __name__ == "__main__":
    main()
