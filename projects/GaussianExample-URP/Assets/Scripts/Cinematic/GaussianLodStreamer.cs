// SPDX-License-Identifier: MIT
// Phase 1 of STREAMED_LOD_DESIGN.md — SuperSplat "Streamed SOG"-style discrete multi-LOD +
// device Gaussian budget, all levels resident from local assets (no eviction yet).
//
// Reads tools/gsplat_lod/chunk_lod.py's manifest.json, builds one GaussianSplatRenderer per
// spatial chunk (+ one always-resident coarse env/background), and every ~N frames:
//   1. frustum-cull chunks by their world AABB (off-screen -> forced to coarsest, ~free to draw)
//   2. per-chunk screen-space-error LOD: closest-point AABB distance x fovScale -> geometric
//      bands (lodBaseDistance * lodMultiplier^i) -> optimalLevel  [port of evaluateNodeLods]
//   3. device Gaussian budget: a continuous _budgetScale damper breathes the bands, then a
//      64 sqrt-distance-bucket greedy balancer degrades farthest-first until under budget
//      [port of gsplat-budget-balancer.js + world._enforceBudget]
//   4. hysteresis: 10-frame cadence + camera dead-band + per-chunk LOD dwell (no popping)
// A chosen level is applied by assigning renderer.m_Asset; the renderer's Update() auto
// Dispose+Recreates its GPU resources. Chunk-internal frustum cull + depth sort come free from
// each renderer's own octree. Editor-only asset loading (AssetDatabase).
//
// Phase 2 (not here) replaces the N renderers with a single GPU buffer pool + slot allocator +
// async streaming + eviction so scenes whose coarse floor exceeds device RAM can stream.
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
    // Manifest POCOs shared by both streamers (Editor GaussianLodStreamer + build-time
    // GaussianLodStreamAsync). Additive v2 schema over v1 — JsonUtility silently drops
    // unknown fields so a v1-only reader still parses v2, but sees empty envFile until
    // it upgrades. B4 (LodManifestValidator) fails-hard on unknown versions to prevent
    // that silent-blackhole trap. Track B design doc: additive over v1 verbatim.
    [Serializable] public class LodManifest {
        public int version; public string scene; public int chunkCount; public int lodLevels;
        public float lodMult; public string envFile; public int envSplatCount;
        public int sourceSplatCount;
        public int lod0PruneRemoved;
        public int[] totalSplatsByLod;
        // v2 additions (nullable / defaulted on v1 payloads).
        public string generator;
        public string[] filenames;             // canonical address table (SuperSplat parity)
        public LodBoundsMeta bounds;           // schema self-tag ("ellipsoidExtent" today)
        public LodEnvMeta environment;         // env tier metadata
        public LodChunkMeta[] chunks;
    }
    [Serializable] public class LodBoundsMeta { public string kind; public float kSigma; }
    [Serializable] public class LodChunkMeta {
        public int id;
        public float[] boundMin; public float[] boundMax;
        // v2 additions: per-Gaussian sum(pos ± |R|·scale·kSigma) reduction (tighter, orientation-aware).
        public float[] boundMinEllipsoid; public float[] boundMaxEllipsoid;
        public float[] centre;
        public LodLevelMeta[] lods;
    }
    [Serializable] public class LodLevelMeta {
        public int level; public string file; public float voxel; public int splatCount;
        // v2 addition: index into LodManifest.filenames[]. Reader prefers this over .file on v2.
        public int fileIdx;
    }
    [Serializable] public class LodEnvMeta {
        public string directory; public string[] files; public int[] fileIdx;
        public int splatCount; public float voxel; public string residency;
        public float[] boundMin; public float[] boundMax;
        public float[] boundMinEllipsoid; public float[] boundMaxEllipsoid;
    }

    // B4: fail-hard version validation. Called by both streamers right after JsonUtility parse.
    // JsonUtility.FromJson silently drops unknown fields — a v2 manifest loaded by a pre-B4 build
    // parses successfully but reads cm.lods[L].file as null when we later swap the schema to a
    // pure-int address (v3+). Guarding on version keeps that failure loud, not silent.
    public static class LodManifestValidator
    {
        public const int kMinSupported = 1;
        public const int kMaxSupported = 2;
        public static bool Validate(LodManifest man, string logTag, out string error)
        {
            error = null;
            if (man == null || man.chunks == null) { error = logTag + " manifest parse failed"; return false; }
            if (man.version < kMinSupported || man.version > kMaxSupported)
            {
                error = $"{logTag} manifest version {man.version} unsupported; runtime accepts v{kMinSupported}..v{kMaxSupported}. " +
                        "Re-bake with: python tools/gsplat_lod/chunk_lod.py <input.spz> --schema-version 2";
                return false;
            }
            if (man.version >= 2)
            {
                if (man.filenames == null || man.filenames.Length == 0)
                {
                    error = $"{logTag} manifest v2 missing required filenames[] address table. Re-bake with --schema-version 2.";
                    return false;
                }
            }
            return true;
        }

        // Resolve the Addressables address for a chunk LOD. On v2 the address is the
        // canonical filenames[fileIdx] entry; on v1 it's the per-leaf .file string.
        public static string ResolveAddr(LodManifest m, LodLevelMeta lm)
        {
            if (m != null && m.version >= 2 && m.filenames != null && lm.fileIdx >= 0 && lm.fileIdx < m.filenames.Length)
                return Path.GetFileNameWithoutExtension(m.filenames[lm.fileIdx]);
            return Path.GetFileNameWithoutExtension(lm.file);
        }

        // Env-tier address resolver. Falls back to v1 top-level envFile if the environment{}
        // block is missing (which happens on v1 payloads).
        public static string ResolveEnvAddr(LodManifest m)
        {
            if (m != null && m.version >= 2 && m.environment != null && m.environment.fileIdx != null
                && m.environment.fileIdx.Length > 0 && m.filenames != null
                && m.environment.fileIdx[0] < m.filenames.Length)
                return Path.GetFileNameWithoutExtension(m.filenames[m.environment.fileIdx[0]]);
            return string.IsNullOrEmpty(m?.envFile) ? null : Path.GetFileNameWithoutExtension(m.envFile);
        }

        // B3: chunk bounds selector. v2 uses the tighter ellipsoid-extent bound; v1 falls back
        // to the legacy percentile-trimmed position AABB (which is a superset — safe to widen to).
        public static void ResolveChunkBounds(LodManifest m, LodChunkMeta cm, out Vector3 bmin, out Vector3 bmax)
        {
            if (m != null && m.version >= 2 && cm.boundMinEllipsoid != null && cm.boundMaxEllipsoid != null
                && cm.boundMinEllipsoid.Length == 3 && cm.boundMaxEllipsoid.Length == 3)
            {
                bmin = new Vector3(cm.boundMinEllipsoid[0], cm.boundMinEllipsoid[1], cm.boundMinEllipsoid[2]);
                bmax = new Vector3(cm.boundMaxEllipsoid[0], cm.boundMaxEllipsoid[1], cm.boundMaxEllipsoid[2]);
            }
            else
            {
                bmin = new Vector3(cm.boundMin[0], cm.boundMin[1], cm.boundMin[2]);
                bmax = new Vector3(cm.boundMax[0], cm.boundMax[1], cm.boundMax[2]);
            }
        }
    }

    // Bakes come and go under tools/gsplat_lod/out/*/manifest.json; scenes save an absolute path
    // at edit time so a nuked bake breaks the scene silently. This walks the sibling out/ dirs
    // if the recorded path is missing and returns the first live one, with a clear log.
    public static class LodManifestResolver
    {
        public static string Resolve(string savedPath, string logTag)
        {
            // A1: FIRST try StreamingAssets — this is the only path that works in a Standalone build.
            // Accepts either a relative subpath ("gsplat_lod/uhq/manifest.json") OR a filename that
            // exists under Application.streamingAssetsPath at any depth.
            if (!string.IsNullOrEmpty(savedPath) && !Path.IsPathRooted(savedPath))
            {
                var saPath = Path.Combine(Application.streamingAssetsPath, savedPath);
                if (File.Exists(saPath))
                {
                    Debug.Log($"{logTag} manifest resolved via StreamingAssets: '{saPath}'");
                    return saPath;
                }
            }
            if (!string.IsNullOrEmpty(savedPath) && File.Exists(savedPath)) return savedPath;
            string outDir = null;
            if (!string.IsNullOrEmpty(savedPath))
            {
                // savedPath = ".../out/<name>/manifest.json"  -> outDir = ".../out"
                var manifestParent = Path.GetDirectoryName(savedPath);           // .../out/<name>
                if (!string.IsNullOrEmpty(manifestParent)) outDir = Path.GetDirectoryName(manifestParent);
            }
            if (string.IsNullOrEmpty(outDir) || !Directory.Exists(outDir))
            {
                Debug.LogError($"{logTag} manifest not found: '{savedPath}' and no sibling out/ dir to search.");
                return null;
            }
            // Prefer uhq first (current canonical bake), then streamed, then anything else.
            string[] prefer = { "uhq", "station4", "env", "streamed", "hq", "lod", "spike" };
            foreach (var name in prefer)
            {
                var cand = Path.Combine(outDir, name, "manifest.json");
                if (File.Exists(cand)) { Debug.LogWarning($"{logTag} recorded manifest missing ('{savedPath}'), FALLBACK to '{cand}'."); return cand; }
            }
            foreach (var d in Directory.GetDirectories(outDir))
            {
                var cand = Path.Combine(d, "manifest.json");
                if (File.Exists(cand)) { Debug.LogWarning($"{logTag} recorded manifest missing ('{savedPath}'), FALLBACK to '{cand}'."); return cand; }
            }
            var have = string.Join(", ", Directory.GetDirectories(outDir));
            Debug.LogError($"{logTag} manifest not found: '{savedPath}'. Available out/ subdirs: [{have}] -- none has a manifest.json. Re-bake with tools/gsplat_lod/chunk_lod.py or streamed_sog.py.");
            return null;
        }
    }

    public class GaussianLodStreamer : MonoBehaviour
    {
        [Header("Source")]
        public string manifestPath = "/Users/devbatuhanluleci/UnityGaussianSplatting_WebGPU_Export/tools/gsplat_lod/out/uhq/manifest.json";
        public string assetFolder = "Assets/GaussianAssets";
        public Camera cam;
        public bool enableEnv = true;

        [Header("Device budget (resident splats)")]
        public int deviceBudget = 1_200_000;

        [Header("Screen-error LOD bands")]
        [Tooltip("World distance at which a chunk drops below full detail (band 0 -> 1).")]
        public float lodBaseDistance = 14f;
        [Tooltip("Distance multiplier per LOD band (2-3).")]
        public float lodMultiplier = 2.0f;

        [Header("Hysteresis")]
        public int evalEveryNFrames = 10;
        public float lodUpdateDistance = 1.0f;   // camera dead-band
        public int lodDwellFrames = 12;          // min frames a chunk holds a level before changing

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
            public GaussianSplatAsset[] lods;    // [0]=fine .. [K-1]=coarse
            public GaussianSplatRenderer rend;
            public Vector3 localCentre;
            public Vector3 localSize;
            public int cur = -1;                 // displayed level
            public int optimal, desired;
            public bool visible;
            public float dist;
            public int lastChangeFrame;
        }

        readonly List<Chunk> m_Chunks = new List<Chunk>();
        GaussianSplatRenderer m_Env;
        int m_EnvCount;
        int m_Frame, m_ResidentSplats, m_VisibleChunks, m_LastEvalFrame = -9999;
        float m_BudgetScale = 1f;
        Vector3 m_LastCamPos = Vector3.positiveInfinity;
        Vector3 m_SceneCentre; float m_SceneRadius;
        readonly List<int>[] m_Bucket = new List<int>[kBuckets];
        readonly Plane[] m_Planes = new Plane[6];

        GaussianSplatAsset LoadAsset(string plyFile)
        {
#if UNITY_EDITOR
            string baseName = Path.GetFileNameWithoutExtension(plyFile);
            return AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetFolder + "/" + baseName + ".asset");
#else
            return null;
#endif
        }

        GaussianSplatRenderer MakeRenderer(string goName, GaussianSplatAsset asset)
        {
            var go = new GameObject(goName);
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            var r = go.AddComponent<GaussianSplatRenderer>();
            r.m_Asset = asset;
            go.SetActive(true);
            return r;
        }

        void Start()
        {
            if (cam == null) cam = Camera.main;
            for (int i = 0; i < kBuckets; i++) m_Bucket[i] = new List<int>(32);

            // Runtime LOD is done here via discrete assets; make each renderer's own octree do
            // frustum cull + sort, and disable the per-renderer stride LOD / budget.
            var settings = GaussianSplatSettings.instance;
            if (settings != null)
            {
                settings.m_EnableOctreeCulling = true;
                settings.m_EnableScreenLod = false;
                settings.m_LodSplatBudget = 0;
            }

            manifestPath = LodManifestResolver.Resolve(manifestPath, "[LodStreamer]");
            if (manifestPath == null) { enabled = false; return; }
            var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath));
            if (!LodManifestValidator.Validate(man, "[LodStreamer]", out var vErr))
            { Debug.LogError(vErr); enabled = false; return; }
            if (man.version >= 2) Debug.Log($"[LodStreamer] manifest v2 loaded, filenames={man.filenames?.Length ?? 0}");
            if (lodMultiplier < 1.05f) lodMultiplier = 1.05f;

            Vector3 mn = Vector3.one * 1e9f, mx = -mn;
            foreach (var cm in man.chunks)
            {
                var c = new Chunk { meta = cm };
                LodManifestValidator.ResolveChunkBounds(man, cm, out var bmin, out var bmax);
                c.localCentre = (cm.centre != null && cm.centre.Length == 3)
                    ? new Vector3(cm.centre[0], cm.centre[1], cm.centre[2])
                    : (bmin + bmax) * 0.5f;
                c.localSize = bmax - bmin;
                c.lods = new GaussianSplatAsset[cm.lods.Length];
                bool ok = true;
                for (int L = 0; L < cm.lods.Length; L++)
                {
                    var addr = LodManifestValidator.ResolveAddr(man, cm.lods[L]);
                    var a = LoadAsset(addr);
                    if (a == null) { Debug.LogError("[LodStreamer] asset missing: " + addr); ok = false; break; }
                    c.lods[L] = a;
                }
                if (!ok) continue;
                mn = Vector3.Min(mn, bmin); mx = Vector3.Max(mx, bmax);
                // start at coarsest for an instant complete image; scheduler refines up
                c.rend = MakeRenderer("Chunk_" + cm.id, c.lods[c.lods.Length - 1]);
                c.cur = c.lods.Length - 1;
                m_Chunks.Add(c);
            }

            var envAddr = LodManifestValidator.ResolveEnvAddr(man);
            if (enableEnv && !string.IsNullOrEmpty(envAddr))
            {
                var envAsset = LoadAsset(envAddr);
                if (envAsset != null) { m_Env = MakeRenderer("Env", envAsset); m_EnvCount = envAsset.splatCount; }
                else Debug.LogWarning("[LodStreamer] env asset missing: " + envAddr);
            }

            m_SceneCentre = (mn + mx) * 0.5f;
            m_SceneRadius = (mx - mn).magnitude * 0.5f;
            if (autoFrameCamera && cam != null)
            {
                cam.transform.position = transform.TransformPoint(m_SceneCentre + new Vector3(0f, 0.15f * m_SceneRadius, -2.2f * m_SceneRadius));
                cam.transform.LookAt(transform.TransformPoint(m_SceneCentre));
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = Mathf.Max(cam.farClipPlane, m_SceneRadius * 12f);
            }
            Debug.Log($"[LodStreamer] {m_Chunks.Count} chunks + env({m_EnvCount}), budget={deviceBudget}, centre={m_SceneCentre} radius={m_SceneRadius:F1}");
        }

        void Update()
        {
            if (cam == null) { cam = Camera.main; if (cam == null) return; }
            m_Frame++;
            // cadence + camera dead-band
            bool camMoved = (cam.transform.position - m_LastCamPos).sqrMagnitude > lodUpdateDistance * lodUpdateDistance;
            bool due = (m_Frame - m_LastEvalFrame) >= Mathf.Max(1, evalEveryNFrames);
            if (!due && !camMoved) return;
            m_LastEvalFrame = m_Frame;
            m_LastCamPos = cam.transform.position;
            Evaluate();
        }

        void Evaluate()
        {
            int K1 = 0;
            GeometryUtility.CalculateFrustumPlanes(cam, m_Planes);
            Vector3 camPos = cam.transform.position;
            float scale = transform.lossyScale.x;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float tanH = tanV * cam.aspect;
            float fovScale = Mathf.Min(tanV, tanH) / kRefTanHalfFov;
            float baseDist = Mathf.Max(0.01f, lodBaseDistance * m_BudgetScale);
            float logMult = Mathf.Log(lodMultiplier);
            float maxDist = 0.001f;

            // pass 1: frustum cull + optimal level by screen error
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                int K = c.lods.Length; K1 = K - 1;
                Vector3 wc = transform.TransformPoint(c.localCentre);
                Vector3 wsize = Vector3.Scale(c.localSize, transform.lossyScale);
                var wb = new Bounds(wc, wsize);
                c.visible = GeometryUtility.TestPlanesAABB(m_Planes, wb);
                float d = Mathf.Sqrt(wb.SqrDistance(camPos));
                c.dist = d;
                if (d > maxDist) maxDist = d;
                if (!c.visible) { c.optimal = K1; continue; }   // off-screen -> coarsest (renderer draws ~0 anyway)
                float effDist = d * fovScale;
                int lv = 0; float thr = baseDist;
                while (lv < K1 && effDist >= thr) { thr *= lodMultiplier; lv++; }
                c.optimal = lv;
            }

            // pass 2: budget. start from optimal, sum resident.
            long total = m_Env != null ? m_EnvCount : 0;
            m_VisibleChunks = 0;
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                c.desired = c.optimal;
                total += c.lods[c.desired].splatCount;
                if (c.visible) m_VisibleChunks++;
            }

            // 64 sqrt-distance-bucket greedy degrade (far-first) until under budget
            if (deviceBudget > 0 && total > deviceBudget)
            {
                for (int b = 0; b < kBuckets; b++) m_Bucket[b].Clear();
                float invMax = (kBuckets - 1) / Mathf.Sqrt(maxDist);
                for (int i = 0; i < m_Chunks.Count; i++)
                {
                    int b = Mathf.Clamp((int)(Mathf.Sqrt(m_Chunks[i].dist) * invMax), 0, kBuckets - 1);
                    m_Bucket[b].Add(i);
                }
                int guard = m_Chunks.Count * 8;
                bool moved = true;
                while (total > deviceBudget && moved && guard-- > 0)
                {
                    moved = false;
                    for (int b = kBuckets - 1; b >= 0 && total > deviceBudget; b--)
                    {
                        var bl = m_Bucket[b];
                        for (int j = 0; j < bl.Count && total > deviceBudget; j++)
                        {
                            var c = m_Chunks[bl[j]];
                            int K1c = c.lods.Length - 1;
                            if (c.desired < K1c)
                            {
                                total += c.lods[c.desired + 1].splatCount - c.lods[c.desired].splatCount;
                                c.desired++;
                                moved = true;
                            }
                        }
                    }
                }
            }

            // pass 3: apply with dwell hysteresis
            int resident = m_Env != null ? m_EnvCount : 0;
            for (int i = 0; i < m_Chunks.Count; i++)
            {
                var c = m_Chunks[i];
                int level = c.desired;
                if (level != c.cur)
                {
                    // dwell: don't thrash unless enough frames passed (first assignment always applies)
                    if (c.cur < 0 || (m_Frame - c.lastChangeFrame) >= lodDwellFrames)
                    {
                        c.rend.m_Asset = c.lods[level];
                        c.cur = level;
                        c.lastChangeFrame = m_Frame;
                    }
                }
                resident += c.lods[c.cur].splatCount;
            }
            m_ResidentSplats = resident;

            // budget damper for NEXT frame: nudge _budgetScale so bands breathe toward budget
            if (deviceBudget > 0)
            {
                float ratio = (float)total / deviceBudget;
                if (ratio < 1f - kBudgetDeadZone || ratio > 1f + kBudgetDeadZone)
                {
                    float target = 1f / Mathf.Sqrt(Mathf.Max(ratio, 1e-3f));
                    // Cap at 1: screen-space error is the quality CEILING; the budget may only ever
                    // tighten bands (<1) under pressure, never over-refine the far field to spend budget.
                    m_BudgetScale = Mathf.Clamp(m_BudgetScale * (1f + (target - 1f) * kBudgetBlend), 0.05f, 1f);
                }
            }
        }

        void OnGUI()
        {
            if (!showHud) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 17 };
            style.normal.textColor = Color.white;
            var sb = new StringBuilder();
            sb.AppendLine($"GaussianLodStreamer  resident={m_ResidentSplats / 1000}K / budget={deviceBudget / 1000}K   visibleChunks={m_VisibleChunks}/{m_Chunks.Count}   env={m_EnvCount / 1000}K   budgetScale={m_BudgetScale:F2}");
            int l0 = 0, l1 = 0, l2 = 0, culled = 0;
            foreach (var c in m_Chunks)
            {
                if (!c.visible) culled++;
                if (c.cur == 0) l0++; else if (c.cur == 1) l1++; else l2++;
            }
            sb.Append($"LOD0(fine)={l0}  LOD1={l1}  LOD2+(coarse)={l2}   offscreen={culled}   baseDist={lodBaseDistance:F0} mult={lodMultiplier:F1}");
            GUI.Label(new Rect(12, 10, 1500, 120), sb.ToString(), style);
        }
    }
}
