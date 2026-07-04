// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System;
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
            public long totalLod0FromAssets;
            public long totalAtPreviewLod;
            public int previewLod;
            public long currentlyRendering;
            public int renderingChunks;
            public long festsaal10mAssetSplats;
            public long[] perLodTotals;
            public long[] perLodAssetTotals;
        }

        static long TryLoadFestsaal10mAssetSplats()
        {
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatting.Runtime.GaussianSplatAsset>(
                "Assets/GaussianAssets/Festsaal 10m bereinigt.asset");
            return asset != null ? asset.splatCount : 0;
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
            stats.festsaal10mAssetSplats = TryLoadFestsaal10mAssetSplats();

            var chunks = streamer.GetComponentsInChildren<GaussianSplatChunk>(true);
            stats.chunkCount = chunks.Length;
            if (stats.chunkCount == 0) return stats;

            int maxLod = GaussianLodGlobalPreview.GetMaxLodLevel(streamer);
            stats.perLodTotals = new long[maxLod + 1];
            stats.perLodAssetTotals = new long[maxLod + 1];

            foreach (var chunk in chunks)
            {
                if (chunk == null) continue;
                if (chunk.gameObject.activeInHierarchy)
                    stats.activeChunkCount++;

                if (chunk.LodSlots != null)
                {
                    foreach (var slot in chunk.LodSlots)
                    {
                        if (slot == null) continue;
                        int lod = slot.lodLevel;
                        if (lod < 0 || lod > maxLod) continue;
                        stats.perLodTotals[lod] += slot.splatCount;
                        var asset = slot.previewAsset;
                        if (asset == null && slot.Renderer != null)
                            asset = slot.Renderer.m_Asset;
                        if (asset != null)
                            stats.perLodAssetTotals[lod] += asset.splatCount;
                    }
                }

                stats.totalLod0 += chunk.GetSplatCount(0);
                stats.totalLod0FromAssets += GetAssetSplatCount(chunk, 0);

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
                        stats.currentlyRendering += asset.splatCount;
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

        static long GetAssetSplatCount(GaussianSplatChunk chunk, int level)
        {
            var slot = chunk.GetLod(level);
            if (slot == null) return 0;
            var asset = slot.previewAsset;
            if (asset == null && slot.Renderer != null)
                asset = slot.Renderer.m_Asset;
            return asset != null ? asset.splatCount : 0;
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
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField("Source SPZ total (raw)", FormatCount(manifest.sourceSplats));
                    if (stats.festsaal10mAssetSplats > 0)
                        EditorGUILayout.LabelField("Festsaal10M monolithic asset", FormatCount(stats.festsaal10mAssetSplats));
                }

                long spzToMono = manifest.sourceSplats - stats.festsaal10mAssetSplats;
                if (stats.festsaal10mAssetSplats > 0 && spzToMono > 100)
                {
                    EditorGUILayout.HelpBox(
                        $"Monolithic import removed {FormatCount(spzToMono)} splats vs raw SPZ " +
                        "(Unity outlier filter on full-scene import). This is normal for Festsaal 10m bereinigt.",
                        MessageType.Info);
                }

                if (manifest.lod0PruneRemoved > 0)
                {
                    float pct = 100f * manifest.lod0PruneRemoved / manifest.sourceSplats;
                    EditorGUILayout.HelpBox(
                        $"Bake removed {FormatCount(manifest.lod0PruneRemoved)} ({pct:F1}%) at LOD0 via --lod0-prune-*.",
                        MessageType.Warning);
                }
            }

            EditorGUILayout.LabelField("Active chunks", $"{stats.activeChunkCount} / {stats.chunkCount}");

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("LOD0 metadata (manifest slots)", FormatCount(stats.totalLod0));
                EditorGUILayout.LabelField("LOD0 from loaded assets (GPU)", FormatCount(stats.totalLod0FromAssets));
                EditorGUILayout.LabelField($"Preview total (LOD{stats.previewLod})", FormatCount(stats.totalAtPreviewLod));
                EditorGUILayout.LabelField("Currently rendering (asset splats)",
                    $"{FormatCount(stats.currentlyRendering)}  ({stats.renderingChunks} chunks)");
            }

            long manifestLod0 = manifest.loaded && manifest.manifestLod0Total > 0
                ? manifest.manifestLod0Total
                : stats.totalLod0;
            long assetLod0 = stats.totalLod0FromAssets;

            if (manifest.loaded && manifestLod0 > 0 && assetLod0 == 0)
            {
                EditorGUILayout.HelpBox(
                    "No chunk LOD0 assets loaded. Run Tools → Gaussian Splats → Batch Import LOD0 " +
                    "(enable Skip outlier filter), then Build Chunk Hierarchy.",
                    MessageType.Error);
            }
            else if (assetLod0 > 0 && manifestLod0 > 0)
            {
                long delta = assetLod0 - manifestLod0;
                if (Math.Abs(delta) > 500)
                {
                    var msgType = delta < -10000 ? MessageType.Error : MessageType.Warning;
                    EditorGUILayout.HelpBox(
                        $"Chunk GPU total ({FormatCount(assetLod0)}) vs manifest LOD0 ({FormatCount(manifestLod0)}): " +
                        $"delta {delta:+#,0;-#,0}. " +
                        (delta < 0
                            ? "Re-import LOD0 with 'Skip outlier filter' enabled (per-chunk filter drops boundary splats)."
                            : "Unexpected surplus — check for duplicate imports."),
                        msgType);
                }
            }

            if (stats.festsaal10mAssetSplats > 0 && assetLod0 > 0)
            {
                long vsMono = assetLod0 - stats.festsaal10mAssetSplats;
                if (Math.Abs(vsMono) > 500)
                {
                    EditorGUILayout.HelpBox(
                        $"Chunk LOD0 GPU ({FormatCount(assetLod0)}) vs Festsaal10M ({FormatCount(stats.festsaal10mAssetSplats)}): " +
                        $"delta {vsMono:+#,0;-#,0}. " +
                        "Use the same Quality preset for both; compare with only one prefab active in scene.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "Chunk LOD0 splat count matches Festsaal10M monolithic asset (within tolerance).",
                        MessageType.Info);
                }
            }

            if (stats.totalLod0 > 0 && assetLod0 > 0 && Math.Abs(stats.totalLod0 - assetLod0) > stats.totalLod0 * 0.005)
            {
                EditorGUILayout.HelpBox(
                    "Slot metadata still shows manifest counts — rebuild hierarchy after re-import to refresh.",
                    MessageType.None);
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
