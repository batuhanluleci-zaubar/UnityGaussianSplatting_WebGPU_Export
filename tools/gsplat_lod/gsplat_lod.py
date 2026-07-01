#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Offline decimated-LOD tool for 3D gaussian splats.

Reads a Niantic .SPZ capture, prunes negligible splats, and voxel-MERGES the rest
into fewer, larger gaussians (moment matching — same volume, not a subsample), then
writes a standard 3DGS .ply that the Unity org.nesnausk GaussianSplatAssetCreator imports.

Larger --voxel => fewer splats. Use --levels to emit a series (LOD0..LODn).

  python3 gsplat_lod.py "Festsaal 10m bereinigt.spz" -o festsaal_lod.ply --voxel 0.04
  python3 gsplat_lod.py in.spz -o festsaal.ply --voxel 0.03 --prune-opacity 0.03 --levels 3
"""
import argparse
import os
import time

from spz_reader import read_spz
from merge import voxel_merge
from ply_writer import write_ply


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input .spz")
    ap.add_argument("-o", "--out", required=True, help="output .ply (base name if --levels > 1)")
    ap.add_argument("--voxel", type=float, default=0.04, help="voxel size for LOD0 (scene units). Bigger = fewer splats.")
    ap.add_argument("--levels", type=int, default=1, help="number of LOD levels (each doubles the voxel size)")
    ap.add_argument("--prune-opacity", type=float, default=0.0, help="drop splats with opacity below this (0..1)")
    ap.add_argument("--prune-min-scale", type=float, default=0.0, help="drop splats whose largest axis scale is below this")
    ap.add_argument("--op-boost", type=float, default=1.0, help="opacity boost before coverage union (1.5 makes coarse LODs read solid)")
    ap.add_argument("--op-cap", type=float, default=0.999, help="cap merged opacity (0.92 avoids hard opaque edges on coarse LODs)")
    args = ap.parse_args()

    t0 = time.time()
    d = read_spz(args.input)
    n0 = d["positions"].shape[0]
    print(f"read {n0:,} splats (sh_degree={d['sh_degree']}) in {time.time()-t0:.1f}s")

    base, ext = os.path.splitext(args.out)
    if ext.lower() != ".ply":
        base, ext = args.out, ".ply"

    for lvl in range(max(1, args.levels)):
        vox = args.voxel * (2.0 ** lvl)
        t1 = time.time()
        m = voxel_merge(d, vox, args.prune_opacity, args.prune_min_scale,
                        op_boost=args.op_boost, op_cap=args.op_cap)
        n1 = m["positions"].shape[0]
        out = f"{base}.ply" if args.levels == 1 else f"{base}_lod{lvl}.ply"
        write_ply(out, m["positions"], m["scales_lin"], m["quats"], m["opacity"], m["dc"], m["sh"])
        sz = os.path.getsize(out) / 1e6
        print(f"  LOD{lvl}: voxel={vox:.4f}  {n0:,} -> {n1:,} splats ({n1/n0:.1%})  {sz:.0f} MB  {time.time()-t1:.1f}s  -> {out}")


if __name__ == "__main__":
    main()
