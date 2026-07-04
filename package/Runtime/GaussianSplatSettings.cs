// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    public enum TransparencyMode
    {
        // no sorting, transparency is stochastic (random) and noisy
        Stochastic,
        // no sorting, transparency is stochastic (half tone) and less noisy
        AlphaBlend,
    }

    public enum TemporalFilter
    {
        None = 0,
        TemporalSimpleMotion = 1,
        TemporalMotion = 2,
    }

    public enum DebugRenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }

    // If an object with this script exists in the scene, then global 3DGS rendering options
    // are used from that script. Otherwise, defaults are used.
    //
    [ExecuteInEditMode] // so that Awake is called in edit mode
    [DefaultExecutionOrder(-100)]
    public class GaussianSplatSettings : MonoBehaviour
    {
        public static GaussianSplatSettings instance
        {
            get
            {
                if (ms_Instance == null)
                    ms_Instance = FindAnyObjectByType<GaussianSplatSettings>();
                if (ms_Instance == null)
                {
                    var go = new GameObject($"{nameof(GaussianSplatSettings)} (Defaults)")
                    {
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    ms_Instance = go.AddComponent<GaussianSplatSettings>();
                    ms_Instance.EnsureResources();
                }
                return ms_Instance;
            }
        }
        static GaussianSplatSettings ms_Instance;

#if UNITY_EDITOR
        /// <summary>
        /// Editor global LOD preview: skip octree culling without mutating m_EnableOctreeCulling on scene objects.
        /// </summary>
        public static bool editorPreviewBypassOctreeCulling;
#endif

        internal bool IsOctreeCullingActive()
        {
            if (!m_EnableOctreeCulling)
                return false;
#if UNITY_EDITOR
            if (!Application.isPlaying && editorPreviewBypassOctreeCulling)
                return false;
#endif
            return true;
        }

        [Tooltip("Gaussian splat transparency rendering algorithm")]
        public TransparencyMode m_Transparency = TransparencyMode.AlphaBlend;

        [Tooltip("How to filter temporal transparency")]
        public TemporalFilter m_TemporalFilter = TemporalFilter.None;
        [Tooltip("How much of new frame to blend in. Higher: more noise, lower: more ghosting.")]
        [Range(0.001f, 1.0f)] public float m_FrameInfluence = 0.05f;
        [Tooltip("Strength of history color rectification clamp. Lower: more flickering, higher: more blur/ghosting.")]
        [Range(0.001f, 10.0f)] public float m_VarianceClampScale = 1.5f;

        public DebugRenderMode m_RenderMode = DebugRenderMode.Splats;
        [Range(1.0f,50.0f)] public float m_PointDisplaySize = 3.0f;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        public bool m_SHOnly;

        [Header("Octree Culling")]
        [Tooltip("Enable octree-based frustum culling for improved performance")]
        public bool m_EnableOctreeCulling = true;
        [Tooltip("Maximum octree depth (4-6 recommended)")]
        [Range(0, 16)] public int m_OctreeMaxDepth = 5;
        [Tooltip("Maximum splats per octree leaf node that avoid split (which is still limited by max depth)")]
        [Range(1, 65536)] public int m_OctreeMaxSplatsPerLeaf = 1;
        [Tooltip("Update culling every N frames (1 = every frame, higher = better performance but less precise)")]
        [Range(1, 20)] public int m_OctreeCullingUpdateInterval = 1;
        [Tooltip("Keep octree sort but skip frustum rejection — draw every splat in the merged pool. " +
                 "Use for budget-bounded SOG streaming where resident count already matches device budget.")]
        public bool m_OctreeSkipFrustumCull = false;

        [Tooltip("Ratio (0-1) of splats considered as 'screen' splats when building the octree. The remainder are treated as background splats(Always draw last in front-to-back alpha blend mode).")]
        [Range(0.0f, 1.0f)] public float m_OctreeSplatRatio = 0.9f;

        [Header("Screen-space LOD (large scenes)")]
        [Tooltip("Distance-banded level of detail: octree nodes that are LARGE on screen (near / what you look at) " +
                 "keep FULL detail; only nodes that are SMALL on screen (far / background) are subsampled. This is the " +
                 "biggest FPS lever for multi-million-splat scenes and preserves the near image.")]
        public bool m_EnableScreenLod = false;
        [Tooltip("Full-detail threshold in on-screen pixels. Nodes projecting LARGER than this keep every splat; " +
                 "smaller (farther) nodes are progressively thinned. Higher = more of the scene stays full (better " +
                 "quality, less speedup). Lower = more aggressive.")]
        [Range(20f, 600f)] public float m_LodFullDetailPixels = 200f;
        [Tooltip("Maximum subsample stride for the farthest / smallest nodes (caps how sparse distant content can get).")]
        [Range(1, 64)] public int m_LodMaxStride = 16;
        [Tooltip("Hard cap on rendered splats (0 = unlimited). Splats are collected front-to-back, so the cap keeps " +
                 "the NEAREST splats and drops the farthest — a device-tuned budget that holds a stable frame time " +
                 "regardless of viewpoint without touching near content. Derive it from measured device frame time.")]
        [Min(0)] public int m_LodSplatBudget = 0;

        [Header("Slice 3: strided-copy cache")]
        [Tooltip("Slice 3: cache per-node strided permutations so the LOD append loop becomes a single MemCpy on hit. " +
                 "Only helps the strided (step>1) path AND only when nodes are big (default floor=32 splats/node). " +
                 "Off by default: scenes with m_OctreeMaxSplatsPerLeaf=1 (e.g. Phase2HQ) hit the floor and see no " +
                 "benefit; enable when you have large-leaf octrees (~200+ splats/leaf) where cache-hit MemCpy beats " +
                 "the per-index strided loop. See gsplat-slice3-strided-cache-findings.md.")]
        public bool m_EnableStridedCache = false;
        [Tooltip("Max cache slots per node (LRU). Each slot stores ceil(nodeSplats/step) ints, so total per-node = " +
                 "sum over cached steps. K=4 covers the typical 3-6 distinct steps per frame and caps worst-case footprint.")]
        [Range(1, 16)] public int m_StridedCacheSlotsPerNode = 4;
        [Tooltip("Global cache byte budget (0 = platform default: 64 MB desktop / 16 MB mobile). Exceeding this triggers " +
                 "cross-node LRU eviction. Warmup shakes out cold nodes fast so a small budget is fine.")]
        [Min(0)] public int m_StridedCacheGlobalByteBudget = 0;
        [Tooltip("Max cache misses processed per frame. Throttles first-frame allocation spikes when the whole visible " +
                 "set is cold; misses beyond this cap fall through to the un-cached strided loop for one frame.")]
        [Range(1, 512)] public int m_StridedCacheMaxMissesPerFrame = 32;

        [Tooltip("Draw octree leaf bounds in the Scene view (OnDrawGizmos)")]
        public bool m_DrawOctreeGizmos = true;

        [Tooltip("Emit per-chunk / per-octree-build Debug.Log lines. Off by default: with a streaming " +
                 "LOD system that builds octrees for every chunk this floods the Console. Turn on when " +
                 "actually debugging octree behaviour.")]
        public bool m_VerboseLog = false;

        [Header("GPU sort (compute)")]
        [Tooltip("P1 SuperSplat-parity: route the per-chunk sort through DEVICE_RADIX_SORT compute shader " +
                 "(package/Shaders/Resources/DeviceRadixSort.compute + GpuSorting.cs) instead of the CPU " +
                 "task-parallel path. Falls back to CPU automatically on platforms that lack compute " +
                 "(WebGL/WebGPU/GLES) via SystemInfo.supportsComputeShaders. Biggest single FPS lever when " +
                 "the sort is the CPU bottleneck (typical at >1M resident splats).")]
        public bool m_UseGpuSort = true;

        // Remove the vertex shader mode option since it's now the only mode
        // [Tooltip("Use vertex shader mode for better WebGL compatibility (disables compute shaders and temporal filtering)")]
        // public bool m_UseVertexShaderMode;

        internal bool isDebugRender => m_RenderMode != DebugRenderMode.Splats;

        // Sorting is needed for debug box rendering and front-to-back alpha blending mode
        internal bool needSorting => m_RenderMode == DebugRenderMode.DebugBoxes || m_Transparency == TransparencyMode.AlphaBlend;

        internal bool resourcesFound { get; private set; }
        bool resourcesLoadAttempted;
        internal Shader shaderSplats { get; private set; }
        internal Shader shaderComposite { get; private set; }
        internal Shader shaderDebugPoints { get; private set; }
        internal Shader shaderDebugBoxes { get; private set; }
        // Compute shader is optional now since we use vertex shader mode
        internal ComputeShader csUtilities { get; private set; }
        // P1 SuperSplat parity: DEVICE_RADIX_SORT compute shader for the GPU sort path.
        // Loaded only if the platform supports compute; otherwise the CPU sort path stays live.
        internal ComputeShader csDeviceRadixSort { get; private set; }
        bool m_GpuSortProbeDone;
        bool m_GpuSortPlatformOk;

        // Compute supported + shader loaded + kernels compile and run on this GPU.
        internal bool gpuSortAvailable
        {
            get
            {
                if (!m_UseGpuSort || csDeviceRadixSort == null || !SystemInfo.supportsComputeShaders)
                    return false;
                if (!m_GpuSortProbeDone)
                {
                    m_GpuSortProbeDone = true;
                    m_GpuSortPlatformOk = ProbeGpuSortKernels(csDeviceRadixSort);
                    if (!m_GpuSortPlatformOk && m_UseGpuSort)
                        Debug.Log("[GaussianSplatSettings] DeviceRadixSort unavailable on this GPU — global/per-node sort uses CPU fallback.");
                }
                return m_GpuSortPlatformOk;
            }
        }

        static bool ProbeGpuSortKernels(ComputeShader cs)
        {
            try
            {
                var sorter = new GpuSorting(cs);
                return sorter.Valid;
            }
            catch
            {
                return false;
            }
        }

        void Awake()
        {
            if (ms_Instance != null && ms_Instance != this)
            {
                if (ms_Instance.gameObject.hideFlags == HideFlags.HideAndDontSave)
                    DestroyImmediate(ms_Instance.gameObject);
                else if (Application.isPlaying)
                    DestroyImmediate(ms_Instance);
                // Edit mode: keep all scene/prefab settings components; last Awake wins for instance pointer.
            }
            ms_Instance = this;
            EnsureResources();
        }

        void EnsureResources()
        {
            if (resourcesLoadAttempted)
                return;
            resourcesLoadAttempted = true;

            shaderSplats = Resources.Load<Shader>("GaussianSplats");
            shaderComposite = Resources.Load<Shader>("GaussianComposite");
            shaderDebugPoints = Resources.Load<Shader>("GaussianDebugRenderPoints");
            shaderDebugBoxes = Resources.Load<Shader>("GaussianDebugRenderBoxes");
            // Do not load compute shader - compute support is intentionally stripped; renderer uses vertex/fragment shaders only
            // csUtilities = Resources.Load<ComputeShader>("GaussianSplatUtilities");
            // ...except for the DEVICE_RADIX_SORT compute shader which we re-enabled for the GPU sort path.
            // Guarded by SystemInfo.supportsComputeShaders at instantiation time (see gpuSortAvailable).
            if (SystemInfo.supportsComputeShaders)
                csDeviceRadixSort = Resources.Load<ComputeShader>("DeviceRadixSort");

            resourcesFound =
                shaderSplats != null && shaderComposite != null && shaderDebugPoints != null && shaderDebugBoxes != null;
            // Compute shaders are optional for vertex shader mode
            UpdateGlobalOptions();
        }

        void OnValidate()
        {
            UpdateGlobalOptions();
        }

        void OnDidApplyAnimationProperties()
        {
            UpdateGlobalOptions();
        }

        void UpdateGlobalOptions()
        {
            // nothing just yet
        }
    }
}
