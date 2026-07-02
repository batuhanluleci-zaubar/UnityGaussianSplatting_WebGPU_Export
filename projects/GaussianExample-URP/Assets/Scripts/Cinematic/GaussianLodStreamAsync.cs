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
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using GaussianSplatting.Runtime;

namespace GsplatLod
{
    public class GaussianLodStreamAsync : MonoBehaviour
    {
        [Header("Source")]
        // Relative path resolved under Application.streamingAssetsPath first; falls back to the
        // absolute dev-machine path via LodManifestResolver when running in the editor.
        public string manifestPath = "gsplat_lod/uhq/manifest.json";
        public Camera cam;
        public bool enableEnv = false;

        [Header("Streaming working set")]
        public int maxResidentChunks = 64;
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
        [Tooltip("Desktop generous 3-4M with RAW LOD0 (top LOD = original chunk splats, no merge). Adreno / Android XR " +
                 "~1M. Drives how much detail the balancer allows resident.")]
        public int deviceBudget = 3_500_000;

        [Header("Screen-error LOD bands")]
        [Tooltip("World-distance threshold where LOD steps from 0->1. Larger = more of the scene stays LOD0 (finer). " +
                 "Roughly tune to (max chunk world extent) * 2..3. For ~5-6m chunks (32ch on Festsaal) use ~20-25; for " +
                 "~10m chunks (16ch) use ~12-15. Retune when you re-bake with different --chunks.")]
        public float lodBaseDistance = 15f;
        [Tooltip("Distance multiplier per LOD band (2-3). Larger = wider bands = sharper transitions but bigger jumps.")]
        public float lodMultiplier = 2.0f;
        [Tooltip("P0(a) SuperSplat-parity behind-camera penalty. A chunk whose centre is directly behind " +
                 "the camera (barely-visible via AABB overshoot) has its effective distance multiplied by this " +
                 "factor -> picks a coarser LOD. 5 = 5x demotion for straight-behind; 1 = disabled.")]
        [Range(1f, 20f)] public float lodBehindPenalty = 5f;

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

        [Header("Full-quality shortcut")]
        [Tooltip("Press this key in play mode to toggle FORCE-MAX-QUALITY: every visible chunk is pinned to " +
                 "LOD0 (raw/finest), budget balancer is bypassed. Great A/B toggle to compare streamed vs " +
                 "full asset quality. Chunks stream up over the next few evals; press again to release.")]
        public KeyCode fullQualityToggleKey = KeyCode.F;
        [Tooltip("Current state (also settable via inspector).")]
        public bool forceMaxQuality = false;
        [Tooltip("When force-max-quality is ON, the concurrency cap is raised so chunks refine to LOD0 faster.")]
        [Range(1, 32)] public int forceQualityConcurrentLoads = 16;

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
            public int slot = -1;
            public int curLevel = -1;        // level currently shown
            public AsyncOperationHandle<GaussianSplatAsset> curH; public bool hasCur;
            public AsyncOperationHandle<GaussianSplatAsset> penH; public bool hasPen; public int penLevel = -1;
            public int lastWantedEval = -9999;
        }

        readonly List<Chunk> m_Chunks = new List<Chunk>();
        GaussianSplatRenderer[] m_Pool;
        int[] m_SlotChunk;
        GaussianSplatRenderer m_Env; AsyncOperationHandle<GaussianSplatAsset> m_EnvH; bool m_HasEnvH; int m_EnvCount;

        int m_Frame, m_Eval, m_LastEvalFrame = -9999, m_ResidentSplats, m_ResidentChunks, m_VisibleChunks, m_InFlight;
        float m_BudgetScale = 1f;
        Vector3 m_LastCamPos = Vector3.positiveInfinity, m_SceneCentre; float m_SceneRadius;

        // Public accessors for external tools (e.g. SplineAutoFromGsplat that fits a Cinemachine
        // tour spline around whatever gsplat scene the streamer loaded).
        public Vector3 SceneCentre => m_SceneCentre;
        public float SceneRadius => m_SceneRadius;
        public bool BoundsReady => m_SceneRadius > 0f && m_Chunks.Count > 0;
        readonly Plane[] m_Planes = new Plane[6];
        readonly List<int>[] m_Bucket = new List<int>[kBuckets];
        readonly List<int> m_VisSorted = new List<int>();

        // B3: cooldown map for delayed Addressables.Release. Keyed by address string so the same
        // (chunk,LOD) can be reused across evict/re-request cycles without a bundle reload.
        struct CoolEntry { public AsyncOperationHandle<GaussianSplatAsset> h; public int framesLeft; public bool valid; }
        readonly Dictionary<string, CoolEntry> m_Cooldown = new Dictionary<string, CoolEntry>();
        readonly List<string> m_CooldownExpired = new List<string>(16);

        void Start()
        {
            if (cam == null) cam = Camera.main;
            for (int i = 0; i < kBuckets; i++) m_Bucket[i] = new List<int>(32);
            var settings = GaussianSplatSettings.instance;
            if (settings != null)
            {
                settings.m_EnableOctreeCulling = true;
                // GAP #2 fix: enable per-node LOD stride ON TOP of our per-chunk asset swap. Chunks with
                // nodes projecting below m_LodFullDetailPixels px get sub-sampled inside the renderer's
                // own octree. Composes with our screen-error LOD (which picks a whole pre-merged asset)
                // for a two-level "chunk picks its asset, nodes within stride if far". Keep budget=0 —
                // our 64-bucket balancer handles totals at chunk granularity.
                settings.m_EnableScreenLod = true;
                settings.m_LodFullDetailPixels = 250f;
                settings.m_LodSplatBudget = 0;
            }

            manifestPath = LodManifestResolver.Resolve(manifestPath, "[StreamAsync]");
            if (manifestPath == null) { enabled = false; return; }
            var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath));
            if (man == null || man.chunks == null) { Debug.LogError("[StreamAsync] manifest parse failed"); enabled = false; return; }
            Debug.Log($"[StreamAsync] manifest loaded: {man.chunks.Length} chunks from '{manifestPath}'");
            if (lodMultiplier < 1.05f) lodMultiplier = 1.05f;

            Vector3 mn = Vector3.one * 1e9f, mx = -mn;
            foreach (var cm in man.chunks)
            {
                var c = new Chunk { meta = cm };
                var bmin = new Vector3(cm.boundMin[0], cm.boundMin[1], cm.boundMin[2]);
                var bmax = new Vector3(cm.boundMax[0], cm.boundMax[1], cm.boundMax[2]);
                c.localCentre = (bmin + bmax) * 0.5f; c.localSize = bmax - bmin;
                c.addr = new string[cm.lods.Length]; c.splatCount = new int[cm.lods.Length];
                for (int L = 0; L < cm.lods.Length; L++) { c.addr[L] = Path.GetFileNameWithoutExtension(cm.lods[L].file); c.splatCount[L] = cm.lods[L].splatCount; }
                mn = Vector3.Min(mn, bmin); mx = Vector3.Max(mx, bmax);
                m_Chunks.Add(c);
            }

            int poolN = Mathf.Clamp(maxResidentChunks, 1, m_Chunks.Count);
            maxResidentChunks = poolN;
            m_Pool = new GaussianSplatRenderer[poolN]; m_SlotChunk = new int[poolN];
            for (int i = 0; i < poolN; i++)
            {
                var go = new GameObject("Slot_" + i); go.SetActive(false); go.transform.SetParent(transform, false);
                m_Pool[i] = go.AddComponent<GaussianSplatRenderer>(); m_SlotChunk[i] = -1;
            }

            if (enableEnv && !string.IsNullOrEmpty(man.envFile))
            {
                var envGo = new GameObject("Env"); envGo.SetActive(false); envGo.transform.SetParent(transform, false);
                m_Env = envGo.AddComponent<GaussianSplatRenderer>();
                m_EnvH = Addressables.LoadAssetAsync<GaussianSplatAsset>(Path.GetFileNameWithoutExtension(man.envFile)); m_HasEnvH = true;
                m_EnvH.Completed += op => { if (op.Status == AsyncOperationStatus.Succeeded && m_Env != null) { m_Env.m_Asset = op.Result; m_EnvCount = op.Result.splatCount; m_Env.gameObject.SetActive(true); } };
            }

            m_SceneCentre = (mn + mx) * 0.5f; m_SceneRadius = (mx - mn).magnitude * 0.5f;
            // Auto-tune the LOD bands to the scene scale ONLY IF the user left it at the default 15
            // (a small number that would push everything to coarsest in a large scene). If the user
            // explicitly set anything else in the inspector, respect it — that's how you get to
            // "10M source, near = raw, mid = LOD1, far = LOD2, ~1-1.5M rendered/frame, hits 120 FPS".
            // Rule of thumb for 120 FPS on desktop: lodBaseDistance ~= chunk world extent * 1.5-2
            // (so ~5-8m for 5m chunks). For quality-first: lodBaseDistance ~= sceneRadius (=~30m).
            if (lodBaseDistance <= 15.5f) lodBaseDistance = m_SceneRadius * 1.2f;
            if (autoFrameCamera && cam != null)
            {
                // Pull the camera IN to a normal framing (~1.3 x radius, close enough that near chunks pick
                // LOD0/1). Old value 2.2 x placed it far outside the natural viewing distance.
                cam.transform.position = transform.TransformPoint(m_SceneCentre + new Vector3(0f, 0.15f * m_SceneRadius, -1.3f * m_SceneRadius));
                cam.transform.LookAt(transform.TransformPoint(m_SceneCentre));
                cam.nearClipPlane = 0.05f; cam.farClipPlane = Mathf.Max(cam.farClipPlane, m_SceneRadius * 12f);
            }
            Debug.Log($"[StreamAsync] {m_Chunks.Count} chunks, pool={poolN}, budget={deviceBudget}, radius={m_SceneRadius:F1}m, lodBaseDistance={lodBaseDistance:F1}m (Addressables async)");
        }

        void Update()
        {
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
                    if (c.hasPen) { Addressables.Release(c.penH); c.hasPen = false; c.penLevel = -1; }
                    if (c.hasCur) { Addressables.Release(c.curH); c.hasCur = false; }
                    if (c.slot >= 0)
                    {
                        m_Pool[c.slot].m_Asset = null;
                        if (m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(false);
                        m_SlotChunk[c.slot] = -1;
                    }
                    c.slot = -1; c.curLevel = -1; c.lastWantedEval = -9999;
                }
                m_ResidentSplats = 0; m_ResidentChunks = 0; m_LastEvalFrame = -9999;
                m_BudgetScale = 1f;
                // Drain cooldown handles too so the reset truly restarts from zero.
                foreach (var kv in m_Cooldown) if (kv.Value.valid) Addressables.Release(kv.Value.h);
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
                    m_Pool[c.slot].m_Asset = c.penH.Result;
                    if (!m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(true);
                    if (c.hasCur) Addressables.Release(c.curH);
                    c.curH = c.penH; c.hasCur = true; c.curLevel = c.penLevel;
                    swapsThisFrame++;
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
                    if (c.slot >= 0)
                    {
                        m_SlotChunk[c.slot] = -1;
                        m_Pool[c.slot].m_Asset = null;
                        if (m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(false);
                        c.slot = -1;
                    }
                    Addressables.Release(c.penH);
                }
                c.hasPen = false; c.penLevel = -1;
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
                    Addressables.Release(c.penH);
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
            if (string.IsNullOrEmpty(addr)) { Addressables.Release(h); return; }
            // If we already have a cooldown entry for this address, release the older one and keep
            // the newer (they refer to the same asset — Addressables refcount treats them equivalently).
            if (m_Cooldown.TryGetValue(addr, out var prev) && prev.valid) Addressables.Release(prev.h);
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
                if (m_Cooldown.TryGetValue(m_CooldownExpired[i], out var e) && e.valid) Addressables.Release(e.h);
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

            long total = m_HasEnvH ? m_EnvCount : 0;
            for (int i = 0; i < m_Chunks.Count; i++) m_Chunks[i].desired = m_Chunks[i].optimal;
            for (int k = 0; k < m_VisSorted.Count; k++) { var c = m_Chunks[m_VisSorted[k]]; total += c.splatCount[c.desired]; }
            if (deviceBudget > 0 && total > deviceBudget && !forceMaxQuality)
            {
                for (int b = 0; b < kBuckets; b++) m_Bucket[b].Clear();
                float invMax = (kBuckets - 1) / Mathf.Sqrt(maxDist);
                for (int k = 0; k < m_VisSorted.Count; k++) { int i = m_VisSorted[k]; m_Bucket[Mathf.Clamp((int)(Mathf.Sqrt(m_Chunks[i].dist) * invMax), 0, kBuckets - 1)].Add(i); }
                int guard = m_VisSorted.Count * 8; bool moved = true;
                while (total > deviceBudget && moved && guard-- > 0)
                {
                    moved = false;
                    for (int b = kBuckets - 1; b >= 0 && total > deviceBudget; b--)
                        foreach (int i in m_Bucket[b]) { var c = m_Chunks[i]; int K1 = c.addr.Length - 1; if (c.desired < K1) { total += c.splatCount[c.desired + 1] - c.splatCount[c.desired]; c.desired++; moved = true; if (total <= deviceBudget) break; } }
                }
            }

            int wantCount = Mathf.Min(maxResidentChunks, m_VisSorted.Count);
            var wanted = new HashSet<int>();
            for (int k = 0; k < wantCount; k++) { int i = m_VisSorted[k]; wanted.Add(i); m_Chunks[i].lastWantedEval = m_Eval; }

            for (int s = 0; s < m_Pool.Length; s++)
            {
                int ci = m_SlotChunk[s];
                if (ci >= 0 && !wanted.Contains(ci) && (m_Eval - m_Chunks[ci].lastWantedEval) > cooldownEvals) ReleaseSlot(s);
            }

            int concurrentCap = slowMotionDemo ? 1
                                : (forceMaxQuality ? Mathf.Max(maxConcurrentLoads, forceQualityConcurrentLoads)
                                                   : maxConcurrentLoads);
            // acquire slots for wanted-not-resident (nearest first); coarse-first load.
            // Gate slot assignment on the load actually starting, so a chunk never gets a slot
            // without a pending load (that would strand it — acquire skips slot>=0, refine needs hasCur).
            for (int k = 0; k < wantCount; k++)
            {
                if (m_InFlight >= concurrentCap) break;   // over concurrency cap -> retry next eval
                int i = m_VisSorted[k]; var c = m_Chunks[i];
                if (c.slot >= 0) continue;
                int free = FindFreeOrEvictableSlot(i);
                if (free < 0) continue;
                m_SlotChunk[free] = i; c.slot = free;
                // B2: staged prefetch — request the coarsest LOD first (guaranteed instant image),
                // then let the refine loop step one level finer per Evaluate. Force-max skips the
                // ramp and loads the target level (LOD0) directly.
                int initialLevel = forceMaxQuality ? c.desired
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

            int resident = m_HasEnvH ? m_EnvCount : 0, rc = 0;
            for (int i = 0; i < m_Chunks.Count; i++) { var c = m_Chunks[i]; if (c.slot >= 0 && c.hasCur) { resident += c.splatCount[c.curLevel]; rc++; } }
            m_ResidentSplats = resident; m_ResidentChunks = rc;
            if (deviceBudget > 0)
            {
                float ratio = (float)total / deviceBudget;
                if (ratio < 1f - kBudgetDeadZone || ratio > 1f + kBudgetDeadZone)
                { float target = 1f / Mathf.Sqrt(Mathf.Max(ratio, 1e-3f)); m_BudgetScale = Mathf.Clamp(m_BudgetScale * (1f + (target - 1f) * kBudgetBlend), 0.05f, 1f); }
            }
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
                        Addressables.Release(c.penH);
                    c.hasPen = false; c.penLevel = -1;
                }
                if (c.hasCur)
                {
                    if (cooldownFrames > 0 && c.curLevel >= 0 && c.curLevel < c.addr.Length)
                        ParkInCooldown(c.addr[c.curLevel], c.curH);
                    else
                        Addressables.Release(c.curH);
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
            if (m_Chunks != null) foreach (var c in m_Chunks) { if (c.hasPen) Addressables.Release(c.penH); if (c.hasCur) Addressables.Release(c.curH); }
            if (m_HasEnvH) Addressables.Release(m_EnvH);
            // B3: drain cooldown map
            foreach (var kv in m_Cooldown) if (kv.Value.valid) Addressables.Release(kv.Value.h);
            m_Cooldown.Clear();
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 17 }; style.normal.textColor = Color.white;
            var sb = new StringBuilder();
            sb.AppendLine($"GaussianLodStreamAsync (Addressables)  resident={m_ResidentSplats / 1000}K / budget={deviceBudget / 1000}K  slots={m_ResidentChunks}/{maxResidentChunks} of {m_Chunks.Count}  visible={m_VisibleChunks}");
            int pending = 0; int noResident = 0;
            foreach (var c in m_Chunks) { if (c.hasPen) pending++; if (c.visible && !c.hasCur) noResident++; }
            string modeTag = forceMaxQuality ? "★ FULL QUALITY (all LOD0)"
                             : slowMotionDemo ? "SLOW-MO demo — [R] reset"
                             : "streaming — [F] toggle full quality, [R] reset";
            sb.AppendLine($"streaming: inFlight={pending}  cooldown={m_Cooldown.Count}  noResident(visible)={noResident}  budgetScale={m_BudgetScale:F2}  env={(enableEnv ? m_EnvCount / 1000 + "K" : "off")}   {modeTag}");

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
