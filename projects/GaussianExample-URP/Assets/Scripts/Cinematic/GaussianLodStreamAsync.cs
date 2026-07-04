// SPDX-License-Identifier: MIT
// Phase 2 M2 of STREAMED_LOD_DESIGN.md — ASYNC streaming via Addressables. Supersedes the
// editor-only GaussianLodStreamManager (which used AssetDatabase and would return null in a
// build). Here every (chunk,LOD) is loaded with Addressables.LoadAssetAsync (non-blocking, works
// in an Android/XR build) and Addressables.Release'd on eviction — Addressables ref-counts the
// asset AND its .bytes dependencies, so CPU memory is genuinely bounded, not just GPU. Combined
// with the bounded renderer pool this is the SuperSplat model: near-instant first image (env /
// coarsest of the nearest chunks stream in first), progressive refine, memory right-sized to the
// device, scenes larger than RAM.
//
// never-drop-visible: a chunk keeps its currently-shown level on screen until the finer level has
// finished loading, then swaps and releases the old handle — no holes/pops during streaming.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using GaussianSplatting.Runtime;
using GaussianSplatting.Runtime.StreamedSog;
using GaussianSplatting.Runtime.Streaming;

namespace GsplatLod
{
    public class GaussianLodStreamAsync : MonoBehaviour
    {
        /// <summary>
        /// Which on-disk format the streamer expects at <see cref="manifestPath"/>.
        /// <c>Auto</c> sniffs the path (see <see cref="SogReader.IsSogPath"/>) and picks
        /// SOG when it sees a .sog dir or a lod-meta.json file, otherwise SPZ.
        /// </summary>
        public enum StreamFormat { Auto, Spz, Sog }

        [Header("Source")]
        // Relative path resolved under Application.streamingAssetsPath first; falls back to the
        // absolute dev-machine path via LodManifestResolver when running in the editor.
        public string manifestPath = "gsplat_lod/synthetic_sog/lod-meta.json";

        [Tooltip("Which streaming format to load. Auto = sniff manifestPath (SOG if .sog dir / lod-meta.json, else SPZ).")]
        [SerializeField] StreamFormat format = StreamFormat.Auto;

        [Tooltip("Populated at Start(): which reader path was chosen after format sniffing.")]
        [SerializeField] StreamFormat resolvedFormat = StreamFormat.Auto;

        // Selected reader token. Non-null after Start() when initialisation succeeded.
        // For SPZ this only holds the manifest path (SPZ path uses Addressables direct); for
        // SOG this holds the parsed SogManifest + flat leaf array + chunk loader.
        ISplatChunkReader m_ChunkReader;

        // SOG-side machinery. Only allocated when resolvedFormat == Sog. The SPZ path leaves
        // these null and the existing Addressables logic below runs unchanged.
        SogManifest m_SogManifest;
        SogChunkLoader m_SogChunkLoader;
        SogStreamer m_SogStreamer;

        // Track C4b: persistent per-frame scratch buffer for SogKdTree.WalkVisibleLeaves.
        // Allocated once at Start() (size = leaf count) so the per-frame walk is zero-alloc
        // — mirrors how SPZ's m_VisSorted List is reused across frames. Disposed in OnDestroy.
        NativeArray<int> m_SogVisibleLeafIdx;

        // Track C4b: FPS-throttled asset-assembly queue. Every frame we drain at most one
        // leaf whose desired-LOD chunk just became resident, decoding its (offset,count)
        // slice into a NativeArray<InputSplatData> via SogReader.DecodeSlice (or its wrapper).
        // The queue is populated inside the SOG Update() body from m_SogStreamer's swap
        // notifications. Runtime InputSplatData -> GaussianSplatAsset materialisation is
        // out of scope for this pass (it is an editor-only heavy pipeline) — for now we
        // decode + log + Dispose so the throttle contract is honoured and the never-evict-
        // visible refcount policy in SogStreamer.ApplyLodChanges is exercised end-to-end.
        readonly Queue<int> m_SogAssemblyQueue = new Queue<int>();

        // Assembly-pending set — mirrors PlayCanvas engine "assemblyPending" so we don't
        // enqueue the same leaf twice while the previous decode is still in flight.
        readonly HashSet<int> m_SogAssemblyPending = new HashSet<int>();

        // Diagnostics: leaves whose desired LOD is now resident vs. total visible (for HUD).
        int m_SogVisibleLeaves;
        int m_SogFrustumVisibleLeaves;
        int m_SogResidentLeaves;

        struct SogLeafDist { public float distSq; public int idx; }
        readonly List<SogLeafDist> m_SogLeafDistScratch = new List<SogLeafDist>(256);
        readonly List<int> m_SogFrustumLeafScratch = new List<int>(256);
        readonly HashSet<int> m_SogFrustumLeafSet = new HashSet<int>();
        readonly List<int> m_SogEvictScratch = new List<int>(256);

        // Nearest frustum-visible leaf LOD debug (HUD).
        int m_SogNearLeafIdx = -1;
        int m_SogNearStreamLod = -1;
        int m_SogNearDesiredLod = -1;
        int m_SogNearAsmLod = -1;
        int m_SogNearPoolLod = -1;
        int m_SogAssembliesTotal;
        int m_SogAssembliesThisFrame;

        GaussianSplatUnifiedWorld m_UnifiedWorld;
        GsplatLodScheduler m_SogScheduler;
        readonly List<GsplatLodScheduler.ChunkView> m_SogChunkViews = new List<GsplatLodScheduler.ChunkView>(128);
        readonly List<int> m_SogVisibleIds = new List<int>(128);
        int[] m_SogBudgetedOptimal;
        bool m_SogCoarseBootstrap = true;
        float m_SogBootstrapStartTime;
        const float kSogBootstrapTimeoutSec = 3f;
        const int kSogBootstrapOffFrustumCap = 32;

        public Camera cam;
        public bool enableEnv = false;

        [Header("Streaming working set")]
        public int maxResidentChunks = 16;
        [Tooltip("Max concurrent async loads in flight (throttles IO / upload spikes).")]
        public int maxConcurrentLoads = 4;
        [Tooltip("Max renderer.m_Asset swaps applied PER FRAME. Each swap triggers the base renderer's " +
                 "Dispose+Recreate, which racesagainst its background parallel sort workers if too many " +
                 "happen at once (ArgumentOutOfRangeException spam in the console during a big [F] burst). " +
                 "Keep low (2-4) — spreading swaps across frames costs nothing perceptually and kills the race.")]
        [Range(1, 16)] public int maxSwapsPerFrame = 3;
        public int cooldownEvals = 4;
        public bool coarseFirst = true;

        [Header("Demo / diagnostics")]
        [Tooltip("Slow-motion streaming so the coarse-first -> progressive-refine behavior is visible frame-by-frame. " +
                 "Forces maxConcurrentLoads=1, evalEveryNFrames to ~30 (1/2 sec at 60fps). Press [R] to reset streaming.")]
        public bool slowMotionDemo = false;
        [Tooltip("Press [R] in play mode to reset streaming state (all resident chunks freed, streaming restarts from " +
                 "coarsest LOD). Useful with slowMotionDemo to watch the progressive refinement.")]
        public bool resetKeyEnabled = true;

        [Header("Device budget (resident splats)")]
        [Tooltip("Desktop SuperSplat profile: 250K global splat budget. Balancer degrades far-first.")]
        public int deviceBudget = 2_000_000;

        [Header("Unified renderer (PlayCanvas gsplat-world)")]
        [Tooltip("SOG path: single merged GaussianSplatRenderer + one DrawProcedural via GpuBufferPool.")]
        public bool useUnifiedRenderer = true;

        [Header("Screen-error LOD bands")]
        [Tooltip("World-distance threshold where LOD steps from 0->1. Larger = more of the scene stays LOD0 (finer). " +
                 "Roughly tune to (max chunk world extent) * 2..3. For ~5-6m chunks (32ch on Festsaal) use ~20-25; for " +
                 "~10m chunks (16ch) use ~12-15. Retune when you re-bake with different --chunks.")]
        public float lodBaseDistance = 15f;
        [Tooltip("Distance multiplier per LOD band (2-3). Larger = wider bands = sharper transitions but bigger jumps. " +
                 "Default 3.0 matches SuperSplat parity — wider bands keep more near-field chunks at LOD0/1, " +
                 "which reduces balancer demotion pressure and helps the finest LOD actually reach visible chunks.")]
        public float lodMultiplier = 3.0f;
        [Tooltip("P0(a) SuperSplat-parity behind-camera penalty. A chunk whose centre is directly behind " +
                 "the camera (barely-visible via AABB overshoot) has its effective distance multiplied by this " +
                 "factor -> picks a coarser LOD. 5 = 5x demotion for straight-behind; 1 = disabled.")]
        [Range(1f, 20f)] public float lodBehindPenalty = 5f;
        [Tooltip("SOG: multiplies LOD0 distance bands (1 = default, 10 = ~10x wider finest-LOD zone). " +
                 "Runtime: [+] / [-] in play mode. Does not rebake — widens when rank-0 is selected.")]
        [Range(1f, 10f)] public float lod0CoverageScale = 1f;

        [Header("Hysteresis")]
        public int evalEveryNFrames = 10;
        public float lodUpdateDistance = 1.0f;

        [Header("SuperSplat parity")]
        [Tooltip("B1: If the optimal LOD is not yet resident, show up to this many coarser levels as fallback " +
                 "instead of hiding the chunk. Matches SuperSplat's lodUnderfillLimit — eliminates visible holes " +
                 "during streaming refinement. 0 = disable (old behavior: hide if optimal not resident).")]
        [Range(0, 10)] public int lodUnderfillLimit = 3;
        [Tooltip("B2: Staged single-step prefetch. When enabled, an Evaluate cycle only requests ONE level " +
                 "finer than current instead of jumping directly to the optimal. Prevents flooding the loader " +
                 "queue with LOD0 requests on scene enter; coarse always completes for every chunk before " +
                 "finer requests contend. Disable to match old jump-to-target behavior.")]
        public bool stagedPrefetch = true;
        [Tooltip("B3: Refcount+cooldown eviction. When a chunk leaves the wanted set, its handle is held in " +
                 "a cooldown for this many frames before Release. On repeated camera dither across a LOD " +
                 "boundary, the handle is reused instead of triggering a bundle reload.")]
        public int cooldownFrames = 100;
        [Tooltip("B4: Very-near instant-LOD bypass. When a chunk is first acquired AND its distance from camera " +
                 "is below (lodBaseDistance * veryNearFraction), skip the coarse-first / stagedPrefetch ramp " +
                 "and load its optimal LOD (typically 0) directly on the first Evaluate. Without this, near " +
                 "chunks need ~4 Evaluates = ~700-850ms at 60 FPS to climb from LOD4 -> LOD0 (one step per " +
                 "evalEveryNFrames=10 cycle), which is why the finest LOD often 'never opens' during a " +
                 "continuous cinematic pan. 0 = disabled (old ramp behavior).")]
        [Range(0f, 1f)] public float veryNearFraction = 0.5f;

        [Header("Full-quality shortcut")]
        [Tooltip("Press this key in play mode to toggle FORCE-MAX-QUALITY: every visible chunk is pinned to " +
                 "LOD0 (raw/finest), budget balancer is bypassed. Great A/B toggle to compare streamed vs " +
                 "full asset quality. Chunks stream up over the next few evals; press again to release.")]
        public KeyCode fullQualityToggleKey = KeyCode.F;
        [Tooltip("Current state (also settable via inspector).")]
        public bool forceMaxQuality = false;
        [Tooltip("When force-max-quality is ON, the concurrency cap is raised so chunks refine to LOD0 faster.")]
        [Range(1, 32)] public int forceQualityConcurrentLoads = 16;

        [Header("Chunk hierarchy")]
        [Tooltip("Root containing Chunk_N / LOD0..LOD4 children. Auto-detected from a child named 'Chunks' when empty.")]
        public Transform chunksRoot;
        [Tooltip("When a pre-built chunk hierarchy exists, stream into per-chunk LOD slots instead of dynamic Slot_N pool.")]
        public bool preferPrebuiltHierarchy = true;

#if UNITY_EDITOR
        [SerializeField, HideInInspector] int editorGlobalPreviewLod = 4;
        public int EditorGlobalPreviewLod => editorGlobalPreviewLod;
#endif

        [Header("Misc")]
        public bool autoFrameCamera = true;
        public bool showHud = true;

        const int kBuckets = 64;
        const float kBudgetDeadZone = 0.4f, kBudgetBlend = 0.3f;
        static readonly float kRefTanHalfFov = Mathf.Tan(22.5f * Mathf.Deg2Rad);

        class Chunk
        {
            public LodChunkMeta meta;
            public string[] addr;            // Addressables address per level
            public int[] splatCount;         // per level
            public Vector3 localCentre, localSize;
            public float dist;
            public bool visible;
            public int optimal, desired;
            public int slot = -1;            // pool mode: renderer slot index
            public GaussianSplatChunk hierarchyChunk; // hierarchy mode: pre-built chunk node
            public int curLevel = -1;        // level currently shown
            public AsyncOperationHandle<GaussianSplatAsset> curH; public bool hasCur;
            public AsyncOperationHandle<GaussianSplatAsset> penH; public bool hasPen; public int penLevel = -1;
            public int lastWantedEval = -9999;
        }

        readonly List<Chunk> m_Chunks = new List<Chunk>();
        GaussianSplatRenderer[] m_Pool;
        int[] m_SlotChunk;
        // v1 single-env legacy path (kept for backward compat / when environment{} block absent).
        GaussianSplatRenderer m_Env; AsyncOperationHandle<GaussianSplatAsset> m_EnvH; bool m_HasEnvH; int m_EnvCount;

        // B5: env tier — parallel queue that lives outside the LOD budget balancer.
        // These are the "sky/far background" chunks: loaded ONCE at Start(), never evicted,
        // never LOD-switched, excluded from m_VisSorted so the balancer can't demote them.
        // Fixed-size renderer pool (== env chunk count) — NOT bounded by maxResidentChunks.
        LodManifest m_Manifest;
        GaussianSplatRenderer[] m_EnvPool;
        AsyncOperationHandle<GaussianSplatAsset>[] m_EnvH2;
        bool[] m_EnvH2Valid;
        int m_EnvResidentSplats;
        int m_EnvChunkCount;

        int m_Frame, m_Eval, m_LastEvalFrame = -9999, m_ResidentSplats, m_ResidentChunks, m_VisibleChunks, m_InFlight;
        float m_BudgetScale = 1f;
        Vector3 m_LastCamPos = Vector3.positiveInfinity, m_SceneCentre; float m_SceneRadius;

        // Public accessors for external tools (e.g. SplineAutoFromGsplat that fits a Cinemachine
        // tour spline around whatever gsplat scene the streamer loaded).
        public Vector3 SceneCentre => m_SceneCentre;
        public float SceneRadius => m_SceneRadius;
        public bool BoundsReady => m_SceneRadius > 0f && m_Chunks.Count > 0;
        public int ResidentSplats => m_ResidentSplats;
        public int ResidentChunks => m_ResidentChunks;
        public int VisibleChunks => m_VisibleChunks;
        public int TotalChunkCount => m_Chunks != null ? m_Chunks.Count : 0;
        readonly Plane[] m_Planes = new Plane[6];
        readonly List<int>[] m_Bucket = new List<int>[kBuckets];
        readonly List<int> m_VisSorted = new List<int>();
        bool m_UseHierarchy;

        // B3: cooldown map for delayed Addressables.Release. Keyed by address string so the same
        // (chunk,LOD) can be reused across evict/re-request cycles without a bundle reload.
        struct CoolEntry { public AsyncOperationHandle<GaussianSplatAsset> h; public int framesLeft; public bool valid; }
        readonly Dictionary<string, CoolEntry> m_Cooldown = new Dictionary<string, CoolEntry>();
        readonly List<string> m_CooldownExpired = new List<string>(16);

        static void SafeRelease(ref AsyncOperationHandle<GaussianSplatAsset> h)
        {
            if (h.IsValid())
                Addressables.Release(h);
        }

        static void SafeRelease(AsyncOperationHandle<GaussianSplatAsset> h)
        {
            if (h.IsValid())
                Addressables.Release(h);
        }

        void ApplyRendererPerformanceSettings()
        {
            var settings = GaussianSplatSettings.instance;
            if (settings == null) return;

            // SOG unified: frustum culling ON (tris follows camera). Screen-LOD stride OFF
            // (stride causes dot-cloud within visible nodes). Pool holds frustum leaves at finest LOD.
            if (IsSogUnifiedPath())
            {
                settings.m_EnableOctreeCulling = true;
                settings.m_OctreeSkipFrustumCull = false;
                settings.m_EnableScreenLod = false;
                settings.m_LodMaxStride = 1;
                settings.m_LodSplatBudget = 0;
                settings.m_OctreeCullingUpdateInterval = 1;
                return;
            }

            settings.m_OctreeSkipFrustumCull = false;

            settings.m_EnableOctreeCulling = true;
            settings.m_EnableScreenLod = true;
            settings.m_LodFullDetailPixels = 200f;
            settings.m_OctreeCullingUpdateInterval = 2;

            int perRendererCap = maxResidentChunks > 0
                ? Mathf.Max(25_000, deviceBudget / maxResidentChunks)
                : 0;
            settings.m_LodSplatBudget = perRendererCap;
        }

        bool IsSogUnifiedPath()
        {
            if (!useUnifiedRenderer) return false;
            if (resolvedFormat == StreamFormat.Sog) return true;
            if (resolvedFormat != StreamFormat.Auto) return false;
            return SogReader.IsSogPath(manifestPath);
        }

        void Start()
        {
            if (cam == null) cam = Camera.main;
            for (int i = 0; i < kBuckets; i++) m_Bucket[i] = new List<int>(32);

            manifestPath = LodManifestResolver.Resolve(manifestPath, "[StreamAsync]");
            if (manifestPath == null) { enabled = false; return; }

            resolvedFormat = format;
            if (resolvedFormat == StreamFormat.Auto)
                resolvedFormat = SogReader.IsSogPath(manifestPath) ? StreamFormat.Sog : StreamFormat.Spz;
            ApplyRendererPerformanceSettings();

            if (resolvedFormat == StreamFormat.Sog)
            {
                if (!SetupSogReader(manifestPath)) { enabled = false; return; }
                // SOG runtime binds here — the SPZ-specific manifest walk below is skipped
                // (SPZ code path only runs when the format-sniff picked SPZ). The SOG streamer
                // owns its own state machine; nothing more to configure at Start() beyond
                // logging what was loaded.
                Debug.Log($"[StreamAsync] SOG reader ready (leaves={m_SogManifest.Leaves.Length}, " +
                          $"chunks={m_SogManifest.Meta.Filenames?.Length ?? 0}, " +
                          $"lodLevels={m_SogManifest.LodLevels})");
                ApplyDeviceBudgetPreset();
                m_SogCoarseBootstrap = true;
                m_SogBootstrapStartTime = Time.time;
                if (useUnifiedRenderer)
                    SetupUnifiedWorld();
                FrameCameraFromSogBounds();
                return;
            }

            m_ChunkReader = new SpzChunkReader(manifestPath);
            var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath));
            // B4: fail-hard version+shape validation. JsonUtility silently drops unknown fields,
            // so an unversioned or too-new payload would blackhole every chunk on an older reader.
            if (!LodManifestValidator.Validate(man, "[StreamAsync]", out var vErr))
            { Debug.LogError(vErr); enabled = false; return; }
            Debug.Log($"[StreamAsync] manifest loaded: {man.chunks.Length} chunks from '{manifestPath}'");
            if (man.version >= 2) Debug.Log($"[StreamAsync] manifest v2 loaded, filenames={man.filenames?.Length ?? 0}");
            m_Manifest = man;
            // SuperSplat parity: min-clamp 1.2 (was 1.05 — too flat, made bands nearly identical).
            if (lodMultiplier < 1.2f) lodMultiplier = 1.2f;

            Vector3 mn = Vector3.one * 1e9f, mx = -mn;
            foreach (var cm in man.chunks)
            {
                var c = new Chunk { meta = cm };
                // B3: v2 -> ellipsoid extents; v1 -> legacy percentile AABB (safe superset).
                LodManifestValidator.ResolveChunkBounds(man, cm, out var bmin, out var bmax);
                c.localCentre = (cm.centre != null && cm.centre.Length == 3)
                    ? new Vector3(cm.centre[0], cm.centre[1], cm.centre[2])
                    : (bmin + bmax) * 0.5f;
                c.localSize = bmax - bmin;
                c.addr = new string[cm.lods.Length]; c.splatCount = new int[cm.lods.Length];
                // B2: v2 -> filenames[fileIdx]; v1 -> per-leaf .file string.
                for (int L = 0; L < cm.lods.Length; L++)
                { c.addr[L] = LodManifestValidator.ResolveAddr(man, cm.lods[L]); c.splatCount[L] = cm.lods[L].splatCount; }
                mn = Vector3.Min(mn, bmin); mx = Vector3.Max(mx, bmax);
                m_Chunks.Add(c);
            }

            m_UseHierarchy = preferPrebuiltHierarchy && TryBindHierarchy(man);

            int poolN = Mathf.Clamp(maxResidentChunks, 1, m_Chunks.Count);
            maxResidentChunks = poolN;
            ApplyRendererPerformanceSettings();
            if (!m_UseHierarchy)
            {
                m_Pool = new GaussianSplatRenderer[poolN]; m_SlotChunk = new int[poolN];
                for (int i = 0; i < poolN; i++)
                {
                    var go = new GameObject("Slot_" + i); go.SetActive(false); go.transform.SetParent(transform, false);
                    m_Pool[i] = go.AddComponent<GaussianSplatRenderer>(); m_SlotChunk[i] = -1;
                }
            }
            else
            {
                // Hierarchy mode: each chunk owns LOD slots; resident cap limits loaded chunks, not renderer pool size.
                foreach (var hc in GetComponentsInChildren<GaussianSplatChunk>(true))
                    hc.ClearResident();
                Debug.Log($"[StreamAsync] hierarchy mode: {CountBoundHierarchyChunks()} chunks with LOD slots (cap={poolN} resident)");
            }

            // B5: env tier. Loads once at Start() from environment.files[] (v2) or envFile (v1),
            // never released, never LOD-switched, never counted in the LOD budget balancer.
            // Env chunks are excluded from m_VisSorted (never demoted) and from scene-bounds
            // accumulation (a whole-scene env would blow up m_SceneRadius and push the auto-tune
            // at line ~209 too far, breaking near-field LOD bands).
            SetupEnvTier(man);

            m_SceneCentre = (mn + mx) * 0.5f; m_SceneRadius = (mx - mn).magnitude * 0.5f;
            // Auto-tune LOD bands only when lodBaseDistance is unset (< 1). Explicit inspector values
            // (e.g. 15 m for ~5 m chunks) are kept so near-field picks LOD0/1 and far picks LOD3/4.
            if (lodBaseDistance < 1f) lodBaseDistance = m_SceneRadius * 1.2f;
            if (autoFrameCamera && cam != null)
            {
                // Camera position/rotation is intentionally NOT set — the scene / user owns the
                // camera transform. Only widen the clip planes so a large scene isn't near/far clipped.
                cam.nearClipPlane = 0.05f; cam.farClipPlane = Mathf.Max(cam.farClipPlane, m_SceneRadius * 12f);
            }
            Debug.Log($"[StreamAsync] {m_Chunks.Count} chunks, pool={poolN}, budget={deviceBudget}, radius={m_SceneRadius:F1}m, lodBaseDistance={lodBaseDistance:F1}m (Addressables async{(m_UseHierarchy ? ", hierarchy" : "")})");
        }

        int CountBoundHierarchyChunks()
        {
            int n = 0;
            foreach (var c in m_Chunks) if (c.hierarchyChunk != null) n++;
            return n;
        }

        /// <summary>
        /// Match manifest chunks to pre-built GaussianSplatChunk children. Returns true when at
        /// least one chunk was bound (requires lodSlots on each chunk node).
        /// </summary>
        bool TryBindHierarchy(LodManifest man)
        {
            Transform root = chunksRoot;
            if (root == null) root = transform.Find(GaussianLodHierarchyBuilder.kDefaultChunksRootName);
            if (root == null) return false;

            var byId = new Dictionary<int, GaussianSplatChunk>();
            foreach (var hc in root.GetComponentsInChildren<GaussianSplatChunk>(true))
            {
                if (hc.LodCount <= 0) hc.RefreshLodSlotsFromChildren();
                if (hc.LodCount > 0) byId[hc.chunkId] = hc;
            }
            if (byId.Count == 0) return false;

            int bound = 0;
            foreach (var c in m_Chunks)
            {
                if (!byId.TryGetValue(c.meta.id, out var hc)) continue;
                c.hierarchyChunk = hc;
                c.localCentre = hc.localCentre;
                c.localSize = hc.localSize;
                bound++;
            }
            if (bound == 0) return false;
            chunksRoot = root;
            return true;
        }

        /// <summary>
        /// Track C4: instantiate the SOG reader stack (manifest + loader + streamer) as a
        /// sibling to the existing SPZ machinery. Called only when resolvedFormat == Sog.
        /// Returns false and disables the component on any error, matching the SPZ
        /// path's "fail-hard on manifest error" behaviour.
        /// </summary>
        bool SetupSogReader(string sogPath)
        {
            try
            {
                m_SogManifest = SogReader.LoadManifest(sogPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[StreamAsync] SOG LoadManifest failed for '{sogPath}': {ex.Message}");
                return false;
            }

            // Currently there is no libwebp binding in this project — the reader still parses
            // the manifest and flattens the kd-tree, but LoadAsync-driven chunk decodes will
            // throw when a caller triggers WebP decode. That is intentional (a Track-C4
            // deliverable is the parse/flatten scaffold + a stub decoder); a real .sog scene
            // will land alongside the netpyoung/unity.webp plugin in a later track.
            IWebPDecoder decoder = new NativeWebPDecoder();
            m_SogChunkLoader = new SogChunkLoader(m_SogManifest.RootDirectory,
                                                   m_SogManifest.Meta.Filenames, decoder)
            {
                CooldownFrames = Mathf.Max(1, cooldownFrames),
            };
            m_SogStreamer = new SogStreamer(m_SogChunkLoader, m_SogManifest.Leaves,
                                             Mathf.Max(1, m_SogManifest.LodLevels))
            {
                LodBaseDistance = lodBaseDistance,
                LodMultiplier   = lodMultiplier,
                Lod0CoverageScale = lod0CoverageScale,
                LodBehindPenalty = lodBehindPenalty,
                VeryNearFraction = veryNearFraction,
                ForceMaxQuality = forceMaxQuality,
                LodUnderfillLimit = lodUnderfillLimit,
            };

            int leafCount = m_SogManifest.Leaves.IsCreated ? m_SogManifest.Leaves.Length : 0;
            m_SogBudgetedOptimal = new int[Mathf.Max(1, leafCount)];
            for (int i = 0; i < m_SogBudgetedOptimal.Length; i++)
                m_SogBudgetedOptimal[i] = -1;

            // Track C4b: one-shot allocation of the per-frame visibility scratch buffer.
            // WalkVisibleLeaves requires outIdx.Length >= leaves.Length.
            m_SogVisibleLeafIdx = new NativeArray<int>(
                Mathf.Max(1, leafCount), Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            m_ChunkReader = new SogChunkReader(m_SogManifest);
            return true;
        }

        void ApplyDeviceBudgetPreset()
        {
            if (deviceBudget > 0) return;
#if UNITY_ANDROID
            deviceBudget = 1_000_000;
#else
            deviceBudget = 2_000_000;
#endif
        }

        void SetupUnifiedWorld()
        {
            m_UnifiedWorld = GetComponent<GaussianSplatUnifiedWorld>();
            if (m_UnifiedWorld == null)
                m_UnifiedWorld = gameObject.AddComponent<GaussianSplatUnifiedWorld>();
            m_UnifiedWorld.Configure(deviceBudget, deviceBudget);
            m_SogScheduler = m_UnifiedWorld.scheduler;
            m_SogScheduler.lodBaseDistance = lodBaseDistance;
            m_SogScheduler.lodMultiplier = lodMultiplier;
            m_SogScheduler.lodBehindPenalty = lodBehindPenalty;
            m_SogScheduler.deviceBudget = deviceBudget;
            m_SogScheduler.forceMaxQuality = forceMaxQuality;
            m_SogScheduler.budgetScale = m_BudgetScale;
            ApplyRendererPerformanceSettings();
            if (m_UnifiedWorld.renderer != null)
                m_UnifiedWorld.renderer.m_SHOrder = 3;
            Debug.Log($"[StreamAsync] Unified world ready (budget={deviceBudget / 1000}K, maxLeaves={maxResidentChunks})");
        }

        /// <summary>
        /// SOG path parity with the SPZ Start() auto-framing block. Without this the
        /// scene camera stays wherever the level designer left it (often far from a
        /// freshly baked gsplat_lod asset centred near the origin).
        /// </summary>
        void FrameCameraFromSogBounds()
        {
            if (m_SogManifest?.Meta?.Tree == null) return;

            float3 wMin;
            float3 wMax;
            bool tight;
            if (m_SogManifest.Leaves.IsCreated
                && m_SogManifest.Leaves.Length > 0
                && SogLeafMath.TryComputeTightSceneBounds(m_SogManifest.Leaves, out wMin, out wMax))
            {
                tight = true;
            }
            else
            {
                tight = false;
                var mn = m_SogManifest.Meta.Tree.BoundMin;
                var mx = m_SogManifest.Meta.Tree.BoundMax;
                wMin = SogCodebooks.InvLogTransform(new float3(mn.x, mn.y, mn.z));
                wMax = SogCodebooks.InvLogTransform(new float3(mx.x, mx.y, mx.z));
            }

            m_SceneCentre = new Vector3((wMin + wMax).x * 0.5f, (wMin + wMax).y * 0.5f, (wMin + wMax).z * 0.5f);
            m_SceneRadius = math.length(wMax - wMin) * 0.5f;
            // Interior profile: wider L0 band than legacy 1.2×radius; explicit inspector value kept when >= 1.
            if (lodBaseDistance < 1f)
                lodBaseDistance = Mathf.Max(8f, m_SceneRadius * 0.15f);
            if (m_SogStreamer != null)
                m_SogStreamer.LodBaseDistance = lodBaseDistance;

            if (!autoFrameCamera || cam == null) return;

            // Camera position/rotation is intentionally NOT set here — the scene / user owns the
            // camera transform. Only the clip planes are widened so a large scene isn't near/far
            // clipped. (Auto-frame pos + LookAt removed on request.)
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, m_SceneRadius * 12f);
            Debug.Log($"[StreamAsync/SOG] clip planes set (near=0.05, far>={m_SceneRadius * 12f:F0}); camera transform left untouched (radius={m_SceneRadius:F1}m, tightLeafBounds={tight})");
        }

        void Update()
        {
            // SOG path is a self-contained state machine — the SPZ Addressables loop below
            // is skipped when the C4 factory routed to SOG. Track C4b wires the full
            // per-frame flow here: Loader.Tick -> WalkVisibleLeaves -> ApplyLodChanges
            // -> throttled asset assembly. Never-evict-visible refcount tracking lives
            // inside SogStreamer.ApplyLodChanges (pendingDecrements map).
            if (resolvedFormat == StreamFormat.Sog)
            {
                m_Frame++;
                UpdateSog();
                return;
            }

            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            m_Frame++;

            // Reset key: free everything and restart streaming from the coarsest LODs.
            // Combined with slowMotionDemo, this is how you WATCH the SuperSplat "instant complete
            // coarse image, then progressive sharpen" behavior — press [R], then over the next few
            // seconds the resident splat count climbs as chunks refine one level per eval.
            if (resetKeyEnabled && Input.GetKeyDown(KeyCode.R))
            {
                for (int i = 0; i < m_Chunks.Count; i++)
                {
                    var c = m_Chunks[i];
                    if (c.hasPen) { SafeRelease(ref c.penH); c.hasPen = false; c.penLevel = -1; }
                    if (c.hasCur) { SafeRelease(ref c.curH); c.hasCur = false; }
                    if (m_UseHierarchy)
                    {
                        c.hierarchyChunk?.ClearResident();
                        c.slot = -1;
                    }
                    else if (c.slot >= 0)
                    {
                        m_Pool[c.slot].m_Asset = null;
                        if (m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(false);
                        m_SlotChunk[c.slot] = -1;
                        c.slot = -1;
                    }
                    c.curLevel = -1; c.lastWantedEval = -9999;
                }
                m_ResidentSplats = 0; m_ResidentChunks = 0; m_LastEvalFrame = -9999;
                m_BudgetScale = 1f;
                // Drain cooldown handles too so the reset truly restarts from zero.
                foreach (var kv in m_Cooldown) if (kv.Value.valid) SafeRelease(kv.Value.h);
                m_Cooldown.Clear();
                Debug.Log("[StreamAsync] reset — restarting stream from coarsest LODs");
            }

            // [F] toggles FORCE-MAX-QUALITY — pins every visible chunk to LOD0 (raw), bypasses budget.
            // A/B compare streamed vs full asset quality with a single keystroke.
            if (Input.GetKeyDown(fullQualityToggleKey))
            {
                forceMaxQuality = !forceMaxQuality;
                m_LastEvalFrame = -9999;         // force immediate re-evaluate so chunks start refining now
                Debug.Log($"[StreamAsync] force max quality = {(forceMaxQuality ? "ON — refining every visible chunk to LOD0" : "OFF — screen-error LOD + budget")}");
            }

            PollLoads();     // advance in-flight loads every frame (assign when ready)
            TickCooldown();  // B3: age out held handles
            bool camMoved = (cam.transform.position - m_LastCamPos).sqrMagnitude > lodUpdateDistance * lodUpdateDistance;
            int evalInterval = slowMotionDemo ? Mathf.Max(evalEveryNFrames, 30) : Mathf.Max(1, evalEveryNFrames);
            if ((m_Frame - m_LastEvalFrame) < evalInterval && !camMoved) return;
            m_LastEvalFrame = m_Frame; m_LastCamPos = cam.transform.position;
            Evaluate();
        }

        // ================================================================
        // Track C4b — SOG per-frame driver
        // ================================================================
        //
        // Flow (mirrors PlayCanvas engine gsplat-octree-instance.js):
        //   1) Loader.Tick()               -> ages cooldown entries, drops long-idle chunks.
        //   2) Compute cam pos/fwd + frustum planes (reuses SPZ per-frame cache).
        //   3) SogKdTree.WalkVisibleLeaves -> writes visible leaf indices into m_SogVisibleLeafIdx.
        //   4) SogStreamer.ApplyLodChanges -> per-leaf desired-LOD selection, AcquireAsync,
        //                                    pendingDecrements never-evict-visible policy.
        //   5) Drain m_SogAssemblyQueue    -> at most one leaf/frame, decode (offset,count) slice.
        //
        // The SPZ path is untouched: the whole method is only entered when
        // resolvedFormat == StreamFormat.Sog.
        Vector3 SogLeafWorldCentre(int leafIdx)
        {
            var leaf = m_SogManifest.Leaves[leafIdx];
            float3 wMin = SogCodebooks.InvLogTransform(leaf.BoundMin);
            float3 wMax = SogCodebooks.InvLogTransform(leaf.BoundMax);
            float3 c = (wMin + wMax) * 0.5f;
            return new Vector3(c.x, c.y, c.z);
        }

        /// <summary>
        /// Finest (lowest rank) LOD whose chunk is already decoded in the loader.
        /// Used for coarse-first assembly when the streamer has not yet swapped to desired LOD.
        /// </summary>
        int SogFinestResidentLod(SogLeafNode leaf)
        {
            unsafe
            {
                for (int lod = 0; lod < leaf.LodCount; lod++)
                {
                    int fileIdx = leaf.LodFileIdx[lod];
                    if (fileIdx >= 0 && m_SogChunkLoader.IsResident(fileIdx))
                        return lod;
                }
            }
            return -1;
        }

        void SyncSogStreamerTuning()
        {
            if (m_SogStreamer == null) return;
            m_SogStreamer.LodBaseDistance = lodBaseDistance * m_BudgetScale;
            m_SogStreamer.LodMultiplier = lodMultiplier;
            m_SogStreamer.Lod0CoverageScale = lod0CoverageScale;
            m_SogStreamer.LodBehindPenalty = lodBehindPenalty;
            m_SogStreamer.VeryNearFraction = veryNearFraction;
            m_SogStreamer.ForceMaxQuality = forceMaxQuality;
            m_SogStreamer.LodUnderfillLimit = lodUnderfillLimit;
        }

        static int SogLeafSplatCountAtLod(SogLeafNode leaf, int lod)
        {
            unsafe
            {
                if (lod < 0 || lod >= leaf.LodCount) return 0;
                return leaf.LodSplatCount[lod];
            }
        }

        void SogEvaluateAndBudget(int visibleCount, Camera activeCam)
        {
            if (m_SogStreamer == null || m_SogBudgetedOptimal == null || m_SogManifest == null)
                return;

            int leafN = m_SogManifest.Leaves.Length;
            if (m_SogCoarseBootstrap)
            {
                for (int i = 0; i < leafN && i < m_SogBudgetedOptimal.Length; i++)
                {
                    var leaf = m_SogManifest.Leaves[i];
                    m_SogBudgetedOptimal[i] = Math.Max(0, leaf.LodCount - 1);
                }
                return;
            }

            m_SogChunkViews.Clear();
            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = m_SogVisibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= leafN) continue;
                var leaf = m_SogManifest.Leaves[leafIdx];
                float closestDist = Mathf.Sqrt(SogLeafMath.ClosestDistSq(leaf, activeCam.transform.position));
                int optimal = m_SogStreamer.ComputeOptimalLod(leafIdx, activeCam);
                m_SogChunkViews.Add(new GsplatLodScheduler.ChunkView
                {
                    id = leafIdx,
                    visible = true,
                    distance = closestDist,
                    worldCentre = SogLeafWorldCentre(leafIdx),
                    lodCount = leaf.LodCount,
                    optimalLod = optimal,
                    desiredLod = optimal,
                    splatCountAtLod = lod => SogLeafSplatCountAtLod(leaf, lod),
                });
            }

            if (m_SogScheduler != null && !forceMaxQuality && m_SogChunkViews.Count > 0)
                m_SogScheduler.BalanceLodBudget(m_SogChunkViews, 0);

            for (int i = 0; i < m_SogChunkViews.Count; i++)
            {
                int leafIdx = m_SogChunkViews[i].id;
                if (leafIdx >= 0 && leafIdx < m_SogBudgetedOptimal.Length)
                    m_SogBudgetedOptimal[leafIdx] = m_SogChunkViews[i].desiredLod;
            }
        }

        void SogAcquireBootstrapChunks(int visibleCount)
        {
            if (!m_SogCoarseBootstrap || m_SogStreamer == null || m_SogManifest == null) return;

            int leafN = m_SogManifest.Leaves.Length;
            int offFrustumQueued = 0;

            for (int leafIdx = 0; leafIdx < leafN; leafIdx++)
            {
                bool inFrustum = m_SogFrustumLeafSet.Contains(leafIdx);
                if (!inFrustum)
                {
                    if (offFrustumQueued >= kSogBootstrapOffFrustumCap) continue;
                    offFrustumQueued++;
                }

                var leaf = m_SogManifest.Leaves[leafIdx];
                int maxLod = Math.Max(0, leaf.LodCount - 1);
                unsafe
                {
                    int fi = leaf.LodFileIdx[maxLod];
                    if (fi >= 0) m_SogChunkLoader.AcquireAsync(fi);
                }
            }

            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = m_SogVisibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= leafN) continue;
                var leaf = m_SogManifest.Leaves[leafIdx];
                int maxLod = Math.Max(0, leaf.LodCount - 1);
                unsafe
                {
                    int fi = leaf.LodFileIdx[maxLod];
                    if (fi >= 0) m_SogChunkLoader.AcquireAsync(fi);
                }
            }
        }

        void TryExitSogCoarseBootstrap(int visibleCount)
        {
            if (!m_SogCoarseBootstrap) return;

            bool timedOut = Time.time - m_SogBootstrapStartTime >= kSogBootstrapTimeoutSec;
            bool coarseReady = visibleCount > 0;
            for (int v = 0; v < visibleCount && coarseReady; v++)
            {
                int leafIdx = m_SogVisibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= m_SogManifest.Leaves.Length) { coarseReady = false; break; }
                var leaf = m_SogManifest.Leaves[leafIdx];
                int maxLod = Math.Max(0, leaf.LodCount - 1);
                unsafe
                {
                    int fi = leaf.LodFileIdx[maxLod];
                    if (fi < 0 || !m_SogChunkLoader.IsResident(fi))
                        coarseReady = false;
                }
            }

            if (timedOut || coarseReady)
            {
                m_SogCoarseBootstrap = false;
                m_LastEvalFrame = -9999;
                Debug.Log("[StreamAsync/SOG] coarse bootstrap complete — switching to screen-error LOD");
            }
        }

        void UpdateSogNearLeafDebug(Camera activeCam)
        {
            m_SogNearLeafIdx = -1;
            m_SogNearStreamLod = m_SogNearDesiredLod = m_SogNearAsmLod = m_SogNearPoolLod = -1;
            if (m_SogStreamer == null || activeCam == null || m_SogManifest == null) return;

            float bestEff = float.MaxValue;
            int bestLeaf = -1;
            for (int i = 0; i < m_SogFrustumLeafScratch.Count; i++)
            {
                int leafIdx = m_SogFrustumLeafScratch[i];
                float eff = m_SogStreamer.ComputeEffectiveDistance(leafIdx, activeCam);
                if (eff < bestEff) { bestEff = eff; bestLeaf = leafIdx; }
            }
            if (bestLeaf < 0) return;

            m_SogNearLeafIdx = bestLeaf;
            m_SogNearStreamLod = m_SogStreamer.GetCurrentLod(bestLeaf);
            m_SogNearDesiredLod = m_SogStreamer.GetLastDesiredLod(bestLeaf);
            var leaf = m_SogManifest.Leaves[bestLeaf];
            m_SogNearAsmLod = SogAssemblyLod(bestLeaf, leaf);
            if (useUnifiedRenderer && m_UnifiedWorld != null && m_UnifiedWorld.lodManager.IsResident(bestLeaf))
                m_SogNearPoolLod = m_UnifiedWorld.lodManager.GetCurrentLod(bestLeaf);
        }

        bool IsSogFrustumLeaf(int leafIdx) => m_SogFrustumLeafSet.Contains(leafIdx);

        /// <summary>Resolve the LOD rank to assemble for a visible leaf (streamer or coarse fallback).</summary>
        int SogAssemblyLod(int leafIdx, SogLeafNode leaf)
        {
            int optimal = m_SogStreamer != null ? m_SogStreamer.GetLastOptimalLod(leafIdx) : 0;
            if (optimal < 0) optimal = 0;
            optimal = Mathf.Clamp(optimal, 0, Math.Max(0, leaf.LodCount - 1));

            if (IsSogFrustumLeaf(leafIdx) || forceMaxQuality || m_SogCoarseBootstrap)
            {
                if (m_SogCoarseBootstrap)
                {
                    int maxLod = Math.Max(0, leaf.LodCount - 1);
                    unsafe
                    {
                        int fi = leaf.LodFileIdx[maxLod];
                        if (fi >= 0 && m_SogChunkLoader.IsResident(fi))
                            return maxLod;
                    }
                    return SogFinestResidentLod(leaf);
                }

                int allowedMax = Mathf.Min(leaf.LodCount - 1, optimal + lodUnderfillLimit);
                unsafe
                {
                    for (int lod = optimal; lod <= allowedMax; lod++)
                    {
                        int fi = leaf.LodFileIdx[lod];
                        if (fi >= 0 && m_SogChunkLoader.IsResident(fi))
                            return lod;
                    }
                    for (int lod = allowedMax; lod >= optimal; lod--)
                    {
                        int fi = leaf.LodFileIdx[lod];
                        if (fi >= 0 && m_SogChunkLoader.IsResident(fi))
                            return lod;
                    }
                    if (leaf.LodCount > 0)
                    {
                        int file0 = leaf.LodFileIdx[0];
                        if (file0 >= 0 && m_SogChunkLoader.IsResident(file0))
                            return 0;
                    }
                }
                int finest = SogFinestResidentLod(leaf);
                if (finest >= 0) return finest;
            }

            int streamLod = m_SogStreamer.GetCurrentLod(leafIdx);
            int finestRes = SogFinestResidentLod(leaf);
            if (streamLod < 0) return finestRes;
            if (finestRes >= 0 && finestRes < streamLod) return finestRes;
            return streamLod;
        }

        /// <summary>Drop pool slots for leaves outside the camera frustum (after bootstrap).</summary>
        bool EvictNonFrustumPoolLeaves(bool sogStreamingFill)
        {
            if (sogStreamingFill || !useUnifiedRenderer || m_UnifiedWorld == null) return false;
            bool evicted = false;
            m_SogEvictScratch.Clear();
            foreach (var slot in m_UnifiedWorld.pool.ActiveSlots())
            {
                if (!IsSogFrustumLeaf(slot.leafId))
                    m_SogEvictScratch.Add(slot.leafId);
            }
            for (int i = 0; i < m_SogEvictScratch.Count; i++)
            {
                m_UnifiedWorld.lodManager.EvictChunk(m_SogEvictScratch[i]);
                evicted = true;
            }
            return evicted;
        }

        void EnqueueSogAssemblyCandidates(int visibleCount)
        {
            if (m_SogCoarseBootstrap)
            {
                int leafN = m_SogManifest.Leaves.Length;
                for (int leafIdx = 0; leafIdx < leafN; leafIdx++)
                {
                    var leaf = m_SogManifest.Leaves[leafIdx];
                    int curLod = SogAssemblyLod(leafIdx, leaf);
                    if (curLod < 0) continue;
                    int fileIdx;
                    unsafe { fileIdx = leaf.LodFileIdx[curLod]; }
                    if (!m_SogChunkLoader.IsResident(fileIdx)) continue;
                    if (m_SogAssemblyPending.Add(leafIdx))
                        m_SogAssemblyQueue.Enqueue(leafIdx);
                }
                return;
            }

            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = m_SogVisibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= m_SogManifest.Leaves.Length) continue;

                var leaf = m_SogManifest.Leaves[leafIdx];
                int curLod = SogAssemblyLod(leafIdx, leaf);
                if (curLod < 0) continue;

                int fileIdx;
                unsafe { fileIdx = leaf.LodFileIdx[curLod]; }
                if (!m_SogChunkLoader.IsResident(fileIdx)) continue;

                int poolLod = -1;
                if (useUnifiedRenderer && m_UnifiedWorld != null && m_UnifiedWorld.lodManager.IsResident(leafIdx))
                    poolLod = m_UnifiedWorld.lodManager.GetCurrentLod(leafIdx);

                if (poolLod >= 0 && curLod >= 0 && curLod < poolLod)
                {
                    if (m_SogAssemblyPending.Add(leafIdx))
                        m_SogAssemblyQueue.Enqueue(leafIdx);
                    continue;
                }

                if (poolLod >= 0 && curLod >= 0 && poolLod == curLod)
                    continue;

                if (m_SogAssemblyPending.Add(leafIdx))
                    m_SogAssemblyQueue.Enqueue(leafIdx);
            }
        }

        /// <summary>True while frustum-visible leaves are still loading into the pool at LOD0.</summary>
        bool SogIsStreamingFill()
        {
            if (!useUnifiedRenderer || m_UnifiedWorld == null || m_SogManifest == null)
                return false;
            for (int i = 0; i < m_SogFrustumLeafScratch.Count; i++)
            {
                int leafIdx = m_SogFrustumLeafScratch[i];
                if (!m_UnifiedWorld.lodManager.IsResident(leafIdx))
                    return true;
                if (m_UnifiedWorld.lodManager.GetCurrentLod(leafIdx) > 0)
                    return true;
            }
            return m_SogAssemblyQueue.Count > 0;
        }

        /// <summary>
        /// Unified SOG: only frustum-visible leaves are streamed/assembled/drawn.
        /// SPZ bootstrap (nearest-first all leaves) is unchanged for non-unified paths.
        /// </summary>
        int BuildSogActiveLeafIndices(float3 camPos, Plane[] planes)
        {
            int leafCount = m_SogManifest.Leaves.Length;
            int frustumCount = SogKdTree.WalkVisibleLeaves(
                m_SogManifest.Leaves, camPos, cam.transform.forward, planes, m_SogVisibleLeafIdx);
            m_SogFrustumVisibleLeaves = frustumCount;
            m_SogFrustumLeafScratch.Clear();
            m_SogFrustumLeafSet.Clear();
            for (int i = 0; i < frustumCount; i++)
            {
                int idx = m_SogVisibleLeafIdx[i];
                m_SogFrustumLeafScratch.Add(idx);
                m_SogFrustumLeafSet.Add(idx);
            }

            return frustumCount;
        }

        void UpdateSog()
        {
            if (m_SogStreamer == null || m_SogChunkLoader == null || m_SogManifest == null)
                return;
            if (!m_SogManifest.Leaves.IsCreated || m_SogManifest.Leaves.Length == 0)
                return;
            if (cam == null) { cam = Camera.main; if (cam == null) return; }

            ApplyRendererPerformanceSettings();

            if (resetKeyEnabled && Input.GetKeyDown(KeyCode.R))
            {
                m_SogAssemblyQueue.Clear();
                m_SogAssemblyPending.Clear();
                m_SogCoarseBootstrap = true;
                m_SogBootstrapStartTime = Time.time;
                m_BudgetScale = 1f;
                m_LastEvalFrame = -9999;
                Debug.Log("[StreamAsync/SOG] reset — clearing assembly backlog, restarting coarse bootstrap");
            }

            if (Input.GetKeyDown(fullQualityToggleKey))
            {
                forceMaxQuality = !forceMaxQuality;
                m_LastEvalFrame = -9999;
                if (m_SogScheduler != null) m_SogScheduler.forceMaxQuality = forceMaxQuality;
                SyncSogStreamerTuning();
            }

            if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                lod0CoverageScale = Mathf.Min(10f, lod0CoverageScale + 1f);
                Debug.Log($"[StreamAsync/SOG] lod0CoverageScale={lod0CoverageScale:F0}");
            }
            if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                lod0CoverageScale = Mathf.Max(1f, lod0CoverageScale - 1f);
                Debug.Log($"[StreamAsync/SOG] lod0CoverageScale={lod0CoverageScale:F0}");
            }

            GeometryUtility.CalculateFrustumPlanes(cam, m_Planes);
            float3 camPos = cam.transform.position;

            int visibleCount = BuildSogActiveLeafIndices(camPos, m_Planes);
            m_SogVisibleLeaves = visibleCount;

            bool camMoved = (cam.transform.position - m_LastCamPos).sqrMagnitude > lodUpdateDistance * lodUpdateDistance;
            int evalInterval = slowMotionDemo ? Mathf.Max(evalEveryNFrames, 30) : Mathf.Max(1, evalEveryNFrames);
            bool doSchedulerEval = (m_Frame - m_LastEvalFrame) >= evalInterval || camMoved || m_SogCoarseBootstrap;

            if (doSchedulerEval)
            {
                m_LastEvalFrame = m_Frame;
                m_LastCamPos = cam.transform.position;

                if (m_SogScheduler != null)
                {
                    m_SogScheduler.lodBaseDistance = lodBaseDistance;
                    m_SogScheduler.lodMultiplier = lodMultiplier;
                    m_SogScheduler.deviceBudget = deviceBudget;
                    m_SogScheduler.forceMaxQuality = forceMaxQuality;
                }

                SogEvaluateAndBudget(visibleCount, cam);

                if (m_SogScheduler != null)
                    m_BudgetScale = m_SogScheduler.budgetScale;
            }

            SyncSogStreamerTuning();

            if (m_SogCoarseBootstrap)
                SogAcquireBootstrapChunks(visibleCount);

            m_SogStreamer.ApplyLodChanges(m_SogVisibleLeafIdx, visibleCount, cam, m_SogBudgetedOptimal);

            float veryNearDist = lodBaseDistance * m_BudgetScale * lod0CoverageScale * veryNearFraction;
            bool hasVeryNearLeaf = forceMaxQuality;
            if (!hasVeryNearLeaf && m_SogStreamer != null && cam != null)
            {
                for (int i = 0; i < m_SogFrustumLeafScratch.Count; i++)
                {
                    if (m_SogStreamer.ComputeEffectiveDistance(m_SogFrustumLeafScratch[i], cam) < veryNearDist)
                    { hasVeryNearLeaf = true; break; }
                }
            }

            // Enqueue frustum-visible leaves for LOD0 assembly; evict off-screen pool slots.
            bool sogStreamingFill = SogIsStreamingFill();
            bool poolDirty = EvictNonFrustumPoolLeaves(sogStreamingFill);

            int residentThisFrame = 0;
            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = m_SogVisibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= m_SogManifest.Leaves.Length) continue;
                var leaf = m_SogManifest.Leaves[leafIdx];
                int curLod = SogAssemblyLod(leafIdx, leaf);
                if (curLod < 0) continue;
                int fileIdx;
                unsafe { fileIdx = leaf.LodFileIdx[curLod]; }
                if (m_SogChunkLoader.IsResident(fileIdx)) residentThisFrame++;
            }
            EnqueueSogAssemblyCandidates(visibleCount);
            m_SogResidentLeaves = residentThisFrame;

            sogStreamingFill = SogIsStreamingFill();
            int asmCap = m_SogCoarseBootstrap
                ? Mathf.Max(32, maxSwapsPerFrame)
                : sogStreamingFill
                ? Mathf.Max(maxSwapsPerFrame, 32)
                : hasVeryNearLeaf
                    ? Mathf.Max(maxSwapsPerFrame, 16)
                    : Mathf.Max(1, maxSwapsPerFrame);
            m_SogAssembliesThisFrame = 0;
            while (m_SogAssembliesThisFrame < asmCap && m_SogAssemblyQueue.Count > 0)
            {
                int leafIdx = m_SogAssemblyQueue.Dequeue();
                m_SogAssemblyPending.Remove(leafIdx);

                if (leafIdx < 0 || leafIdx >= m_SogManifest.Leaves.Length) continue;
                var leaf = m_SogManifest.Leaves[leafIdx];
                int curLod = SogAssemblyLod(leafIdx, leaf);
                if (curLod < 0 || curLod >= leaf.LodCount) continue;

                int fileIdx; int offset; int count;
                unsafe
                {
                    fileIdx = leaf.LodFileIdx[curLod];
                    offset  = leaf.LodOffset[curLod];
                    count   = leaf.LodSplatCount[curLod];
                }
                if (count <= 0) continue;
                if (!m_SogChunkLoader.IsResident(fileIdx))
                {
                    // Raced against a cooldown-driven eviction — put it back for a later frame.
                    if (m_SogAssemblyPending.Add(leafIdx)) m_SogAssemblyQueue.Enqueue(leafIdx);
                    continue;
                }

                // Track C4c: decode -> GpuBufferPool -> unified single-draw renderer.
                try
                {
                    var chunk = m_SogChunkLoader.GetChunkResource(fileIdx);
                    if (chunk == null) continue;

                    var splats = new NativeArray<InputSplatData>(
                        count, Allocator.Persistent, NativeArrayOptions.ClearMemory);
                    try
                    {
                        SogReader.DecodeChunkSliceForStreamer(
                            chunk, offset, count, splats, m_SogManifest.PlayCanvasCoords);
                        m_SogAssembliesTotal++;
                        m_SogAssembliesThisFrame++;

                        if (useUnifiedRenderer && m_UnifiedWorld != null)
                        {
                            var camWorld = cam.transform.position;
                            if (!m_UnifiedWorld.lodManager.RequestChunk(
                                    leafIdx, curLod, splats, camWorld, SogLeafWorldCentre))
                                Debug.LogWarning($"[StreamAsync/SOG] pool full — could not add leaf {leafIdx} ({count} splats)");
                            else
                                poolDirty = true;
                        }
                    }
                    finally
                    {
                        splats.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[StreamAsync/SOG] assemble leaf {leafIdx} lod {curLod} chunk {fileIdx} failed: {ex.Message}");
                }
            }

            if (useUnifiedRenderer && m_UnifiedWorld != null)
            {
                m_ResidentSplats = m_UnifiedWorld.pool.residentSplats;
                m_ResidentChunks = 0;
                foreach (var _ in m_UnifiedWorld.pool.ActiveSlots()) m_ResidentChunks++;

                if (poolDirty || m_UnifiedWorld.pool.needsRebuild)
                    m_UnifiedWorld.SyncRendererFromPoolThrottled(SogIsStreamingFill());
            }

            TryExitSogCoarseBootstrap(visibleCount);

            if (m_SogScheduler != null)
            {
                m_SogScheduler.lodBaseDistance = lodBaseDistance;
                m_SogScheduler.lodMultiplier = lodMultiplier;
                m_SogScheduler.deviceBudget = deviceBudget;
                m_SogScheduler.forceMaxQuality = forceMaxQuality;
            }
            SyncSogStreamerTuning();
            UpdateSogNearLeafDebug(cam);
        }

        void PollLoads()
        {
            m_InFlight = 0;
            // Cap the number of m_Asset SWAPS applied per frame. Each swap triggers the renderer's
            // Update() -> Dispose+Recreate cycle, which cancels-in-flight and rebuilds the octree.
            // If a burst (like the [F] toggle promoting dozens of chunks to LOD0 at once) applies
            // many swaps in one frame, the base octree's parallel sort workers race with the list
            // rebuild and throw ArgumentOutOfRangeException. Throttling to a small handful per frame
            // gives each swap a full frame to settle before the next -> no race.
            int swapsThisFrame = 0;
            // Same cap regardless of mode — force-mode wants to converge fast but the race with the
            // background sort worker is proportional to swap rate. A cap of 2-3 gives ~1 s convergence
            // over 60 fps (plenty fast) and drops the race count to zero in testing.
            int swapCap = maxSwapsPerFrame;
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                if (!c.hasPen) continue;
                if (!c.penH.IsDone) { m_InFlight++; continue; }
                if (swapsThisFrame >= swapCap) { m_InFlight++; continue; }   // defer this swap to next frame
                if (c.penH.Status == AsyncOperationStatus.Succeeded && c.slot >= 0)
                {
                    ApplyLoadedLod(c, c.penH.Result, c.penLevel);
                    if (c.hasCur) SafeRelease(ref c.curH);
                    c.curH = c.penH; c.hasCur = true; c.curLevel = c.penLevel;
                    swapsThisFrame++;
                }
                else if (c.penH.Status == AsyncOperationStatus.Succeeded)
                {
                    // Load finished after chunk was evicted — release without binding.
                    SafeRelease(ref c.penH);
                }
                else
                {
                    // A3: fail-LOUD so a bad Addressables bake shows up in Player.log with the
                    // exact address that failed instead of silently blackholing the chunk.
                    if (c.penH.Status == AsyncOperationStatus.Failed)
                    {
                        string addr = (c.penLevel >= 0 && c.penLevel < c.addr.Length) ? c.addr[c.penLevel] : "?";
                        Debug.LogError($"[StreamAsync] load FAILED chunk={i} lod={c.penLevel} addr={addr} status={c.penH.Status} ex={c.penH.OperationException}");
                    }
                    // Strand-slot fix: release the reserved pool slot so acquire can retry on the
                    // next Evaluate — before this, a failed load permanently pinned m_SlotChunk[slot]
                    // and the chunk stayed invisible forever.
                    if (!m_UseHierarchy && c.slot >= 0)
                    {
                        m_SlotChunk[c.slot] = -1;
                        m_Pool[c.slot].m_Asset = null;
                        if (m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(false);
                        c.slot = -1;
                    }
                    else if (m_UseHierarchy)
                    {
                        c.hierarchyChunk?.ClearResident();
                    }
                    SafeRelease(ref c.penH);
                }
                c.hasPen = false; c.penLevel = -1;
            }
        }

        void ApplyLoadedLod(Chunk c, GaussianSplatAsset asset, int level)
        {
            if (c.hierarchyChunk != null)
                c.hierarchyChunk.ApplyLod(level, asset);
            else if (c.slot >= 0 && m_Pool != null)
            {
                m_Pool[c.slot].m_Asset = asset;
                if (!m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(true);
            }
        }

        void StartLoad(Chunk c, int level)
        {
            if (c.hasPen && c.penLevel == level) return;         // already loading this level
            if (c.hasCur && c.curLevel == level && !c.hasPen) return; // already resident
            if (c.hasPen)
            {
                // Cancel the currently pending load — park it in the cooldown map if it's already
                // resolved (so a re-request can reuse the completed handle without a bundle re-read).
                if (c.penH.IsDone && c.penH.Status == AsyncOperationStatus.Succeeded && c.penLevel >= 0)
                    ParkInCooldown(c.addr[c.penLevel], c.penH);
                else
                    SafeRelease(ref c.penH);
                c.hasPen = false;
            }
            string wantAddr = c.addr[level];
            // B3: cooldown-reuse. If this address is in cooldown, revive the handle instead of
            // asking Addressables for a fresh load.
            if (m_Cooldown.TryGetValue(wantAddr, out var ent) && ent.valid)
            {
                m_Cooldown.Remove(wantAddr);
                c.penH = ent.h;
                c.hasPen = true; c.penLevel = level;
                return;
            }
            c.penH = Addressables.LoadAssetAsync<GaussianSplatAsset>(wantAddr);
            c.hasPen = true; c.penLevel = level;
        }

        void ParkInCooldown(string addr, AsyncOperationHandle<GaussianSplatAsset> h)
        {
            if (string.IsNullOrEmpty(addr)) { SafeRelease(h); return; }
            // If we already have a cooldown entry for this address, release the older one and keep
            // the newer (they refer to the same asset — Addressables refcount treats them equivalently).
            if (m_Cooldown.TryGetValue(addr, out var prev) && prev.valid) SafeRelease(prev.h);
            m_Cooldown[addr] = new CoolEntry { h = h, framesLeft = Mathf.Max(1, cooldownFrames), valid = true };
        }

        readonly List<string> m_CooldownKeys = new List<string>(32);
        void TickCooldown()
        {
            if (m_Cooldown.Count == 0) return;
            m_CooldownExpired.Clear();
            // Snapshot keys FIRST — mutating the dictionary inside a foreach on itself throws
            // InvalidOperationException: Collection was modified.
            m_CooldownKeys.Clear();
            foreach (var k in m_Cooldown.Keys) m_CooldownKeys.Add(k);
            for (int i = 0; i < m_CooldownKeys.Count; i++)
            {
                var key = m_CooldownKeys[i];
                var e = m_Cooldown[key];
                e.framesLeft--;
                if (e.framesLeft <= 0) m_CooldownExpired.Add(key);
                else m_Cooldown[key] = e;
            }
            for (int i = 0; i < m_CooldownExpired.Count; i++)
            {
                if (m_Cooldown.TryGetValue(m_CooldownExpired[i], out var e) && e.valid) SafeRelease(e.h);
                m_Cooldown.Remove(m_CooldownExpired[i]);
            }
        }

        // B1: pick the finest resident level within lodUnderfillLimit steps of the optimal target.
        // Only returns curLevel if it's within tolerance; caller keeps current binding in that case.
        // Returns -1 if no acceptable fallback is resident (caller decides whether to hide).
        // Currently informational — the never-drop-visible logic in PollLoads already keeps a
        // coarser resident visible while a finer level loads. This method exposes the same intent
        // to external callers (e.g. RendererMarkerRecorder) for diagnostics.
        public int SelectUnderfillLevel_Diag(int chunkIndex, int optimal)
        {
            if (chunkIndex < 0 || chunkIndex >= m_Chunks.Count) return -1;
            var c = m_Chunks[chunkIndex];
            if (!c.hasCur || c.curLevel < 0) return -1;
            int diff = c.curLevel - optimal;
            if (diff < 0) return c.curLevel;
            if (diff <= lodUnderfillLimit) return c.curLevel;
            return -1;
        }

        void SetupEnvTier(LodManifest man)
        {
            if (!enableEnv) return;
            // v2 path: environment.files[] can hold multiple env assets (curated far background).
            // v1 path: single top-level envFile string. In both cases the pool sizes to the count,
            // is not bounded by maxResidentChunks, and each asset stays resident forever.
            List<string> envAddrs = new List<string>(4);
            if (man.version >= 2 && man.environment != null && man.environment.files != null && man.environment.files.Length > 0)
            {
                for (int i = 0; i < man.environment.files.Length; i++)
                {
                    string a = null;
                    if (man.environment.fileIdx != null && i < man.environment.fileIdx.Length
                        && man.filenames != null && man.environment.fileIdx[i] < man.filenames.Length)
                        a = Path.GetFileNameWithoutExtension(man.filenames[man.environment.fileIdx[i]]);
                    else
                        a = Path.GetFileNameWithoutExtension(man.environment.files[i]);
                    if (!string.IsNullOrEmpty(a)) envAddrs.Add(a);
                }
            }
            else if (!string.IsNullOrEmpty(man.envFile))
            {
                envAddrs.Add(Path.GetFileNameWithoutExtension(man.envFile));
            }
            if (envAddrs.Count == 0) return;

            m_EnvChunkCount = envAddrs.Count;
            m_EnvPool = new GaussianSplatRenderer[m_EnvChunkCount];
            m_EnvH2 = new AsyncOperationHandle<GaussianSplatAsset>[m_EnvChunkCount];
            m_EnvH2Valid = new bool[m_EnvChunkCount];

            for (int i = 0; i < m_EnvChunkCount; i++)
            {
                var go = new GameObject("Env_" + i); go.SetActive(false); go.transform.SetParent(transform, false);
                m_EnvPool[i] = go.AddComponent<GaussianSplatRenderer>();
                int idx = i;
                m_EnvH2[i] = Addressables.LoadAssetAsync<GaussianSplatAsset>(envAddrs[i]);
                m_EnvH2Valid[i] = true;
                m_EnvH2[i].Completed += op =>
                {
                    if (op.Status == AsyncOperationStatus.Succeeded && m_EnvPool != null && idx < m_EnvPool.Length && m_EnvPool[idx] != null)
                    {
                        m_EnvPool[idx].m_Asset = op.Result;
                        m_EnvResidentSplats += op.Result.splatCount;
                        m_EnvPool[idx].gameObject.SetActive(true);
                    }
                    else if (op.Status == AsyncOperationStatus.Failed)
                    {
                        Debug.LogError($"[StreamAsync] env asset load FAILED idx={idx} ex={op.OperationException}");
                    }
                };
            }
            // Legacy m_HasEnvH stays false — the new env tier owns the accounting.
            Debug.Log($"[StreamAsync] env tier: {m_EnvChunkCount} always-resident asset(s) queued");
        }

        void Evaluate()
        {
            m_Eval++;
            GeometryUtility.CalculateFrustumPlanes(cam, m_Planes);
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;                                // P0(a): behind-camera penalty
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float fovScale = Mathf.Min(tanV, tanV * cam.aspect) / kRefTanHalfFov;
            float baseDist = Mathf.Max(0.01f, lodBaseDistance * m_BudgetScale);
            float maxDist = 0.001f;

            m_VisSorted.Clear();
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i]; int K1 = c.addr.Length - 1;
                Vector3 wc = transform.TransformPoint(c.localCentre);
                var wb = new Bounds(wc, Vector3.Scale(c.localSize, transform.lossyScale));
                c.visible = GeometryUtility.TestPlanesAABB(m_Planes, wb);
                c.dist = Mathf.Sqrt(wb.SqrDistance(camPos));
                if (c.dist > maxDist) maxDist = c.dist;
                if (!c.visible) { c.optimal = K1; continue; }
                if (forceMaxQuality) { c.optimal = 0; m_VisSorted.Add(i); continue; }   // [F]-toggle: pin visible to LOD0

                // P0(a) SuperSplat-parity: BEHIND-CAMERA / PERIPHERAL DEMOTION. Chunks whose centre is
                // behind the camera plane (visible only because their AABB overshoots the frustum) pay
                // a distance penalty so they pick a coarser LOD. Frees 3-8% of the splat budget on Aura
                // and cleans up cheap-visibility chunks (e.g. behind an arch that's just barely in view).
                Vector3 toChunk = wc - camPos;
                float invLen = 1f / Mathf.Max(c.dist, 0.001f);
                float behindT = Mathf.Max(0f, -Vector3.Dot(camFwd, toChunk * invLen));  // 0 = in front, 1 = directly behind
                float effDist = c.dist * fovScale * (1f + behindT * (lodBehindPenalty - 1f));
                int lv = 0; float thr = baseDist;
                while (lv < K1 && effDist >= thr) { thr *= lodMultiplier; lv++; }
                c.optimal = lv; m_VisSorted.Add(i);
            }
            m_VisibleChunks = m_VisSorted.Count;
            m_VisSorted.Sort((a, b) => m_Chunks[a].dist.CompareTo(m_Chunks[b].dist));

            // B5: env-tier splats are a fixed always-resident floor, tracked separately from
            // the LOD budget balancer. The balancer's ceiling is (deviceBudget - envFloor) so
            // LOD chunks never fight the env tier for slots.
            long envFloor = (m_HasEnvH ? m_EnvCount : 0) + m_EnvResidentSplats;
            long lodBudget = (deviceBudget > 0) ? System.Math.Max(1, (long)deviceBudget - envFloor) : 0;
            long total = 0;
            for (int i = 0; i < m_Chunks.Count; i++) m_Chunks[i].desired = m_Chunks[i].optimal;
            for (int k = 0; k < m_VisSorted.Count; k++) { var c = m_Chunks[m_VisSorted[k]]; total += c.splatCount[c.desired]; }
            if (lodBudget > 0 && total > lodBudget && !forceMaxQuality)
            {
                for (int b = 0; b < kBuckets; b++) m_Bucket[b].Clear();
                float invMax = (kBuckets - 1) / Mathf.Sqrt(maxDist);
                for (int k = 0; k < m_VisSorted.Count; k++) { int i = m_VisSorted[k]; m_Bucket[Mathf.Clamp((int)(Mathf.Sqrt(m_Chunks[i].dist) * invMax), 0, kBuckets - 1)].Add(i); }
                int guard = m_VisSorted.Count * 8; bool moved = true;
                while (total > lodBudget && moved && guard-- > 0)
                {
                    moved = false;
                    for (int b = kBuckets - 1; b >= 0 && total > lodBudget; b--)
                        foreach (int i in m_Bucket[b]) { var c = m_Chunks[i]; int K1 = c.addr.Length - 1; if (c.desired < K1) { total += c.splatCount[c.desired + 1] - c.splatCount[c.desired]; c.desired++; moved = true; if (total <= lodBudget) break; } }
                }
            }

            int wantCount = Mathf.Min(maxResidentChunks, m_VisSorted.Count);
            var wanted = new HashSet<int>();
            for (int k = 0; k < wantCount; k++) { int i = m_VisSorted[k]; wanted.Add(i); m_Chunks[i].lastWantedEval = m_Eval; }

            if (m_UseHierarchy)
            {
                for (int i = 0; i < m_Chunks.Count; i++)
                {
                    var c = m_Chunks[i];
                    if (c.slot >= 0 && !wanted.Contains(i) && (m_Eval - c.lastWantedEval) > cooldownEvals)
                        ReleaseChunk(i);
                }
            }
            else
            {
                for (int s = 0; s < m_Pool.Length; s++)
                {
                    int ci = m_SlotChunk[s];
                    if (ci >= 0 && !wanted.Contains(ci) && (m_Eval - m_Chunks[ci].lastWantedEval) > cooldownEvals)
                        ReleaseSlot(s);
                }
            }

            int concurrentCap = slowMotionDemo ? 1
                                : (forceMaxQuality ? Mathf.Max(maxConcurrentLoads, forceQualityConcurrentLoads)
                                                   : maxConcurrentLoads);
            // acquire for wanted-not-resident (nearest first); coarse-first load.
            for (int k = 0; k < wantCount; k++)
            {
                if (m_InFlight >= concurrentCap) break;
                int i = m_VisSorted[k]; var c = m_Chunks[i];
                if (c.slot >= 0) continue;
                if (!TryAcquireChunk(i)) continue;
                bool veryNear = veryNearFraction > 0f && c.dist < lodBaseDistance * veryNearFraction;
                int initialLevel = (forceMaxQuality || veryNear) ? c.desired
                                 : ((coarseFirst || stagedPrefetch) ? c.addr.Length - 1 : c.desired);
                StartLoad(c, initialLevel); m_InFlight++;
            }
            // refine resident chunks one level toward desired (nearest first, throttled)
            for (int k = 0; k < m_VisSorted.Count && m_InFlight < concurrentCap; k++)
            {
                var c = m_Chunks[m_VisSorted[k]];
                if (c.slot < 0 || !c.hasCur) continue;
                if (c.curLevel != c.desired && !(c.hasPen))
                {
                    // Force-max-quality jumps STRAIGHT to the target LOD (skipping intermediates) so the
                    // [F] toggle snaps to full quality within a few evals instead of 4-5 progressive steps.
                    // Normal mode refines one level at a time for smooth streaming.
                    int step = forceMaxQuality ? c.desired
                             : (c.curLevel > c.desired ? c.curLevel - 1 : c.curLevel + 1);
                    StartLoad(c, step); m_InFlight++;
                }
            }

            int resident = (int)envFloor; int rc = 0;
            for (int i = 0; i < m_Chunks.Count; i++) { var c = m_Chunks[i]; if (c.slot >= 0 && c.hasCur) { resident += c.splatCount[c.curLevel]; rc++; } }
            m_ResidentSplats = resident; m_ResidentChunks = rc;
            if (lodBudget > 0)
            {
                float ratio = (float)total / lodBudget;
                if (ratio < 1f - kBudgetDeadZone || ratio > 1f + kBudgetDeadZone)
                { float target = 1f / Mathf.Sqrt(Mathf.Max(ratio, 1e-3f)); m_BudgetScale = Mathf.Clamp(m_BudgetScale * (1f + (target - 1f) * kBudgetBlend), 0.05f, 1f); }
            }
        }

        int CountAcquiredChunks()
        {
            int n = 0;
            foreach (var c in m_Chunks) if (c.slot >= 0) n++;
            return n;
        }

        bool TryAcquireChunk(int chunkIndex)
        {
            if (m_UseHierarchy)
            {
                if (CountAcquiredChunks() < maxResidentChunks)
                {
                    m_Chunks[chunkIndex].slot = 0;
                    return true;
                }
                // Evict farthest acquired chunk that is not in the wanted set this frame.
                int worst = -1; float worstDist = m_Chunks[chunkIndex].dist;
                for (int i = 0; i < m_Chunks.Count; i++)
                {
                    var c = m_Chunks[i];
                    if (c.slot >= 0 && c.dist > worstDist) { worstDist = c.dist; worst = i; }
                }
                if (worst >= 0) { ReleaseChunk(worst); m_Chunks[chunkIndex].slot = 0; return true; }
                return false;
            }

            int free = FindFreeOrEvictableSlot(chunkIndex);
            if (free < 0) return false;
            m_SlotChunk[free] = chunkIndex;
            m_Chunks[chunkIndex].slot = free;
            return true;
        }

        void ReleaseChunk(int chunkIndex)
        {
            var c = m_Chunks[chunkIndex];
            if (c.hasPen)
            {
                if (cooldownFrames > 0 && c.penH.IsDone && c.penH.Status == AsyncOperationStatus.Succeeded && c.penLevel >= 0)
                    ParkInCooldown(c.addr[c.penLevel], c.penH);
                else
                    SafeRelease(ref c.penH);
                c.hasPen = false; c.penLevel = -1;
            }
            if (c.hasCur)
            {
                if (cooldownFrames > 0 && c.curLevel >= 0 && c.curLevel < c.addr.Length)
                    ParkInCooldown(c.addr[c.curLevel], c.curH);
                else
                    SafeRelease(ref c.curH);
                c.hasCur = false;
            }
            c.hierarchyChunk?.ClearResident();
            c.slot = -1; c.curLevel = -1;
        }

        int FindFreeOrEvictableSlot(int wantChunk)
        {
            for (int s = 0; s < m_Pool.Length; s++) if (m_SlotChunk[s] < 0) return s;
            int worst = -1; float worstDist = m_Chunks[wantChunk].dist;
            for (int s = 0; s < m_Pool.Length; s++) { int ci = m_SlotChunk[s]; if (ci >= 0 && m_Chunks[ci].dist > worstDist) { worstDist = m_Chunks[ci].dist; worst = s; } }
            if (worst >= 0) { ReleaseSlot(worst); return worst; }
            return -1;
        }

        void ReleaseSlot(int slot)
        {
            int ci = m_SlotChunk[slot];
            if (ci >= 0)
            {
                var c = m_Chunks[ci];
                // B3: park pending + current handles in cooldown so a rapid dither-back reuses them
                // (matches PlayCanvas's cooldownTicks behavior). Immediate release only when the
                // cooldown map is disabled.
                if (c.hasPen)
                {
                    if (cooldownFrames > 0 && c.penH.IsDone && c.penH.Status == AsyncOperationStatus.Succeeded && c.penLevel >= 0)
                        ParkInCooldown(c.addr[c.penLevel], c.penH);
                    else
                        SafeRelease(ref c.penH);
                    c.hasPen = false; c.penLevel = -1;
                }
                if (c.hasCur)
                {
                    if (cooldownFrames > 0 && c.curLevel >= 0 && c.curLevel < c.addr.Length)
                        ParkInCooldown(c.addr[c.curLevel], c.curH);
                    else
                        SafeRelease(ref c.curH);
                    c.hasCur = false;
                }
                c.slot = -1; c.curLevel = -1;
            }
            m_SlotChunk[slot] = -1;
            m_Pool[slot].m_Asset = null;
            if (m_Pool[slot].gameObject.activeSelf) m_Pool[slot].gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            // Track C4: SOG teardown. Loader disposes its resident SogChunkResource entries;
            // manifest disposes the persistent NativeArray<SogLeafNode> backing the flat tree.
            m_SogStreamer?.Dispose(); m_SogStreamer = null;
            m_SogChunkLoader?.Dispose(); m_SogChunkLoader = null;
            // Track C4b: the per-frame visibility scratch buffer is owned by this component.
            if (m_SogVisibleLeafIdx.IsCreated) m_SogVisibleLeafIdx.Dispose();
            m_SogVisibleLeafIdx = default;
            m_SogAssemblyQueue.Clear();
            m_SogAssemblyPending.Clear();
            if (m_ChunkReader is SogChunkReader scr) { scr.Dispose(); m_SogManifest = null; }
            else m_SogManifest?.Dispose();
            m_SogManifest = null;
            m_ChunkReader = null;

            if (m_Chunks != null)
                foreach (var c in m_Chunks)
                {
                    if (c.hasPen) SafeRelease(ref c.penH);
                    if (c.hasCur) SafeRelease(ref c.curH);
                }
            if (m_HasEnvH) SafeRelease(ref m_EnvH);
            // B5: release env-tier handles (never released at runtime — cleaned only on teardown).
            if (m_EnvH2 != null)
            {
                for (int i = 0; i < m_EnvH2.Length; i++)
                {
                    if (m_EnvH2Valid != null && m_EnvH2Valid[i])
                    {
                        SafeRelease(ref m_EnvH2[i]);
                        m_EnvH2Valid[i] = false;
                    }
                }
            }
            // B3: drain cooldown map
            foreach (var kv in m_Cooldown) if (kv.Value.valid) SafeRelease(kv.Value.h);
            m_Cooldown.Clear();
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 17 }; style.normal.textColor = Color.white;
            var sb = new StringBuilder();
            // Track C4b: SOG has its own accounting — different from the SPZ Addressables HUD
            // (no Addressables handles, no pool slots, no env tier). Show the numbers that
            // the streamer actually tracks so play-verify has a live signal.
            if (resolvedFormat == StreamFormat.Sog)
            {
                int leaves = m_SogManifest != null && m_SogManifest.Leaves.IsCreated
                    ? m_SogManifest.Leaves.Length : 0;
                int tracked = m_SogChunkLoader != null ? m_SogChunkLoader.TrackedChunkCount : 0;
                int draws = GaussianSplatUnifiedWorld.IsUnifiedDrawReady ? 1 : 0;
                int drawnSplats = 0;
                if (m_UnifiedWorld != null && m_UnifiedWorld.renderer != null && m_UnifiedWorld.renderer.octree != null)
                    drawnSplats = m_UnifiedWorld.renderer.octree.visibleSplatCount;
                var gs = GaussianSplatSettings.instance;
                bool screenLod = gs != null && gs.m_EnableScreenLod;
                int poolLod0 = 0, poolLodCoarse = 0;
                if (m_UnifiedWorld != null)
                {
                    foreach (var slot in m_UnifiedWorld.pool.ActiveSlots())
                    {
                        if (slot.lodLevel == 0) poolLod0++;
                        else poolLodCoarse++;
                    }
                }
                string sortMode = GaussianSplatOctree.lastGlobalSortMode;
                sb.AppendLine($"GaussianLodStreamAsync (SOG unified)  budget={deviceBudget / 1000}K  budgetScale={m_BudgetScale:F2}  " +
                              $"bootstrap={(m_SogCoarseBootstrap ? "ON" : "off")}  " +
                              $"resident={m_ResidentSplats / 1000}K  drawn={drawnSplats / 1000}K  slots={m_ResidentChunks}/{leaves}  " +
                              $"poolLod0={poolLod0}  poolCoarse={poolLodCoarse}  screenLod={screenLod}  " +
                              $"active={m_SogVisibleLeaves}  frustum={m_SogFrustumVisibleLeaves}  draws={draws}  " +
                              $"lod0Scale={lod0CoverageScale:F0}  nearLod={m_SogNearStreamLod}/{m_SogNearDesiredLod}/{m_SogNearAsmLod}/{m_SogNearPoolLod}  " +
                              $"sortMode={sortMode}  asm/frame={m_SogAssembliesThisFrame}  queue={m_SogAssemblyQueue.Count}  " +
                              $"[F]=maxQ  [+/-]=lod0Scale");
                GUI.Label(new Rect(12, 10, 1600, 80), sb.ToString(), style);
                return;
            }
            sb.AppendLine($"GaussianLodStreamAsync (Addressables{(m_UseHierarchy ? ", hierarchy" : "")})  resident={m_ResidentSplats / 1000}K / budget={deviceBudget / 1000}K  slots={m_ResidentChunks}/{maxResidentChunks} of {m_Chunks.Count}  visible={m_VisibleChunks}");
            int pending = 0; int noResident = 0;
            foreach (var c in m_Chunks) { if (c.hasPen) pending++; if (c.visible && !c.hasCur) noResident++; }
            string modeTag = forceMaxQuality ? "★ FULL QUALITY (all LOD0)"
                             : slowMotionDemo ? "SLOW-MO demo — [R] reset"
                             : "streaming — [F] toggle full quality, [R] reset";
            int envK = (m_EnvCount + m_EnvResidentSplats) / 1000;
            sb.AppendLine($"streaming: inFlight={pending}  cooldown={m_Cooldown.Count}  noResident(visible)={noResident}  budgetScale={m_BudgetScale:F2}  env={(enableEnv ? envK + "K (" + m_EnvChunkCount + ")" : "off")}   {modeTag}");

            // Per-LOD histogram (columns: LOD0 fine .. LODn coarse) — WATCH chunks climb from coarse
            // to fine as SuperSplat's "progressive refinement" streams in.
            int maxLod = 0;
            foreach (var c in m_Chunks) if (c.addr != null && c.addr.Length > maxLod) maxLod = c.addr.Length;
            var counts = new int[maxLod];
            foreach (var c in m_Chunks) if (c.hasCur && c.curLevel >= 0 && c.curLevel < maxLod) counts[c.curLevel]++;
            sb.Append("chunks per LOD:  ");
            for (int i = 0; i < maxLod; i++) sb.Append($"L{i}={counts[i]}  ");
            GUI.Label(new Rect(12, 10, 1600, 140), sb.ToString(), style);
        }
    }
}
