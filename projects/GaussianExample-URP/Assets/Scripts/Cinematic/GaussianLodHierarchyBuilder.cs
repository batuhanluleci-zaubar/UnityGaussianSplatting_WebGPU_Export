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
                        if (asset != null && L == levelsToBuild - 1)
                        {
                            slot.SetActiveLod(asset, true);
                            chunk.activeLod = L;
                            chunk.isResident = true;
                        }
                        else
                            slot.Clear();
                    }
                    else slot.Clear();

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

            if (prefabContents != null && !string.IsNullOrEmpty(prefabPath))
                PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath);

            ApplyGlobalPreviewToAllChunks(streamer, streamer.EditorGlobalPreviewLod);

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
            foreach (var chunk in streamer.GetComponentsInChildren<GaussianSplatChunk>(true))
            {
                if (chunk == null || chunk.LodCount == 0) continue;
                chunk.SetEditorPreviewLod(Mathf.Clamp(level, 0, chunk.LodCount - 1));
            }
            EditorUtility.SetDirty(streamer);
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
