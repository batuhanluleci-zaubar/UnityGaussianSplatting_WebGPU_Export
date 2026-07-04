# gsplat_lod bake presets

## SuperSplat parity (recommended) — `rebake_via_splat_transform.sh`

Uses [@playcanvas/splat-transform](https://github.com/playcanvas/splat-transform): global decimate (%50 → %25 → …) then spatial chunk + Streamed SOG.

```bash
./rebake_via_splat_transform.sh [source.spz] [StreamingAssets/out] [staging_dir]
```

| Profile | `-C` (chunk K) | `-X` extent (m) | Notes |
|---------|----------------|-----------------|-------|
| Desktop / UHQ | 512 | 16 | PlayCanvas default |
| Festsaal 200k | 128 | 6 | Current SyntheticSogScene |
| Mobile / XR | 128 | 6 | Smaller stream units |

Env vars: `LEVELS=5`, `CHUNK_K=128`, `CHUNK_EXT=6`, `PRUNE_ASPECT=30` (sog_baker parity).

Before decimation, `prune_spz_aspect.py` drops splats with aspect ratio above `PRUNE_ASPECT`.
After copy, `tighten_sog_tree_bounds.py` clips splat-transform mega-node bounds in `lod-meta.json`.

## Legacy Python path — `rebake_festsaal_200k_sog.sh`

Per-leaf voxel merge via `sog_baker.py` (chunk-first pyramid). LOD0 = raw source with `--raw-lod0`.

Use when Node/splat-transform is unavailable; LOD1+ distribution differs from SuperSplat.
