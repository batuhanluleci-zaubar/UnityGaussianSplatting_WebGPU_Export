// SPDX-License-Identifier: MIT
// PlayCanvas gsplat-world — single renderer, single draw over merged pool asset.

using UnityEngine;

namespace GaussianSplatting.Runtime.Streaming
{
    [DisallowMultipleComponent]
    public sealed class GaussianSplatUnifiedWorld : MonoBehaviour
    {
        [SerializeField] int m_MaxSplats = 250_000;
        // Standard per-node draw (like the monolithic reference) renders sharper than the unified
        // global-sort DrawProcedural path — A/B swap test showed the SAME asset is crisp via standard
        // draw but soft via unified draw. Route the streamed merged asset through the standard path.
        [SerializeField] bool m_UseUnifiedDraw = false;

        GpuBufferPool m_Pool;
        StreamingLodManager m_LodManager;
        GaussianSplatRenderer m_Renderer;
        GsplatLodScheduler m_Scheduler;

        public GpuBufferPool pool => m_Pool;
        public StreamingLodManager lodManager => m_LodManager;
        public GsplatLodScheduler scheduler => m_Scheduler;
        public GaussianSplatRenderer renderer => m_Renderer;
        public bool useUnifiedDraw => m_UseUnifiedDraw;

        public static GaussianSplatUnifiedWorld active { get; private set; }

        /// <summary>True when unified world is active and its renderer is ready (== 1 gsplat draw).</summary>
        public static bool IsUnifiedDrawReady =>
            active != null && active.useUnifiedDraw && active.m_Renderer != null &&
            active.m_Renderer.isActiveAndEnabled && active.m_Renderer.HasValidAsset && active.m_Renderer.HasValidRenderSetup;

        void Awake()
        {
            active = this;
            EnsureInitialized();
        }

        /// <summary>Creates pool, LOD manager, scheduler, and renderer if not yet alive (Edit-mode menu tests).</summary>
        public void EnsureInitialized()
        {
            if (m_Pool != null) return;

            m_Pool = new GpuBufferPool(m_MaxSplats);
            m_LodManager = new StreamingLodManager(m_Pool);
            m_Scheduler = new GsplatLodScheduler { deviceBudget = m_MaxSplats };

            var go = new GameObject("UnifiedSplatRenderer");
            go.transform.SetParent(transform, false);
            m_Renderer = go.AddComponent<GaussianSplatRenderer>();
        }

        public void Configure(int maxSplats, long deviceBudget)
        {
            m_MaxSplats = maxSplats;
            EnsureInitialized();
            if (m_Scheduler != null)
                m_Scheduler.deviceBudget = deviceBudget;
        }

        const float kStreamingSyncIntervalSec = 0.25f;
        float m_LastPoolSyncTime = -999f;
        int m_LastSyncedSplatCount = -1;

        /// <summary>
        /// Push merged pool asset to the renderer. During initial streaming fill,
        /// throttle full octree rebuilds to at most ~4/sec so progressive load
        /// does not stall the frame budget.
        /// </summary>
        public void SyncRendererFromPoolThrottled(bool streamingFill)
        {
            if (streamingFill && m_Pool != null && m_Pool.needsRebuild
                && m_Pool.residentSplats >= m_MaxSplats * 0.9f
                && Time.unscaledTime - m_LastPoolSyncTime < kStreamingSyncIntervalSec)
                return;

            SyncRendererFromPool(streamingFill);
            m_LastPoolSyncTime = Time.unscaledTime;
        }

        public void SyncRendererFromPool() => SyncRendererFromPool(false);

        public void SyncRendererFromPool(bool streamingFill)
        {
            EnsureInitialized();
            if (m_Renderer == null || m_Pool == null) return;

            int splatCount = m_Pool.ActiveSplatCount();
            if (!m_Pool.needsRebuild && m_Renderer.m_Asset != null && m_Renderer.octreeBuilt
                && splatCount == m_LastSyncedSplatCount)
            {
                if (m_UseUnifiedDraw && m_Renderer.m_Octree != null)
                    m_Renderer.m_Octree.useGlobalGpuSort = true;
                return;
            }

            bool didRebuild = m_Pool.needsRebuild;
            var asset = didRebuild ? m_Pool.RebuildMergedAsset() : m_Pool.mergedAsset;
            splatCount = m_Pool.mergedSplatCount;
            if (asset == null)
            {
                m_Renderer.m_Asset = null;
                m_LastSyncedSplatCount = -1;
                return;
            }

            // Morton reorder can change indices without changing count — always rebuild octree after merge.
            bool skipOctree = streamingFill && m_Renderer.octreeBuilt && !didRebuild;

            m_Renderer.m_Asset = asset;
            m_Renderer.ReloadAssetFromRuntime(skipOctree);
            if (m_UseUnifiedDraw && m_Renderer.m_Octree != null)
                m_Renderer.m_Octree.useGlobalGpuSort = true;
            m_LastSyncedSplatCount = splatCount;
        }

        public void RegisterWithRenderSystem()
        {
            if (m_Renderer != null && m_UseUnifiedDraw)
                GaussianSplatRenderSystem.instance.SetUnifiedWorld(this);
        }

        void OnEnable()
        {
            active = this;
            RegisterWithRenderSystem();
        }

        void OnDisable()
        {
            if (active == this) active = null;
            GaussianSplatRenderSystem.instance.ClearUnifiedWorld(this);
        }

        void OnDestroy()
        {
            m_LodManager = null;
            m_Pool?.Dispose();
            m_Pool = null;
            if (active == this) active = null;
        }
    }
}
