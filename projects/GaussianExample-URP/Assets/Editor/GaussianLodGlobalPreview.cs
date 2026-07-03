// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    public static class GaussianLodGlobalPreview
    {
        public const string kPreviewLodProperty = "editorGlobalPreviewLod";

        public static int GetMaxLodLevel(GaussianLodStreamAsync streamer)
        {
            if (streamer == null) return GaussianLodHierarchyBuilder.kDefaultLodLevels - 1;

            int max = 0;
            foreach (var chunk in streamer.GetComponentsInChildren<GaussianSplatChunk>(true))
            {
                if (chunk != null && chunk.LodCount > 0)
                    max = Mathf.Max(max, chunk.LodCount - 1);
            }
            return max > 0 ? max : GaussianLodHierarchyBuilder.kDefaultLodLevels - 1;
        }

        public static ApplyResult Apply(GaussianLodStreamAsync streamer, int level)
        {
            var result = new ApplyResult();
            if (streamer == null || Application.isPlaying) return result;

            PrepareFullScenePreview(streamer);

            int maxLod = GetMaxLodLevel(streamer);
            level = Mathf.Clamp(level, 0, maxLod);

            Undo.SetCurrentGroupName("Global LOD Preview");
            int undoGroup = Undo.GetCurrentGroup();

            var chunks = streamer.GetComponentsInChildren<GaussianSplatChunk>(true);
            foreach (var chunk in chunks)
            {
                if (chunk == null || chunk.LodCount == 0) continue;
                Undo.RecordObject(chunk, "Global LOD Preview");
                int chunkLevel = Mathf.Clamp(level, 0, chunk.LodCount - 1);
                chunk.SetEditorPreviewLod(chunkLevel);
                result.appliedChunks++;
                result.totalSplats += chunk.GetSplatCount(chunkLevel);
            }

            Undo.RecordObject(streamer, "Global LOD Preview");
            var so = new SerializedObject(streamer);
            var lodProp = so.FindProperty(kPreviewLodProperty);
            if (lodProp != null)
            {
                lodProp.intValue = level;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(streamer);
            SceneView.RepaintAll();

            result.lodLevel = level;
            return result;
        }

        static void PrepareFullScenePreview(GaussianLodStreamAsync streamer)
        {
            GaussianSplatChunkSelection.ShowAllChunksMenu();

            foreach (var chunk in streamer.GetComponentsInChildren<GaussianSplatChunk>(true))
            {
                if (chunk == null) continue;
                if (chunk.HasTransformOffset)
                {
                    Undo.RecordObject(chunk.transform, "Global LOD Preview");
                    chunk.ResetTransformToOrigin();
                }
                if (!chunk.gameObject.activeSelf)
                {
                    Undo.RecordObject(chunk.gameObject, "Global LOD Preview");
                    chunk.gameObject.SetActive(true);
                }
            }

            var settings = streamer.GetComponent<GaussianSplatting.Runtime.GaussianSplatSettings>();
            if (settings != null && settings.m_EnableOctreeCulling)
            {
                Undo.RecordObject(settings, "Global LOD Preview");
                settings.m_EnableOctreeCulling = false;
            }
        }

        public struct ApplyResult
        {
            public int lodLevel;
            public int appliedChunks;
            public long totalSplats;
        }

        public static void DrawProgressFill(GaussianLodStreamAsync streamer, SerializedObject serializedStreamer)
        {
            if (streamer == null || Application.isPlaying) return;

            int chunkCount = streamer.GetComponentsInChildren<GaussianSplatChunk>(true).Length;
            if (chunkCount == 0)
            {
                EditorGUILayout.HelpBox(
                    "Build the chunk hierarchy first to preview LODs for all chunks at once.",
                    MessageType.Info);
                return;
            }

            var lodProp = serializedStreamer.FindProperty(kPreviewLodProperty);
            int maxLod = GetMaxLodLevel(streamer);
            int current = lodProp != null ? lodProp.intValue : maxLod;
            if (current < 0 || current > maxLod) current = maxLod;

            EditorGUILayout.LabelField("Global LOD Preview (all chunks)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Chunk transforms must stay at (0,0,0) — splat assets use absolute scene coordinates. " +
                "This preview shows all chunks and temporarily disables octree culling for a full-scene composite.",
                MessageType.Info);

            float t = maxLod > 0 ? (float)current / maxLod : 0f;
            var barRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(24));
            EditorGUI.ProgressBar(barRect, t, $"LOD{current} — all {chunkCount} chunks  (0=fine .. {maxLod}=coarse)");

            EditorGUI.BeginChangeCheck();
            int next = EditorGUILayout.IntSlider("Scrub global LOD", current, 0, maxLod);
            if (EditorGUI.EndChangeCheck())
            {
                var r = Apply(streamer, next);
                if (lodProp != null) lodProp.intValue = r.lodLevel;
                serializedStreamer.Update();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Apply global LOD now"))
                {
                    var r = Apply(streamer, current);
                    if (lodProp != null) lodProp.intValue = r.lodLevel;
                    serializedStreamer.Update();
                }
                if (GUILayout.Button("Show all chunks"))
                    GaussianSplatChunkSelection.ShowAllChunksMenu();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("◀ Finer (all)") && current > 0)
                {
                    var r = Apply(streamer, current - 1);
                    if (lodProp != null) lodProp.intValue = r.lodLevel;
                    serializedStreamer.Update();
                }
                if (GUILayout.Button("Coarser (all) ▶") && current < maxLod)
                {
                    var r = Apply(streamer, current + 1);
                    if (lodProp != null) lodProp.intValue = r.lodLevel;
                    serializedStreamer.Update();
                }
            }
        }
    }
}
#endif
