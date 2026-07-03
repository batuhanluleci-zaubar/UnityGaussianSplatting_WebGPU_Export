// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    [CustomEditor(typeof(GaussianSplatChunk))]
    public class GaussianSplatChunkEditor : UnityEditor.Editor
    {
        SerializedProperty m_ChunkId;
        SerializedProperty m_LocalCentre;
        SerializedProperty m_LocalSize;
        SerializedProperty m_EditorPreviewLod;

        void OnEnable()
        {
            m_ChunkId = serializedObject.FindProperty("chunkId");
            m_LocalCentre = serializedObject.FindProperty("localCentre");
            m_LocalSize = serializedObject.FindProperty("localSize");
            m_EditorPreviewLod = serializedObject.FindProperty("editorPreviewLod");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var chunk = (GaussianSplatChunk)target;
            chunk.RefreshLodSlotsFromChildren();

            EditorGUILayout.PropertyField(m_ChunkId);
            EditorGUILayout.PropertyField(m_LocalCentre);
            EditorGUILayout.PropertyField(m_LocalSize);

            if (chunk.HasTransformOffset)
            {
                EditorGUILayout.HelpBox(
                    "Chunk transform is offset from origin — splats will render in the wrong place (gaps, missing floor). " +
                    "Click Reset below or run Gaussian Splatting → Reset Chunk Transforms On Prefab.",
                    MessageType.Error);
            }

            EditorGUILayout.Space(6);
            DrawLodProgressFill(chunk);

            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset Transform to Origin"))
                {
                    Undo.RecordObject(chunk.transform, "Reset Chunk Transform");
                    chunk.ResetTransformToOrigin();
                    EditorUtility.SetDirty(chunk);
                }
                if (GUILayout.Button("Focus Chunk"))
                    GaussianSplatChunkSelection.FocusChunk(chunk);
            }

            serializedObject.ApplyModifiedProperties();
        }

        void SetPreviewLod(GaussianSplatChunk chunk, int level)
        {
            Undo.RecordObject(chunk, "Preview LOD");
            chunk.SetEditorPreviewLod(level);
            m_EditorPreviewLod.intValue = level;
            serializedObject.ApplyModifiedProperties();
            SceneView.RepaintAll();
        }

        void DrawLodProgressFill(GaussianSplatChunk chunk)
        {
            int lodCount = chunk.LodCount;
            if (lodCount == 0)
            {
                EditorGUILayout.HelpBox("No LOD slots found under this chunk.", MessageType.Info);
                return;
            }

            int maxLod = lodCount - 1;
            int current = m_EditorPreviewLod.intValue;
            if (current < 0 || current > maxLod)
                current = chunk.activeLod >= 0 ? chunk.activeLod : maxLod;

            EditorGUILayout.LabelField("LOD Preview", EditorStyles.boldLabel);

            float t = maxLod > 0 ? (float)current / maxLod : 0f;
            var barRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(22));
            EditorGUI.ProgressBar(barRect, t, $"LOD{current}  ({current + 1}/{lodCount})");

            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.IntSlider("Scrub LOD", current, 0, maxLod);
            if (EditorGUI.EndChangeCheck())
                SetPreviewLod(chunk, next);

            var slot = chunk.GetLod(next);
            if (slot != null)
            {
                EditorGUILayout.LabelField("Splat count", slot.splatCount.ToString("N0"), EditorStyles.miniLabel);
                EditorGUILayout.ObjectField("Preview asset", slot.previewAsset,
                    typeof(GaussianSplatting.Runtime.GaussianSplatAsset), false);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀ Finer") && next > 0)
                    SetPreviewLod(chunk, next - 1);
                if (GUILayout.Button("Coarser ▶") && next < maxLod)
                    SetPreviewLod(chunk, next + 1);
            }
        }
    }

    /// <summary>
    /// Chunk isolation runs only on explicit focus (Hierarchy double-click, Focus button, F key),
    /// not on ordinary single-click selection.
    /// </summary>
    [InitializeOnLoad]
    static class GaussianSplatChunkSelection
    {
        static readonly List<GameObject> s_HiddenSiblings = new List<GameObject>(64);
        static GaussianSplatChunk s_IsolatedChunk;

        static GaussianSplatChunkSelection()
        {
            EditorApplication.hierarchyWindowItemOnGUI += OnHierarchyItemGUI;
            Selection.selectionChanged += OnSelectionChanged;
            SceneView.duringSceneGui += OnSceneGUI;
        }

        static void OnHierarchyItemGUI(int instanceID, Rect selectionRect)
        {
            if (Application.isPlaying) return;
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0 || e.clickCount != 2) return;

            var go = EditorUtility.InstanceIDToObject(instanceID) as GameObject;
            if (go == null) return;

            var chunk = ResolveChunk(go);
            if (chunk == null) return;

            FocusChunk(chunk);
        }

        static void OnSceneGUI(SceneView view)
        {
            if (Application.isPlaying) return;
            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode != KeyCode.F || e.alt || e.control || e.shift)
                return;
            if (EditorGUIUtility.editingTextField) return;

            var chunk = TryGetSelectedChunk();
            if (chunk == null) return;

            FocusChunk(chunk);
            e.Use();
        }

        static void OnSelectionChanged()
        {
            if (Application.isPlaying) return;
            if (s_IsolatedChunk == null) return;

            var selected = TryGetSelectedChunk();
            if (selected == null || selected != s_IsolatedChunk)
                RestoreAllChunks();
        }

        static GaussianSplatChunk TryGetSelectedChunk()
        {
            if (Selection.gameObjects.Length != 1) return null;

            var go = Selection.activeGameObject;
            if (go == null || go.name == GaussianLodHierarchyBuilder.kDefaultChunksRootName) return null;

            return ResolveChunk(go);
        }

        static GaussianSplatChunk ResolveChunk(GameObject go) =>
            go.GetComponent<GaussianSplatChunk>() ?? go.GetComponentInParent<GaussianSplatChunk>();

        public static void FocusChunk(GaussianSplatChunk chunk)
        {
            if (chunk == null) return;

            RestoreAllChunks();
            if (chunk.HasTransformOffset)
            {
                Undo.RecordObject(chunk.transform, "Focus Chunk");
                chunk.ResetTransformToOrigin();
                EditorUtility.SetDirty(chunk);
            }

            s_IsolatedChunk = chunk;
            HideSiblingChunks(chunk);
            Frame(chunk);
            SceneView.RepaintAll();
        }

        static void HideSiblingChunks(GaussianSplatChunk selected)
        {
            var parent = selected.transform.parent;
            if (parent == null) return;

            // SceneVisibilityManager does not stop GaussianSplatRenderer — reset eye icons, use SetActive.
            var svm = SceneVisibilityManager.instance;
            svm.Show(parent.gameObject, true);
            svm.Show(selected.gameObject, true);

            if (!selected.gameObject.activeSelf)
            {
                Undo.RecordObject(selected.gameObject, "Isolate Chunk");
                selected.gameObject.SetActive(true);
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                var sibling = parent.GetChild(i).gameObject;
                if (sibling == selected.gameObject) continue;

                svm.Show(sibling, true);
                if (!sibling.activeSelf) continue;

                Undo.RecordObject(sibling, "Isolate Chunk");
                sibling.SetActive(false);
                s_HiddenSiblings.Add(sibling);
            }
        }

        static void RestoreAllChunks()
        {
            if (s_HiddenSiblings.Count == 0 && s_IsolatedChunk == null)
                return;

            var svm = SceneVisibilityManager.instance;
            for (int i = 0; i < s_HiddenSiblings.Count; i++)
            {
                var go = s_HiddenSiblings[i];
                if (go == null) continue;
                Undo.RecordObject(go, "Show All Chunks");
                go.SetActive(true);
                svm.Show(go, true);
            }
            s_HiddenSiblings.Clear();
            s_IsolatedChunk = null;
            SceneView.RepaintAll();
        }

        public static void Frame(GaussianSplatChunk chunk)
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || chunk == null) return;
            sv.Frame(chunk.WorldBounds, false);
            sv.Repaint();
        }

        [MenuItem("Gaussian Splatting/Show All Chunks")]
        public static void ShowAllChunksMenu()
        {
            RestoreAllChunks();
            // Reset any leftover SceneVisibilityManager state from the old approach.
            var chunks = Object.FindObjectsByType<GaussianSplatChunk>(FindObjectsSortMode.None);
            var svm = SceneVisibilityManager.instance;
            foreach (var c in chunks)
            {
                if (c == null) continue;
                svm.Show(c.gameObject, true);
                if (!c.gameObject.activeSelf)
                {
                    Undo.RecordObject(c.gameObject, "Show All Chunks");
                    c.gameObject.SetActive(true);
                }
            }
            SceneView.RepaintAll();
        }
    }

    static class GaussianSplatChunkTransformMenu
    {
        const string kPrefab = "Assets/GaussianLodStreamAsync.prefab";

        [MenuItem("Gaussian Splatting/Reset All Chunk Transforms")]
        public static void ResetAllInOpenScenes()
        {
            var chunks = Object.FindObjectsByType<GaussianSplatChunk>(FindObjectsSortMode.None);
            ResetChunks(chunks);
        }

        [MenuItem("Gaussian Splatting/Reset Chunk Transforms On GaussianLodStreamAsync Prefab")]
        public static void ResetPrefab()
        {
            var contents = PrefabUtility.LoadPrefabContents(kPrefab);
            try
            {
                var chunks = contents.GetComponentsInChildren<GaussianSplatChunk>(true);
                ResetChunks(chunks);
                PrefabUtility.SaveAsPrefabAsset(contents, kPrefab);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        static void ResetChunks(GaussianSplatChunk[] chunks)
        {
            int n = 0;
            foreach (var c in chunks)
            {
                if (c == null || !c.HasTransformOffset) continue;
                Undo.RecordObject(c.transform, "Reset Chunk Transform");
                c.ResetTransformToOrigin();
                EditorUtility.SetDirty(c);
                n++;
            }
            Debug.Log($"[GaussianSplatChunk] Reset {n} chunk transforms to origin (splat assets use absolute scene coords).");
        }
    }
}
#endif
