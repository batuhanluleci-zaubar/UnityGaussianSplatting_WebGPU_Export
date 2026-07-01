You are building a Gaussian Splatting viewer for Unity that runs on AR glasses and renders *animated* splats (4D), streamed with level-of-detail. Most of the code will be written by Claude Code. Your job is different and just as important: you own the environment, the device, and the judgment. Read this whole thing once, then keep it open.  
────────────────────────  
*Part 1 — Your role vs. Claude Code's role*  
*Part 1 — Your role vs. Claude Code's role*  
This matters more than any technical detail, so it comes first. The work splits cleanly, because Claude Code has no glasses, cannot see rendered output, and cannot feel a stutter.  
*What Claude Code does (the volume of implementation):*  
* Writes the subsystem code: the loader, the LOD scheduler, the decode shaders, the buffer pool, the sequence player.  
* Reads the upstream open-source repos and follows their constraints.  
* Writes the unit tests, golden-image tests, and the on-device debug overlay.  
* Implements the file-format reader against the shared spec.  
*What you do (the things an agent literally cannot):*  
* *Set up the environment.* Unity 6 LTS project, URP, OpenXR with the Android XR provider, Vulkan, IL2CPP, ARM64, package versions pinned. Signing, build pipeline, adb deploy to the Aura.  
* *Run it on the device.* Every milestone is validated on the actual glasses, not just the editor. The editor lies about performance and about stereo.  
* *Measure and profile.* GPU timings, thermal behavior, memory ceilings. You produce the numbers that drive every design decision.  
* *Judge it visually.* Popping, flicker, swimming, see-through legibility. These are human calls.  
* *Review Claude Code's pull requests*, especially the risky graphics areas (more on which ones below).  
* *Drive Claude Code well* (Part 6).  
Rule of thumb: if it needs the hardware, your eyes, or a yes/no on quality, it is yours. If it is typing code against a clear spec, hand it to Claude Code.  
────────────────────────  
*Part 2 — What the project is*  
ZAUBAR builds location-based AR experiences for cultural heritage and tourism. This initiative turns 3D scenes into Gaussian Splats and plays them back on AR glasses with cinematic quality. Picture a historical banquet hall you can walk through, with performers reconstructed as splats instead of traditional meshes.  
Three big pieces. You own the third.  
* *Capture (Unreal Engine).* Scenes are authored in UE5 and filmed with virtual cameras. Virtual cameras give us perfect camera poses, unlimited viewpoints, and linear-light images. For animation, the scene is rendered frame by frame with the rig held constant.  
* *Training + packaging (Python/CLI).* Captured images become Gaussian Splats via a trainer, then get packed into a streaming format. Built by others. You consume the output.  
* *The Unity viewer (you).* A runtime that loads the packed splat files and renders them on the glasses at full frame rate, streaming detail based on where the camera looks. Static scenes first (3D), then animated sequences (4D).  
*Target device:* XREAL Project Aura, Android XR. It is optical see-through (you see the real world through the glasses with rendered content added on top). Mobile-class GPU (Adreno). Graphics API on device is Vulkan. Unity 6 LTS, URP, OpenXR.  
*Two device facts to internalize now:*  
*Two device facts to internalize now:*  
* *See-through means additive.* Black pixels render as fully transparent, you just see reality. Dark splat content looks faint. This is physics, not a bug. Your blending must be premultiplied alpha and correct.  
* *Mobile GPU means bandwidth and overdraw decide everything.* Splats are millions of overlapping transparent quads. The two things that kill frame rate are sorting cost and alpha-blending fill cost. Every decision below exists to keep those two under control.  
────────────────────────  
*Part 3 — What a Gaussian Splat is (just enough)*  
*Part 3 — What a Gaussian Splat is (just enough)*  
A splat scene is a big list of 3D blobs. Each blob has a position, an orientation (quaternion), a 3-axis scale, a color, an opacity, and spherical-harmonic (SH) coefficients that make color shift with viewing angle (this is the realistic sheen). A scene is hundreds of thousands to several million of these.  
To render: project each splat to the screen as an ellipse, sort all of them back-to-front by depth, alpha-blend in that order. The sort is the expensive part and must be redone whenever the camera moves enough. No textures, no triangles, just sorted, blended blobs.  
*Animated (4D) splats* are the same idea across time. The clean version: the same splats persist frame to frame and only their position/rotation/color change. If a splat is the same splat in every frame, you can store frame 2 as just the differences from frame 1, and your LOD choices stay stable over time (no flicker). Our capture pipeline guarantees this same-splats-every-frame property. You rely on it heavily.  
────────────────────────  
*Part 4 — The open-source pieces you stand on*  
*Part 4 — The open-source pieces you stand on*  
You are not writing a renderer from scratch. You assemble and extend. Claude Code must read the actual repo READMEs before using any of these; the APIs change, and it should not trust its memory.  
* wuyize25/gsplat-unity *(MIT) — your renderer foundation.* Best Unity splat renderer to fork. Gives you GPU depth-sorting, XR stereo (single-pass instanced), async CPU-to-GPU upload, and a packed data mode. It draws splats inside Unity's normal transparent queue rather than a separate offscreen texture, which is the right design for AR. You fork this.  
* b0nes164/GPUSorting *(MIT) — the sorter.* Very fast GPU radix sort, but needs GPU subgroup/wave operations. Whether the Aura's Adreno driver supports these is the single most important unknown in the project (Milestone 0). If not, you need a fallback sort, and that changes your budget.  
* playcanvas/splat-transform *(MIT) — LOD + packing tool.* CLI and library that generates level-of-detail hierarchies and orders splats spatially. The packaging step uses it; you must understand its output so your loader matches.  
* *PlayCanvas engine and* sparkjsdev/spark *(both MIT) — the architecture you are copying.* These solved splat LOD streaming, in the browser. You will not use their code, but their design (a spatial tree of splats, a fixed per-frame splat budget, paging detail in and out) is exactly what you port to Unity. When unsure how the scheduler should behave, read how Spark's LoD splat tree and PlayCanvas LOD streaming work and mirror it.  
* nianticlabs/spz *(MIT) and zstd (BSD) — compression.* SPZ is a compact format using parallel compressed streams; zstd is the general compressor. Our container borrows these patterns. Integrate libzstd as a small native plugin for fast on-device decompression.  
* HiFi-Human/DynGsplat-unity *(REFERENCE ONLY, custom license) — for 4D.* A Unity project that plays animated splat sequences with the keyframe-plus-deltas idea. *Do not copy its code*, its license is not open for our use. Read it to understand the architecture (how it chunks frames into blocks, keeps only a couple of blocks in memory). Our 4D codec is a clean reimplementation.  
*Forbidden:* the Inria gaussian-splatting repos and anything bundling them (including the 4D training repos 4DGaussians and SpacetimeGaussians). Non-commercial license. Read the papers for technique, do not use the code. The Apache-licensed gsplat (nerfstudio) is the clean training base the Python team uses.  
────────────────────────  
*Part 5 — The file format you load*  
The packaging step produces a custom container (.zsplat static, .zsplat4d animated), specified in a shared spec doc. Your loader and scheduler are built around its shape:  
* The scene is divided into *spatial chunks* (a grid in space). The scheduler decides per chunk how much detail to load.  
* Each chunk stores splats in *nested LOD ranks*: LOD 0 is the first N0 splats, LOD 1 the first N1 (a superset), and so on. Raising detail means appending splats, never reloading what you have. This is what makes LOD transitions cheap.  
* Within a chunk, attributes are *separate, individually compressed streams*: positions, rotations, scales, color+opacity, and each SH band on its own. Far-away chunks simply do not fetch their SH streams. Each stream is zstd-compressed and byte-addressable, so over the network you fetch exactly the byte range you need (HTTP Range).  
* A *manifest* lists chunks, their per-LOD splat counts, bounding boxes, and byte offsets.  
For 4D: frames group into *blocks* (a GOP, ~20 frames). Each block has one keyframe (a normal chunk set) plus, per following frame, *sparse deltas*: (which-splat, new-value) pairs only for splats that changed, kept separate per attribute and per LOD rank. Color/SH deltas use a small per-block codebook. The contract: a viewer fetching only position/rotation/scale deltas at half frame rate, skipping SH, must still render a valid lower-fidelity result.  
────────────────────────  
*Part 6 — What gets built, in order*  
*Part 6 — What gets built, in order*  
Each milestone ends in something runnable on the device. Do not start the next until the current one's acceptance check passes on the Aura. For each, the split between you and Claude Code is noted.  
*Milestone 0 — Device truth. Start here.*  
You cannot design the budget until you measure the device. Smallest possible thing: fork gsplat-unity, get one static splat file rendering on the Aura, in stereo, in a linear-color-space URP project, on Vulkan. Then measure.  
* *You:* stand up the whole Unity/OpenXR/Android XR/Vulkan environment, build, deploy to device, run the measurements, write DEVICE_PROFILE.md.  
* *Claude Code:* the minimal render harness and the measurement hooks (timestamp queries, the microbenchmark scenes).  
Measure: do subgroup/wave ops work (so GPUSorting runs)? Sort time at 100k / 400k / 1M splats. Full-screen alpha-blend fill at native and 0.75x res. Upload throughput. zstd decompress speed. Refresh rate. How the see-through compositor handles alpha. Sustained GPU time per frame after 15 minutes (thermal throttling is real on glasses).  
From those numbers, derive your initial splat budget: the resident splat count where sort + blend stays well within frame time. This is the go/no-go that sets everything else.  
*Milestone 1 — Static rendering at budget.*  
Render a real packed .zsplat (no streaming yet, load a fixed LOD). Get fundamentals right: premultiplied alpha for see-through, correct stereo in single-pass instanced (historic bugs around per-eye indexing, test both eyes explicitly), splats sorting and blending properly, linear color correct end to end.  
Render a real packed .zsplat (no streaming yet, load a fixed LOD). Get fundamentals right: premultiplied alpha for see-through, correct stereo in single-pass instanced (historic bugs around per-eye indexing, test both eyes explicitly), splats sorting and blending properly, linear color correct end to end.  
* *You:* verify all of the above on-device, both eyes, visually sign off on color and blending.  
* *Claude Code:* the loader and the draw path on top of the fork.  
*Milestone 2 — The streaming runtime (core of the static viewer).*  
Built as subsystems behind clean interfaces:  
* Buffer pool: GPU buffers allocated once at startup, sized to the budget. Slot allocator; nested LOD upgrades only append rows. Zero allocations during steady-state.  
* IO + decode: read .zsplat from local storage (memory-mapped) and over network (UnityWebRequest Range headers); decompress zstd on worker threads; feed the async-upload path.  
* LOD scheduler: per-chunk screen-space error from bounding box and camera; greedily spend the budget on chunks that reduce error most per splat; downgrade farthest/cheapest under pressure; hysteresis and minimum dwell time so LODs do not flicker; prefetch on predicted camera motion.  
* Sort + draw: global GPU sort over resident splats when the view changes enough; procedural instanced draw; fallback sort behind the same interface if M0 demanded it.  
* Debug overlay: on-device HUD with fps, GPU ms split into sort/blend/upload, resident splats per LOD, queue depth, pool occupancy, plus a heatmap mode coloring splats by chunk LOD. You will not survive without this.  
* *You:* run the walkthrough on-device, watch for popping, confirm the memory plateau over a long soak, read the overlay and tell Claude Code where the budget is being blown.  
* *Claude Code:* all five subsystems and their tests.  
Acceptance: cold start to stable image in roughly two seconds locally; native frame rate through a 60-second walkthrough; no visible popping at walking speed; flat memory over a 30-minute soak.  
*Milestone 3 — Animated splats, the 4D viewer. Your headline feature.*  
*Milestone 3 — Animated splats, the 4D viewer. Your headline feature.*  
Extend the static runtime to play sequences. Precondition: training gives blocks with constant splat count and order. Do not add motion-tracking heuristics, the synthetic pipeline makes correspondence a given.  
* Sequence player: a ring buffer of ~2 decoded blocks; a compute shader (ApplyDeltas) scatter-writes sparse deltas onto the resident buffers into the back buffer of a double-buffered pair; swap on the frame tick; resort every frame (motion changes depth order); a timeline API (play, pause, seek, loop) with an injectable clock so audio can drive it later in NEONFOLK.  
* Temporal LOD, three levers by distance: drop the SH delta streams; halve or quarter frame rate and interpolate on the GPU between fetched frames (lerp positions/scales, slerp rotations); reduce the LOD rank as in 3D. All three reuse machinery you already built.  
* *You:* validate on-device that a ~10-second performer plays composited into the static hall within budget, with no temporal flicker at any LOD tier (record a slow pan to check), and seek under half a second.  
* *Claude Code:* the player, the decode kernel, the temporal-LOD logic.  
*Milestone 4 — Device hardening.*  
See-through compositing pass (confirm premultiplied alpha through the Android XR compositor; optional brightness lift for legibility). Foveated rendering if the platform exposes it. Thermal soak and budget tuning. World anchoring so splats stay locked to a spot in the room as you move (no swimming, the AR quality bar). Clean pause/resume that releases and rebuilds pools.  
* *You:* all on-device validation, the world-anchoring quality call, the thermal tuning.  
* *Claude Code:* the compositing pass, foveation hookup, lifecycle code.  
────────────────────────  
*Part 7 — How to drive Claude Code on this*  
*Part 7 — How to drive Claude Code on this*  
This is a real-time graphics codebase with hardware constraints and subtle correctness traps. Claude Code is very capable here but needs structure.  
* *Seed a* CLAUDE.md *at the repo root* with the hard rules so they are enforced every session: linear color space everywhere (no sRGB conversions in the data path), Vulkan-only assumptions for device code, zero steady-state allocations in the hot path, the licensing rules from Part 4 (never import Inria-licensed code, DynGsplat is read-only), coordinate conversions live in one tested place, and never guess a device number (if it is not in DEVICE_PROFILE.md, mark it TODO and tell you to measure it).  
* *One task, one pull request*, each mapped to a milestone acceptance point. Have Claude Code write the PR description against that criterion. This stops it sprawling.  
* *Make it read before it writes.* Before any work touching the renderer fork, the sorter, or the packer, instruct it to fetch and read the upstream README and implementation docs. Splat libraries have non-obvious constraints (the subgroup-ops requirement, the gamma/linear trap, stereo eye-indexing) it will only respect if it reads the source.  
* *Lead with the debug overlay and tests.* Have it build the on-device HUD early and write tests as it goes: golden-image tests on desktop, unit tests for the slot allocator and scheduler with scripted camera poses, allocation tests proving zero steady-state garbage. Graphics bugs are visual and timing-dependent; without instrumentation you are both blind.  
* *Force the hard questions up.* When it hits something device-dependent or ambiguous, it surfaces it in the PR as an explicit TODO or question, it does not silently pick a value. Milestone 0 exists to convert unknowns into measured facts. Protect that.  
* *Where to distrust the agent and review hardest:* anything touching stereo/XR rendering correctness (easy to get subtly wrong, looks almost right) and the sort cadence on device (the line between smooth and stuttering). Review those PRs carefully and test on the actual glasses, never just the editor.  
Remember the division: Claude Code proposes and implements; you set up the environment, deploy, measure, look, and decide.  
────────────────────────  
*Part 8 — The one thing that determines success*  
*Part 8 — The one thing that determines success*  
The mobile GPU is the whole game. The algorithms are known and the libraries exist. What is hard is fitting millions of sorted, blended, animated blobs into a mobile thermal budget on a see-through display. That is why you start by measuring the device, why every subsystem has a splat budget, and why the debug overlay is not optional. Build the smallest thing that renders on the Aura, learn what the hardware gives you, and let those numbers drive every decision after.  
Start with Milestone 0.  
