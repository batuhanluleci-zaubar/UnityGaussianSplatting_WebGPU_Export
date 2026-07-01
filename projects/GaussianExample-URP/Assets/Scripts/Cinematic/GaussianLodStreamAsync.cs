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
        public string manifestPath = "/Users/devbatuhanluleci/UnityGaussianSplatting_WebGPU_Export/tools/gsplat_lod/out/uhq/manifest.json";
        public Camera cam;
        public bool enableEnv = false;

        [Header("Streaming working set")]
        public int maxResidentChunks = 64;
        [Tooltip("Max concurrent async loads in flight (throttles IO / upload spikes).")]
        public int maxConcurrentLoads = 4;
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

        [Header("Hysteresis")]
        public int evalEveryNFrames = 10;
        public float lodUpdateDistance = 1.0f;

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

        void Start()
        {
            if (cam == null) cam = Camera.main;
            for (int i = 0; i < kBuckets; i++) m_Bucket[i] = new List<int>(32);
            var settings = GaussianSplatSettings.instance;
            if (settings != null) { settings.m_EnableOctreeCulling = true; settings.m_EnableScreenLod = false; settings.m_LodSplatBudget = 0; }

            manifestPath = LodManifestResolver.Resolve(manifestPath, "[StreamAsync]");
            if (manifestPath == null) { enabled = false; return; }
            var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath));
            if (man == null || man.chunks == null) { Debug.LogError("[StreamAsync] manifest parse failed"); enabled = false; return; }
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
            // Auto-tune the LOD bands to the scene scale so we don't render the whole scene at coarsest
            // just because the user opened the scene with a far camera. lodBaseDistance -> "where LOD steps
            // from 0->1" needs to be on the same ORDER as the camera-to-content distance for a normally-framed
            // shot. Rule: base ~= scene radius. For a 25m-radius scene this gives base=25 (previous default
            // was 15, which pushed most chunks to LOD3/4 from a ~55m auto-framed camera).
            lodBaseDistance = Mathf.Max(lodBaseDistance, m_SceneRadius * 1.2f);
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
            bool camMoved = (cam.transform.position - m_LastCamPos).sqrMagnitude > lodUpdateDistance * lodUpdateDistance;
            int evalInterval = slowMotionDemo ? Mathf.Max(evalEveryNFrames, 30) : Mathf.Max(1, evalEveryNFrames);
            if ((m_Frame - m_LastEvalFrame) < evalInterval && !camMoved) return;
            m_LastEvalFrame = m_Frame; m_LastCamPos = cam.transform.position;
            Evaluate();
        }

        void PollLoads()
        {
            m_InFlight = 0;
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                if (!c.hasPen) continue;
                if (!c.penH.IsDone) { m_InFlight++; continue; }
                if (c.penH.Status == AsyncOperationStatus.Succeeded && c.slot >= 0)
                {
                    m_Pool[c.slot].m_Asset = c.penH.Result;
                    if (!m_Pool[c.slot].gameObject.activeSelf) m_Pool[c.slot].gameObject.SetActive(true);
                    if (c.hasCur) Addressables.Release(c.curH);
                    c.curH = c.penH; c.hasCur = true; c.curLevel = c.penLevel;
                }
                else { Addressables.Release(c.penH); }   // failed, or slot lost while loading
                c.hasPen = false; c.penLevel = -1;
            }
        }

        void StartLoad(Chunk c, int level)
        {
            if (c.hasPen && c.penLevel == level) return;         // already loading this level
            if (c.hasCur && c.curLevel == level && !c.hasPen) return; // already resident
            if (c.hasPen) { Addressables.Release(c.penH); c.hasPen = false; }
            c.penH = Addressables.LoadAssetAsync<GaussianSplatAsset>(c.addr[level]);
            c.hasPen = true; c.penLevel = level;
        }

        void Evaluate()
        {
            m_Eval++;
            GeometryUtility.CalculateFrustumPlanes(cam, m_Planes);
            Vector3 camPos = cam.transform.position;
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
                float effDist = c.dist * fovScale; int lv = 0; float thr = baseDist;
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
                // Force-max skips the "instant coarse image" ramp and loads the target level (LOD0) directly.
                StartLoad(c, forceMaxQuality ? c.desired : (coarseFirst ? c.addr.Length - 1 : c.desired)); m_InFlight++;
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
                if (c.hasPen) { Addressables.Release(c.penH); c.hasPen = false; c.penLevel = -1; }
                if (c.hasCur) { Addressables.Release(c.curH); c.hasCur = false; }
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
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 17 }; style.normal.textColor = Color.white;
            var sb = new StringBuilder();
            sb.AppendLine($"GaussianLodStreamAsync (Addressables)  resident={m_ResidentSplats / 1000}K / budget={deviceBudget / 1000}K  slots={m_ResidentChunks}/{maxResidentChunks} of {m_Chunks.Count}  visible={m_VisibleChunks}");
            int pending = 0; foreach (var c in m_Chunks) if (c.hasPen) pending++;
            string modeTag = forceMaxQuality ? "★ FULL QUALITY (all LOD0)"
                             : slowMotionDemo ? "SLOW-MO demo — [R] reset"
                             : "streaming — [F] toggle full quality, [R] reset";
            sb.AppendLine($"streaming: inFlight(loading)={pending}   budgetScale={m_BudgetScale:F2}   env={(enableEnv ? m_EnvCount / 1000 + "K" : "off")}   {modeTag}");

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
