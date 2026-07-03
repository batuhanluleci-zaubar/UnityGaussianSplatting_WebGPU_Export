# P0b Re-bake Results — area-weighted moment-matching merge

Baker: `tools/gsplat_lod/merge.py` — commit `945b1c5` swapped opacity-only weight +
coverage-union alpha + `op_boost=1.3` inflation for area-weighted moment matching
(Knud-Thomsen p=1.6075 ellipsoid area) with mass-conserving
`alpha_m = min(1, sum(o_i * area_i) / area_merged)`.

Source: `projects/GaussianExample-URP/Assets/Festsaal 10m bereinigt.spz` (9.7M splats).
Bake CLI (full LOD0 — no LOD0 prune; use --lod0-prune-* only if you want floater cleanup on LOD0):
```
python3 chunk_lod.py "…/Festsaal 10m bereinigt.spz" -o out/uhq \
  --chunks 64 --levels 5 --voxel 0.05 --lod-mult 2.0 --op-boost 1.3 \
  --schema-version 2 \
  --prune-opacity 0.05 --prune-max-scale 0.3 --prune-aspect-ratio 30 \
  --raw-lod0 --no-env
```
Or: `./rebake_uhq_full_lod0.sh`

**Previous bake** applied `--prune-*` to LOD0 as well, removing **3.43M splats** (mostly
`--prune-aspect-ratio 30`). Chunking/KD-split does not drop splats — only optional prune does.
Bake time: 27 s. Output: 320 .ply files (64 chunks × 5 LOD). Splat counts per LOD unchanged:
`6.27M / 1.26M / 401K / 114K / 32K`.

## Numerical validation (workflow verified, chunks c0/c30/c54 aggregate)

| LOD | splats | op_mean before → after | op_p90 before → after | scale_max_mean before → after |
|-----|--------|-------------------------|------------------------|--------------------------------|
| LOD0 | 275 185 | bit-exact preserved (raw) | bit-exact | bit-exact |
| LOD1 | 53 151 | **0.6248 → 0.4372 (−30 %)** | **0.9990 → 0.9648** (pin released) | 0.03614 → 0.03867 (+7 %) |
| LOD2 | 16 860 | **0.7107 → 0.4982 (−29.9 %)** | 0.9990 → 0.9990 (dense-core sat.) | 0.05282 → 0.05817 (+10.1 %) |
| LOD3 | 4 841 | **0.7415 → 0.5537 (−25.3 %)** | 0.9990 → 0.9990 | 0.08032 → 0.08852 (+10.2 %) |
| LOD4 | 1 345 | **0.7583 → 0.5894 (−22.3 %)** | 0.9990 → 0.9990 | 0.12565 → 0.13396 (+6.6 %) |

- **Predicted opacity-lowering effect: CONFIRMED** at every merged LOD.
- **p90 pin release: CONFIRMED at LOD1** aggregate (0.9648) and even harder on the
  peripheral chunk c54 individually (0.8965). LOD2-4 aggregate p90 legitimately stays at
  the 0.999 saturation cap where voxels genuinely have `sum(o_i * A_i) > A_merged` — not
  a bug, that's how mass-conservation is supposed to look on dense-core chunks.
- **LOD0 bit-exact preservation: CONFIRMED** on all 3 sample chunks. `--raw-lod0` skips the
  merge module entirely and only runs prune.
- **Splat counts: UNCHANGED per LOD.** KD-split + voxelization pipeline untouched.
- **Aspect ratio drop at LOD4:** 5.48 → 4.85 (−11.4 %). Moment matching produces more
  isotropic merged ellipsoids by construction — expected and desirable (fewer needle
  artefacts at coarse levels), still well below `--prune-aspect-ratio 30`.

## Visual A/B in editor

`Assets/Screenshots/p0b_after_bake.png` — camera at (-1.3, 5, -15), all 20 visible chunks
at LOD0 (raw): 20 chunks / 1.96 M splats / 90 FPS. **LOD0 pixel-identical to pre-P0b
baseline** — as predicted.

`Assets/Screenshots/p0b_coarse_after.png` — same camera with `lodBaseDistance=1.5,
lodMultiplier=1.5` forcing near chunks into coarse levels (byLvl L0=2 L2=4 L3=9 L4=5,
236 K splats, 208 FPS). Coarse chunks visibly render as sparser semi-transparent grids
rather than the old washed-out opaque blob. Left half shows fine-detail LOD0/2 columns
next to right-half LOD3/4 grids — no "everything is 0.999 alpha" saturation.

## Open item (workflow flagged, not a blocker)

`op_min` at merged LODs dropped 0.0663 → 0.0200 at every level because the old
`op_boost=1.3` was inflating the post-prune tail and the new formula leaves it untouched.
Post-merge children can land below the 0.05 pre-merge prune threshold. Two possibilities:
either (a) intended — mass-conserving alpha_m naturally produces near-transparent merged
splats when constituents are near-transparent, or (b) prune only runs pre-merge and would
benefit from a post-merge second pass. Not blocking the bake but worth clarifying next
time the merge module is touched.

## Assets are gitignored

The 320 baked `.asset` files live under `projects/GaussianExample-URP/Assets/GaussianAssets/`
which is in `.gitignore` (they're regenerable from the .ply + baker). This doc captures the
numerical outcome so future contributors can reproduce the same P0b state from the .spz.
