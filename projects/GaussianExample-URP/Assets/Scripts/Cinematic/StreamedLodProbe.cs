// SPDX-License-Identifier: MIT
// De-risk spike for STREAMED_LOD_DESIGN.md: independent per-chunk multi-LOD selected by
// screen-space error and SWAPPED at runtime. Reads tools/gsplat_lod chunk_lod.py's
// manifest.json, builds one GaussianSplatRenderer per chunk, and each eval picks LOD0 (near,
// fine) or LOD1 (far, coarse) by the chunk's projected on-screen size. Editor-only (loads
// baked assets via AssetDatabase). Self-frames a camera so it does not depend on the host
// scene's scale/coordinate space.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using GaussianSplatting.Runtime;
#if UNITY_EDITOR
using UnityEditor;
#endif

[Serializable] public class GsLodManifest { public int version; public string scene; public int chunkCount; public int lodLevels; public GsLodChunk[] chunks; }
[Serializable] public class GsLodChunk { public int id; public float[] boundMin; public float[] boundMax; public float[] centre; public GsLodLevel[] lods; }
[Serializable] public class GsLodLevel { public int level; public string file; public float voxel; public int splatCount; }

public class StreamedLodProbe : MonoBehaviour
{
    public string manifestPath = "/Users/devbatuhanluleci/UnityGaussianSplatting_WebGPU_Export/tools/gsplat_lod/out/spike/manifest.json";
    public string assetFolder = "Assets/GaussianAssets";
    public Camera cam;
    [Tooltip("Chunk projected bigger than this many px on screen => LOD0 (fine), else LOD1 (coarse).")]
    public float fullDetailPixels = 420f;
    public int evalEveryNFrames = 1;
    public bool autoFrameCamera = true;
    public bool showHud = true;
    public bool forceLod0 = false;   // A/B: pin every chunk to fine LOD (measure full-detail cost)

    class Chunk { public GsLodChunk meta; public GaussianSplatRenderer rend; public GaussianSplatAsset[] lods; public int cur = -1; public Vector3 localCentre; public float localExtent; }
    readonly List<Chunk> m_Chunks = new List<Chunk>();
    int m_Frame, m_ResidentSplats, m_FullSplats;
    public int ResidentSplats => m_ResidentSplats;
    public int FullSplats => m_FullSplats;
    public Vector3 SceneCentre { get; private set; }
    public float SceneRadius { get; private set; }

    void Start()
    {
        if (cam == null) cam = Camera.main;
        if (!File.Exists(manifestPath)) { Debug.LogError("[Probe] manifest not found: " + manifestPath); enabled = false; return; }
        var man = JsonUtility.FromJson<GsLodManifest>(File.ReadAllText(manifestPath));
        if (man == null || man.chunks == null) { Debug.LogError("[Probe] manifest parse failed"); enabled = false; return; }

        Vector3 mn = Vector3.one * 1e9f, mx = -mn;
        foreach (var cm in man.chunks)
        {
            var c = new Chunk { meta = cm };
            c.localCentre = new Vector3(cm.centre[0], cm.centre[1], cm.centre[2]);
            c.localExtent = Mathf.Max(cm.boundMax[0] - cm.boundMin[0], Mathf.Max(cm.boundMax[1] - cm.boundMin[1], cm.boundMax[2] - cm.boundMin[2]));
            c.lods = new GaussianSplatAsset[cm.lods.Length];
            bool ok = true;
            for (int L = 0; L < cm.lods.Length; L++)
            {
                string baseName = Path.GetFileNameWithoutExtension(cm.lods[L].file);
                GaussianSplatAsset a = null;
#if UNITY_EDITOR
                a = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetFolder + "/" + baseName + ".asset");
#endif
                if (a == null) { Debug.LogError("[Probe] asset not loaded: " + baseName); ok = false; break; }
                c.lods[L] = a;
            }
            if (!ok) continue;
            m_FullSplats += c.lods[0].splatCount;
            mn = Vector3.Min(mn, new Vector3(cm.boundMin[0], cm.boundMin[1], cm.boundMin[2]));
            mx = Vector3.Max(mx, new Vector3(cm.boundMax[0], cm.boundMax[1], cm.boundMax[2]));

            var go = new GameObject("Chunk_" + cm.id);
            go.SetActive(false);                       // set asset before OnEnable so resources build once
            go.transform.SetParent(transform, false);
            var r = go.AddComponent<GaussianSplatRenderer>();
            r.m_Asset = c.lods[0];
            go.SetActive(true);
            c.rend = r; c.cur = 0;
            m_Chunks.Add(c);
        }

        SceneCentre = (mn + mx) * 0.5f;
        SceneRadius = (mx - mn).magnitude * 0.5f;
        if (autoFrameCamera && cam != null)
        {
            cam.transform.position = transform.TransformPoint(SceneCentre + new Vector3(0f, 0.15f * SceneRadius, -2.2f * SceneRadius));
            cam.transform.LookAt(transform.TransformPoint(SceneCentre));
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, SceneRadius * 12f);
        }
        Debug.Log($"[Probe] {m_Chunks.Count} chunks, full={m_FullSplats} splats, centre={SceneCentre} radius={SceneRadius:F1}");
    }

    void Update()
    {
        if (cam == null) { cam = Camera.main; if (cam == null) return; }
        m_Frame++;
        if (m_Frame % Mathf.Max(1, evalEveryNFrames) != 0) return;

        float focal = Screen.height / (2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
        float scale = transform.lossyScale.x;
        int resident = 0;
        foreach (var c in m_Chunks)
        {
            int desired;
            if (forceLod0) desired = 0;
            else
            {
                Vector3 wc = transform.TransformPoint(c.localCentre);
                float dist = Vector3.Distance(cam.transform.position, wc);
                float projPx = (c.localExtent * scale) * focal / Mathf.Max(dist, 0.001f);
                desired = projPx >= fullDetailPixels ? 0 : Mathf.Min(1, c.lods.Length - 1);
            }
            if (desired != c.cur) { c.cur = desired; c.rend.m_Asset = c.lods[desired]; }
            resident += c.lods[c.cur].splatCount;
        }
        m_ResidentSplats = resident;
    }

    void OnGUI()
    {
        if (!showHud) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 18 };
        style.normal.textColor = Color.white;
        var sb = new StringBuilder();
        sb.AppendLine($"StreamedLodProbe   resident={m_ResidentSplats / 1000}K / full={m_FullSplats / 1000}K   fullPx={fullDetailPixels:F0}{(forceLod0 ? "  [FORCE LOD0]" : "")}");
        foreach (var c in m_Chunks) sb.Append($"c{c.meta.id}=LOD{c.cur}({c.lods[c.cur].splatCount / 1000}K)   ");
        GUI.Label(new Rect(12, 10, 1400, 120), sb.ToString(), style);
    }
}
