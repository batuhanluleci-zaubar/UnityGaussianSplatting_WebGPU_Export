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
from merge import voxel_merge, _quat_to_R
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


def chunk_aabb(sub, k_sigma=3.0):
    """Robust world AABB: percentile-trimmed position bounds (a few giant outlier splats
    otherwise inflate the box to uselessness — the known outlier-bounds trap) expanded by a
    percentile scale margin. The chunk still RENDERS all its splats; this box is only the
    metadata used for frustum-cull + distance/screen-error LOD selection.

    Returns four values: (lo, hi, loE, hiE) where
      - lo/hi are the v1 percentile-trimmed position AABB (backward-compat),
      - loE/hiE are the v2 ellipsoid-extent bounds via sum(pos ± |R|·scale·kSigma):
          per-Gaussian oriented-box extents summed over positions -> a tighter but
          conservative bound that accounts for splat shape/orientation. The reduction
          uses the 99.9 percentile per axis to shrug off single-splat outliers (giant
          floaters otherwise blow the box up 2x)."""
    p = sub["positions"].astype(np.float64)
    lo = np.percentile(p, 0.5, axis=0)
    hi = np.percentile(p, 99.5, axis=0)
    margin = float(np.percentile(sub["scales_lin"], 95.0)) * 2.0  # robust ~2 sigma, outlier-proof
    # Ellipsoid-extent bound. R has shape [N,3,3]; ext[n,i] = sum_j |R[n,i,j]| * scale[n,j] * kSigma
    R = _quat_to_R(sub["quats"])
    scales = sub["scales_lin"].astype(np.float64)
    ext = np.einsum('nij,nj->ni', np.abs(R), scales) * float(k_sigma)
    # Use 99.9 percentile trim on the reducer so a single giant-scale outlier does not
    # inflate the ellipsoid box by 2x+ (see risk note in the design).
    loE = np.percentile(p - ext, 0.1, axis=0)
    hiE = np.percentile(p + ext, 99.9, axis=0)
    return (lo - margin).tolist(), (hi + margin).tolist(), loE.tolist(), hiE.tolist()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("input", help="input .spz")
    ap.add_argument("-o", "--out", default="out/spike", help="output directory")
    ap.add_argument("--chunks", type=int, default=4, help="approx number of spatial chunks")
    ap.add_argument("--levels", type=int, default=2, help="LOD levels per chunk (LOD0 fine .. LODk coarse)")
    ap.add_argument("--voxel", type=float, default=0.05, help="voxel size for LOD0 (each level multiplies by --lod-mult)")
    ap.add_argument("--lod-mult", type=float, default=2.5, help="voxel growth per LOD level")
    ap.add_argument("--op-boost", type=float, default=1.3, help="opacity boost so coarse levels read solid")
    ap.add_argument("--prune-opacity", type=float, default=0.0, help="prune splats with opacity below this before merging (kills semi-transparent floaters)")
    ap.add_argument("--prune-min-scale", type=float, default=0.0, help="prune splats whose largest axis scale is below this")
    ap.add_argument("--prune-max-scale", type=float, default=0.0,
                    help="prune GIANT-scale floater ellipsoids before merging (e.g. --prune-max-scale 0.3 kills anything > 30cm)")
    ap.add_argument("--prune-aspect-ratio", type=float, default=0.0,
                    help="prune NEEDLE-shaped anisotropic floaters (long thin splats -> visible streaks). "
                         "Try 30 for noisy captures. In Festsaal: median ratio 8.9, 99%%-ile 2180 -> pass 30 kills top few %%.")
    ap.add_argument("--no-env", dest="env", action="store_false", help="skip the always-resident coarse whole-scene env asset")
    ap.add_argument("--raw-lod0", action="store_true",
                    help="LOD0 = RAW splats of the chunk (no merge). Max fidelity when close, matches SuperSplat's "
                         "top-LOD design. LOD1+ still voxel-merged; --voxel becomes the LOD1 voxel size.")
    ap.add_argument("--schema-version", type=int, default=2, choices=(1, 2),
                    help="Manifest schema version. 2 (default) emits SuperSplat-parity filenames[] address "
                         "table + per-leaf fileIdx + boundMinEllipsoid/boundMaxEllipsoid + environment{} block. "
                         "1 emits the legacy v1 shape (no filenames[], no ellipsoid bounds, top-level envFile).")
    ap.add_argument("--k-sigma", type=float, default=3.0,
                    help="Ellipsoid bound k-sigma multiplier (v2 only). Larger = more conservative bounds.")
    ap.set_defaults(env=True)
    args = ap.parse_args()

    if args.lod_mult < 1.2:
        print(f"WARNING: --lod-mult {args.lod_mult} < 1.2 — SuperSplat parity plan recommends >= 1.2 (default 3.0). "
              f"Very small multipliers overlap LOD bands and defeat progressive refinement.")

    t0 = time.time()
    d = read_spz(args.input)
    n0 = d["positions"].shape[0]
    scene = os.path.splitext(os.path.basename(args.input))[0].replace(" ", "_")
    os.makedirs(args.out, exist_ok=True)
    print(f"read {n0:,} splats (sh_degree={d['sh_degree']}) in {time.time()-t0:.1f}s -> scene '{scene}'")

    leaves = kd_split(d["positions"].astype(np.float64), args.chunks)
    print(f"KD-split into {len(leaves)} chunks: sizes {[l.shape[0] for l in leaves]}")

    is_v2 = (args.schema_version >= 2)
    manifest = {"version": args.schema_version, "scene": scene, "chunkCount": len(leaves),
                "lodLevels": args.levels, "voxel0": args.voxel, "lodMult": args.lod_mult,
                "chunks": []}
    if is_v2:
        manifest["generator"] = "chunk_lod.py v2"
        manifest["filenames"] = []  # SuperSplat-parity canonical address table
        manifest["bounds"] = {"kind": "ellipsoidExtent", "kSigma": float(args.k_sigma)}
    filenames = manifest["filenames"] if is_v2 else None
    total_by_lod = [0] * args.levels

    for cid, idx in enumerate(leaves):
        sub = subset(d, idx)
        bmin, bmax, bminE, bmaxE = chunk_aabb(sub, k_sigma=args.k_sigma)
        centre = [(a + b) * 0.5 for a, b in zip(bmin, bmax)]
        entry = {"id": cid, "boundMin": bmin, "boundMax": bmax, "centre": centre, "lods": []}
        if is_v2:
            entry["boundMinEllipsoid"] = bminE
            entry["boundMaxEllipsoid"] = bmaxE
        for lvl in range(args.levels):
            fname = f"{scene}_c{cid}_lod{lvl}.ply"
            if lvl == 0 and args.raw_lod0:
                # LOD0 = raw chunk splats, optionally floater-pruned. No merge -> pixel-perfect near view.
                keep = np.ones(sub["positions"].shape[0], bool)
                if args.prune_opacity > 0: keep &= sub["opacity"] >= args.prune_opacity
                if args.prune_min_scale > 0: keep &= sub["scales_lin"].max(axis=1) >= args.prune_min_scale
                if args.prune_max_scale > 0: keep &= sub["scales_lin"].max(axis=1) <= args.prune_max_scale
                if args.prune_aspect_ratio > 0:
                    keep &= (sub["scales_lin"].max(axis=1) / np.maximum(sub["scales_lin"].min(axis=1), 1e-6)) <= args.prune_aspect_ratio
                raw = {k: (v[keep] if isinstance(v, np.ndarray) and v.ndim >= 1 and v.shape[0] == sub["positions"].shape[0] else v) for k, v in sub.items()}
                n1 = raw["positions"].shape[0]
                write_ply(os.path.join(args.out, fname), raw["positions"], raw["scales_lin"],
                          raw["quats"], raw["opacity"], raw["dc"], raw["sh"])
                lod_entry = {"level": 0, "file": fname, "voxel": 0.0, "splatCount": int(n1)}
                if is_v2:
                    lod_entry["fileIdx"] = len(filenames)
                    filenames.append(fname)
                entry["lods"].append(lod_entry)
                print(f"  c{cid} lod0 (raw):                 {idx.shape[0]:,} -> {n1:,} splats -> {fname}")
            else:
                vox = args.voxel * (args.lod_mult ** max(0, lvl - (1 if args.raw_lod0 else 0)))
                m = voxel_merge(sub, vox, args.prune_opacity, args.prune_min_scale,
                                args.prune_max_scale, args.prune_aspect_ratio, op_boost=args.op_boost)
                n1 = m["positions"].shape[0]
                write_ply(os.path.join(args.out, fname), m["positions"], m["scales_lin"],
                          m["quats"], m["opacity"], m["dc"], m["sh"])
                lod_entry = {"level": lvl, "file": fname, "voxel": round(vox, 5), "splatCount": int(n1)}
                if is_v2:
                    lod_entry["fileIdx"] = len(filenames)
                    filenames.append(fname)
                entry["lods"].append(lod_entry)
                print(f"  c{cid} lod{lvl}: voxel={vox:.4f}  {idx.shape[0]:,} -> {n1:,} splats -> {fname}")
            total_by_lod[lvl] += n1
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
        # Top-level v1-compat mirrors (pre-B4 readers ignore environment{}).
        manifest["envFile"] = envname
        manifest["envSplatCount"] = int(ne)
        if is_v2:
            # Ellipsoid extent of the env asset (which is the whole scene).
            env_bmin, env_bmax, env_bminE, env_bmaxE = chunk_aabb(me, k_sigma=args.k_sigma)
            env_fi = len(filenames)
            filenames.append(envname)
            manifest["environment"] = {
                "directory": "env",
                "files": [envname],
                "fileIdx": [env_fi],
                "splatCount": int(ne),
                "voxel": round(vox_env, 5),
                "residency": "alwaysOn",
                "boundMin": env_bmin,
                "boundMax": env_bmax,
                "boundMinEllipsoid": env_bminE,
                "boundMaxEllipsoid": env_bmaxE,
            }
        print(f"  env: voxel={vox_env:.4f}  {n0:,} -> {ne:,} splats -> {envname}")

    with open(os.path.join(args.out, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=2)
    print(f"\nmanifest.json written. total by LOD: {total_by_lod} (full={n0:,})")
    print(f"done in {time.time()-t0:.1f}s -> {args.out}")


if __name__ == "__main__":
    main()
