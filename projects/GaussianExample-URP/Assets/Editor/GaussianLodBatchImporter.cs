// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using GsplatLod;
using UnityEditor;
using UnityEngine;

namespace GsplatLod.Editor
{
    public class GaussianLodBatchImporter : EditorWindow
    {
        const string kDefaultManifest = "tools/gsplat_lod/out/uhq/manifest.json";
        const string kDefaultPlyFolder = "tools/gsplat_lod/out/uhq";
        const string kDefaultOutput = "Assets/GaussianAssets";
        const string kFestsaalSpz = "Assets/Festsaal 10m bereinigt.spz";
        const string kFestsaal200kSpzManifest = "gsplat_lod/festsaal_200k_spz/manifest.json";

        string m_ManifestPath = kDefaultManifest;
        string m_PlyFolder = kDefaultPlyFolder;
        string m_OutputFolder = kDefaultOutput;
        GaussianSplatAssetCreator.DataQuality m_Quality = GaussianSplatAssetCreator.DataQuality.Medium;
        int m_MinLod = 0;
        int m_MaxLod = 0;
        bool m_SkipExisting;
        bool m_SkipOutlierFilterLod0 = true;
        Vector2 m_Scroll;

        [MenuItem("Tools/Gaussian Splats/Batch Import LOD Chunks")]
        public static void Open()
        {
            var w = GetWindow<GaussianLodBatchImporter>("LOD Batch Import");
            w.minSize = new Vector2(420, 380);
        }

        [MenuItem("Tools/Gaussian Splats/Batch Import LOD0 (UHQ Festsaal, Medium)")]
        public static void ImportLod0FestsaalMedium() =>
            LogResult(RunImport(MakeLod0Options(GaussianSplatAssetCreator.DataQuality.Medium)));

        [MenuItem("Tools/Gaussian Splats/Batch Import LOD0 (UHQ Festsaal, High)")]
        public static void ImportLod0FestsaalHigh() =>
            LogResult(RunImport(MakeLod0Options(GaussianSplatAssetCreator.DataQuality.High)));

        [MenuItem("Tools/Gaussian Splats/Re-import Festsaal 10m SPZ (Medium)")]
        public static void ReimportFestsaalMedium() =>
            LogResult(ReimportMonolithic(GaussianSplatAssetCreator.DataQuality.Medium, skipOutlierFilter: true));

        [MenuItem("Tools/Gaussian Splats/Re-import Festsaal 10m SPZ (High)")]
        public static void ReimportFestsaalHigh() =>
            LogResult(ReimportMonolithic(GaussianSplatAssetCreator.DataQuality.High, skipOutlierFilter: true));

        [MenuItem("Tools/Gaussian Splats/Batch Import SPZ Stream (Festsaal 200k)")]
        public static void ImportFestsaal200kSpzStream() =>
            LogResult(RunImportSpz(MakeFestsaal200kSpzOptions()));

        /// <summary>Import all SPZ chunks from festsaal_200k_spz manifest + register Addressables.</summary>
        [MenuItem("Tools/Gaussian Splats/Import + Register Festsaal 200k SPZ Stream (Full)")]
        public static void ImportFestsaal200kSpzStreamFull()
        {
            var r = RunImportSpz(MakeFestsaal200kSpzOptions());
            LogResult(r);
            if (!r.success) return;

            var addr = GaussianLodAddressablesRegistrar.RegisterFromManifest(kFestsaal200kSpzManifest, kDefaultOutput);
            if (addr.success) Debug.Log($"[LOD Batch Import] {addr.message}");
            else Debug.LogWarning($"[LOD Batch Import] Addressables: {addr.message}");
        }

        /// <summary>Automation / MCP entry point (no dialogs).</summary>
        public static bool ImportFestsaal200kSpzStreamSilent(out string summary) =>
            ImportFestsaal200kSpzStreamSilentInternal(out summary);

        static bool ImportFestsaal200kSpzStreamSilentInternal(out string summary)
        {
            summary = "";
            var r = RunImportSpz(MakeFestsaal200kSpzOptions());
            if (!r.success) { summary = r.message; return false; }

            var addr = GaussianLodAddressablesRegistrar.RegisterFromManifest(kFestsaal200kSpzManifest, kDefaultOutput);
            summary = $"{r.message}\n{addr.message}";
            return r.success && addr.success;
        }

        static BatchImportOptions MakeFestsaal200kSpzOptions() => new BatchImportOptions
        {
            manifestPath = kFestsaal200kSpzManifest,
            plyFolder = "",
            outputFolder = kDefaultOutput,
            quality = GaussianSplatAssetCreator.DataQuality.Medium,
            minLod = 0,
            maxLod = 4,
            skipExisting = false,
            skipOutlierFilterLod0 = true,
        };

        /// <summary>
        /// One-shot: re-import monolithic SPZ + all chunk LOD0 (Medium, no per-chunk outlier filter),
        /// rebuild GaussianLodStreamAsync hierarchy, apply global LOD0 preview.
        /// </summary>
        [MenuItem("Tools/Gaussian Splats/Fix Festsaal LOD0 Parity (Full Pipeline)")]
        public static void FixFestsaalLod0Parity() => FixFestsaalLod0ParityInternal(silent: false);

        /// <summary>Same as menu item but no confirmation dialogs (automation / MCP).</summary>
        public static bool FixFestsaalLod0ParitySilent(out string summary) =>
            FixFestsaalLod0ParityInternal(silent: true, out summary);

        static void FixFestsaalLod0ParityInternal(bool silent) =>
            FixFestsaalLod0ParityInternal(silent, out _);

        static bool FixFestsaalLod0ParityInternal(bool silent, out string summary)
        {
            summary = "";
            if (!silent && !EditorUtility.DisplayDialog("Fix Festsaal LOD0 parity?",
                    "Re-imports Festsaal 10m bereinigt.spz (Medium) and all 64 chunk LOD0 PLY files " +
                    "with outlier filter OFF, then refreshes LOD0 preview assets (LOD1–4 refs preserved).\n\n" +
                    "This takes several minutes. Continue?",
                    "Run", "Cancel"))
            {
                summary = "Cancelled.";
                return false;
            }

            var mono = ReimportMonolithic(GaussianSplatAssetCreator.DataQuality.Medium, skipOutlierFilter: true, confirm: false);
            if (!mono.success)
            {
                LogResult(mono);
                summary = mono.message;
                return false;
            }

            var chunks = RunImport(MakeLod0Options(GaussianSplatAssetCreator.DataQuality.Medium));
            if (!chunks.success)
            {
                LogResult(chunks);
                summary = chunks.message;
                return false;
            }

            if (!RefreshLod0PreviewAssetsOnStreamer(out string rebuildMsg))
            {
                Debug.LogError($"[LOD Batch Import] LOD0 preview refresh failed: {rebuildMsg}");
                summary = rebuildMsg;
                return false;
            }

            var addr = GaussianLodAddressablesRegistrar.RegisterFromManifest(kDefaultManifest, kDefaultOutput);
            if (!addr.success)
                Debug.LogWarning($"[LOD Batch Import] Addressables registration: {addr.message}");
            else
                Debug.Log($"[LOD Batch Import] {addr.message}");

            summary = $"Parity fix complete.\n" +
                      $"  Monolithic: {mono.totalSplats:N0} splats\n" +
                      $"  Chunk LOD0: {chunks.totalSplats:N0} splats (manifest {chunks.manifestExpected:N0})\n" +
                      $"  {rebuildMsg}";
            Debug.Log($"[LOD Batch Import] {summary}\n" +
                      "Compare with Festsaal10M — enable only ONE prefab at a time in the scene.");
            return true;
        }

        const string kStreamerPrefabPath = "Assets/GaussianLodStreamAsync.prefab";

        static bool RefreshLod0PreviewAssetsOnStreamer(out string message)
        {
            var opts = new GaussianLodHierarchyBuilder.BuildOptions
            {
                assetFolder = kDefaultOutput,
                assignPreviewAssets = true,
                refreshLodMin = 0,
                refreshLodMax = 0,
                preserveExistingPreviewWhenMissing = true,
                applyPreviewLodLevel = 0,
            };

            var messages = new List<string>();
            bool anySuccess = false;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kStreamerPrefabPath);
            var prefabStreamer = prefab != null ? prefab.GetComponent<GaussianLodStreamAsync>() : null;
            if (prefabStreamer != null)
            {
                opts.manifestPath = prefabStreamer.manifestPath;
                var prefabResult = GaussianLodHierarchyBuilder.RefreshPreviewAssets(prefabStreamer, opts);
                if (prefabResult.success) anySuccess = true;
                messages.Add($"Prefab: {prefabResult.message}");
            }

            var sceneStreamers = UnityEngine.Object.FindObjectsByType<GaussianLodStreamAsync>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var streamer in sceneStreamers)
            {
                if (streamer == prefabStreamer) continue;

                EnsureStreamerSettings(streamer);
                opts.manifestPath = streamer.manifestPath;
                var sceneResult = GaussianLodHierarchyBuilder.RefreshPreviewAssets(streamer, opts);
                if (sceneResult.success) anySuccess = true;
                messages.Add($"{streamer.gameObject.scene.name}: {sceneResult.message}");
                EditorUtility.SetDirty(streamer);
            }

            if (!anySuccess && prefabStreamer == null && sceneStreamers.Length == 0)
            {
                message = $"No GaussianLodStreamAsync in scene or prefab at {kStreamerPrefabPath}.";
                return false;
            }

            message = string.Join("\n", messages);
            return anySuccess;
        }

        static void EnsureStreamerSettings(GaussianLodStreamAsync streamer)
        {
            if (streamer == null || streamer.GetComponent<GaussianSplatSettings>() != null)
                return;

            var removed = PrefabUtility.GetRemovedComponents(streamer.gameObject);
            foreach (var rc in removed)
            {
                if (rc.assetComponent is GaussianSplatSettings)
                {
                    PrefabUtility.RevertRemovedComponent(
                        streamer.gameObject, (Component)rc.assetComponent, InteractionMode.AutomatedAction);
                    EditorUtility.SetDirty(streamer);
                    return;
                }
            }
        }

        static BatchImportOptions MakeLod0Options(GaussianSplatAssetCreator.DataQuality quality) =>
            new BatchImportOptions
            {
                manifestPath = kDefaultManifest,
                plyFolder = kDefaultPlyFolder,
                outputFolder = kDefaultOutput,
                quality = quality,
                minLod = 0,
                maxLod = 0,
                skipExisting = false,
                skipOutlierFilterLod0 = true,
            };

        void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            EditorGUILayout.LabelField("Batch Import LOD Chunks", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Imports PLY files from manifest.json into GaussianSplatAssets.\n" +
                "LOD0: enable 'Skip outlier filter' so chunk totals match manifest (~9.70M). " +
                "Per-chunk filtering drops ~25K valid splats vs monolithic import.\n" +
                "Use Medium to match Festsaal10M default, or High for better fidelity.",
                MessageType.Info);

            m_ManifestPath = EditorGUILayout.TextField("Manifest (relative)", m_ManifestPath);
            m_PlyFolder = EditorGUILayout.TextField("PLY Folder (relative)", m_PlyFolder);
            m_OutputFolder = EditorGUILayout.TextField("Output Folder", m_OutputFolder);
            m_Quality = (GaussianSplatAssetCreator.DataQuality)EditorGUILayout.EnumPopup("Quality", m_Quality);
            m_MinLod = EditorGUILayout.IntSlider("Min LOD", m_MinLod, 0, 4);
            m_MaxLod = EditorGUILayout.IntSlider("Max LOD", m_MaxLod, m_MinLod, 4);
            m_SkipOutlierFilterLod0 = EditorGUILayout.Toggle(
                new GUIContent("Skip outlier filter (LOD0)", "Keeps all PLY splats; recommended for chunk LOD0 parity."),
                m_SkipOutlierFilterLod0);
            m_SkipExisting = EditorGUILayout.Toggle("Skip if asset exists", m_SkipExisting);

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Import", GUILayout.Height(32)))
            {
                LogResult(RunImport(new BatchImportOptions
                {
                    manifestPath = m_ManifestPath,
                    plyFolder = m_PlyFolder,
                    outputFolder = m_OutputFolder,
                    quality = m_Quality,
                    minLod = m_MinLod,
                    maxLod = m_MaxLod,
                    skipExisting = m_SkipExisting,
                    skipOutlierFilterLod0 = m_SkipOutlierFilterLod0,
                }));
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Monolithic (Festsaal10M)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Re-import SPZ Medium"))
                    LogResult(ReimportMonolithic(GaussianSplatAssetCreator.DataQuality.Medium, true));
                if (GUILayout.Button("Re-import SPZ High"))
                    LogResult(ReimportMonolithic(GaussianSplatAssetCreator.DataQuality.High, true));
            }

            EditorGUILayout.Space(6);
            if (GUILayout.Button("Fix Festsaal LOD0 Parity (Full Pipeline)", GUILayout.Height(28)))
                FixFestsaalLod0Parity();

            EditorGUILayout.EndScrollView();
        }

        static void LogResult(BatchImportResult result)
        {
            if (result.success)
                Debug.Log($"[LOD Batch Import] {result.message}");
            else
                Debug.LogError($"[LOD Batch Import] {result.message}");
        }

        public struct BatchImportOptions
        {
            public string manifestPath;
            public string plyFolder;
            public string outputFolder;
            public GaussianSplatAssetCreator.DataQuality quality;
            public int minLod;
            public int maxLod;
            public bool skipExisting;
            public bool skipOutlierFilterLod0;
        }

        public struct BatchImportResult
        {
            public bool success;
            public string message;
            public int imported;
            public int skipped;
            public int failed;
            public long totalSplats;
            public long manifestExpected;
        }

        static BatchImportResult ReimportMonolithic(
            GaussianSplatAssetCreator.DataQuality quality, bool skipOutlierFilter, bool confirm = true)
        {
            string spzPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", kFestsaalSpz));
            if (!File.Exists(spzPath))
                return Fail($"SPZ not found: {kFestsaalSpz}");

            if (confirm && !EditorUtility.DisplayDialog("Re-import Festsaal 10m?",
                    $"Overwrite 'Festsaal 10m bereinigt.asset' with {quality} quality?", "Import", "Cancel"))
                return Fail("Cancelled.");

            bool ok = GaussianSplatAssetCreator.TryImportFromFile(
                new GaussianSplatAssetCreator.ImportSettings
                {
                    inputFile = spzPath,
                    outputFolder = kDefaultOutput,
                    quality = quality,
                    importCameras = false,
                    skipOutlierFilter = skipOutlierFilter,
                }, out string err);

            if (!ok)
                return Fail(err);

            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(
                $"{kDefaultOutput}/Festsaal 10m bereinigt.asset");
            return new BatchImportResult
            {
                success = true,
                message = $"Re-imported Festsaal 10m bereinigt.asset ({quality}): {asset?.splatCount:N0} splats.",
                imported = 1,
                totalSplats = asset != null ? asset.splatCount : 0,
            };
        }

        public static BatchImportResult RunImport(BatchImportOptions opts)
        {
            string manifestPath = ResolveManifestPath(opts.manifestPath);
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return Fail($"Manifest not found: '{opts.manifestPath}'");

            if (string.IsNullOrEmpty(opts.outputFolder) || !opts.outputFolder.StartsWith("Assets/"))
                return Fail($"Output folder must be under Assets/, was '{opts.outputFolder}'");

            Directory.CreateDirectory(opts.outputFolder);

            LodManifest man;
            try { man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail($"Manifest parse error: {ex.Message}"); }

            if (!LodManifestValidator.Validate(man, "[BatchImport]", out var vErr))
                return Fail(vErr);

            string plyRoot = ResolvePlyRoot(opts.plyFolder, manifestPath);
            if (!Directory.Exists(plyRoot))
                return Fail($"PLY folder not found: '{plyRoot}'");

            int minLod = Mathf.Max(0, opts.minLod);
            int maxLod = Mathf.Max(minLod, opts.maxLod);
            int imported = 0, skipped = 0, failed = 0;
            long totalSplats = 0;
            long manifestExpected = 0;
            var errors = new List<string>();

            var filesToImport = new List<(string plyPath, string addr, int splatCount, int lodLevel)>();
            foreach (var cm in man.chunks)
            {
                if (cm.lods == null) continue;
                foreach (var lm in cm.lods)
                {
                    if (lm.level < minLod || lm.level > maxLod) continue;
                    string addr = LodManifestValidator.ResolveAddr(man, lm);
                    string plyName = !string.IsNullOrEmpty(lm.file) ? lm.file : $"{addr}.ply";
                    string plyPath = Path.Combine(plyRoot, plyName);
                    filesToImport.Add((plyPath, addr, lm.splatCount, lm.level));
                    if (lm.level == 0)
                        manifestExpected += lm.splatCount;
                }
            }

            for (int i = 0; i < filesToImport.Count; i++)
            {
                var (plyPath, addr, expectedCount, lodLevel) = filesToImport[i];
                string assetPath = $"{opts.outputFolder}/{addr}.asset";

                if (EditorUtility.DisplayCancelableProgressBar("LOD Batch Import",
                        $"{addr} ({i + 1}/{filesToImport.Count})", (float)i / filesToImport.Count))
                {
                    EditorUtility.ClearProgressBar();
                    return new BatchImportResult
                    {
                        success = false,
                        message = "Import cancelled.",
                        imported = imported,
                        skipped = skipped,
                        failed = failed,
                        totalSplats = totalSplats,
                        manifestExpected = manifestExpected,
                    };
                }

                if (opts.skipExisting && File.Exists(assetPath))
                {
                    var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                    if (existing != null)
                    {
                        skipped++;
                        totalSplats += existing.splatCount;
                        continue;
                    }
                }

                if (!File.Exists(plyPath))
                {
                    failed++;
                    errors.Add($"Missing PLY: {plyPath}");
                    continue;
                }

                bool skipFilter = opts.skipOutlierFilterLod0 && lodLevel == 0;
                bool ok = GaussianSplatAssetCreator.TryImportFromFile(
                    new GaussianSplatAssetCreator.ImportSettings
                    {
                        inputFile = plyPath,
                        outputFolder = opts.outputFolder,
                        quality = opts.quality,
                        importCameras = false,
                        skipOutlierFilter = skipFilter,
                    }, out string err);

                if (!ok)
                {
                    failed++;
                    errors.Add($"{addr}: {err}");
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                if (asset == null)
                {
                    failed++;
                    errors.Add($"{addr}: asset not created at {assetPath}");
                    continue;
                }

                imported++;
                totalSplats += asset.splatCount;

                if (expectedCount > 0 && asset.splatCount != expectedCount)
                {
                    Debug.LogWarning(
                        $"[BatchImport] {addr}: PLY/manifest {expectedCount:N0} splats, asset {asset.splatCount:N0}" +
                        (skipFilter ? "" : " (try Skip outlier filter for LOD0)"));
                }
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (failed == 0 && imported > 0)
            {
                var addr = GaussianLodAddressablesRegistrar.RegisterFromManifest(opts.manifestPath, opts.outputFolder);
                if (addr.success)
                    Debug.Log($"[LOD Batch Import] {addr.message}");
                else
                    Debug.LogWarning($"[LOD Batch Import] Addressables: {addr.message}");
            }

            string msg = $"Imported {imported}, skipped {skipped}, failed {failed}. " +
                         $"Asset splats: {totalSplats:N0}";
            if (manifestExpected > 0 && minLod == 0 && maxLod == 0)
            {
                long delta = totalSplats - manifestExpected;
                msg += $", manifest LOD0: {manifestExpected:N0} (delta {delta:+#,0;-#,0})";
            }
            if (errors.Count > 0)
                msg += $". Errors: {string.Join("; ", errors.GetRange(0, Math.Min(3, errors.Count)))}";

            return new BatchImportResult
            {
                success = failed == 0,
                message = msg,
                imported = imported,
                skipped = skipped,
                failed = failed,
                totalSplats = totalSplats,
                manifestExpected = manifestExpected,
            };
        }

        /// <summary>
        /// Import .spz chunk files listed in a v2 manifest (streamed_sog.py output).
        /// SPZ files are resolved next to manifest.json under StreamingAssets.
        /// </summary>
        public static BatchImportResult RunImportSpz(BatchImportOptions opts)
        {
            string manifestPath = ResolveManifestPath(opts.manifestPath);
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
                return Fail($"Manifest not found: '{opts.manifestPath}'");

            if (string.IsNullOrEmpty(opts.outputFolder) || !opts.outputFolder.StartsWith("Assets/"))
                return Fail($"Output folder must be under Assets/, was '{opts.outputFolder}'");

            Directory.CreateDirectory(opts.outputFolder);

            LodManifest man;
            try { man = JsonUtility.FromJson<LodManifest>(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail($"Manifest parse error: {ex.Message}"); }

            if (!LodManifestValidator.Validate(man, "[BatchImport SPZ]", out var vErr))
                return Fail(vErr);

            string spzRoot = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(spzRoot) || !Directory.Exists(spzRoot))
                return Fail($"SPZ folder not found: '{spzRoot}'");

            int minLod = Mathf.Max(0, opts.minLod);
            int maxLod = Mathf.Max(minLod, opts.maxLod);
            int imported = 0, skipped = 0, failed = 0;
            long totalSplats = 0;
            long manifestExpected = 0;
            var errors = new List<string>();

            var filesToImport = new List<(string spzPath, string addr, int splatCount, int lodLevel)>();
            foreach (var cm in man.chunks)
            {
                if (cm.lods == null) continue;
                foreach (var lm in cm.lods)
                {
                    if (lm.level < minLod || lm.level > maxLod) continue;
                    string addr = LodManifestValidator.ResolveAddr(man, lm);
                    string spzName = !string.IsNullOrEmpty(lm.file) ? lm.file
                        : (man.filenames != null && lm.fileIdx >= 0 && lm.fileIdx < man.filenames.Length
                            ? man.filenames[lm.fileIdx] : $"{addr}.spz");
                    string spzPath = Path.Combine(spzRoot, spzName);
                    filesToImport.Add((spzPath, addr, lm.splatCount, lm.level));
                    if (lm.level == 0)
                        manifestExpected += lm.splatCount;
                }
            }

            for (int i = 0; i < filesToImport.Count; i++)
            {
                var (spzPath, addr, expectedCount, lodLevel) = filesToImport[i];
                string assetPath = $"{opts.outputFolder}/{addr}.asset";

                if (EditorUtility.DisplayCancelableProgressBar("SPZ Stream Import",
                        $"{addr} ({i + 1}/{filesToImport.Count})", (float)i / filesToImport.Count))
                {
                    EditorUtility.ClearProgressBar();
                    return new BatchImportResult
                    {
                        success = false,
                        message = "Import cancelled.",
                        imported = imported,
                        skipped = skipped,
                        failed = failed,
                        totalSplats = totalSplats,
                        manifestExpected = manifestExpected,
                    };
                }

                if (opts.skipExisting && File.Exists(assetPath))
                {
                    var existing = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                    if (existing != null)
                    {
                        skipped++;
                        totalSplats += existing.splatCount;
                        continue;
                    }
                }

                if (!File.Exists(spzPath))
                {
                    failed++;
                    errors.Add($"Missing SPZ: {spzPath}");
                    continue;
                }

                bool skipFilter = opts.skipOutlierFilterLod0 && lodLevel == 0;
                bool ok = GaussianSplatAssetCreator.TryImportFromFile(
                    new GaussianSplatAssetCreator.ImportSettings
                    {
                        inputFile = spzPath,
                        outputFolder = opts.outputFolder,
                        quality = opts.quality,
                        importCameras = false,
                        skipOutlierFilter = skipFilter,
                    }, out string err);

                if (!ok)
                {
                    failed++;
                    errors.Add($"{addr}: {err}");
                    continue;
                }

                var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(assetPath);
                if (asset == null)
                {
                    failed++;
                    errors.Add($"{addr}: asset not created at {assetPath}");
                    continue;
                }

                imported++;
                totalSplats += asset.splatCount;

                if (expectedCount > 0 && asset.splatCount != expectedCount)
                {
                    Debug.LogWarning(
                        $"[BatchImport SPZ] {addr}: manifest {expectedCount:N0} splats, asset {asset.splatCount:N0}");
                }
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string msg = $"SPZ import: {imported} imported, {skipped} skipped, {failed} failed. " +
                         $"Asset splats: {totalSplats:N0}";
            if (manifestExpected > 0 && minLod == 0 && maxLod == 0)
            {
                long delta = totalSplats - manifestExpected;
                msg += $", manifest LOD0: {manifestExpected:N0} (delta {delta:+#,0;-#,0})";
            }
            if (errors.Count > 0)
                msg += $". Errors: {string.Join("; ", errors.GetRange(0, Math.Min(3, errors.Count)))}";

            return new BatchImportResult
            {
                success = failed == 0,
                message = msg,
                imported = imported,
                skipped = skipped,
                failed = failed,
                totalSplats = totalSplats,
                manifestExpected = manifestExpected,
            };
        }

        static string ResolveManifestPath(string manifestPath)
        {
            if (!string.IsNullOrEmpty(manifestPath) && File.Exists(manifestPath))
                return manifestPath;

            var repoPath = LodManifestResolver.ResolveRepoRelativePath(manifestPath);
            if (!string.IsNullOrEmpty(repoPath))
                return repoPath;

            return LodManifestResolver.Resolve(manifestPath, "[BatchImport]");
        }

        static string ResolvePlyRoot(string plyFolder, string manifestPath)
        {
            if (Path.IsPathRooted(plyFolder) && Directory.Exists(plyFolder))
                return plyFolder;

            string rel = plyFolder.Replace('\\', '/').TrimStart('/');
            string root = LodManifestResolver.TryGetMonorepoRoot();
            string fromRepo = Path.GetFullPath(Path.Combine(root, rel));
            if (Directory.Exists(fromRepo))
                return fromRepo;

            string fromManifest = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(fromManifest) && Directory.Exists(fromManifest))
                return fromManifest;

            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", rel));
        }

        static BatchImportResult Fail(string msg) =>
            new BatchImportResult { success = false, message = msg };
    }
}
#endif
