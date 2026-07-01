// SPDX-License-Identifier: MIT
// Phase 2 (M1) of STREAMED_LOD_DESIGN.md — streaming RESIDENCY + eviction on the per-chunk
// renderer model. Unlike Phase 1 (GaussianLodStreamer holds every chunk resident), this keeps a
// BOUNDED working set: a fixed pool of reusable GaussianSplatRenderers smaller than the chunk
// count. The nearest visible chunks (within the device budget) acquire a pool slot; farther
// chunks show only the always-resident coarse env floor. As the camera moves, chunks stream in
// (acquire slot -> load coarsest -> refine one level/eval) and out (leave the set -> cooldown ->
// release slot). Memory is bounded by pool size x level, independent of total scene chunk count —
// this is what lets a scene have far more chunks than fit in device RAM (SuperSplat's model).
//
// The "load" primitive here is a synchronous editor AssetDatabase load, staged/throttled over
// frames (loadsPerFrame) so it behaves coarse-first and progressive; swapping it for async
// Addressables / raw-byte streaming is a drop-in change. Phase 2b replaces the N pooled
// renderers with a single GPU buffer pool + slot allocator (raw-byte, inline colour) for one
// unified draw; the residency/eviction logic here is unchanged by that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using GaussianSplatting.Runtime;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GsplatLod
{
    public class GaussianLodStreamManager : MonoBehaviour
    {
        [Header("Source")]
        public string manifestPath = "/Users/devbatuhanluleci/UnityGaussianSplatting_WebGPU_Export/tools/gsplat_lod/out/lod/manifest.json";
        public string assetFolder = "Assets/GaussianAssets";
        public Camera cam;
        [Tooltip("Draw the whole-scene coarse env floor. OFF by default: it is a DISTANT-BACKGROUND/skybox concept " +
                 "-- drawn over an interior it overlays huge coarse blobs and hazes near content, and it is redundant " +
                 "when the pool already keeps every visible chunk resident (that IS the coarse floor). Enable only for " +
                 "a genuine far background, or as a gap-filler when intentionally under-provisioning the pool.")]
        public bool enableEnv = false;

        [Header("Streaming working set")]
        [Tooltip("Fixed pooled renderers = the resident working set. Size it >= the max VISIBLE chunk count so on-screen " +
                 "chunks are never dropped; eviction then frees only OFF-SCREEN chunks (bounds memory for scenes with far " +
                 "more chunks than fit). If < visible count, the farthest visible chunks fall back to env/hole.")]
        public int maxResidentChunks = 24;
        [Tooltip("Max slot acquisitions + refinements applied per eval (staged / coarse-first).")]
        public int loadsPerFrame = 3;
        [Tooltip("Evals a chunk keeps its slot after leaving the working set (anti-thrash).")]
        public int cooldownEvals = 4;
        public bool coarseFirst = true;

        [Header("Device budget (resident splats)")]
        public int deviceBudget = 1_200_000;

        [Header("Screen-error LOD bands")]
        public float lodBaseDistance = 14f;
        public float lodMultiplier = 2.0f;

        [Header("Hysteresis")]
        public int evalEveryNFrames = 10;
        public float lodUpdateDistance = 1.0f;

        [Header("Misc")]
        public bool autoFrameCamera = true;
        public bool showHud = true;

        const int kBuckets = 64;
        const float kBudgetDeadZone = 0.4f;
        const float kBudgetBlend = 0.3f;
        static readonly float kRefTanHalfFov = Mathf.Tan(22.5f * Mathf.Deg2Rad);

        class Chunk
        {
            public LodChunkMeta meta;
            public GaussianSplatAsset[] lods;   // [0]=fine .. [K-1]=coarse
            public Vector3 localCentre, localSize;
            public float dist;
            public bool visible;
            public int optimal, desired;        // screen-error + budget target level
            public int slot = -1;               // pool slot index, or -1 (env only)
            public int curLevel = -1;           // level currently loaded in the slot
            public int lastWantedEval = -9999;
        }

        readonly List<Chunk> m_Chunks = new List<Chunk>();
        GaussianSplatRenderer[] m_Pool;
        int[] m_SlotChunk;                       // slot -> chunk index (-1 free)
        GaussianSplatRenderer m_Env;
        int m_EnvCount;

        int m_Frame, m_Eval, m_LastEvalFrame = -9999, m_ResidentSplats, m_ResidentChunks, m_VisibleChunks, m_LoadsThisEval;
        float m_BudgetScale = 1f;
        Vector3 m_LastCamPos = Vector3.positiveInfinity, m_SceneCentre;
        float m_SceneRadius;
        readonly Plane[] m_Planes = new Plane[6];
        readonly List<int>[] m_Bucket = new List<int>[kBuckets];
        List<int> m_VisSorted = new List<int>();

        GaussianSplatAsset LoadAsset(string plyFile)
        {
#if UNITY_EDITOR
            return AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetFolder + "/" + Path.GetFileNameWithoutExtension(plyFile) + ".asset");
#else
            return null;
#endif
        }

        GaussianSplatRenderer MakeRenderer(string goName, GaussianSplatAsset asset, bool active)
        {
            var go = new GameObject(goName);
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            var r = go.AddComponent<GaussianSplatRenderer>();
            r.m_Asset = asset;
            go.SetActive(active);
            return r;
        }

        void Start()
        {
            if (cam == null) cam = Camera.main;
            for (int i = 0; i < kBuckets; i++) m_Bucket[i] = new List<int>(32);

            var settings = GaussianSplatSettings.instance;
            if (settings != null) { settings.m_EnableOctreeCulling = true; settings.m_EnableScreenLod = false; settings.m_LodSplatBudget = 0; }

            if (!File.Exists(manifestPath)) { Debug.LogError("[StreamMgr] manifest not found: " + manifestPath); enabled = false; return; }
            var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath));
            if (man == null || man.chunks == null) { Debug.LogError("[StreamMgr] manifest parse failed"); enabled = false; return; }
            if (lodMultiplier < 1.05f) lodMultiplier = 1.05f;

            Vector3 mn = Vector3.one * 1e9f, mx = -mn;
            foreach (var cm in man.chunks)
            {
                var c = new Chunk { meta = cm };
                var bmin = new Vector3(cm.boundMin[0], cm.boundMin[1], cm.boundMin[2]);
                var bmax = new Vector3(cm.boundMax[0], cm.boundMax[1], cm.boundMax[2]);
                c.localCentre = (bmin + bmax) * 0.5f; c.localSize = bmax - bmin;
                c.lods = new GaussianSplatAsset[cm.lods.Length];
                bool ok = true;
                for (int L = 0; L < cm.lods.Length; L++) { var a = LoadAsset(cm.lods[L].file); if (a == null) { Debug.LogError("[StreamMgr] asset missing: " + cm.lods[L].file); ok = false; break; } c.lods[L] = a; }
                if (!ok) continue;
                mn = Vector3.Min(mn, bmin); mx = Vector3.Max(mx, bmax);
                m_Chunks.Add(c);
            }

            // fixed pool of reusable renderers (start empty/disabled)
            int poolN = Mathf.Clamp(maxResidentChunks, 1, m_Chunks.Count);
            maxResidentChunks = poolN;
            m_Pool = new GaussianSplatRenderer[poolN];
            m_SlotChunk = new int[poolN];
            for (int i = 0; i < poolN; i++) { m_Pool[i] = MakeRenderer("Slot_" + i, null, false); m_SlotChunk[i] = -1; }

            if (enableEnv && !string.IsNullOrEmpty(man.envFile))
            {
                var envAsset = LoadAsset(man.envFile);
                if (envAsset != null) { m_Env = MakeRenderer("Env", envAsset, true); m_EnvCount = envAsset.splatCount; }
            }

            m_SceneCentre = (mn + mx) * 0.5f; m_SceneRadius = (mx - mn).magnitude * 0.5f;
            if (autoFrameCamera && cam != null)
            {
                cam.transform.position = transform.TransformPoint(m_SceneCentre + new Vector3(0f, 0.15f * m_SceneRadius, -2.2f * m_SceneRadius));
                cam.transform.LookAt(transform.TransformPoint(m_SceneCentre));
                cam.nearClipPlane = 0.05f; cam.farClipPlane = Mathf.Max(cam.farClipPlane, m_SceneRadius * 12f);
            }
            Debug.Log($"[StreamMgr] {m_Chunks.Count} chunks, pool={poolN}, env={m_EnvCount}, budget={deviceBudget}, radius={m_SceneRadius:F1}");
        }

        void Update()
        {
            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            m_Frame++;
            bool camMoved = (cam.transform.position - m_LastCamPos).sqrMagnitude > lodUpdateDistance * lodUpdateDistance;
            bool due = (m_Frame - m_LastEvalFrame) >= Mathf.Max(1, evalEveryNFrames);
            if (!due && !camMoved) return;
            m_LastEvalFrame = m_Frame; m_LastCamPos = cam.transform.position;
            Evaluate();
        }

        void Evaluate()
        {
            m_Eval++;
            m_LoadsThisEval = 0;
            GeometryUtility.CalculateFrustumPlanes(cam, m_Planes);
            Vector3 camPos = cam.transform.position;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float fovScale = Mathf.Min(tanV, tanV * cam.aspect) / kRefTanHalfFov;
            float baseDist = Mathf.Max(0.01f, lodBaseDistance * m_BudgetScale);
            float maxDist = 0.001f;

            // pass 1: cull + screen-error optimal + collect visible sorted by distance
            m_VisSorted.Clear();
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                int K1 = c.lods.Length - 1;
                Vector3 wc = transform.TransformPoint(c.localCentre);
                var wb = new Bounds(wc, Vector3.Scale(c.localSize, transform.lossyScale));
                c.visible = GeometryUtility.TestPlanesAABB(m_Planes, wb);
                c.dist = Mathf.Sqrt(wb.SqrDistance(camPos));
                if (c.dist > maxDist) maxDist = c.dist;
                if (!c.visible) { c.optimal = K1; continue; }
                float effDist = c.dist * fovScale;
                int lv = 0; float thr = baseDist;
                while (lv < K1 && effDist >= thr) { thr *= lodMultiplier; lv++; }
                c.optimal = lv;
                m_VisSorted.Add(i);
            }
            m_VisibleChunks = m_VisSorted.Count;
            m_VisSorted.Sort((a, b) => m_Chunks[a].dist.CompareTo(m_Chunks[b].dist));

            // pass 2: budget over visible (start at optimal, degrade far-first)
            long total = m_Env != null ? m_EnvCount : 0;
            for (int i = 0; i < m_Chunks.Count; i++) { var c = m_Chunks[i]; c.desired = c.optimal; }
            for (int k = 0; k < m_VisSorted.Count; k++) total += m_Chunks[m_VisSorted[k]].lods[m_Chunks[m_VisSorted[k]].desired].splatCount;
            if (deviceBudget > 0 && total > deviceBudget)
            {
                for (int b = 0; b < kBuckets; b++) m_Bucket[b].Clear();
                float invMax = (kBuckets - 1) / Mathf.Sqrt(maxDist);
                for (int k = 0; k < m_VisSorted.Count; k++) { int i = m_VisSorted[k]; int b = Mathf.Clamp((int)(Mathf.Sqrt(m_Chunks[i].dist) * invMax), 0, kBuckets - 1); m_Bucket[b].Add(i); }
                int guard = m_VisSorted.Count * 8; bool moved = true;
                while (total > deviceBudget && moved && guard-- > 0)
                {
                    moved = false;
                    for (int b = kBuckets - 1; b >= 0 && total > deviceBudget; b--)
                        foreach (int i in m_Bucket[b])
                        {
                            var c = m_Chunks[i]; int K1 = c.lods.Length - 1;
                            if (c.desired < K1) { total += c.lods[c.desired + 1].splatCount - c.lods[c.desired].splatCount; c.desired++; moved = true; if (total <= deviceBudget) break; }
                        }
                }
            }

            // pass 3: working set = nearest visible chunks up to pool size (whose desired isn't env-only-coarsest)
            int wantCount = Mathf.Min(maxResidentChunks, m_VisSorted.Count);
            var wanted = new HashSet<int>();
            for (int k = 0; k < wantCount; k++) { int i = m_VisSorted[k]; wanted.Add(i); m_Chunks[i].lastWantedEval = m_Eval; }

            // pass 4: release slots of chunks no longer wanted (after cooldown)
            for (int s = 0; s < m_Pool.Length; s++)
            {
                int ci = m_SlotChunk[s];
                if (ci < 0) continue;
                if (!wanted.Contains(ci) && (m_Eval - m_Chunks[ci].lastWantedEval) > cooldownEvals)
                {
                    ReleaseSlot(s);
                }
            }

            // pass 5: acquire slots for wanted-but-not-resident (nearest first), staged
            for (int k = 0; k < wantCount && m_LoadsThisEval < loadsPerFrame; k++)
            {
                int i = m_VisSorted[k];
                var c = m_Chunks[i];
                if (c.slot >= 0) continue;                 // already resident
                int free = FindFreeOrEvictableSlot(i);
                if (free < 0) continue;                     // pool full of nearer chunks
                AssignSlot(free, i, coarseFirst ? c.lods.Length - 1 : c.desired);
                m_LoadsThisEval++;
            }

            // pass 6: refine resident chunks one level toward desired (staged), nearest first
            for (int k = 0; k < m_VisSorted.Count && m_LoadsThisEval < loadsPerFrame; k++)
            {
                int i = m_VisSorted[k];
                var c = m_Chunks[i];
                if (c.slot < 0) continue;
                if (c.curLevel != c.desired)
                {
                    int step = c.curLevel > c.desired ? c.curLevel - 1 : c.curLevel + 1;  // move one level toward desired
                    m_Pool[c.slot].m_Asset = c.lods[step];
                    c.curLevel = step;
                    m_LoadsThisEval++;
                }
            }

            // tally + budget damper (<=1 ceiling)
            int resident = m_Env != null ? m_EnvCount : 0, rc = 0;
            for (int i = 0; i < m_Chunks.Count; i++) { var c = m_Chunks[i]; if (c.slot >= 0 && c.curLevel >= 0) { resident += c.lods[c.curLevel].splatCount; rc++; } }
            m_ResidentSplats = resident; m_ResidentChunks = rc;
            if (deviceBudget > 0)
            {
                float ratio = (float)total / deviceBudget;
                if (ratio < 1f - kBudgetDeadZone || ratio > 1f + kBudgetDeadZone)
                {
                    float target = 1f / Mathf.Sqrt(Mathf.Max(ratio, 1e-3f));
                    m_BudgetScale = Mathf.Clamp(m_BudgetScale * (1f + (target - 1f) * kBudgetBlend), 0.05f, 1f);
                }
            }
        }

        int FindFreeOrEvictableSlot(int wantChunk)
        {
            for (int s = 0; s < m_Pool.Length; s++) if (m_SlotChunk[s] < 0) return s;
            // pool full: evict the resident chunk that is farthest AND farther than the wanted one
            int worst = -1; float worstDist = m_Chunks[wantChunk].dist;
            for (int s = 0; s < m_Pool.Length; s++)
            {
                int ci = m_SlotChunk[s];
                if (ci < 0) continue;
                if (m_Chunks[ci].dist > worstDist) { worstDist = m_Chunks[ci].dist; worst = s; }
            }
            if (worst >= 0) { ReleaseSlot(worst); return worst; }
            return -1;
        }

        void AssignSlot(int slot, int chunkIndex, int level)
        {
            var c = m_Chunks[chunkIndex];
            var go = m_Pool[slot].gameObject;
            m_Pool[slot].m_Asset = c.lods[level];
            if (!go.activeSelf) go.SetActive(true);
            m_SlotChunk[slot] = chunkIndex;
            c.slot = slot; c.curLevel = level;
        }

        void ReleaseSlot(int slot)
        {
            int ci = m_SlotChunk[slot];
            if (ci >= 0) { m_Chunks[ci].slot = -1; m_Chunks[ci].curLevel = -1; }
            m_SlotChunk[slot] = -1;
            m_Pool[slot].m_Asset = null;
            if (m_Pool[slot].gameObject.activeSelf) m_Pool[slot].gameObject.SetActive(false);
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 17 };
            style.normal.textColor = Color.white;
            var sb = new StringBuilder();
            sb.AppendLine($"GaussianLodStreamManager  resident={m_ResidentSplats / 1000}K / budget={deviceBudget / 1000}K   slots={m_ResidentChunks}/{maxResidentChunks} (of {m_Chunks.Count} chunks)   visible={m_VisibleChunks}   env={m_EnvCount / 1000}K");
            int refining = 0;
            foreach (var c in m_Chunks) if (c.slot >= 0 && c.curLevel != c.desired) refining++;
            sb.Append($"streaming: refining={refining}   budgetScale={m_BudgetScale:F2}   loadsPerFrame={loadsPerFrame}   cooldown={cooldownEvals}   (far chunks -> env floor)");
            GUI.Label(new Rect(12, 10, 1500, 120), sb.ToString(), style);
        }
    }
}
