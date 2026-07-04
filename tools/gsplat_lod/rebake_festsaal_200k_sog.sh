#!/usr/bin/env bash
# Legacy sog_baker path — prefer rebake_via_splat_transform.sh for SuperSplat parity.
# Re-bake Festsaal 200k SOG with FULL LOD0 fidelity (all ~195K source splats).
# Aspect/opacity prune applies only to merged LOD1+ (coarse streaming levels).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SPZ="$ROOT/projects/GaussianExample-URP/Assets/Festsaal 200k bereinigt.spz"
OUT="$ROOT/projects/GaussianExample-URP/Assets/StreamingAssets/gsplat_lod/festsaal_200k_sog"
STAGE="$ROOT/tools/gsplat_lod/out/festsaal_200k_sog"
cd "$ROOT/tools/gsplat_lod"

if [[ -f .venv/bin/activate ]]; then
  source .venv/bin/activate
else
  python3 -m venv .venv
  source .venv/bin/activate
  pip install Pillow numpy --quiet
fi

python3 sog_baker.py "$SPZ" -o "$STAGE" \
  --raw-lod0 \
  --levels 5 \
  --lod-chunk-count 128 \
  --lod-chunk-extent 6.0 \
  --voxel 0.05 \
  --lod-mult 2.0 \
  --prune-opacity 0.04 \
  --prune-max-scale 0.3 \
  --prune-aspect-ratio 30

echo ""
echo "Copying to StreamingAssets..."
rm -rf "$OUT"
mkdir -p "$(dirname "$OUT")"
cp -R "$STAGE" "$OUT"
echo "Done. Verify lod-meta.json: counts[0] should match sourceSplatCount (~195K)."
