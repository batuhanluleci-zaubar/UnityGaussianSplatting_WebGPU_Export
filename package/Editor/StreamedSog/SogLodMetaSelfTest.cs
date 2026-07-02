// SPDX-License-Identifier: MIT
// Track C1: minimal in-editor smoke test for the lod-meta.json v1 parser.
//
// Menu path: "Tools/GaussianSplatting/StreamedSog/Run SogLodMeta Self-Test"
// Logs a Debug.Log line on success, Debug.LogError on failure — no test framework
// dependency (keeps the parent asmdef free of com.unity.test-framework).

using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime.StreamedSog;

namespace GaussianSplatting.Editor.StreamedSog
{
    public static class SogLodMetaSelfTest
    {
        const string k_SyntheticLeaf1Lod1 =
            "{\n" +
            "  \"version\": 1,\n" +
            "  \"count\": 4,\n" +
            "  \"counts\": [4],\n" +
            "  \"lodLevels\": 1,\n" +
            "  \"filenames\": [\"chunk0\"],\n" +
            "  \"tree\": {\n" +
            "    \"bound\": { \"min\": [-1, -1, -1], \"max\": [1, 1, 1] },\n" +
            "    \"lods\": {\n" +
            "      \"0\": { \"file\": 0, \"offset\": 0, \"count\": 4 }\n" +
            "    }\n" +
            "  }\n" +
            "}";

        [MenuItem("Tools/GaussianSplatting/StreamedSog/Run SogLodMeta Self-Test")]
        public static void Run()
        {
            try
            {
                var meta = SogLodMeta.Parse(k_SyntheticLeaf1Lod1);
                if (meta == null)
                {
                    Debug.LogError("[SogLodMetaSelfTest] FAIL: parse returned null");
                    return;
                }
                if (meta.Version != 1)
                {
                    Debug.LogError($"[SogLodMetaSelfTest] FAIL: version {meta.Version} != 1");
                    return;
                }
                if (meta.LodLevels != 1)
                {
                    Debug.LogError($"[SogLodMetaSelfTest] FAIL: lodLevels {meta.LodLevels} != 1");
                    return;
                }
                if (meta.Filenames == null || meta.Filenames.Length != 1 || meta.Filenames[0] != "chunk0")
                {
                    Debug.LogError("[SogLodMetaSelfTest] FAIL: filenames array mismatch");
                    return;
                }
                if (meta.Tree == null || meta.Tree.Children != null || meta.Tree.Lods == null)
                {
                    Debug.LogError("[SogLodMetaSelfTest] FAIL: expected a single leaf");
                    return;
                }
                if (!meta.Tree.Lods.TryGetValue(0, out var entry) || entry.Count != 4 || entry.File != 0)
                {
                    Debug.LogError("[SogLodMetaSelfTest] FAIL: LOD 0 entry mismatch");
                    return;
                }

                // Also confirm the strict interior-children==2 rule triggers.
                bool threw = false;
                try
                {
                    SogLodMeta.Parse(
                        "{\"version\":1,\"count\":0,\"counts\":[],\"lodLevels\":1,\"filenames\":[]," +
                        "\"tree\":{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]}," +
                        "\"children\":[{\"bound\":{\"min\":[0,0,0],\"max\":[1,1,1]}," +
                        "\"lods\":{\"0\":{\"file\":0,\"offset\":0,\"count\":1}}}]}}");
                }
                catch (System.FormatException)
                {
                    threw = true;
                }
                if (!threw)
                {
                    Debug.LogError("[SogLodMetaSelfTest] FAIL: interior with 1 child did not throw");
                    return;
                }

                Debug.Log($"[SogLodMetaSelfTest] PASS: parsed 1-leaf/1-LOD, total count = {meta.TotalCount}, " +
                          $"strict validation active.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SogLodMetaSelfTest] FAIL (exception): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
