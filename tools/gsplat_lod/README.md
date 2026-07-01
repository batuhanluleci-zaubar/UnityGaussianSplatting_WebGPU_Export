# gsplat_lod — offline decimated-LOD tool for gaussian splats

Turns a huge gaussian-splat capture into a **much smaller, visually near-identical** asset by
**merging** clustered splats into fewer, larger ones (moment matching — same volume, *not* a
subsample). This is the highest quality-per-FPS win for big scenes and is what SuperSplat's
"Streamed SOG" does offline.

Measured on the 9.7M "Festsaal 10m" capture (desktop editor, heavy orbit view):

| Asset | Splats | FPS | Look |
|---|---|---|---|
| original | 9.7M | 15.2 | reference |
| **merged (voxel 0.05)** | **1.84M (19%)** | **73.0 (4.8×)** | near-identical |

## How it works
1. **Decode** the `.spz` (Niantic SPZ v2/v3) into physical attributes (`spz_reader.py`).
2. **Prune** negligible splats (low opacity / sub-pixel), optional.
3. **Voxel-merge** (`merge.py`): splats in one voxel fuse into one gaussian via **moment matching**
   - mean = opacity-weighted average of centres
   - covariance = Σ wᵢ(Σᵢ + (μᵢ−μ)(μᵢ−μ)ᵀ)/Σw  (parallel-axis — the spread term is what keeps
     the blob covering the footprint, so no holes), then `eigh` → scale + rotation (+ reflection fix)
   - opacity = coverage union `1−∏(1−αᵢ)` with an opacity boost so coarse levels read solid
   - colour/SH = opacity-weighted mean
4. **Write** a standard INRIA 3DGS `.ply` (`ply_writer.py`) that the Unity `GaussianSplatAssetCreator`
   imports (opacity→logit, scale→log, rot→wxyz, SH→channel-major).

## Usage
```bash
python3 gsplat_lod.py "Festsaal 10m bereinigt.spz" -o out/festsaal_lod.ply --voxel 0.05
# more aggressive + solid coarse look:
python3 gsplat_lod.py in.spz -o out/festsaal.ply --voxel 0.06 --op-boost 1.4 --prune-opacity 0.03
# a LOD ladder (each level ~doubles the voxel):
python3 gsplat_lod.py in.spz -o out/festsaal.ply --voxel 0.04 --levels 3
```
`--voxel` is the main dial (bigger = fewer splats). Needs `numpy` + `plyfile`.

## Bake into Unity
1. Run the tool → a `.ply` (keep it **outside** `Assets/` so Unity doesn't slow-import it).
2. **Window → Gaussian Splat Creator** → Input = that `.ply` → Quality Medium → **Create Asset**.
3. Assign the baked `GaussianSplatAsset` to your `GaussianSplatRenderer`.

(The runtime near-full/far-coarse LOD + splat budget in `GaussianSplatSettings` still stack on top
of the merged asset for even more FPS on mobile.)

## Files
- `spz_reader.py` — vectorised SPZ v2/v3 decoder (mirrors the C# `SPZFileReader`).
- `merge.py` — moment-matching voxel merge.
- `ply_writer.py` — INRIA 3DGS `.ply` writer matching the Unity importer's conventions.
- `gsplat_lod.py` — CLI.
