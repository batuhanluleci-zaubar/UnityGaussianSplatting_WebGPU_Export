// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    public static class GaussianLodSceneStats
    {
        public struct ManifestInfo
        {
            public bool loaded;
            public long sourceSplats;
            public long lod0PruneRemoved;
            public long manifestLod0Total;
        }

        public struct Stats
        {
            public int chunkCount;
            public int activeChunkCount;
            public long totalLod0;
            public long totalAtPreviewLod;
            public int previewLod;
            public long currentlyRendering;
            public int renderingChunks;
            public long[] perLodTotals;
        }

        public static string FormatCount(long count)
        {
            if (count >= 1_000_000)
                return $"{count / 1_000_000f:F2}M  ({count:N0})";
            if (count >= 10_000)
                return $"{count / 1_000f:F0}K  ({count:N0})";
            return count.ToString("N0");
        }

        public static ManifestInfo TryLoadManifest(GaussianLodStreamAsync streamer)
        {
            var info = new ManifestInfo();
            if (streamer == null) return info;

            string path = LodManifestResolver.Resolve(streamer.manifestPath, "[SceneStats]");
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return info;

            try
            {
                var man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(path));
                if (man == null) return info;

                info.loaded = true;
                info.sourceSplats = man.sourceSplatCount;
                info.lod0PruneRemoved = man.lod0PruneRemoved;

                if (man.totalSplatsByLod != null && man.totalSplatsByLod.Length > 0)
                    info.manifestLod0Total = man.totalSplatsByLod[0];
                else if (man.chunks != null)
                {
                    foreach (var cm in man.chunks)
                    {
                        if (cm?.lods == null || cm.lods.Length == 0) continue;
                        info.manifestLod0Total += cm.lods[0].splatCount;
                    }
                }
            }
            catch
            {
                // ignore parse errors in stats panel
            }

            return info;
        }

        public static Stats Gather(GaussianLodStreamAsync streamer, int previewLod)
        {
            var stats = new Stats { previewLod = previewLod };
            if (streamer == null) return stats;

            var chunks = streamer.GetComponentsInChildren<GaussianSplatChunk>(true);
            stats.chunkCount = chunks.Length;
            if (stats.chunkCount == 0) return stats;

            int maxLod = GaussianLodGlobalPreview.GetMaxLodLevel(streamer);
            stats.perLodTotals = new long[maxLod + 1];

            foreach (var chunk in chunks)
            {
                if (chunk == null) continue;
                if (chunk.gameObject.activeInHierarchy)
                    stats.activeChunkCount++;

                for (int lod = 0; lod <= maxLod; lod++)
                {
                    long n = chunk.GetSplatCount(lod);
                    if (lod < stats.perLodTotals.Length)
                        stats.perLodTotals[lod] += n;
                }

                stats.totalLod0 += chunk.GetSplatCount(0);

                int previewLevel = Mathf.Clamp(previewLod, 0, chunk.LodCount > 0 ? chunk.LodCount - 1 : 0);
                stats.totalAtPreviewLod += chunk.GetSplatCount(previewLevel);

                if (!chunk.gameObject.activeInHierarchy) continue;

                bool foundActive = false;
                if (chunk.LodSlots != null)
                {
                    foreach (var slot in chunk.LodSlots)
                    {
                        if (slot == null || !slot.gameObject.activeSelf) continue;
                        var asset = slot.previewAsset;
                        if (asset == null && slot.Renderer != null)
                            asset = slot.Renderer.m_Asset;
                        if (asset == null) continue;
                        stats.currentlyRendering += slot.splatCount;
                        stats.renderingChunks++;
                        foundActive = true;
                        break;
                    }
                }

                if (!foundActive && chunk.activeLod >= 0 && chunk.isResident)
                {
                    stats.currentlyRendering += chunk.GetSplatCount(chunk.activeLod);
                    stats.renderingChunks++;
                }
            }

            return stats;
        }

        public static void Draw(GaussianLodStreamAsync streamer, int previewLod)
        {
            if (streamer == null) return;

            EditorGUILayout.LabelField("Scene Splat Stats", EditorStyles.boldLabel);

            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("Resident splats (runtime)", FormatCount(streamer.ResidentSplats));
                EditorGUILayout.LabelField("Resident / visible chunks",
                    $"{streamer.ResidentChunks} / {streamer.VisibleChunks}  (of {streamer.TotalChunkCount})",
                    EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Device budget", FormatCount(streamer.deviceBudget), EditorStyles.miniLabel);
            }

            var stats = Gather(streamer, previewLod);
            var manifest = TryLoadManifest(streamer);

            if (stats.chunkCount == 0)
            {
                EditorGUILayout.HelpBox("No chunk hierarchy — build chunks to see scene splat totals.", MessageType.Info);
                return;
            }

            if (manifest.loaded && manifest.sourceSplats > 0)
            {
                long retained = manifest.manifestLod0Total > 0 ? manifest.manifestLod0Total : stats.totalLod0;
                long removed = manifest.lod0PruneRemoved > 0
                    ? manifest.lod0PruneRemoved
                    : manifest.sourceSplats - retained;

                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.LabelField("Source SPZ total", FormatCount(manifest.sourceSplats));

                if (removed > 0)
                {
                    float pct = 100f * removed / manifest.sourceSplats;
                    EditorGUILayout.HelpBox(
                        $"Bake removed {FormatCount(removed)} ({pct:F1}%) at LOD0 via --prune-* / --lod0-prune-* flags. " +
                        "Chunking itself does not drop splats — only optional floater pruning does. " +
                        "Re-bake with --raw-lod0 and all --lod0-prune-* 0 for full fidelity.",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.LabelField("Active chunks", $"{stats.activeChunkCount} / {stats.chunkCount}");

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Full scene total (LOD0)", FormatCount(stats.totalLod0));
                EditorGUILayout.LabelField($"Preview total (LOD{stats.previewLod})", FormatCount(stats.totalAtPreviewLod));
                EditorGUILayout.LabelField("Currently rendering",
                    $"{FormatCount(stats.currentlyRendering)}  ({stats.renderingChunks} chunks)");
            }

            if (stats.perLodTotals != null && stats.perLodTotals.Length > 1)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < stats.perLodTotals.Length; i++)
                    sb.Append($"L{i}={FormatCount(stats.perLodTotals[i])}   ");
                EditorGUILayout.LabelField("Per-LOD totals (all chunks)", sb.ToString(), EditorStyles.miniLabel);
            }

        }
    }
}
#endif
