#!/usr/bin/env bash
# Re-bake UHQ with FULL LOD0 fidelity (no LOD0 pruning).
# LOD1+ still use floater prune for cleaner coarse levels.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPZ="$ROOT/projects/GaussianExample-URP/Assets/Festsaal 10m bereinigt.spz"
OUT="$ROOT/tools/gsplat_lod/out/uhq"
cd "$ROOT/tools/gsplat_lod"

python3 chunk_lod.py "$SPZ" -o "$OUT" \
  --chunks 64 --levels 5 --voxel 0.05 --lod-mult 2.0 --op-boost 1.3 \
  --schema-version 2 \
  --prune-opacity 0.05 --prune-max-scale 0.3 --prune-aspect-ratio 30 \
  --raw-lod0 --no-env

echo ""
echo "Done. LOD0 should match source splat count (check manifest sourceSplatCount vs totalSplatsByLod[0])."
echo "Re-import .ply files into Unity (Gaussian Splat Creator) and rebuild chunk hierarchy."
