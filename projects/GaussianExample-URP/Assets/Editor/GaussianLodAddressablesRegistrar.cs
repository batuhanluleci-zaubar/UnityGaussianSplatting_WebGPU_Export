// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace GsplatLod.Editor
{
    /// <summary>
    /// Registers chunk GaussianSplatAsset files in Addressables with address = asset file name
    /// (no extension), matching manifest ResolveAddr / GaussianLodStreamAsync.StartLoad keys.
    /// </summary>
    public static class GaussianLodAddressablesRegistrar
    {
        const string kDefaultAssetFolder = "Assets/GaussianAssets";
        const string kFestsaalPrefix = "Festsaal_10m_bereinigt";

        public struct RegisterResult
        {
            public bool success;
            public string message;
            public int registered;
            public int updated;
            public int skipped;
        }

        [MenuItem("Tools/Gaussian Splats/Register Festsaal Chunk Addressables")]
        public static void RegisterFestsaalMenu()
        {
            var r = RegisterFestsaalChunks();
            if (r.success) Debug.Log($"[Addressables] {r.message}");
            else Debug.LogError($"[Addressables] {r.message}");
        }

        [MenuItem("Tools/Gaussian Splats/Register Festsaal 200k SPZ Stream Addressables")]
        public static void RegisterFestsaal200kSpzMenu()
        {
            var r = RegisterFromManifest("gsplat_lod/festsaal_200k_spz/manifest.json", kDefaultAssetFolder);
            if (r.success) Debug.Log($"[Addressables] {r.message}");
            else Debug.LogError($"[Addressables] {r.message}");
        }

        public static RegisterResult RegisterFestsaalChunks(string assetFolder = kDefaultAssetFolder)
        {
            var paths = CollectFestsaalAssetPaths(assetFolder);
            return RegisterAssetPaths(paths, $"{kFestsaalPrefix} chunks");
        }

        public static RegisterResult RegisterFromManifest(string manifestPath, string assetFolder = kDefaultAssetFolder)
        {
            manifestPath = LodManifestResolver.Resolve(manifestPath, "[Addressables]");
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return Fail($"Manifest not found: '{manifestPath}'");

            LodManifest man;
            try { man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath)); }
            catch (System.Exception ex) { return Fail($"Manifest parse error: {ex.Message}"); }

            if (!LodManifestValidator.Validate(man, "[Addressables]", out var vErr))
                return Fail(vErr);

            var paths = new List<string>();
            foreach (var cm in man.chunks)
            {
                if (cm.lods == null) continue;
                foreach (var lm in cm.lods)
                {
                    string addr = LodManifestValidator.ResolveAddr(man, lm);
                    if (string.IsNullOrEmpty(addr)) continue;
                    string path = $"{assetFolder}/{addr}.asset";
                    if (File.Exists(path))
                        paths.Add(path);
                }
            }

            if (man.version >= 2 && man.filenames != null)
            {
                foreach (var fn in man.filenames)
                {
                    if (string.IsNullOrEmpty(fn)) continue;
                    string addr = Path.GetFileNameWithoutExtension(fn);
                    string path = $"{assetFolder}/{addr}.asset";
                    if (File.Exists(path) && !paths.Contains(path))
                        paths.Add(path);
                }
            }

            return RegisterAssetPaths(paths, "manifest entries");
        }

        static List<string> CollectFestsaalAssetPaths(string assetFolder)
        {
            string full = Path.GetFullPath(assetFolder);
            if (!Directory.Exists(full))
                return new List<string>();

            return Directory.GetFiles(full, $"{kFestsaalPrefix}_c*_lod*.asset")
                .Select(p => assetFolder + "/" + Path.GetFileName(p).Replace('\\', '/'))
                .OrderBy(p => p)
                .ToList();
        }

        static RegisterResult RegisterAssetPaths(IList<string> assetPaths, string label)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
                return Fail("AddressableAssetSettings not found. Open Addressables Groups window once to initialize.");

            var group = settings.DefaultGroup;
            if (group == null)
                return Fail("No default Addressables group configured.");

            int registered = 0, updated = 0, skipped = 0;
            foreach (string assetPath in assetPaths)
            {
                string path = assetPath.Replace('\\', '/');
                if (!path.StartsWith("Assets/") || !File.Exists(path))
                    continue;

                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                    continue;

                string address = Path.GetFileNameWithoutExtension(path);
                var entry = settings.FindAssetEntry(guid);
                if (entry != null)
                {
                    if (entry.address != address)
                    {
                        entry.address = address;
                        updated++;
                    }
                    else skipped++;

                    if (entry.parentGroup != group)
                        settings.MoveEntry(entry, group);

                    continue;
                }

                entry = settings.CreateOrMoveEntry(guid, group, false, false);
                entry.address = address;
                registered++;
            }

            EditorUtility.SetDirty(settings);
            EditorUtility.SetDirty(group);
            AssetDatabase.SaveAssets();

            return new RegisterResult
            {
                success = assetPaths.Count > 0,
                message = $"Registered {registered}, updated {updated}, skipped {skipped} Addressables entries for {label} ({assetPaths.Count} assets). " +
                          "Editor Play: use Addressables > Play Mode Script > Use Asset Database (fast). " +
                          "Standalone: build Addressables before Player build.",
                registered = registered,
                updated = updated,
                skipped = skipped,
            };
        }

        static RegisterResult Fail(string msg) =>
            new RegisterResult { success = false, message = msg };
    }
}
#endif
