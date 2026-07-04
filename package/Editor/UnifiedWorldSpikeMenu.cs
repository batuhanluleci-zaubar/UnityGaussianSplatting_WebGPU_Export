// SPDX-License-Identifier: MIT
#if UNITY_EDITOR
using Unity.Collections;
using UnityEngine;
using GaussianSplatting.Runtime;
using GaussianSplatting.Runtime.Streaming;

namespace GaussianSplatting.Editor
{
    /// <summary>M0 spike: two synthetic chunks -> merged pool -> single draw.</summary>
    public static class UnifiedWorldSpikeMenu
    {
        [UnityEditor.MenuItem("Tools/GaussianSplatting/Test/Unified World M0 Spike")]
        public static void RunSpike()
        {
            var root = new GameObject("UnifiedWorldSpike");
            var world = root.AddComponent<GaussianSplatUnifiedWorld>();
            world.Configure(250_000, 250_000);

            for (int leaf = 0; leaf < 2; leaf++)
            {
                var splats = new NativeArray<InputSplatData>(5, Allocator.Temp);
                for (int i = 0; i < 5; i++)
                {
                    float t = (leaf * 5 + i) * 0.1f;
                    splats[i] = new InputSplatData
                    {
                        pos = new Vector3(t, t * 0.5f, t * 0.3f),
                        scale = new Vector3(0.02f, 0.02f, 0.02f),
                        rot = Quaternion.identity,
                        opacity = 0.9f,
                        dc0 = new Vector3(0.8f, 0.7f, 0.6f),
                    };
                }
                world.lodManager.RequestChunk(leaf, 0, splats);
                splats.Dispose();
            }
            world.SyncRendererFromPool();
            Debug.Log($"[M0 Spike] merged splats={world.pool.mergedSplatCount} " +
                      $"unifiedDraw={GaussianSplatUnifiedWorld.IsUnifiedDrawReady} " +
                      $"globalGpuSort=enabled (expect SortGlobalGpu=1 dispatch/frame in Profiler when playing)");
        }
    }
}
#endif
