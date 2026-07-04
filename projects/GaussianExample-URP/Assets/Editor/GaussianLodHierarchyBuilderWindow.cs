// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    public class GaussianLodHierarchyBuilderWindow : EditorWindow
    {
        GaussianLodStreamAsync m_Target;
        string m_AssetFolder = "Assets/GaussianAssets";
        int m_LodLevels = GaussianLodHierarchyBuilder.kDefaultLodLevels;
        bool m_AssignPreviewAssets = true;
        bool m_ClearExisting = true;
        Vector2 m_Scroll;

        [MenuItem("Gaussian Splatting/LOD Chunk Hierarchy Builder")]
        public static void Open()
        {
            var w = GetWindow<GaussianLodHierarchyBuilderWindow>("LOD Chunk Builder");
            w.minSize = new Vector2(360, 280);
        }

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField("LOD Chunk Hierarchy Builder", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Creates Chunk_N objects with LOD0..LOD4 children under the streamer. " +
                "Each LOD child gets a GaussianSplatRenderer (octree culling per slot). " +
                "GaussianLodStreamAsync binds to this hierarchy at runtime for Addressables streaming.",
                MessageType.Info);

            m_Target = (GaussianLodStreamAsync)EditorGUILayout.ObjectField("Streamer", m_Target, typeof(GaussianLodStreamAsync), true);
            if (m_Target == null && Selection.activeGameObject != null)
                m_Target = Selection.activeGameObject.GetComponentInParent<GaussianLodStreamAsync>();

            if (m_Target != null)
                EditorGUILayout.LabelField("Manifest", m_Target.manifestPath, EditorStyles.miniLabel);

            m_AssetFolder = EditorGUILayout.TextField("Asset Folder", m_AssetFolder);
            m_LodLevels = EditorGUILayout.IntSlider("LOD Levels / Chunk", m_LodLevels, 1, 8);
            m_AssignPreviewAssets = EditorGUILayout.Toggle("Assign Preview Assets (Editor)", m_AssignPreviewAssets);
            m_ClearExisting = EditorGUILayout.Toggle("Clear Existing Chunks", m_ClearExisting);

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(m_Target == null))
            {
                if (GUILayout.Button("Build Hierarchy", GUILayout.Height(32)))
                    RunBuild();

                if (GUILayout.Button("Clear Hierarchy"))
                {
                    if (EditorUtility.DisplayDialog("Clear LOD hierarchy?", "Remove all Chunk_* objects?", "Clear", "Cancel"))
                    {
                        var r = GaussianLodHierarchyBuilder.Clear(m_Target);
                        Debug.Log($"[HierarchyBuilder] {r.message}");
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        void RunBuild()
        {
            var opts = new GaussianLodHierarchyBuilder.BuildOptions
            {
                manifestPath = m_Target.manifestPath,
                assetFolder = m_AssetFolder,
                expectedLodLevels = m_LodLevels,
                assignPreviewAssets = m_AssignPreviewAssets,
                clearExisting = m_ClearExisting,
            };
            var result = GaussianLodHierarchyBuilder.Build(m_Target, opts);
            if (result.success)
                Debug.Log($"[HierarchyBuilder] {result.message} ({result.lodSlotsCreated} slots)");
            else
                Debug.LogError($"[HierarchyBuilder] {result.message}");
        }
    }

    static class GaussianLodHierarchyBuilderMenu
    {
        const string kDefaultPrefab = "Assets/GaussianLodStreamAsync.prefab";

        [MenuItem("Gaussian Splatting/Build LOD Hierarchy On Selected")]
        public static void BuildOnSelected()
        {
            var streamer = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<GaussianLodStreamAsync>()
                : null;
            if (streamer == null)
            {
                Debug.LogError("[HierarchyBuilder] Select a GameObject with GaussianLodStreamAsync.");
                return;
            }
            RunDefaultBuild(streamer);
        }

        [MenuItem("Gaussian Splatting/Wire All LOD Renderer Assets On Streamer")]
        public static void WireAllOnDefaultPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kDefaultPrefab);
            var streamer = prefab != null ? prefab.GetComponent<GaussianLodStreamAsync>() : null;
            if (streamer == null)
            {
                Debug.LogError($"[HierarchyBuilder] Prefab not found or has no streamer: {kDefaultPrefab}");
                return;
            }

            var contents = PrefabUtility.LoadPrefabContents(kDefaultPrefab);
            try
            {
                streamer = contents.GetComponent<GaussianLodStreamAsync>();
                int wired = GaussianLodHierarchyBuilder.WireAllPreviewAssets(streamer);
                GaussianLodGlobalPreview.Apply(streamer, streamer.EditorGlobalPreviewLod);
                PrefabUtility.SaveAsPrefabAsset(contents, kDefaultPrefab);
                Debug.Log($"[HierarchyBuilder] Wired {wired} LOD renderer assets on prefab.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            foreach (var sceneStreamer in Object.FindObjectsByType<GaussianLodStreamAsync>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (sceneStreamer == streamer) continue;
                int n = GaussianLodHierarchyBuilder.WireAllPreviewAssets(sceneStreamer);
                GaussianLodGlobalPreview.Apply(sceneStreamer, sceneStreamer.EditorGlobalPreviewLod);
                EditorUtility.SetDirty(sceneStreamer);
                Debug.Log($"[HierarchyBuilder] Wired {n} LOD renderer assets on {sceneStreamer.gameObject.scene.name}.");
            }
        }

        [MenuItem("Gaussian Splatting/Build LOD Hierarchy On GaussianLodStreamAsync Prefab")]
        public static void BuildOnDefaultPrefab()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kDefaultPrefab);
            if (prefab == null)
            {
                Debug.LogError($"[HierarchyBuilder] Prefab not found: {kDefaultPrefab}");
                return;
            }
            var streamer = prefab.GetComponent<GaussianLodStreamAsync>();
            if (streamer == null)
            {
                Debug.LogError("[HierarchyBuilder] Prefab has no GaussianLodStreamAsync.");
                return;
            }
            RunDefaultBuild(streamer);
        }

        static void RunDefaultBuild(GaussianLodStreamAsync streamer)
        {
            var opts = new GaussianLodHierarchyBuilder.BuildOptions
            {
                manifestPath = streamer.manifestPath,
                assetFolder = "Assets/GaussianAssets",
                expectedLodLevels = GaussianLodHierarchyBuilder.kDefaultLodLevels,
                assignPreviewAssets = true,
                clearExisting = true,
            };
            var r = GaussianLodHierarchyBuilder.Build(streamer, opts);
            if (r.success) Debug.Log($"[HierarchyBuilder] {r.message} ({r.lodSlotsCreated} slots)");
            else Debug.LogError($"[HierarchyBuilder] {r.message}");
        }
    }

    [CustomEditor(typeof(GaussianLodStreamAsync))]
    public class GaussianLodStreamAsyncEditor : UnityEditor.Editor
    {
        SerializedProperty m_EditorGlobalPreviewLod;

        void OnEnable()
        {
            m_EditorGlobalPreviewLod = serializedObject.FindProperty(GaussianLodGlobalPreview.kPreviewLodProperty);
            EditorApplication.update += OnEditorUpdate;
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
        }

        void OnEditorUpdate()
        {
            if (Application.isPlaying && target != null)
                Repaint();
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawDefaultInspector();

            var streamer = (GaussianLodStreamAsync)target;
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Chunk Hierarchy", EditorStyles.boldLabel);

            int chunkCount = streamer.GetComponentsInChildren<GaussianSplatChunk>(true).Length;
            EditorGUILayout.LabelField("Pre-built chunks in hierarchy", chunkCount.ToString());

            int previewLod = m_EditorGlobalPreviewLod != null ? m_EditorGlobalPreviewLod.intValue : 0;
            GaussianLodSceneStats.Draw(streamer, previewLod);

            EditorGUILayout.Space(6);
            GaussianLodGlobalPreview.DrawProgressFill(streamer, serializedObject);

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build Chunk Hierarchy"))
                {
                    var opts = new GaussianLodHierarchyBuilder.BuildOptions
                    {
                        manifestPath = streamer.manifestPath,
                        expectedLodLevels = GaussianLodHierarchyBuilder.kDefaultLodLevels,
                        assignPreviewAssets = true,
                        clearExisting = true,
                    };
                    var r = GaussianLodHierarchyBuilder.Build(streamer, opts);
                    if (r.success)
                    {
                        Debug.Log($"[HierarchyBuilder] {r.message}");
                        GaussianLodGlobalPreview.Apply(streamer, m_EditorGlobalPreviewLod?.intValue ?? 4);
                    }
                    else Debug.LogError($"[HierarchyBuilder] {r.message}");
                }
                if (GUILayout.Button("Open Builder Window"))
                    GaussianLodHierarchyBuilderWindow.Open();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
