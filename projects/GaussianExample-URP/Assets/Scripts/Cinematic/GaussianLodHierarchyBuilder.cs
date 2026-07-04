// SPDX-License-Identifier: MIT
// Builds the Chunk_N / LOD0..LOD4 hierarchy under a GaussianLodStreamAsync root from
// manifest.json + baked GaussianSplatAsset files. Shared between the editor window and
// the custom inspector.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using GaussianSplatting.Runtime;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GsplatLod
{
    public static class GaussianLodHierarchyBuilder
    {
        public const string kDefaultChunksRootName = "Chunks";
        public const int kDefaultLodLevels = 5;

        public struct BuildOptions
        {
            public string manifestPath;
            public string assetFolder;
            public int expectedLodLevels;
            public bool assignPreviewAssets;
            public bool clearExisting;
            public string chunksRootName;
            /// <summary>Refresh only slots in [min,max]. -1 = all levels.</summary>
            public int refreshLodMin;
            public int refreshLodMax;
            /// <summary>When disk asset is missing, keep the slot's existing previewAsset.</summary>
            public bool preserveExistingPreviewWhenMissing;
            /// <summary>After refresh/build, apply this global preview LOD (-1 = streamer default).</summary>
            public int applyPreviewLodLevel;
        }

        public struct BuildResult
        {
            public bool success;
            public string message;
            public int chunkCount;
            public int lodSlotsCreated;
        }

#if UNITY_EDITOR
        public static BuildResult Build(GaussianLodStreamAsync streamer, BuildOptions opts)
        {
            if (streamer == null)
                return Fail("No streamer target.");

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(streamer.gameObject);
            bool editingPrefabAsset = !string.IsNullOrEmpty(prefabPath)
                && AssetDatabase.GetAssetPath(streamer.gameObject) == prefabPath;
            GameObject prefabContents = null;
            if (editingPrefabAsset)
            {
                prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
                streamer = prefabContents.GetComponent<GaussianLodStreamAsync>();
                if (streamer == null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                    return Fail("Prefab has no GaussianLodStreamAsync.");
                }
            }

            try
            {
                return BuildInternal(streamer, opts, prefabPath, prefabContents);
            }
            finally
            {
                if (prefabContents != null)
                    PrefabUtility.UnloadPrefabContents(prefabContents);
            }
        }

        /// <summary>
        /// Updates previewAsset / metadata on existing Chunk_N / LOD* slots without recreating hierarchy.
        /// </summary>
        public static BuildResult RefreshPreviewAssets(GaussianLodStreamAsync streamer, BuildOptions opts)
        {
            if (streamer == null)
                return Fail("No streamer target.");

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(streamer.gameObject);
            bool editingPrefabAsset = !string.IsNullOrEmpty(prefabPath)
                && AssetDatabase.GetAssetPath(streamer.gameObject) == prefabPath;
            GameObject prefabContents = null;
            if (editingPrefabAsset)
            {
                prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
                streamer = prefabContents.GetComponent<GaussianLodStreamAsync>();
                if (streamer == null)
                {
                    PrefabUtility.UnloadPrefabContents(prefabContents);
                    return Fail("Prefab has no GaussianLodStreamAsync.");
                }
            }

            try
            {
                return RefreshPreviewAssetsInternal(streamer, opts, prefabPath, prefabContents);
            }
            finally
            {
                if (prefabContents != null)
                    PrefabUtility.UnloadPrefabContents(prefabContents);
            }
        }

        static BuildResult RefreshPreviewAssetsInternal(GaussianLodStreamAsync streamer, BuildOptions opts,
            string prefabPath, GameObject prefabContents)
        {
            opts.manifestPath = LodManifestResolver.Resolve(opts.manifestPath ?? streamer.manifestPath, "[HierarchyBuilder]");
            if (string.IsNullOrEmpty(opts.manifestPath) || !File.Exists(opts.manifestPath))
                return Fail($"Manifest not found: '{opts.manifestPath}'");

            LodManifest man;
            try { man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(opts.manifestPath)); }
            catch (Exception ex) { return Fail($"Manifest parse error: {ex.Message}"); }

            if (!LodManifestValidator.Validate(man, "[HierarchyBuilder]", out var vErr))
                return Fail(vErr);

            string assetFolder = string.IsNullOrEmpty(opts.assetFolder) ? "Assets/GaussianAssets" : opts.assetFolder;
            string rootName = string.IsNullOrEmpty(opts.chunksRootName) ? kDefaultChunksRootName : opts.chunksRootName;

            Transform chunksRoot = streamer.transform.Find(rootName);
            if (chunksRoot == null)
                return Fail($"No '{rootName}' root — run Build Hierarchy first.");

            int slotsRefreshed = 0;
            for (int ci = 0; ci < man.chunks.Length; ci++)
            {
                var cm = man.chunks[ci];
                if (cm.lods == null || cm.lods.Length == 0) continue;

                if (EditorUtility.DisplayCancelableProgressBar("Refresh LOD Preview Assets",
                        $"Chunk {cm.id + 1}/{man.chunks.Length}", (float)ci / man.chunks.Length))
                {
                    EditorUtility.ClearProgressBar();
                    return Fail("Refresh cancelled.");
                }

                Transform chunkT = chunksRoot.Find($"Chunk_{cm.id}");
                if (chunkT == null)
                {
                    Debug.LogWarning($"[HierarchyBuilder] Chunk_{cm.id} missing — skipped refresh.");
                    continue;
                }

                var chunk = chunkT.GetComponent<GaussianSplatChunk>();
                if (chunk == null) continue;

                for (int L = 0; L < cm.lods.Length; L++)
                {
                    if (!IsLodInRefreshRange(L, opts)) continue;

                    var lm = cm.lods[L];
                    Transform lodT = chunkT.Find($"LOD{L}");
                    if (lodT == null) continue;

                    var slot = lodT.GetComponent<GaussianSplatLodSlot>();
                    if (slot == null) continue;

                    string addr = LodManifestValidator.ResolveAddr(man, lm);
                    slot.lodLevel = L;
                    slot.addressableKey = addr;
                    slot.splatCount = lm.splatCount;

                    if (opts.assignPreviewAssets && !string.IsNullOrEmpty(addr))
                    {
                        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>($"{assetFolder}/{addr}.asset");
                        if (asset != null)
                        {
                            slot.previewAsset = asset;
                            if (asset.splatCount != lm.splatCount)
                            {
                                Debug.LogWarning(
                                    $"[HierarchyBuilder] chunk {cm.id} LOD{L}: asset has {asset.splatCount:N0} splats " +
                                    $"but manifest expects {lm.splatCount:N0} — re-import {addr}.ply");
                            }
                        }
                        else if (!opts.preserveExistingPreviewWhenMissing)
                        {
                            slot.previewAsset = null;
                        }
                    }

                    slot.WirePreviewToRenderer();

                    slotsRefreshed++;
                    EditorUtility.SetDirty(slot);
                }

                chunk.RefreshLodSlotsFromChildren();
                EditorUtility.SetDirty(chunk);
            }

            EditorUtility.ClearProgressBar();

            int previewLod = opts.applyPreviewLodLevel >= 0
                ? opts.applyPreviewLodLevel
                : streamer.EditorGlobalPreviewLod;
            SetStreamerPreviewLod(streamer, previewLod);
            WireAllPreviewAssets(streamer);
            ApplyGlobalPreviewToAllChunks(streamer, previewLod);

            EditorUtility.SetDirty(streamer);

            if (prefabContents != null && !string.IsNullOrEmpty(prefabPath))
                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);

            return new BuildResult
            {
                success = true,
                message = $"Refreshed {slotsRefreshed} LOD slot(s) on {man.chunks.Length} chunks (LOD preview {previewLod}).",
                chunkCount = man.chunks.Length,
                lodSlotsCreated = slotsRefreshed,
            };
        }

        static bool IsLodInRefreshRange(int lodLevel, BuildOptions opts)
        {
            int min = opts.refreshLodMin < 0 ? 0 : opts.refreshLodMin;
            int max = opts.refreshLodMax < 0 ? int.MaxValue : opts.refreshLodMax;
            return lodLevel >= min && lodLevel <= max;
        }

        static void SetStreamerPreviewLod(GaussianLodStreamAsync streamer, int level)
        {
            var so = new SerializedObject(streamer);
            var lodProp = so.FindProperty("editorGlobalPreviewLod");
            if (lodProp != null)
            {
                lodProp.intValue = level;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static BuildResult BuildInternal(GaussianLodStreamAsync streamer, BuildOptions opts,
            string prefabPath, GameObject prefabContents)
        {
            opts.manifestPath = LodManifestResolver.Resolve(opts.manifestPath ?? streamer.manifestPath, "[HierarchyBuilder]");
            if (string.IsNullOrEmpty(opts.manifestPath) || !File.Exists(opts.manifestPath))
                return Fail($"Manifest not found: '{opts.manifestPath}'");

            LodManifest man;
            try { man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(opts.manifestPath)); }
            catch (Exception ex) { return Fail($"Manifest parse error: {ex.Message}"); }

            if (!LodManifestValidator.Validate(man, "[HierarchyBuilder]", out var vErr))
                return Fail(vErr);

            int lodLevels = opts.expectedLodLevels > 0 ? opts.expectedLodLevels : (man.lodLevels > 0 ? man.lodLevels : kDefaultLodLevels);
            if (man.lodLevels > 0 && man.lodLevels != lodLevels)
                Debug.LogWarning($"[HierarchyBuilder] manifest declares lodLevels={man.lodLevels}, building {lodLevels} slots per chunk.");

            string assetFolder = string.IsNullOrEmpty(opts.assetFolder) ? "Assets/GaussianAssets" : opts.assetFolder;
            string rootName = string.IsNullOrEmpty(opts.chunksRootName) ? kDefaultChunksRootName : opts.chunksRootName;

            Transform chunksRoot = streamer.transform.Find(rootName);
            if (chunksRoot == null)
            {
                var go = new GameObject(rootName);
                Undo.RegisterCreatedObjectUndo(go, "Create Chunks Root");
                go.transform.SetParent(streamer.transform, false);
                chunksRoot = go.transform;
            }
            else if (opts.clearExisting)
            {
                for (int i = chunksRoot.childCount - 1; i >= 0; i--)
                    Undo.DestroyObjectImmediate(chunksRoot.GetChild(i).gameObject);
            }

            int slotsCreated = 0;
            for (int ci = 0; ci < man.chunks.Length; ci++)
            {
                var cm = man.chunks[ci];
                if (EditorUtility.DisplayCancelableProgressBar("LOD Chunk Hierarchy",
                        $"Chunk {cm.id + 1}/{man.chunks.Length}", (float)ci / man.chunks.Length))
                {
                    EditorUtility.ClearProgressBar();
                    return Fail("Build cancelled.");
                }

                if (cm.lods == null || cm.lods.Length == 0)
                {
                    Debug.LogWarning($"[HierarchyBuilder] chunk {cm.id} has no LOD entries — skipped.");
                    continue;
                }

                string chunkName = $"Chunk_{cm.id}";
                Transform chunkT = chunksRoot.Find(chunkName);
                GameObject chunkGo;
                if (chunkT == null)
                {
                    chunkGo = new GameObject(chunkName);
                    Undo.RegisterCreatedObjectUndo(chunkGo, "Create Chunk");
                    chunkGo.transform.SetParent(chunksRoot, false);
                }
                else
                {
                    chunkGo = chunkT.gameObject;
                    if (opts.clearExisting)
                    {
                        for (int i = chunkGo.transform.childCount - 1; i >= 0; i--)
                            Undo.DestroyObjectImmediate(chunkGo.transform.GetChild(i).gameObject);
                    }
                }

                var chunk = chunkGo.GetComponent<GaussianSplatChunk>();
                if (chunk == null)
                    chunk = Undo.AddComponent<GaussianSplatChunk>(chunkGo);

                LodManifestValidator.ResolveChunkBounds(man, cm, out var bmin, out var bmax);
                chunk.chunkId = cm.id;
                chunk.localCentre = (cm.centre != null && cm.centre.Length == 3)
                    ? new Vector3(cm.centre[0], cm.centre[1], cm.centre[2])
                    : (bmin + bmax) * 0.5f;
                chunk.localSize = bmax - bmin;
                chunk.ResetTransformToOrigin();
                chunk.ClearResident();

                int levelsToBuild = Mathf.Min(lodLevels, cm.lods.Length);
                var slots = new List<GaussianSplatLodSlot>(levelsToBuild);
                for (int L = 0; L < levelsToBuild; L++)
                {
                    var lm = cm.lods[L];
                    string lodName = $"LOD{L}";
                    Transform lodT = chunkGo.transform.Find(lodName);
                    GameObject lodGo;
                    if (lodT == null)
                    {
                        lodGo = new GameObject(lodName);
                        Undo.RegisterCreatedObjectUndo(lodGo, "Create LOD Slot");
                        lodGo.transform.SetParent(chunkGo.transform, false);
                    }
                    else lodGo = lodT.gameObject;

                    var slot = lodGo.GetComponent<GaussianSplatLodSlot>();
                    if (slot == null)
                        slot = Undo.AddComponent<GaussianSplatLodSlot>(lodGo);

                    if (lodGo.GetComponent<GaussianSplatRenderer>() == null)
                        Undo.AddComponent<GaussianSplatRenderer>(lodGo);

                    string addr = LodManifestValidator.ResolveAddr(man, lm);
                    slot.lodLevel = L;
                    slot.addressableKey = addr;
                    slot.splatCount = lm.splatCount;

                    if (opts.assignPreviewAssets && !string.IsNullOrEmpty(addr))
                    {
                        string assetPath = $"{assetFolder}/{addr}.asset";
                        var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                        slot.previewAsset = asset;
                        if (asset != null && asset.splatCount != lm.splatCount)
                        {
                            Debug.LogWarning(
                                $"[HierarchyBuilder] chunk {cm.id} LOD{L}: asset has {asset.splatCount:N0} splats " +
                                $"but manifest expects {lm.splatCount:N0} — re-import {addr}.ply");
                        }
                        slot.WirePreviewToRenderer();
                    }
                    else if (!opts.assignPreviewAssets)
                        slot.Clear();

                    slots.Add(slot);
                    slotsCreated++;
                }

                chunk.RefreshLodSlotsFromChildren();
                EditorUtility.SetDirty(chunk);
            }

            EditorUtility.ClearProgressBar();

            // Wire streamer fields
            var so = new SerializedObject(streamer);
            var chunksRootProp = so.FindProperty("chunksRoot");
            var preferProp = so.FindProperty("preferPrebuiltHierarchy");
            if (chunksRootProp != null) chunksRootProp.objectReferenceValue = chunksRoot;
            if (preferProp != null) preferProp.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(streamer);
            EditorUtility.SetDirty(chunksRoot.gameObject);

            int previewLod = opts.applyPreviewLodLevel >= 0
                ? opts.applyPreviewLodLevel
                : streamer.EditorGlobalPreviewLod;
            SetStreamerPreviewLod(streamer, previewLod);
            WireAllPreviewAssets(streamer);
            ApplyGlobalPreviewToAllChunks(streamer, previewLod);

            if (prefabContents != null && !string.IsNullOrEmpty(prefabPath))
                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);

            return new BuildResult
            {
                success = true,
                message = $"Built {man.chunks.Length} chunks × {lodLevels} LOD slots under '{rootName}'.",
                chunkCount = man.chunks.Length,
                lodSlotsCreated = slotsCreated,
            };
        }

        static void ApplyGlobalPreviewToAllChunks(GaussianLodStreamAsync streamer, int level)
        {
            if (streamer == null) return;
            GaussianSplatSettings.editorPreviewBypassOctreeCulling = true;
            foreach (var chunk in streamer.GetComponentsInChildren<GaussianSplatChunk>(true))
            {
                if (chunk == null || chunk.LodCount == 0) continue;
                chunk.SetEditorPreviewLod(Mathf.Clamp(level, 0, chunk.LodCount - 1));
            }
            RefreshActivePreviewRenderers(streamer);
            EditorUtility.SetDirty(streamer);
            SceneView.RepaintAll();
        }

        static void RefreshActivePreviewRenderers(GaussianLodStreamAsync streamer)
        {
            if (streamer == null) return;
            foreach (var slot in streamer.GetComponentsInChildren<GaussianSplatLodSlot>(true))
            {
                if (slot == null) continue;
                slot.WirePreviewToRenderer();
                if (!slot.gameObject.activeSelf) continue;
                var r = slot.Renderer;
                if (r == null || r.m_Asset == null) continue;
                r.EditorForceReloadAsset();
            }
        }

        /// <summary>Wires previewAsset into every slot's GaussianSplatRenderer (editor + prefab save).</summary>
        public static int WireAllPreviewAssets(GaussianLodStreamAsync streamer)
        {
            if (streamer == null) return 0;
            int n = 0;
            foreach (var slot in streamer.GetComponentsInChildren<GaussianSplatLodSlot>(true))
            {
                if (slot == null || slot.previewAsset == null) continue;
                slot.WirePreviewToRenderer();
                EditorUtility.SetDirty(slot);
                n++;
            }
            EditorUtility.SetDirty(streamer);
            return n;
        }

        public static BuildResult Clear(GaussianLodStreamAsync streamer, string chunksRootName = null)
        {
            if (streamer == null) return Fail("No streamer target.");

            string prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(streamer.gameObject);
            bool editingPrefabAsset = !string.IsNullOrEmpty(prefabPath)
                && AssetDatabase.GetAssetPath(streamer.gameObject) == prefabPath;
            GameObject prefabContents = null;
            if (editingPrefabAsset)
            {
                prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
                streamer = prefabContents.GetComponent<GaussianLodStreamAsync>();
            }

            try
            {
                return ClearInternal(streamer, chunksRootName, prefabPath, prefabContents);
            }
            finally
            {
                if (prefabContents != null)
                    PrefabUtility.UnloadPrefabContents(prefabContents);
            }
        }

        static BuildResult ClearInternal(GaussianLodStreamAsync streamer, string chunksRootName,
            string prefabPath, GameObject prefabContents)
        {
            string rootName = string.IsNullOrEmpty(chunksRootName) ? kDefaultChunksRootName : chunksRootName;
            Transform chunksRoot = streamer.transform.Find(rootName);
            if (chunksRoot == null)
                return new BuildResult { success = true, message = "No Chunks root to clear." };

            for (int i = chunksRoot.childCount - 1; i >= 0; i--)
                Undo.DestroyObjectImmediate(chunksRoot.GetChild(i).gameObject);
            Undo.DestroyObjectImmediate(chunksRoot.gameObject);

            var so = new SerializedObject(streamer);
            var chunksRootProp = so.FindProperty("chunksRoot");
            if (chunksRootProp != null) chunksRootProp.objectReferenceValue = null;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(streamer);

            if (prefabContents != null && !string.IsNullOrEmpty(prefabPath))
                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);

            return new BuildResult { success = true, message = "Cleared chunk hierarchy." };
        }

        static BuildResult Fail(string msg) => new BuildResult { success = false, message = msg };
#endif
    }
}
