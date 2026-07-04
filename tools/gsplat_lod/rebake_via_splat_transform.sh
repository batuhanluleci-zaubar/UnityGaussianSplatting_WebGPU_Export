#!/usr/bin/env bash
# SuperSplat-parity bake via @playcanvas/splat-transform (global decimate → chunk → Streamed SOG).
# Requires Node.js 18+ and network for first npx fetch.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SPZ="${1:-$ROOT/projects/GaussianExample-URP/Assets/Festsaal 200k bereinigt.spz}"
OUT="${2:-$ROOT/projects/GaussianExample-URP/Assets/StreamingAssets/gsplat_lod/festsaal_200k_sog}"
STAGE="${3:-$ROOT/tools/gsplat_lod/out/festsaal_200k_sog_st}"
LEVELS="${LEVELS:-5}"
CHUNK_K="${CHUNK_K:-128}"
CHUNK_EXT="${CHUNK_EXT:-6}"
PRUNE_ASPECT="${PRUNE_ASPECT:-30}"

if [[ ! -f "$SPZ" ]]; then
  echo "Source not found: $SPZ" >&2
  exit 1
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

ST="npx --yes @playcanvas/splat-transform"

echo "=== splat-transform SuperSplat LOD pyramid ==="
echo "Source: $SPZ"
echo "Levels: $LEVELS  chunk: ${CHUNK_K}K / ${CHUNK_EXT}m  prune aspect: ${PRUNE_ASPECT}"

BAKE_SPZ="$WORKDIR/source_pruned.spz"
echo "Pruning needle splats (aspect>${PRUNE_ASPECT})..."
python3 "$SCRIPT_DIR/prune_spz_aspect.py" "$SPZ" "$BAKE_SPZ" --prune-aspect-ratio "$PRUNE_ASPECT"
SPZ="$BAKE_SPZ"

# Progressive decimate levels (SuperSplat default pattern for 5 LODs)
DECIMATES=(50 25 12 6)
LOD_INPUTS=("$SPZ")
for i in $(seq 0 $((LEVELS - 2))); do
  if (( i < ${#DECIMATES[@]} )); then
    pct="${DECIMATES[$i]}"
  else
    pct="${DECIMATES[$((${#DECIMATES[@]} - 1))]}"
  fi
  out="$WORKDIR/lod$((i + 1)).spz"
  echo "Decimate LOD$((i + 1)): ${pct}%"
  $ST "$SPZ" -F "${pct}%" "$out"
  LOD_INPUTS+=("$out")
done

echo "Writing Streamed SOG manifest..."
rm -rf "$STAGE"
mkdir -p "$STAGE"

CMD=($ST)
for ((lvl = 0; lvl < LEVELS; lvl++)); do
  CMD+=("${LOD_INPUTS[$lvl]}" "-l" "$lvl")
done
CMD+=("$STAGE/lod-meta.json" "--filter-nan" "-C" "$CHUNK_K" "-X" "$CHUNK_EXT")

"${CMD[@]}"

echo "Copying to StreamingAssets..."
rm -rf "$OUT"
mkdir -p "$(dirname "$OUT")"
cp -R "$STAGE" "$OUT"

echo "Tightening lod-meta tree bounds..."
python3 "$SCRIPT_DIR/tighten_sog_tree_bounds.py" "$OUT/lod-meta.json"

python3 - <<PY
import json, sys
meta = json.load(open("$OUT/lod-meta.json"))
c0 = meta.get("counts", [0])[0]
src = meta.get("count", c0)
print(f"LOD0={c0:,}  source/count={src:,}  levels={meta.get('lodLevels')}")
if c0 != src:
    print("WARNING: LOD0 != count field", file=sys.stderr)
PY

echo "Done: $OUT"
