# Gaussian-Splat Tour + AR — Improvement Plan & Roadmap

Status of this system: a Cinemachine + Splines cinematic tour and an Android XR (OpenXR)
viewer over a gaussian-splat scene, with per-eye stereo and XR performance tuning. This doc
is the prioritized roadmap. **Done** = implemented & in-repo; **Rec** = recommended next.

Priority: **P0** ship-blocking · **P1** high value · **P2** nice-to-have · **P3** long-term.
Effort: S (hours) · M (1–2 days) · L (week+).

---

## 0. Defects fixed in this pass
| Item | Status |
|---|---|
| Tour orbited the **outlier-inflated bounds centre** → camera flew into the walls | ✅ **Done** — `GsplatAutoFramer` (percentile-trimmed median of chunk centres) → robust centre `(-12,60,4)` + radius 372; orbit rebuilt at R≈633, far clip raised. Verified visually (hall now framed). |
| Splat is ~700 m across → buried inside it in AR | ✅ **Done** — `XrSplatPlacer` rescales to ~0.75 m and places it in front of the user, with `Recenter()`. |
| Mono splat in stereo | ✅ **Done** (previous pass) — per-eye `UNITY_MATRIX_MV`. |

---

## A. Tour / camera
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| A1 | Robust auto-framing of the orbit | P0 | M | ✅ Done (`GsplatAutoFramer`) |
| A2 | Editor **"Auto-Frame Tour"** menu item (one click reframes any splat) | P1 | S | Rec |
| A3 | Establishing wide shot → ease into the orbit on start | P2 | S | Rec |
| A4 | Waypoint / POI tour mode (stop at points of interest), not just orbit | P2 | M | Rec |
| A5 | Editor gizmos: draw the spline + framing sphere in Scene view | P2 | S | Rec |

## B. AR / XR
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| B1 | Room-scale rescale + place in front of user | P0 | M | ✅ Done (`XrSplatPlacer`) |
| B2 | **Input bindings** (recenter / play-pause / scale) via XR Interaction Toolkit + Input System backend | P1 | M | Rec — no input is wired yet |
| B3 | **World anchoring** (`ARAnchor`) so content stays put as the user walks | P1 | M | Rec — AR Anchor feature already enabled |
| B4 | Grab-to-move / pinch-to-scale the splat | P2 | M | Rec |
| B5 | Real-world **occlusion** of the splat (AR Occlusion feature is enabled — wire depth) | P2 | M | Rec |
| B6 | Graceful XR-init failure → fall back to desktop tour + on-screen message | P1 | S | Rec |

## C. Rendering quality
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| C1 | Per-eye stereo correctness | P0 | M | ✅ Done |
| C2 | **Verify SH degree** — confirm view-dependent SH isn't being dropped (washed-out look is usually SH loss, not framing) | P1 | S | Rec — quick check |
| C3 | Tone/exposure parity desktop↔device | P2 | S | Rec |
| C4 | Edge anti-aliasing for splats (supersample or desktop temporal filter) | P2 | M | Rec |

## D. Performance
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| D1 | Per-eye resolution scale (biggest lever) | P0 | S | ✅ Done (`XrPerformanceTuner`) |
| D2 | Foveated rendering enabled + level set | P1 | S | ✅ Done |
| D3 | Cull/sort throttle (every 2 frames) | P1 | S | ✅ Done |
| D4 | **On-device profiling** → set final res scale / cull interval / splat budget | P0 | M | Rec — device-gated, the real validation |
| D5 | Distance **LOD / splat budget** (octree supports it) for big scenes | P1 | M | Rec |
| D6 | A/B **Stochastic** transparency (no depth sort) on device | P2 | S | Rec (toggle already exposed) |
| D7 | Wire VRS/foveation into the **custom splat RT pass** specifically | P2 | M | Rec |
| D8 | Single-pass-instanced stereo (max perf) — splat draw reuses `SV_InstanceID`, so this is a rewrite | P3 | L | Rec |

## E. Robustness / developer experience
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| E1 | One `GsplatExperience` manager that composes tour + AR + perf (today it's several components) | P2 | M | Rec |
| E2 | Null-guards / graceful degradation if asset or URP feature is missing | P1 | S | Rec |
| E3 | Editor **setup wizard / validator** ("scene is XR-ready?") | P2 | M | Rec |
| E4 | Ensure the URP **renderer feature is registered** on the active renderer (not just the package) | P1 | S | Rec — verify before device build |

## F. Build / pipeline / asset
| # | Item | Pri | Effort | Status |
|---|---|---|---|---|
| F1 | Separate **WebGL vs Android** build profiles (the project keeps switching target) | P1 | S | Rec |
| F2 | Re-bake the heavy asset with **Cluster-64k SH** (WebGL 2 GB / mobile memory) | P1 | S | Rec (from earlier session) |
| F3 | Decimated **asset LOD variants** for mobile | P2 | M | Rec |
| F4 | Automated build/CI | P3 | L | Rec |

---

## Recommended sequence (next sessions)
1. **On-device bring-up** (D4): build to the glasses, confirm stereo + the perf tuner, read GPU times. This drives everything else.
2. **Input + anchoring** (B2, B3, B6): make the AR viewer actually usable — recenter button, world-locked content, safe fallback.
3. **Quality pass** (C2): verify SH so it's not washed out; tune exposure.
4. **LOD/budget** (D5) + **build profiles** (F1, F2) once target FPS is known.
5. **Editor tooling** (A2, E3) to make this reusable for the next capture.
6. Long-term: single-pass-instanced (D8) only if profiling shows multi-pass is the bottleneck.

## What was applied this pass
- `Assets/Scripts/Cinematic/GsplatAutoFramer.cs` — robust content centre/radius from chunk data.
- `Assets/Scripts/Cinematic/XrSplatPlacer.cs` — AR rescale + place-in-front + recenter.
- Rebuilt the cinematic orbit around the robust centre (scene), raised camera far clip.
- (Previous pass) per-eye stereo + `XrPerformanceTuner` + foveation.

---

## PERFORMANCE: profiling + screen-LOD (9.7M "Festsaal 10m" scene)

**Measured on desktop editor (fixed camera), 9.7M-splat asset:**
| Config | Visible splats | FPS |
|---|---|---|
| Baseline (octree frustum cull only) | 2.48M | **15.2** (66 ms) |
| renderScale 0.6 (fillrate ×0.36) | 2.48M | 19.1 — **only ~10 ms is fillrate** |
| **Screen-LOD targetPx=5 + renderScale 0.7** | **831K** | **51.1 (19.6 ms) — 3.4×** |

**Diagnosis:** the scene is **instance/geometry-bound, not fillrate-bound**. Each splat is one
instanced quad (2 tris) via `DrawProcedural(instanceCount = visibleCount)`; the octree frustum-
culls to ~2.5M but then emits *every* splat in each visible node. So the dominant lever is
**cutting the visible splat count**.

**Implemented — screen-space LOD (D5, done):** `GaussianSplatOctree.SortVisibleSplatsByDepth`
now subsamples each visible node by its projected pixel size — near nodes keep all splats, far/
small nodes keep every N-th (up to `m_LodMaxStride`). Controlled by new `GaussianSplatSettings`
fields: `m_EnableScreenLod`, `m_LodTargetPixels` (higher = fewer splats), `m_LodMaxStride`.
`XrPerformanceTuner` enables it under XR. Verified: at 67% fewer splats the Gothic hall is still
fully intact (splats overlap heavily, so decimation barely shows).

**Tuning:** `m_LodTargetPixels` is the main dial — 2 ≈ subtle (−29%), 4 = balanced default,
5–6 = aggressive (−67%, still clean). Stack with resolution/foveation for XR.

**Not worth it here (verified traps):** Hi-Z/occlusion (no opaque depth in the transparent pass),
OIT (use Stochastic), tile rasterizer (Adreno already tiles), VQ/Norm compression (cuts bytes,
not splat count), faster radix sort (sort is async CPU, off the critical path), the sub-0.5px
shader discard (fillrate only — runs *after* the vertex/instance cost).

**Bigger offline wins (recommended next):** prune + re-bake the asset (SuperSplat / 3dgsconverter,
−20–40%), or a merged-LOD hierarchy (merge_lod.py / Hierarchical 3DGS, 2–10×).
