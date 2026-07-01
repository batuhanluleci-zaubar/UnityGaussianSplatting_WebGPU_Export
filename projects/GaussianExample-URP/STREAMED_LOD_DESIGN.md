# Streamed multi-LOD design — SuperSplat "Streamed SOG" for this Unity renderer

A concrete, phased architecture to bring SuperSplat's **Streamed SOG** behaviour
(instant coarse image → progressive per-chunk refinement under a device-tuned Gaussian
budget) to the `org.nesnausk` / WindyYam WebGPU gsplat renderer, on **desktop + Android XR**.

Grounded in: `playcanvas/splat-transform` (offline format) + `playcanvas/engine`
`src/scene/gsplat-unified/` (runtime scheduler, MIT) + `sparkjsdev/spark` (alt reference)
+ an audit of this repo's `package/Runtime`.

---

## TL;DR

- **We are already ~70% there.** The load-bearing runtime pieces exist and should be
  **reused, not rebuilt**: our moment-matching merge (`tools/gsplat_lod`), the 256-splat
  chunk container, the octree, and the `_SplatIndexMap` interval-draw path (which is
  *already* an arbitrary-resident-subset draw — streaming is "write different indices").
- **The one design decision to copy from SuperSplat:** LOD levels are **independent,
  complete decimated copies per chunk**, selected **one-per-chunk** by screen error and
  **swapped** (replace), never accumulated/nested. Coarse LOD is a standalone full image;
  refine = load the finer chunk, free the coarse one *once the finer is resident*.
- **Recommended target: Phase 1** (discrete multi-LOD + device budget, everything resident
  from local `StreamingAssets`). ~80% of the win at ~20% of the risk. **Gate Phase 2**
  (per-chunk streaming + eviction) on a *measured* memory-ceiling breach on the XR device.
- **Color/SH come along for free.** Color is indexed by `realIdx` through the Morton map
  in the shader (`GaussianSplats.shader:451`), so merged LOD assets keep colour + SH with
  **no per-LOD colour atlas** — a big simplification vs the first read.

---

## Implementation status (2026-07-01)

- ✅ **Spike** (de-risk) — `tools/gsplat_lod/chunk_lod.py` + `StreamedLodProbe.cs`. Proved
  independent per-chunk LOD + swap-by-screen-error, no seams, colour/SH free.
- ✅ **Phase 1** — `GaussianLodStreamer.cs` (`GsplatLod` namespace) + `chunk_lod.py --env`.
  Manifest → 16 chunks × 3 LOD + always-resident env floor; per-chunk screen-error bands
  (`lodBaseDistance·lodMultiplier^i × fovScale`); frustum cull (off-screen → coarsest, drawn
  ~free by each renderer's own octree); 64 √-distance-bucket budget balancer (degrade
  far-first) + `_budgetScale` damper **capped ≤ 1** (screen-error is the quality ceiling; budget
  only tightens); 10-frame cadence + camera dead-band + LOD dwell hysteresis. **Verified in
  editor:** near view LOD0=7 / LOD1=3 / LOD2=6, 5 chunks culled off-screen, resident pinned
  at the 1.2 M budget, 82–86 FPS; wide view all 16 assemble into a seamless hall.
  Draws N per-chunk `GaussianSplatRenderer`s (fine for ~16–32 chunks).
- ⏳ **Phase 2** (next, conditional) — single GPU buffer pool + slot allocator + async
  streaming + eviction, to scale past ~32 chunks and stream scenes whose coarse floor exceeds
  device RAM. Gate on a measured XR memory-ceiling breach.

## 1. What SuperSplat "Streamed SOG" actually is

`splat-transform` produces a `lod-meta.json` manifest + a spatial tree of chunks. For each
`(chunk, LOD-level)` there is an **independent** decimated splat set (you build the levels
yourself with repeated `--decimate 50% / 25% / 10%`, tag them `--lod 0/1/2`, and the combine
step only bins/chunks them — it does *not* auto-build the pyramid).

- **Decimation** (`decimate.ts`) = progressive pairwise **moment-matching merge** (k-NN
  graph, KL-style edge cost, greedy disjoint pairing, law-of-total-variance covariance →
  eigh → scale/quat, mass-conserving opacity). This is **exactly what `tools/gsplat_lod`
  already does** (voxel-grid instead of k-NN graph — both valid). ✅ keep our tool.
- **Chunking** (`write-lod.ts::build`): a median-split KD-tree over all levels' centroids;
  stop splitting a node into a chunk when `count ≤ 512K` **AND** `largestDim ≤ 16 m`
  (`--lod-chunk-count`, `--lod-chunk-extent` — the two stop thresholds; split if *either*
  is exceeded). Tune **hard down** for Android XR (~64–128 K / 4–8 m) so a chunk is a cheap
  stream/evict unit.
- **Manifest leaf** carries a **tight world-space AABB** (from ellipsoid corners,
  `exp(scale)·rot`, not centroid extents) + per-level `{file, offset, count}` spans → you
  can cull + pick LOD + plan memory **without opening any chunk file**.
- **Environment** (`--lod -1`): a coarse whole-scene asset, **always resident**, never
  culled/evicted → the far field never goes empty mid-stream.
- The format assumes **no HTTP** — chunks are just files on disk; `ensureFileResource(id)` /
  `getFileResource(id)` is transport-agnostic. We load from `StreamingAssets`.

> We **do not** adopt WebP + SOG `meta.json` — that adds a WebP decode + requantize step
> that is pure cost on Adreno. We bake our own **256-splat Norm11/Norm6** container (what
> the renderer already reads) in the same spatial-chunk × multi-LOD layout.

## 2. Runtime scheduler (from `playcanvas/engine` gsplat-unified)

Per **~10 frames** (or on camera move), 1:1 portable CPU logic:

1. **Per-chunk screen-space LOD** (`gsplat-octree-instance.js::evaluateNodeLods`):
   closest point on the chunk AABB → `distance`; multiply by
   `fovScale = min(tanHalfV, tanHalfH)/tan(22.5°)` (wider FOV → coarser sooner ≈ pixel
   error); `optimalLod =` first `i` where `distance < lodBaseDistance · lodMultiplier^i`
   (`lodMultiplier ≈ 2–3`, floor 1.2; bands precomputed once/frame). **Replaces** our
   per-node stride subsample — we now pick a whole pre-merged level (higher quality at the
   same instance count, because coarse = moment-merged, not skip-decimated). Optional stride
   kept as a final micro-throttle on the chosen level.
2. **Device-tuned Gaussian budget**, two-stage:
   - **Damper** (`world.js::_enforceBudget`): `ratio = optimalSplats / deviceBudget`; if
     outside dead-zone `[0.6, 1.4]`, nudge a persistent `_budgetScale` toward `1/√ratio`
     at blend **0.3** (clamp `0.01..100`). It multiplies `lodBaseDistance` and scales
     `lodMultiplier` by `pow(scale, −0.2)` — the bands *breathe* toward budget over a few
     frames. Dead-zone + 0.3 blend = the anti-oscillation trick.
   - **Bucket balancer** (`gsplat-budget-balancer.js`, ~150 lines, port near-verbatim): bin
     visible chunks into **64 √-distance buckets**; over budget → **degrade far-first**
     (63→0), under budget → **upgrade near-first** (0→63), ±1 level/chunk/pass, loop until
     stable. Net ≈ error-reduced-per-splat without a priority queue. This is a direct
     upgrade of our front-to-back splat-budget cap.
3. **Underfill** (`lodUnderfillLimit` 1–2 on XR): show an already-resident **coarser** level
   while the optimal one streams → the **instant complete image**.
4. **Prefetch** one level finer per chunk (`prefetchNextLod`) — coarse for the whole scene
   lands before any chunk jumps to fine.
5. **Never-drop-visible**: keep the on-screen level until the new level is fully resident;
   free the old slot only the moment the new one uploads (`pendingDecrements` /
   `pendingVisibleAdds`) → no popping/holes.
6. **Hysteresis trio** (all non-optional or you get XR head-motion flicker): 10-frame
   cadence (latched under back-pressure) · camera dead-band (`lodUpdateDistance ≈ 1`,
   FOV > 2%) · eviction **cooldown** (`cooldownTicks` ~100 desktop / ~30 XR).
7. **Back-pressure**: cap chunk loads + GPU `SetData` bytes/frame; run LOD-eval + diff off
   the render thread; dedup to once/frame.

## 3. Offline format (extend `tools/gsplat_lod`)

Emit **spatial chunks × K independent LOD levels + manifest**, in our own binary:

1. Build a KD-tree over full-detail centroids; stop at `count ≤ cap AND largestDim ≤ extent`
   (desktop 512 K / 16 m; XR 64–128 K / 4–8 m).
2. Per chunk, run the existing voxel merge at K ratios → **independent** copies:
   `LOD0` full, `LOD1 ≈ 1/3`, `LOD2 ≈ 1/9`, `LOD3 ≈ 1/27` (geometric).
3. Bake each `(chunk, LOD)` as a contiguous run of 256-splat chunks:
   - coarse (`LOD ≥ 2`) → one **monolithic pack per level** (cheap always-resident floor),
   - fine (`LOD0/1`) → **per-chunk files** `chunk_<id>_lod<L>.bin` (load/evict individually).
   - Morton-sort each chunk's splats before write (GPU locality).
4. Per-chunk **tight ellipsoid-corner AABB** (`calcBound`) → better cull/error bound than
   the current normalized `ChunkInfo` ranges.
5. One **always-resident env** asset (coarsest whole-scene merge).

Manifest `gaussianlod_manifest.json` (mirrors `LodMeta`):

```json
{ "version": 1, "scene": "...", "coordSpace": "...", "envFile": "env.bin",
  "totalSplatsPerLevel": [ ... ], "chunkExtent": 16, "chunkSplatCap": 524288,
  "chunks": [ { "id": 0, "boundMin":[x,y,z], "boundMax":[x,y,z],
    "lods": [ { "level":0, "file":"chunk_0_lod0.bin", "byteOffset":0,
                "splatCount":250000, "chunkCount256": 977 } ] } ] }
```

`byteOffset/splatCount` give exact memory spans → budget + slot-allocate **without opening
a file**. ⚠️ Bake bounds + data in the **same coordinate frame you cull in** (splat-transform's
PLY-space gotcha), or record one fixed load-time transform in `coordSpace`.

## 4. Integration — near-zero shader change

Today: octree writes a permuted `realIdx` list → `m_VisibleIndicesBuffer`
(`StructuredBuffer<uint>`) → `vert()` does `realIdx = _SplatIndexMap[instID]` →
`LoadSplatData(realIdx)`. That is already an arbitrary-resident-subset draw.

- **Phase 1:** all levels resident in the (larger) buffers at fixed offsets; the scheduler
  appends the **chosen level's** index range per chunk into `m_VisibleIndicesBuffer` — same
  buffer, same shader, different indices.
- **Phase 2:** the three buffers become a fixed **`GpuBufferPool`**; a `(chunk,LOD)` lives
  at a pool byte offset from the slot allocator. **Bake the pool offset into the indices**
  written to `m_VisibleIndicesBuffer` → still **zero shader change** (or one extra
  `chunkBaseOffset[]` indirection — negligible).
- Sync residency in `GaussianSplatURPFeature.RecordRenderGraph()` before the draw. For XR,
  evaluate LOD/budget **once** (dominant/combined eye), draw both eye passes over the same
  pool + index buffer.

## 5. Phased plan

| Phase | Deliverable | Effort / risk |
|---|---|---|
| **0 — Exists** | `tools/gsplat_lod` moment-merge (✅ matches `decimate.ts`) · 256-splat baker · octree cull + near/far stride LOD + front-to-back budget · `_SplatIndexMap` interval draw | done |
| **1 — Discrete multi-LOD + budget (RECOMMENDED PRIMARY)** | Extend baker → chunks × K levels + manifest + env. New: manifest loader, per-chunk screen-error scheduler, ported bucket balancer + `_budgetScale` damper, hysteresis trio. All levels resident (no eviction). Instant coarse image + progressive per-chunk refine + device budget, **no async-IO risk**. | **~2–3 wk**, med. Biggest work = baker chunk-tree + multi-level emit + scheduler; draw & budget adapt existing code. |
| **2 — Per-chunk streaming + eviction (CONDITIONAL)** | `GpuBufferPool` + `BlockAllocator` (port `core/block-allocator.js`), async file loader (per-frame IO/upload cap), `StreamingLodManager` (residency diff + cooldown evict + prefetch). For scenes whose coarse floor can't fit device RAM. | **~3–5 wk**, high. Allocator/defrag + never-drop-visible during in-flight loads are the sharp edges. |
| **3 — XR/mobile hardening + 4D-ready** | Per-platform profiles from `SystemInfo.graphicsMemorySize` + **measured** `targetFrameMs` (not the 13 ms placeholder). Adreno tuning. Shared resident buffer across eye passes. 4D = add a frame/segment id to the `(chunk,LOD)` key → slots into the same pool/allocator/scheduler. | **~2–4 wk** + device iteration, med. |

**Recommendation:** ship **Phase 1** first. Then, on the tightest Android XR target, measure
the memory of *(env floor + all chunks at their coarsest level)*. If that fits under the
ceiling with headroom → Phase 1 is sufficient; you draw budgeted subsets of a fully-resident
set, **stop there**. Only if the coarse floor *itself* breaches the ceiling do you need
Phase 2. Keep the Phase 1 asset layout streaming-ready (per-chunk fine files, monolithic
coarse packs, byte-offset manifest) so Phase 2 is an **additive loader/allocator layer**,
not a re-bake.

## 6. Reuse map (file:symbol)

**REUSE (don't rebuild):**
- `tools/gsplat_lod/merge.py` — matches `decimate.ts` math. Extend to N levels per chunk + manifest.
- `GaussianSplatAsset.cs:232` `ChunkInfo` — the allocation unit; extend with `lodLevel,
  residencyFlags, gpuOffset, splatCount` at struct **end** behind a format-version bump.
- `GaussianSplatOctree.cs:731` `UpdateVisibleIndicesBuffer` + `GaussianSplats.shader` `vert`
  — the resident-subset interval draw. **Zero shader change**; only index *contents* change.
- `GaussianSplatOctree.cs:927` `SortVisibleSplatsByDepth` + budget cap (`:1088`) — the
  bucket balancer slots in here.
- `GaussianSplatRenderer.cs:592` `CreateResourcesForAsset` — factor into a `GpuBufferPool`.
- `GaussianSplatURPFeature.cs:44` `RecordRenderGraph` — pre-render residency sync hook.

**BUILD (new):** `GaussianLodManifest` + loader · per-chunk screen-error scheduler (port
`evaluateNodeLods`) · budget balancer + `_budgetScale` damper · [P2] `GpuBufferPool` +
`BlockAllocator` (port `core/block-allocator.js`) · async file loader · `StreamingLodManager`
(port `computeAllocationDiff` + cooldown + prefetch) · env bake step · per-platform profile.

**PORT-FROM (MIT):** `playcanvas/engine` `src/scene/gsplat-unified/{gsplat-octree-instance,
gsplat-budget-balancer, gsplat-world, gsplat-world-state, gsplat-interval-data}.js` +
`src/core/block-allocator.js`. `splat-transform` `write-lod.ts` only as a **format** reference.

## 7. Pitfalls (learned from the refs + our own prior scars)

1. **LODs are independent copies, not nested residuals** — refine = *swap*, not accumulate.
   Getting this wrong doubles memory and breaks the merge math.
2. **Coordinate-space match** — bake AABBs + data in the frame you cull in, or one fixed
   load-time transform. Mismatch → scheduler picks wrong chunks (subtle quality bug).
3. **Tight ellipsoid-corner AABBs**, not centroid extents — loose bounds over-refine chunk
   edges and waste budget.
4. **Color/SH are free** — colour is `realIdx`-indexed via Morton (`GaussianSplats.shader:451`),
   so merged LOD assets keep colour + SH with **no per-LOD atlas**. (Corrects the first read.)
5. **Keep the two chunk copies in sync** (our prior SH-quality scar: StreamingAssets vs
   Generated) — make the baker the single source of truth; checksum packs in the manifest.
6. **Anti-oscillation is non-optional** — port *all four* (damper dead-zone+blend, 10-frame
   cadence, camera dead-band, never-drop-visible). Cherry-picking → XR popping.
7. **Adreno frame stalls** — whole-asset `SetData` stalls the frame; [P2] cap per-frame IO +
   upload bytes, run diff/eval off the render thread. Measure device `targetFrameMs`, don't
   assume 13 ms (our prior finding).
8. **Preserve SH degree** through the multi-level merge (our SH gates). Coarse levels may drop
   SH bands, but **deliberately per-level in the baker**, not accidentally.
9. **[P2] allocator fragmentation/defrag** — a grow/defrag forces a full re-render; free a
   coarse slot only after its finer replacement is resident. Port the pending-diff discipline.
10. **Don't over-build** — if the coarse floor + env fits device RAM (measure it), Phase 2's
    allocator/eviction is dead weight. Gate on a measured breach, not "can't hold 9.7 M"
    (you never hold 9.7 M at once once discrete LODs exist).
