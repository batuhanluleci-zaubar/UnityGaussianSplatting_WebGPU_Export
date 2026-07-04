# A/B: Monolithic vs SOG Stream (Festsaal 200k)

Same source SPZ: `Assets/Festsaal 200k bereinigt.spz` (195,416 splats).

## Bounds comparison

| Source | Centre (m) | Span (m) | Notes |
|--------|------------|----------|-------|
| SPZ AABB | (-1.7, 2.5, 1.9) | ~57 × 15 × 36 | Python decode |
| Monolithic `.asset` | (-2.3, 3.4, 0.6) | ~50 × 13 × 22 | Unity import bounds |
| SOG root (pre-tighten) | (22.6, -4.9, 2.3) | ~348 | splat-transform mega-node outlier |
| SOG root (post-tighten, pruned rebake) | (-1.7, -1.6, -3.4) | ~51 | LOD0=130,309 after aspect≤30 prune |
| Runtime tight leaves | ~(-2, 2.5, 2) | ~60 | `SogLeafMath.TryComputeTightSceneBounds` |

## Scale / aspect (LOD0)

| Metric | Raw SPZ | SOG chunk decode | After runtime clamp (6:1) |
|--------|---------|------------------|---------------------------|
| aspect p50 | 13.8 | 13 | 6.0 cap |
| aspect p95 | 2631 | 2471 | 6.0 |
| % aspect > 30 | 33% | 33% | 0% |
| Pruned SPZ (aspect≤30) | — | 130,309 splats | — |

Decode matches SPZ; streaks are data/bake, not decode error.

## Unity A/B procedure

1. **SOG path:** Open `SyntheticSogScene.unity`, Play. HUD should show `poolLod0≈159`, `nearLod=0/0/0/0`.
2. **Monolithic path:** Duplicate scene or disable `GaussianLodStreamAsync`, add `GaussianSplatRenderer` with `Assets/GaussianAssets/Festsaal 200k bereinigt.asset`.
3. **Same camera:** Set `autoFrameCamera=false` on streamer; position camera at centre `(-2, 2.5, 2)` + offset `(0, 4, -8)` looking at centre.
4. **Compare:** Wall streaks / needle artefacts should match between paths → confirms bake/data issue, not streaming decode.
5. **After rebake with prune:** Re-run step 1; streak density should drop vs monolithic (monolithic still has full 33% needles unless import prunes).

## Expected outcome

- **Similar visuals** SOG vs monolithic → streaming OK; fix is aspect prune + bounds + render scale.
- **SOG worse only** → investigate pool merge / chunk seams (unlikely given HUD L0 full).

## Related fixes (this rollout)

- `FrameCameraFromSogBounds` uses tight leaf union (skips mega-nodes).
- `rebake_via_splat_transform.sh`: `PRUNE_ASPECT=30` + `tighten_sog_tree_bounds.py`.
- `Medium_PipelineAsset`: `m_RenderScale` → 1.0.
