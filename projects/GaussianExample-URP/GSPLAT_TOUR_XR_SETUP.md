# Cinematic Spline Tour + Android XR — Setup & Notes

This project now has two features layered on the gaussian-splat scene (`GSTestScene`):

1. A **Cinemachine + Splines cinematic tour** (auto fly-through of the splat).
2. **Android XR (OpenXR)** scaffolding so the project can build & run on AR glasses
   (e.g. XREAL Aura / Android XR).

One scene serves both: at startup `XrSceneBootstrap` picks **Desktop/WebGL tour** or
**XR** based on whether an XR device is active.

---

## 1. Cinematic Spline Tour (desktop / WebGL)

**Scene objects**
- `Cinematic Spline Path` — a **closed** `SplineContainer` orbit around the splat
  (radius ≈ the camera's framing distance, 8 knots, gentle height bob).
- `Tour LookAt` — empty at the splat centre; the camera frames this.
- `CM Tour Camera` — `CinemachineCamera` + `CinemachineSplineDolly` (Body, position only) +
  `CinemachineRotationComposer` (Aim → frames `Tour LookAt`) + `SplineCinematicDirector`.
- `Main Camera` — carries the `CinemachineBrain` that blends to the tour camera.

**Driver:** `Assets/Scripts/Cinematic/SplineCinematicDirector.cs`
- Advances `CinemachineSplineDolly.CameraPosition` in normalized [0..1].
- Inspector: `Duration` (s per pass), `Loop` (Once / **Loop** / PingPong), `Easing` curve,
  `Start` position, `Play On Start`.
- Public API: `Play() / Pause() / TogglePlayPause() / Restart() / SetNormalizedPosition(t)`.
- Verified in Play mode: the camera dollies the closed orbit and stays aimed at the splat
  (aim error ≈ 0°).

**Tuning the path:** select `Cinematic Spline Path` and move/add knots in the Scene view
(Splines tool). For a seamless continuous loop keep the spline **Closed** and the easing
**linear**; for an open path use `PingPong` + an ease-in/out curve.

---

## 2. Android XR (OpenXR) scaffolding

**Packages added:** OpenXR 1.17, AR Foundation 6.4, Android XR (OpenXR) 1.3.1,
XR Management 4.5, XR Core-Utils 2.6 (XR Origin), Input System 1.19, XR Hands 1.7,
Composition Layers 2.4, plus Cinemachine 3.1.7 + Splines.

**XR Plug-in Management (Android)** — configured in script:
- OpenXR loader assigned for Android.
- Render mode = **Multi-pass** (deliberate — see the stereo note below).
- Enabled OpenXR features: Android XR Support, OpenXR Lifecycle, AR Session / Camera /
  Occlusion / Plane / Anchor, Hand Tracking, plus Hand-interaction + KHR-simple interaction profiles.

**Player settings (Android):** Vulkan graphics API, IL2CPP, ARM64, min SDK 29.

**Scene rig**
- `AR Session` (`ARSession` + `ARInputManager`).
- `XR Origin` → `Camera Offset` → `XR Camera`
  (`Camera` with a black/transparent clear for **optical see-through** glasses,
  legacy `TrackedPoseDriver` = center-eye pose).
- `XR Tour Rig Driver` (on `XR Origin`, **disabled by default**): an optional on-rails AR
  fly-through that moves the whole rig along the same spline while head tracking still owns
  the look direction. Enable the component for a guided AR tour.
- `XR Bootstrap` (`XrSceneBootstrap`): on `Awake`, if an XR device is active it enables the
  XR Origin + AR Session and disables the desktop tour camera; otherwise it does the reverse.
  An `Editor Override` (Auto / ForceXR / ForceDesktop) helps test in the Editor.

`XR Origin` and `AR Session` are inactive in the saved scene (desktop is the editor default);
the bootstrap activates them on-device.

### Building for Android XR
1. Build target is already switched to **Android**.
2. Connect the glasses (XREAL Aura / Android XR), then **File → Build & Run** (or Build).
3. If a "new Input System backend" restart prompt appears, accept it (Input System 1.19 is installed).

### Device note (passthrough)
The `XR Camera` uses a transparent clear, correct for **optical see-through** (additive)
glasses like XREAL Aura — the black renders as transparent and the splat overlays the real
world. For a **video passthrough** device instead, add `ARCameraManager` + `ARCameraBackground`
to `XR Camera`.

---

## ⚠️ Known limitation: gaussian-splat stereo rendering

The bundled splat renderer (`package/Runtime/GaussianSplatRenderer.cs`) is **single-view by
design** — it sets the camera position / screen params **once per frame**
(`// Screen params and camera position are the same for all instances`) and has no
single-pass-instanced stereo path (no `unity_StereoEyeIndex`, no per-eye sort/projection).

Consequence on an HMD:
The original renderer cached a single (mono) model-view matrix and reused it for both eyes.

### ✅ Stereo correctness — implemented (multi-pass)

The render path already supports multi-pass stereo structurally (the splat RT inherits
`cameraTargetDescriptor`, the composite uses XR-aware `Blitter.BlitCameraTexture`). The only
mono assumption was in the shader, now fixed:

- `GaussianSplats.shader` projects the 2D covariance and the corrected screen centre with
  **`UNITY_MATRIX_MV`** instead of the cached mono `_MatrixMV` — identical in mono, **per-eye
  under multi-pass**, so splat shape + parallax are now correct for each eye.
- View-dependent SH uses the built-in per-eye `_WorldSpaceCameraPos`.
- The **temporal filter is disabled under XR** (`GaussianSplatURPFeature` + `GaussianSplatRenderer`):
  its single history buffer / per-eye motion vectors would ghost across eyes.

Verified in the editor: mono rendering is byte-for-byte unchanged (the substitution is an
identity in mono); stereo correctness is by-construction and needs final on-device confirmation.

### ✅ Performance tuning — implemented (`XrPerformanceTuner`)

`Assets/Scripts/Cinematic/XrPerformanceTuner.cs` (on **XR Bootstrap**, applied only under XR):

- **Per-eye resolution scale** (`XRSettings.eyeTextureResolutionScale`, default **0.7**) — the
  single biggest fill-rate lever; the splat RT inherits it. This is what actually buys FPS.
- **Foveated rendering** — OpenXR `FoveatedRenderingFeature` is enabled and the tuner sets the
  display foveation level (Adreno fixed foveation).
- **Sort/cull throttle** — ensures octree culling on and re-culls/re-sorts every 2 frames
  (`m_OctreeCullingUpdateInterval`); option to switch transparency to **Stochastic** (no depth
  sort) for more speed at the cost of some noise.

All values are inspector-tunable; drive the finals from on-device profiling.

### Remaining (optional, device-gated)
- **Single-pass-instanced** rendering for max perf — non-trivial here because the splat draw uses
  `SV_InstanceID` for the splat index (collides with eye instancing). Multi-pass + 0.7 res +
  foveation is expected to be sufficient for the ~195K asset.
- **Foveation on the splat RT pass specifically** — fixed foveation applies to the eye buffers;
  if profiling shows the custom splat pass isn't benefiting, it must opt into VRS explicitly.
- **On-device profiling** (Snapdragon Profiler / Unity GPU Profiler) to set the final
  resolution scale, cull interval, and splat budget.

---

## Files added
- `Assets/Scripts/Cinematic/SplineCinematicDirector.cs` — desktop tour driver.
- `Assets/Scripts/Cinematic/XrTourRigDriver.cs` — optional on-rails AR tour (rig follows spline).
- `Assets/Scripts/Cinematic/XrSceneBootstrap.cs` — desktop-vs-XR mode switch at startup.
- `Assets/Scripts/Cinematic/XrPerformanceTuner.cs` — XR resolution/foveation/cull tuning.
- `Assets/XR/XRGeneralSettingsPerBuildTarget.asset` — XR Plug-in Management (OpenXR for Android).

## Renderer changes (in `package/`)
- `Shaders/Resources/GaussianSplats.shader` — per-eye `UNITY_MATRIX_MV` + `_WorldSpaceCameraPos`.
- `Runtime/GaussianSplatURPFeature.cs` + `Runtime/GaussianSplatRenderer.cs` — disable temporal filter under XR.
