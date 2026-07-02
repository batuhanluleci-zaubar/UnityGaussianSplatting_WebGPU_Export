// SPDX-License-Identifier: MIT

using System; // Added for Exception
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;      // Alt#1 sort/submit split
using UnityEngine;
// Added for simple threading support
using System.Threading;
using System.Buffers;
using System.Threading.Tasks;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Octree-based spatial acceleration structure for Gaussian splat frustum culling.
    /// Divides scene bounds into hierarchical octants for efficient culling of static splats.
    /// </summary>
    public class GaussianSplatOctree
    {
        public class OctreeNode
        {
            public Bounds bounds;
            public Vector3 center;
            // For leaf nodes we store original splat indices that lie within this node's bounds.
            // For internal nodes this may be null or empty, will be sorted.
            public List<int> splatIndices;
            // Child node indices (indices into m_Nodes). Null or empty for leaf nodes.
            public List<int> childIndices;
            public bool isLeaf;
            // Track if this node's splats are sorted for current camera view
            public bool isSorted;
            // Store the camera position used for last sort (to detect when re-sort needed)
            public Vector3 lastSortCameraPosition;
            // Cached maximum extent (largest half-size axis) for angular sort threshold calculations
            public float maxExtent;
            // Persistent native copy of splat indices for native sorting (read-only input).
            public NativeArray<int> nativeSplatIndices;
            public bool nativeIndicesValid;

            // Slice 3: strided-permutation cache.
            // splatIndices under LOD is walked as `for(j=0;j<count;j+=step) dst[currentIndex++] = splatIndices[j];`
            // where step is discrete and bounded to [1, m_LodMaxStride]. The permutation is a pure function of
            // (splatIndices contents, step), so we cache one NativeArray<int> per (node, step) slot and swap the
            // per-index copy for a single UnsafeUtility.MemCpy on hit — the same shape as the step==1 fast path
            // above at L1196-1209. Slot 0 is unused (step is never 0); slot 1 is populated on demand and coexists
            // with the m_AppendScratch-based step==1 fast path (both are correct — cache path is preferred once warm).
            // stridedCacheEpoch[step] must equal node.sortEpoch for the slot to be valid; any splatIndices mutation
            // must call MarkSplatIndicesDirty(this) which bumps sortEpoch and blanket-invalidates all slots.
            public NativeArray<int>[] stridedCache;    // length = lodMaxStride+1, alloc lazily on first hit
            public int[] stridedCacheEpoch;            // per-slot epoch; slot valid iff == sortEpoch
            public int[] stridedCacheLastFrame;        // per-slot last-frame-used for LRU eviction
            public int sortEpoch;                       // bumped on every splatIndices mutation (see MarkSplatIndicesDirty)
        }

        public struct SplatInfo
        {
            public float3 position;
            public int originalIndex;
        }

        readonly List<OctreeNode> m_Nodes = new();
        NativeArray<int> m_VisibleSplatIndices;
        bool m_VisibleSplatIndicesValid;
        int m_TotalSplats; // Persist total splat count after releasing build-time list

        // Configuration
        int m_MaxDepth;
        int m_MaxSplatsPerLeaf;
        Bounds m_RootBounds;
        bool m_Built;

        // GPU buffer for visible splat indices (updated per frame/N frames)
        GraphicsBuffer m_VisibleIndicesBuffer;

        // Outlier splat indices that lie outside the main root bounds (always included in culling)
        readonly List<int> m_OthersIndices = new();
        // Persistent native copy of outlier indices (read-only input for native sort). Lazy-init.
        NativeArray<int> m_OthersNativeIndices;
        bool m_OthersNativeValid;
        
        // Reusable array for distance sorting to avoid allocations
        (float distance, int index)[] m_DistanceSortArray;

        // Structure to store visible node references with their distance for hierarchical sorting
        struct VisibleNodeRef
        {
            public float distance;
            public int nodeIndex; // Index into m_Nodes instead of copying splat indices
        }

        // Slice 1 / Fix A: static IComparer struct — replaces per-frame delegate lambda in
        // m_VisibleNodeRefs.Sort. List<T>.Sort dispatches through IComparer<T> for struct comparers
        // as a devirtualized call, eliminating both the per-frame delegate allocation and the virtual
        // dispatch overhead on ~60k comparisons that Comparison<T>/Comparer<T>.Create introduced.
        readonly struct VisibleNodeRefDistanceComparer : IComparer<VisibleNodeRef>
        {
            public int Compare(VisibleNodeRef a, VisibleNodeRef b)
            {
                // Front-to-back: smallest distance first. Use float.CompareTo to preserve NaN handling.
                return a.distance.CompareTo(b.distance);
            }
        }
        static readonly VisibleNodeRefDistanceComparer s_VisibleNodeRefDistanceComparer = default;

        // Reusable list for visible node references during sorting
        readonly List<VisibleNodeRef> m_VisibleNodeRefs = new();

        // Slice 1 / Fix D: reusable scratch int[] for the append-loop bulk-copy in
        // SortVisibleSplatsByDepth. Grown geometrically; never shrunk.
        int[] m_AppendScratch = System.Array.Empty<int>();
        void EnsureAppendScratch(int required)
        {
            if (m_AppendScratch.Length < required)
            {
                int newSize = m_AppendScratch.Length == 0 ? 1024 : m_AppendScratch.Length;
                while (newSize < required) newSize *= 2;
                m_AppendScratch = new int[newSize];
            }
        }

        // Slice 4 / Rank 1: pooled per-frame scratch for ParallelSortVisibleNodes to eliminate
        // per-call allocations of snapshot[], nodesToSort List, tasks[], workLock, and the
        // GetNextWorkIndex local-function closure. ParallelSortVisibleNodes is called serially
        // per octree (one call per SortVisibleSplatsByDepth); the previous-frame's tasks must be
        // complete before a new fanout is spawned (see previousSortTasksCompleted guard in
        // SortVisibleSplatsByDepth). That guard makes these instance fields safe to reuse.
        VisibleNodeRef[] m_SortScratchSnapshot = System.Array.Empty<VisibleNodeRef>();
        int m_SortScratchSnapshotLength;
        readonly List<int> m_SortScratchNodesToSort = new();
        readonly object m_SortWorkLock = new object();
        int m_SortNextWorkIndex;

        void EnsureSortScratchSnapshot(int required)
        {
            if (m_SortScratchSnapshot.Length < required)
            {
                int newSize = m_SortScratchSnapshot.Length == 0 ? 64 : m_SortScratchSnapshot.Length;
                while (newSize < required) newSize *= 2;
                m_SortScratchSnapshot = new VisibleNodeRef[newSize];
            }
        }

        // Hoisted from ParallelSortVisibleNodes local-function; called by worker Tasks.
        // Synchronization semantics unchanged: workers coordinate on m_SortWorkLock and pull
        // work indices from m_SortNextWorkIndex.
        int GetNextSortWorkIndex()
        {
            lock (m_SortWorkLock)
            {
                if (m_SortNextWorkIndex >= 0)
                    return m_SortNextWorkIndex--;
                return -1;
            }
        }
        
        // Reusable stack for non-recursive octree traversal
        readonly Stack<int> m_TraversalStack = new();
        
        // Enable / disable parallel sorting (public for runtime tuning)
        public bool enableParallelSorting = true;
        // Configurable number of worker threads for node sorting (excluding main thread)
        public int parallelSortThreads = 8; // Default sort threads, Safe for most of the platform
        // Maximum number of nodes to sort per frame to ensure closest nodes are prioritized during camera movement
        // This prevents frame time spikes by limiting sort work and ensures closest nodes are sorted first
        public int maxSortNodesPerFrame = 256; // In sequential path we do sort over time
        // Angular threshold for re-sorting: minimum cosine of angle change before re-sort is needed
        // cosine(15°) ≈ 0.966, cosine(30°) ≈ 0.866, cosine(45°) ≈ 0.707
        public float sortDirectionThreshold = 0.9f; // ~25.8° angle change threshold
        // Simplified outlier sorting strategy:
        // We keep an average radial distance (ring radius) of outliers from the root center.
        // Re-sorting occurs only when camera has moved more than (outlierRingRadius * outlierResortMoveFraction).
        // If the computed ring radius is zero (edge case), we fall back to a small constant.
        public float outlierResortMoveFraction = 0.1f; // 10% of ring radius movement triggers re-sort
        public float minOutlierResortDistance = 0.05f; // Fallback minimum distance if radius very small
        bool m_OthersSorted;            // Track if outliers are currently sorted
        Vector3 m_LastOthersSortCamPos; // Camera position at last outlier sort
        float m_OutlierRingRadius;      // Average radial distance of outliers from scene center

        Task[] m_SortTasks;
        
        // Native sorting job handles for WebGL platform
        readonly List<NativeSorting.SortJobHandle> m_NativeSortJobs = new();
        // Track which jobs correspond to which data structures
        readonly List<NativeSortJobInfo> m_NativeJobInfos = new();

        // Alt#1 diagnostic markers — split the GaussianSplatRenderGraph blob into sort vs submit
        // so we can data-drive P3 (unified buffer refactor) vs alternatives. Zero-alloc, safe in
        // Development + editor + player; picked up by ProfilerRecorder. See RendererMarkerRecorder.
        static readonly ProfilerMarker s_SortMarker = new ProfilerMarker("GaussianSplatOctree.SortChunks");
        // TEMPORARY sub-markers to split the 8.5ms SortVisibleSplatsByDepth into stages.
        // Remove once the bottleneck is identified. See RendererMarkerRecorder for readout wiring.
        static readonly ProfilerMarker s_SortCollectMarker = new ProfilerMarker("GaussianSplatOctree.Sort.Collect");   // (a) traversal + node-ref sort
        static readonly ProfilerMarker s_SortStartMarker = new ProfilerMarker("GaussianSplatOctree.Sort.Start");       // (b) StartNativeSortJobs or ParallelSortVisibleNodes
        static readonly ProfilerMarker s_SortWaitMarker = new ProfilerMarker("GaussianSplatOctree.Sort.Wait");         // (c) CollectNativeSortResults or Task.WaitAll
        static readonly ProfilerMarker s_SortAppendMarker = new ProfilerMarker("GaussianSplatOctree.Sort.Append");     // (d) append into m_VisibleSplatIndices
        // Slice 2 / Rank 1: split (d) Append into CPU strided-copy and GPU upload so we can attribute
        // the LockBufferForWrite win separately from the per-node List<int>->NativeArray copy loop.
        static readonly ProfilerMarker s_SortAppendCpuMarker = new ProfilerMarker("GaussianSplatOctree.Sort.AppendCpu");       // (d1) strided List<int> -> NativeArray fill
        static readonly ProfilerMarker s_SortAppendUploadMarker = new ProfilerMarker("GaussianSplatOctree.Sort.AppendUpload"); // (d2) UpdateVisibleIndicesBuffer GPU upload

        // Slice 3: per-frame counters for the strided-copy cache.
        //
        // Unity's ProfilerCounter<T> / ProfilerCounterValue<T> API is scattered across package versions;
        // we take the simplest cross-version approach: static aggregate fields that RendererMarkerRecorder
        // reads directly for CSV logging. Aggregation is sum across every octree updating in the frame,
        // which is what we want (the design targets total sort work per frame).
        //
        // Reset semantics: the recorder consumes each field once per LateUpdate. To keep the values sane
        // across many octrees, we snapshot into "public read-out" fields and reset the per-frame
        // accumulators at each SortVisibleSplats entry (per-octree). Bytes is the sum of live bytes across
        // all octrees — we recompute it as an atomic add/sub at every alloc/evict/dispose.
        //
        // NOTE: these are NOT thread-safe by design — every write happens on the main thread in
        // SortVisibleSplats / Clear. If that ever changes, wrap in Interlocked.
        public static int s_LastFrameCacheHits;
        public static int s_LastFrameCacheMisses;
        public static int s_LastFrameCacheEvictions;
        public static long s_LiveCacheBytes;

        // Per-octree per-frame accumulators — reset at top of SortVisibleSplats, folded into the public
        // static aggregate at end of frame.
        int m_StridedCacheHitsThisFrame;
        int m_StridedCacheMissesCountThisFrame;
        int m_StridedCacheEvictionsThisFrame;

        // Slice 3: cache accounting. m_StridedCacheBytes is the sum of NativeArray<int>.Length*sizeof(int) across
        // every live slot on every node in THIS octree. Under the global byte budget, cross-node LRU eviction is
        // driven by node.stridedCacheLastFrame[step] — the oldest-touched slot goes first.
        long m_StridedCacheBytes;
        // Public read-only view for tests / verify script. Bytes here is per-octree; the ProfilerCounter aggregates
        // per-frame across all octrees that update it (last-writer-wins is fine for a diagnostic).
        public long stridedCacheBytes => m_StridedCacheBytes;
        // Per-frame miss throttle counter — reset at the top of SortVisibleSplats.
        int m_StridedCacheMissesThisFrame;

        // Slice 3: cross-node LRU eviction candidate. Walking every node on every eviction is O(N_visible^2); instead
        // we track the set of nodes that currently own >=1 cached slot and scan that on eviction. Add on populate,
        // remove on last-slot-freed.
        readonly List<int> m_StridedCacheOwningNodes = new();
        readonly HashSet<int> m_StridedCacheOwningSet = new();

        // Slice 2 / Rank 4: per-frame frustum-plane cache. GeometryUtility.CalculateFrustumPlanes
        // marshals via P/Invoke AND allocates a new Plane[6] every call — we hit it up to 20 times
        // per frame today (1x SortVisibleSplatsByDepth + 1x PerformOctreeCulling per chunk).
        // Keyed on (Time.frameCount, cameraInstanceID) so XR two-eye and shadow-caster passes still
        // recompute per-camera per-frame while sibling octrees share the result.
        static readonly Plane[] s_FramePlanes = new Plane[6];
        static int s_FramePlanesFrame = -1;
        static int s_FramePlanesCamId;
        static Plane[] GetCachedFrustumPlanes(Camera cam)
        {
            int camId = cam.GetInstanceID();
            int frame = Time.frameCount;
            if (s_FramePlanesFrame != frame || s_FramePlanesCamId != camId)
            {
                GeometryUtility.CalculateFrustumPlanes(cam, s_FramePlanes);
                s_FramePlanesFrame = frame;
                s_FramePlanesCamId = camId;
            }
            return s_FramePlanes;
        }

        // Global native positions buffer (all splat positions) to avoid per-job copying
        NativeArray<float3> m_AllPositionsNative;
        bool m_AllPositionsNativeValid;
        
        struct NativeSortJobInfo
        {
            public bool isOutlierJob;
            public int nodeIndex; // -1 for outlier jobs
            public Vector3 cameraPosition;
            public NativeArray<int> inputIndices; // Per-job input splat indices (owned by job if disposeInput == true)
            public NativeArray<int> sortedIndices; // Per-job output sorted indices
            public bool disposeInput; // Whether we should dispose inputIndices when job completes
        }

        public int nodeCount => m_Nodes.Count;
        public int totalSplats => m_TotalSplats;
        public bool isBuilt => m_Built;
        public GraphicsBuffer visibleIndicesBuffer => m_VisibleIndicesBuffer;
        public int visibleSplatCount { get; private set; }

        // Helper to get splat position directly from global native buffer
        bool TryGetSplatPosition(int originalIndex, out float3 pos)
        {
            if (m_AllPositionsNativeValid && originalIndex >= 0 && originalIndex < m_AllPositionsNative.Length)
            {
                pos = m_AllPositionsNative[originalIndex];
                return true;
            }
            pos = default;
            return false;
        }

        // Helper to ensure the visible splat indices native array is large enough
        void EnsureVisibleSplatIndicesCapacity(int requiredCapacity)
        {
            if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated || m_VisibleSplatIndices.Length < requiredCapacity)
            {
                if (m_VisibleSplatIndicesValid && m_VisibleSplatIndices.IsCreated)
                {
                    try { m_VisibleSplatIndices.Dispose(); } catch {}
                }
                
                // Allocate with some extra space to avoid frequent reallocations
                int bufferSize = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 1));
                try
                {
                    m_VisibleSplatIndices = new NativeArray<int>(bufferSize, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    m_VisibleSplatIndicesValid = true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Failed to allocate visible splat indices native array: {ex.Message}");
                    m_VisibleSplatIndicesValid = false;
                }
            }
        }

        /// <summary>
        /// Initialize octree parameters. Call this before building.
        /// </summary>
        /// <param name="maxDepth">Maximum tree depth (typically 4-6)</param>
        /// <param name="maxSplatsPerLeaf">Maximum splats per leaf node (typically 64-256)</param>
        public void Initialize(int maxDepth = 5, int maxSplatsPerLeaf = 128)
        {
            m_MaxDepth = maxDepth;
            m_MaxSplatsPerLeaf = maxSplatsPerLeaf;

            // Print available system cores (both .NET and Unity reports)
            int envCores = Environment.ProcessorCount;
            int unityCores = SystemInfo.processorCount;
            if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"Available cores - Environment.ProcessorCount: {envCores}, SystemInfo.processorCount: {unityCores} (Might not be accurate on Web platform)");

            // Check if native threading is supported on current platform
            bool isWebPlatform = Application.platform == RuntimePlatform.WebGLPlayer;
            
            if(enableParallelSorting) 
            {
                // Try to initialize native sorting for supported platforms
                int nativeWorkers = Mathf.Max(1, envCores - 1); // Conservative worker count for all platforms
                NativeSorting.Initialize(nativeWorkers);

                int nativeWorkerCount = NativeSorting.GetWorkerCount();
                if (NativeSorting.IsAvailable && nativeWorkerCount > 0)
                {
                    parallelSortThreads = nativeWorkerCount;

                    string platformName = isWebPlatform ? "WebGL" : "native";
                    if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"GaussianSplatOctree: {platformName} platform — using native threading with {parallelSortThreads} workers");
                }
                else if (isWebPlatform)
                {
                    // WebGL without native support - fallback to sequential
                    enableParallelSorting = false;
                    Debug.LogWarning("GaussianSplatOctree: WebGL platform — native threading unavailable, using single-threaded fallback.");
                }
                else
                {
                    int reportedCores = SystemInfo.processorCount;
                    if (reportedCores > 0)
                        parallelSortThreads = reportedCores;
                }
            }
            
            if(enableParallelSorting)
                // Inform about the number of threads that will be used for parallel sorting
                if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"GaussianSplatOctree: parallelSortThreads set to {parallelSortThreads}");
        }

        /// <summary>
        /// Build octree from splat position data and bounds.
        /// </summary>
        public void Build(NativeArray<float3> splatPositions, Bounds sceneBounds, float splatPercent)
        {
            Clear();
            // m_OthersNodeIndex removed - use m_OthersIndices list instead

            if (splatPositions.Length == 0)
            {
                Debug.LogWarning("GaussianSplatOctree.Build: No splat positions provided");
                return;
            }

            if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"Building octree with {splatPositions.Length} splats, bounds: {sceneBounds}");

            // Compute center of mass and identify 95% closest splats
            int total = splatPositions.Length;
            m_TotalSplats = total;
            float3 com = float3.zero;
            for (int i = 0; i < total; i++)
                com += splatPositions[i];
            com /= total;

            var distList = new List<(int idx, float d)>(total);
            for (int i = 0; i < total; i++)
            {
                float distance = math.distance(splatPositions[i], com);
                distList.Add((i, distance));
            }
            distList.Sort((a, b) => a.d.CompareTo(b.d));

            // Reorder m_SplatInfos so that the closest part are first, others last
            int inCount = Mathf.CeilToInt(total * splatPercent);
            inCount = Mathf.Clamp(inCount, 1, total);
            int othersCount = total - inCount;

            // Local build-time splat info list
            var splatInfos = new List<SplatInfo>(total);
            for (int i = 0; i < total; i++)
            {
                int src = distList[i].idx;
                splatInfos.Add(new SplatInfo { position = splatPositions[src], originalIndex = src });
            }

            // Create / update global native positions buffer
            if (m_AllPositionsNativeValid)
            {
                if (m_AllPositionsNative.IsCreated) m_AllPositionsNative.Dispose();
                m_AllPositionsNativeValid = false;
            }
            try
            {
                m_AllPositionsNative = new NativeArray<float3>(total, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < splatInfos.Count; i++)
                {
                    var si = splatInfos[i];
                    int orig = si.originalIndex;
                    if ((uint)orig < (uint)total)
                        m_AllPositionsNative[orig] = si.position;
                }
                m_AllPositionsNativeValid = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to allocate global native positions buffer: {ex.Message}");
                if (m_AllPositionsNative.IsCreated) m_AllPositionsNative.Dispose();
                m_AllPositionsNativeValid = false;
            }

            // Create root bounds based on the inCount splats (centered on center-of-mass)
            Bounds rootBounds;
            if (inCount > 0)
            {
                float3 min = splatInfos[0].position;
                float3 max = splatInfos[0].position;
                for (int i = 1; i < inCount; i++)
                {
                    min = math.min(min, splatInfos[i].position);
                    max = math.max(max, splatInfos[i].position);
                }
                rootBounds = new Bounds((max + min) * 0.5f, max - min);
            }
            else
            {
                // Fallback to provided scene bounds
                rootBounds = sceneBounds;
            }

            m_RootBounds = rootBounds;

            // Build tree recursively using only the in-root splats
            m_Nodes.Clear();

            // Create root node covering the in-root splats
            var rootNode = new OctreeNode
            {
                bounds = m_RootBounds,
                center = m_RootBounds.center,
                splatIndices = null,
                childIndices = null,
                isLeaf = false,
                maxExtent = Mathf.Max(m_RootBounds.extents.x, Mathf.Max(m_RootBounds.extents.y, m_RootBounds.extents.z))
            };
            m_Nodes.Add(rootNode);

            // Build recursively starting from root (only for the in-root partition)
            var rootSplatList = new List<int>(inCount);
            for (int i = 0; i < inCount; i++) rootSplatList.Add(i); // indices into splatInfos
            BuildRecursive(0, 0, rootSplatList, splatInfos);

            // Handle remaining outliers: put their original indices into m_SplatIndices and track them in m_OthersIndices
            m_OthersIndices.Clear();
            if (othersCount > 0)
            {
                for (int i = 0; i < othersCount; i++)
                {
                    int orig = splatInfos[inCount + i].originalIndex;
                    m_OthersIndices.Add(orig);
                }
            }
            m_OthersSorted = false; // reset outlier sorting state after build
            m_LastOthersSortCamPos = Vector3.zero;
            // Compute average outlier ring radius (ignore min/max & extra stats for simplicity)
            m_OutlierRingRadius = 0f;
            if (othersCount > 0 && m_AllPositionsNativeValid)
            {
                Vector3 center = m_RootBounds.center;
                double accum = 0.0;
                for (int i = 0; i < othersCount; i++)
                {
                    int orig = splatInfos[inCount + i].originalIndex;
                    if (orig >= 0 && orig < m_AllPositionsNative.Length)
                    {
                        float3 p = m_AllPositionsNative[orig];
                        accum += Vector3.Distance(center, (Vector3)p);
                    }
                }
                m_OutlierRingRadius = (float)(accum / othersCount);
            }

            // Tighten bounding boxes starting from leaves and propagating up
            TightenBounds();

            m_Built = true;

            // Ensure a GPU buffer exists even if there are no visible splats yet.
            // Allocate a minimal 1-entry structured buffer so renderer code can safely bind/check it.
            if (m_VisibleIndicesBuffer == null)
            {
                // Slice 2 / Rank 3: LockBufferForWrite usage — required flag for the fast path in
                // UpdateVisibleIndicesBuffer. Zero-fill via a one-shot LockBufferForWrite instead of
                // SetData (still a cold-path one-off, but keeps the code path symmetrical).
                m_VisibleIndicesBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    1, sizeof(uint))
                {
                    name = "GaussianSplatVisibleIndices"
                };
                var mapped = m_VisibleIndicesBuffer.LockBufferForWrite<uint>(0, 1);
                mapped[0] = 0u;
                m_VisibleIndicesBuffer.UnlockBufferAfterWrite<uint>(1);
                visibleSplatCount = 0;
            }

            if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"Octree build completed: {m_Nodes.Count} total nodes, others={m_OthersIndices.Count}");

            EnsureVisibleSplatIndicesCapacity(m_TotalSplats);
        }

        void BuildRecursive(int nodeIndex, int depth, List<int> splatList, List<SplatInfo> splatInfos)
        {
            var node = m_Nodes[nodeIndex];

            // Check termination conditions
            if (depth >= m_MaxDepth || splatList.Count <= m_MaxSplatsPerLeaf)
            {
                // Make this a leaf node and store original indices for this leaf
                node.isLeaf = true;
                node.splatIndices = new List<int>(splatList.Count);
                for (int i = 0; i < splatList.Count; i++)
                {
                    int infoIdx = splatList[i];
                    if (infoIdx < 0 || infoIdx >= splatInfos.Count)
                    {
                        Debug.LogError($"Octree leaf node splat info index out of bounds: {infoIdx} >= {splatInfos.Count}");
                        continue;
                    }
                    node.splatIndices.Add(splatInfos[infoIdx].originalIndex);
                }
                // Slice 3: initial fill counts as a mutation — but epoch starts at 0 and cache slots start at
                // epoch 0 too, so we must bump here or the FIRST populate would false-hit an empty slot.
                MarkSplatIndicesDirty(node);

                m_Nodes[nodeIndex] = node;
                return;
            }

            // Create 8 child nodes
            var center = node.bounds.center;
            var size = node.bounds.size * 0.5f;

            node.childIndices = new List<int>(8);
            node.isLeaf = false;
            m_Nodes[nodeIndex] = node;

            // Create child bounds
            var childBounds = new Bounds[8];
            for (int i = 0; i < 8; i++)
            {
                var offset = new Vector3(
                    (i & 1) != 0 ? size.x * 0.5f : -size.x * 0.5f,
                    (i & 2) != 0 ? size.y * 0.5f : -size.y * 0.5f,
                    (i & 4) != 0 ? size.z * 0.5f : -size.z * 0.5f
                );
                childBounds[i] = new Bounds(center + offset, size);
            }

            // Distribute splats to children
            var childSplatsIdx = new List<int>[8];
            for (int i = 0; i < 8; i++) childSplatsIdx[i] = new List<int>();

            // Assign splats (using splatList which holds indices into m_SplatInfos) to child nodes
            for (int ii = 0; ii < splatList.Count; ii++)
            {
                int infoIdx = splatList[ii];
                if (infoIdx < 0 || infoIdx >= splatInfos.Count)
                {
                    Debug.LogError($"Octree splat distribution info index out of bounds: {infoIdx} >= {splatInfos.Count}");
                    continue;
                }

                var splat = splatInfos[infoIdx];

                int childIndex = 0;
                if (splat.position.x > center.x) childIndex |= 1;
                if (splat.position.y > center.y) childIndex |= 2;
                if (splat.position.z > center.z) childIndex |= 4;

                childSplatsIdx[childIndex].Add(infoIdx);
            }

            // Create child nodes
            for (int i = 0; i < 8; i++)
            {
                var childNode = new OctreeNode
                {
                    bounds = childBounds[i],
                    center = childBounds[i].center,
                    splatIndices = null,
                    childIndices = null,
                    isLeaf = childSplatsIdx[i].Count == 0,
                    maxExtent = Mathf.Max(childBounds[i].extents.x, Mathf.Max(childBounds[i].extents.y, childBounds[i].extents.z))
                };

                int childNodeIndex = m_Nodes.Count;
                m_Nodes.Add(childNode);

                // Register child index with parent
                node.childIndices.Add(childNodeIndex);
                // Update parent reference in the global list (node is a reference type)
                m_Nodes[nodeIndex] = node;

                // Recursively build child only if it has splats
                if (childSplatsIdx[i].Count > 0)
                {
                    BuildRecursive(childNodeIndex, depth + 1, childSplatsIdx[i], splatInfos);
                }
            }
        }

        /// <summary>
        /// Tighten bounding boxes for all nodes based on actual splat positions.
        /// Starts from leaf nodes and propagates up to parent nodes.
        /// </summary>
        void TightenBounds()
        {
            if (m_Nodes.Count == 0)
                return;

            int tightenedNodes = 0;
            
            // Process nodes in reverse order to handle leaves first, then propagate up
            for (int i = m_Nodes.Count - 1; i >= 0; i--)
            {
                if (TightenNodeBounds(i))
                    tightenedNodes++;
            }

            if (GaussianSplatSettings.instance.m_VerboseLog) Debug.Log($"Octree bounds tightened: {tightenedNodes}/{m_Nodes.Count} nodes updated");
        }

        /// <summary>
        /// Tighten the bounds of a specific node based on its splats or child bounds.
        /// </summary>
        /// <returns>True if the bounds were changed, false otherwise</returns>
        bool TightenNodeBounds(int nodeIndex)
        {
            if (nodeIndex >= m_Nodes.Count)
                return false;

            var node = m_Nodes[nodeIndex];
            var originalBounds = node.bounds;

            if (node.isLeaf)
            {
                if (node.splatIndices != null && node.splatIndices.Count > 0)
                {
                    int firstSplatIdx = node.splatIndices[0];
                    if (TryGetSplatPosition(firstSplatIdx, out float3 firstPos))
                    {
                        float3 min = firstPos;
                        float3 max = firstPos;
                        for (int i = 1; i < node.splatIndices.Count; i++)
                        {
                            int splatIdx = node.splatIndices[i];
                            if (TryGetSplatPosition(splatIdx, out float3 pos))
                            {
                                min = math.min(min, pos);
                                max = math.max(max, pos);
                            }
                        }
                        Vector3 center = (Vector3)((min + max) * 0.5f);
                        Vector3 size = (Vector3)(max - min);
                        const float minSize = 0.001f;
                        size.x = Mathf.Max(size.x, minSize);
                        size.y = Mathf.Max(size.y, minSize);
                        size.z = Mathf.Max(size.z, minSize);
                        node.bounds = new Bounds(center, size);
                        node.maxExtent = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f;
                        m_Nodes[nodeIndex] = node;
                        return !BoundsAreEqual(originalBounds, node.bounds);
                    }
                }
                return false;
            }
            else
            {
                // For internal nodes, calculate bounds based on child node bounds
                if (node.childIndices != null && node.childIndices.Count > 0)
                {
                    bool hasValidChild = false;
                    float3 min = float3.zero;
                    float3 max = float3.zero;

                    foreach (int childIndex in node.childIndices)
                    {
                        if (childIndex < m_Nodes.Count)
                        {
                            var childNode = m_Nodes[childIndex];
                            
                            // Only include non-empty children in bounds calculation
                            bool childHasContent = childNode.isLeaf 
                                ? (childNode.splatIndices != null && childNode.splatIndices.Count > 0)
                                : (childNode.childIndices != null && childNode.childIndices.Count > 0);

                            if (childHasContent)
                            {
                                Vector3 childMin = childNode.bounds.min;
                                Vector3 childMax = childNode.bounds.max;

                                if (!hasValidChild)
                                {
                                    min = (float3)childMin;
                                    max = (float3)childMax;
                                    hasValidChild = true;
                                }
                                else
                                {
                                    min = math.min(min, (float3)childMin);
                                    max = math.max(max, (float3)childMax);
                                }
                            }
                        }
                    }

                    // Update bounds if we found valid children
                    if (hasValidChild)
                    {
                        Vector3 center = (Vector3)((min + max) * 0.5f);
                        Vector3 size = (Vector3)(max - min);
                        
                        // Ensure minimum size to avoid zero-size bounds
                        const float minSize = 0.001f;
                        size.x = Mathf.Max(size.x, minSize);
                        size.y = Mathf.Max(size.y, minSize);
                        size.z = Mathf.Max(size.z, minSize);

                        node.bounds = new Bounds(center, size);
                        node.maxExtent = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.5f;

                        // Update the node in the list
                        m_Nodes[nodeIndex] = node;
                        
                        // Check if bounds actually changed
                        return !BoundsAreEqual(originalBounds, node.bounds);
                    }
                }
                return false; // No valid children, bounds unchanged
            }
        }

        /// <summary>
        /// Helper method to compare two bounds for equality with small tolerance.
        /// </summary>
        bool BoundsAreEqual(Bounds a, Bounds b)
        {
            const float tolerance = 1e-6f;
            return Vector3.Distance(a.center, b.center) < tolerance && 
                   Vector3.Distance(a.size, b.size) < tolerance;
        }

        /// <summary>
        /// Perform frustum culling and update visible splat indices.
        /// Returns number of visible splats.
        /// </summary>
        public int CullFrustum(Camera camera)
        {
            if (!m_Built)
                return 0;

            // Estimate capacity needed (total splats as upper bound)
            int estimatedCapacity = m_TotalSplats;

            if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
            {
                visibleSplatCount = 0;
                return 0;
            }

            // Slice 2 / Rank 4: cached frustum planes — up to 20 chunks share the same camera-frame result.
            var frustumPlanes = GetCachedFrustumPlanes(camera);

            // Traverse octree and collect visible splats
            int currentIndex = 0;
            CullNodeRecursive(0, frustumPlanes, ref currentIndex);

            // Always include 'others' outlier splats
            if (m_OthersIndices.Count > 0)
            {
                // Ensure we have enough space for outliers
                if (currentIndex + m_OthersIndices.Count > m_VisibleSplatIndices.Length)
                {
                    if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
                    {
                        visibleSplatCount = 0;
                        return 0;
                    }
                }
                
                // Copy outlier indices
                for (int i = 0; i < m_OthersIndices.Count; i++)
                {
                    m_VisibleSplatIndices[currentIndex + i] = m_OthersIndices[i];
                }
                currentIndex += m_OthersIndices.Count;
            }

            visibleSplatCount = currentIndex;

            // Update GPU buffer
            UpdateVisibleIndicesBuffer();

            return visibleSplatCount;
        }

        void CullNodeRecursive(int nodeIndex, Plane[] frustumPlanes, ref int currentIndex)
        {
            if (nodeIndex >= m_Nodes.Count)
                return;

            var node = m_Nodes[nodeIndex];

            // Test node bounds against frustum
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, node.bounds))
                return; // Node is outside frustum

            if (node.isLeaf)
            {
                // Add all splats in this leaf to visible list (skip empty leaves)
                if (node.splatIndices != null && node.splatIndices.Count > 0)
                {
                    // Ensure we have enough space
                    if (currentIndex + node.splatIndices.Count > m_VisibleSplatIndices.Length)
                    {
                        if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
                            return;
                    }
                    
                    // Copy splat indices
                    for (int i = 0; i < node.splatIndices.Count; i++)
                    {
                        m_VisibleSplatIndices[currentIndex + i] = node.splatIndices[i];
                    }
                    currentIndex += node.splatIndices.Count;
                }
            }
            else
            {
                // Recursively test child nodes - only traverse non-empty children
                // Traverse registered child indices
                if (node.childIndices != null)
                {
                    foreach (var childIndex in node.childIndices)
                    {
                        if (childIndex < m_Nodes.Count)
                        {
                            var childNode = m_Nodes[childIndex];
                            if ((childNode.splatIndices != null && childNode.splatIndices.Count > 0) || !childNode.isLeaf)
                            {
                                CullNodeRecursive(childIndex, frustumPlanes, ref currentIndex);
                            }
                        }
                    }
                }
            }
        }

        void UpdateVisibleIndicesBuffer()
        {
            if (visibleSplatCount == 0)
                return;

            if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
            {
                Debug.LogWarning("Visible splat indices native array is invalid during buffer update");
                return;
            }

            // Ensure buffer is large enough
            int requiredSize = visibleSplatCount;
            if (m_VisibleIndicesBuffer == null || m_VisibleIndicesBuffer.count < requiredSize)
            {
                m_VisibleIndicesBuffer?.Dispose();
                // Allocate with some extra space to avoid frequent reallocations
                int bufferSize = Mathf.NextPowerOfTwo(requiredSize);
                // Slice 2 / Rank 3: LockBufferForWrite usage flag — enables zero-copy mapped-VRAM
                // upload path via LockBufferForWrite/UnlockBufferAfterWrite instead of the double-copy
                // SetData staging round-trip. On drivers that can't map the target directly this falls
                // back to a staging path but still eliminates one driver submission per SetData call.
                m_VisibleIndicesBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    GraphicsBuffer.UsageFlags.LockBufferForWrite,
                    bufferSize, sizeof(uint))
                {
                    name = "GaussianSplatVisibleIndices"
                };
            }

            // Slice 2 / Rank 3: replace SetData with LockBufferForWrite + UnsafeUtility.MemCpy.
            // Kills 20 driver round-trips per frame (one per chunk). Also removes the safety-handle
            // marshalling needed for the ConvertExistingDataToNativeArray path.
            unsafe
            {
                var mapped = m_VisibleIndicesBuffer.LockBufferForWrite<uint>(0, visibleSplatCount);
                UnsafeUtility.MemCpy(
                    mapped.GetUnsafePtr(),
                    m_VisibleSplatIndices.GetUnsafeReadOnlyPtr(),
                    (long)visibleSplatCount * sizeof(uint));
                m_VisibleIndicesBuffer.UnlockBufferAfterWrite<uint>(visibleSplatCount);
            }
        }

        /// <summary>
        /// Get debug information about octree structure.
        /// </summary>
        public void GetDebugInfo(out int leafNodes, out int maxDepthReached, out int maxSplatsInLeaf)
        {
            leafNodes = 0;
            maxDepthReached = 0;
            maxSplatsInLeaf = 0;

            GetDebugInfoRecursive(0, 0, ref leafNodes, ref maxDepthReached, ref maxSplatsInLeaf);
        }

        void GetDebugInfoRecursive(int nodeIndex, int depth, ref int leafNodes, ref int maxDepth, ref int maxSplats)
        {
            if (nodeIndex >= m_Nodes.Count)
                return;

            var node = m_Nodes[nodeIndex];
            maxDepth = Mathf.Max(maxDepth, depth);

            if (node.isLeaf)
            {
                // Only count non-empty leaves
                if (node.splatIndices != null && node.splatIndices.Count > 0)
                {
                    leafNodes++;
                    maxSplats = Mathf.Max(maxSplats, node.splatIndices.Count);
                }
            }
            else
            {
                // Traverse registered child indices
                if (node.childIndices != null)
                {
                    foreach (var childIndex in node.childIndices)
                    {
                        if (childIndex < m_Nodes.Count)
                        {
                            GetDebugInfoRecursive(childIndex, depth + 1, ref leafNodes, ref maxDepth, ref maxSplats);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Draw wireframe boxes for each non-empty leaf node. Call this from a MonoBehaviour's OnDrawGizmos or OnDrawGizmosSelected.
        /// </summary>
        public void DrawLeafBoundsGizmos(Color color)
        {
            if (!m_Built || m_Nodes.Count == 0)
                return;

            var prev = Gizmos.color;
            Gizmos.color = color;

            for (int i = 0; i < m_Nodes.Count; i++)
            {
                var node = m_Nodes[i];
                if (!node.isLeaf)
                    continue;

                // Skip empty leaves
                if (node.splatIndices == null || node.splatIndices.Count <= 0)
                    continue;

                Gizmos.DrawWireCube(node.bounds.center, node.bounds.size);
            }

            Gizmos.color = prev;
        }

        // Slice 3: single-entry point for invalidating strided-permutation cache.
        // Any code that mutates node.splatIndices (adds, removes, reorders) MUST call this immediately after.
        // Bumping sortEpoch invalidates every cache slot for the node in O(1) — the NativeArrays themselves
        // stay allocated and will be reused on the next populate for that (node, step) pair, so we don't
        // thrash the allocator when only a re-sort (same-size permutation) happens.
        //
        // NOTE ON THREADING: this MUST be called on the main thread only. Parallel sort workers write to
        // splatIndices off-thread; the epoch bump lives with the main-thread apply step (ApplySortedNodeResults
        // and the sequential SortNodeSplats path) so that the append loop's read of sortEpoch stays coherent
        // without needing Volatile/Interlocked. See risk note in the design.
        static void MarkSplatIndicesDirty(OctreeNode node)
        {
            if (node == null) return;
            // Wrap once every ~2 billion mutations. Even at that limit, existing slot epochs would have to
            // match the new value exactly to false-hit — vanishingly unlikely, but sub 0 wraparound avoided
            // by staying in signed positive space is fine (sortEpoch starts at 0, initial epoch entries are 0
            // in newly-alloc'd int[]s, so the FIRST bump takes us to 1 and initial arrays never false-hit).
            unchecked { node.sortEpoch++; }
        }

        // Slice 3: dispose every cached NativeArray on a single node. Called on Clear() and on per-node
        // teardown. Zero-safe: null and un-created arrays are skipped. Updates m_StridedCacheBytes.
        void DisposeNodeStridedCache(OctreeNode node)
        {
            if (node?.stridedCache == null) return;
            for (int s = 0; s < node.stridedCache.Length; s++)
            {
                var arr = node.stridedCache[s];
                if (arr.IsCreated)
                {
                    m_StridedCacheBytes -= (long)arr.Length * sizeof(int);
                    try { arr.Dispose(); } catch {}
                    node.stridedCache[s] = default;
                }
                if (node.stridedCacheEpoch != null) node.stridedCacheEpoch[s] = 0;
                if (node.stridedCacheLastFrame != null) node.stridedCacheLastFrame[s] = 0;
            }
            if (m_StridedCacheBytes < 0) m_StridedCacheBytes = 0;
        }

        // Slice 3: global cross-node LRU. Called when a new allocation would push m_StridedCacheBytes past the
        // configured budget. Frees whole slots from the least-recently-used owner until we're back under
        // budget (or we've evicted every non-current-frame slot). Returns true if any bytes freed.
        bool EvictStridedCacheDownTo(long targetBytes, int currentFrame)
        {
            if (m_StridedCacheBytes <= targetBytes) return false;
            bool anyFreed = false;
            // Bounded pass: worst-case scan all owning nodes twice. If we can't free below target
            // (e.g. all slots are from THIS frame and untouchable), we bail — caller falls back.
            int guard = 0;
            while (m_StridedCacheBytes > targetBytes && guard++ < 8)
            {
                int oldestFrame = int.MaxValue;
                int oldestNodeIdx = -1;
                int oldestSlot = -1;
                long oldestBytes = 0;
                for (int i = 0; i < m_StridedCacheOwningNodes.Count; i++)
                {
                    int ni = m_StridedCacheOwningNodes[i];
                    if ((uint)ni >= (uint)m_Nodes.Count) continue;
                    var n = m_Nodes[ni];
                    if (n?.stridedCache == null) continue;
                    for (int s = 0; s < n.stridedCache.Length; s++)
                    {
                        var arr = n.stridedCache[s];
                        if (!arr.IsCreated) continue;
                        int lf = n.stridedCacheLastFrame[s];
                        // Never evict a slot touched THIS frame — it may still be needed later this frame.
                        if (lf == currentFrame) continue;
                        if (lf < oldestFrame)
                        {
                            oldestFrame = lf;
                            oldestNodeIdx = ni;
                            oldestSlot = s;
                            oldestBytes = (long)arr.Length * sizeof(int);
                        }
                    }
                }
                if (oldestNodeIdx < 0) break; // nothing evictable
                var victim = m_Nodes[oldestNodeIdx];
                var victimArr = victim.stridedCache[oldestSlot];
                m_StridedCacheBytes -= oldestBytes;
                try { victimArr.Dispose(); } catch {}
                victim.stridedCache[oldestSlot] = default;
                victim.stridedCacheEpoch[oldestSlot] = 0;
                victim.stridedCacheLastFrame[oldestSlot] = 0;
                m_StridedCacheEvictionsThisFrame++;
                anyFreed = true;
                // If node has no live slots left, unregister as owner.
                bool anyLive = false;
                for (int s = 0; s < victim.stridedCache.Length; s++)
                    if (victim.stridedCache[s].IsCreated) { anyLive = true; break; }
                if (!anyLive) StridedCacheUnregisterOwner(oldestNodeIdx);
            }
            if (m_StridedCacheBytes < 0) m_StridedCacheBytes = 0;
            return anyFreed;
        }

        void StridedCacheRegisterOwner(int nodeIndex)
        {
            if (m_StridedCacheOwningSet.Add(nodeIndex))
                m_StridedCacheOwningNodes.Add(nodeIndex);
        }
        void StridedCacheUnregisterOwner(int nodeIndex)
        {
            if (m_StridedCacheOwningSet.Remove(nodeIndex))
                m_StridedCacheOwningNodes.Remove(nodeIndex);
        }

        // Slice 3: default platform byte budget when m_StridedCacheGlobalByteBudget == 0. Desktop 64 MB,
        // mobile / XR / WebGL 16 MB — the design note explicitly caps XREAL Aura at 16 MB.
        static long DefaultStridedCacheByteBudget()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.Android:
                case RuntimePlatform.IPhonePlayer:
                case RuntimePlatform.WebGLPlayer:
                    return 16L * 1024 * 1024;
                default:
                    return 64L * 1024 * 1024;
            }
        }

        public void Clear()
        {
            // Cleanup native sorting jobs
            CleanupNativeSortJobs();
            // Dispose per-node native buffers before clearing list
            for (int i = 0; i < m_Nodes.Count; i++)
            {
                var n = m_Nodes[i];
                if (n.nativeIndicesValid && n.nativeSplatIndices.IsCreated)
                {
                    try { n.nativeSplatIndices.Dispose(); } catch {}
                    n.nativeIndicesValid = false;
                }
                // Slice 3: strided-cache teardown per node.
                DisposeNodeStridedCache(n);
            }
            m_StridedCacheOwningNodes.Clear();
            m_StridedCacheOwningSet.Clear();
            // Publish live-bytes drop to the global aggregate before resetting local counter.
            s_LiveCacheBytes -= m_StridedCacheBytes;
            if (s_LiveCacheBytes < 0) s_LiveCacheBytes = 0;
            m_StridedCacheBytes = 0;
            if (m_OthersNativeValid && m_OthersNativeIndices.IsCreated)
            {
                try { m_OthersNativeIndices.Dispose(); } catch {}
                m_OthersNativeValid = false;
            }
            m_Nodes.Clear();
            if (m_VisibleSplatIndicesValid && m_VisibleSplatIndices.IsCreated)
            {
                try { m_VisibleSplatIndices.Dispose(); } catch {}
                m_VisibleSplatIndicesValid = false;
            }
            m_VisibleNodeRefs.Clear();
            m_TraversalStack.Clear(); // Clear the reusable stack
            m_VisibleIndicesBuffer?.Dispose();
            m_VisibleIndicesBuffer = null;
            m_DistanceSortArray = null; // Release sort array memory
            visibleSplatCount = 0;
            m_Built = false;
            m_OthersIndices.Clear();
            m_OthersSorted = false;
            m_LastOthersSortCamPos = Vector3.zero;
            m_OutlierRingRadius = 0f;

            if (m_AllPositionsNativeValid && m_AllPositionsNative.IsCreated)
            {
                m_AllPositionsNative.Dispose();
                m_AllPositionsNativeValid = false;
            }

            m_TotalSplats = 0;
        }

        public void Dispose()
        {
            Clear();
            
            // Shutdown native sorting if it was initialized
            if (NativeSorting.IsAvailable)
            {
                NativeSorting.Shutdown();
            }
        }
        
        void CleanupNativeSortJobs()
        {
            for (int i = m_NativeSortJobs.Count - 1; i >= 0; i--)
            {
                var handle = m_NativeSortJobs[i];
                if (handle.IsValid)
                {
                    try { NativeSorting.CleanupJob(handle); } catch { }
                }
                var info = m_NativeJobInfos[i];
                try
                {
                    if (info.disposeInput && info.inputIndices.IsCreated) info.inputIndices.Dispose();
                    if (info.sortedIndices.IsCreated) info.sortedIndices.Dispose();
                }
                catch { }
                m_NativeSortJobs.RemoveAt(i);
                m_NativeJobInfos.RemoveAt(i);
            }
        }

        /// <summary>
        /// Sort visible splat indices by 3D distance from camera (front-to-back for alpha blending).
        /// Hierarchical sorting optimization.
        /// </summary>
        public void SortVisibleSplatsByDepth(Camera camera)
        {
            if (!m_Built)
                return;
            using var _sortScope = s_SortMarker.Auto();   // Alt#1 marker — measures front-to-back CPU sort
            var camPosition = camera.transform.position;

            if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
            {
                visibleSplatCount = 0;
                return;
            }

            // Slice 2 / Rank 2: empty octree short-circuit — no nodes, nothing to sort or upload.
            // Prior sort tasks (if any) must still be allowed to complete on their own; we only skip
            // the per-frame work here. m_SortTasks are joined non-blockingly on the next real frame.
            if (m_Nodes.Count == 0)
            {
                visibleSplatCount = 0;
                return;
            }

            // TEMP sub-marker (a): visible-node traversal + m_VisibleNodeRefs.Sort
            m_VisibleNodeRefs.Clear();
            var frustumPlanes = GetCachedFrustumPlanes(camera);
            // Slice 2 / Rank 2: root-AABB reject — if the octree root is fully outside the frustum,
            // skip traversal, sort-start, and Append entirely. This is exactly the same TestPlanesAABB
            // that CullNodeRecursive would do at the root; hoisting it lets us also skip the sort-start
            // work below. Uses m_Nodes[0].bounds (root, world-space-equivalent-under-identity-transform)
            // vs camera-derived world-space planes — same semantics as CullNodeRecursive today.
            if (!GeometryUtility.TestPlanesAABB(frustumPlanes, m_Nodes[0].bounds))
            {
                visibleSplatCount = 0;
                return;
            }
            s_SortCollectMarker.Begin();
            CollectVisibleNodesWithDistance(0, frustumPlanes, camPosition);
            // Node-ref sort belongs in (a); it runs on every path.
            // In the parallel paths below it's kicked off after the workers spawn, but the cost is
            // the same, and folding it here gives a clean "traversal + node-ref sort" bucket.
            // (See the second .Sort call below — we skip re-sorting inside the parallel branches.)
            // Slice 1 / Fix A: static struct comparer replaces per-frame lambda (no delegate alloc, no virtual dispatch)
            m_VisibleNodeRefs.Sort(s_VisibleNodeRefDistanceComparer);
            s_SortCollectMarker.End();

            // Slice 2 / Rank 2: zero-visible-refs early exit — nothing to sort or append. Outliers are
            // handled by the caller-side CullNodeRecursive path already; here we ONLY skip the sort/append
            // pipeline. Still probe m_SortTasks so any prior async work drains cleanly next frame.
            if (m_VisibleNodeRefs.Count == 0 && m_OthersIndices.Count == 0)
            {
                visibleSplatCount = 0;
                return;
            }

            if (enableParallelSorting)
            {
                if (NativeSorting.IsAvailable)
                {
                    // Use native sorting for supported platforms
                    // TEMP sub-marker (c): CollectNativeSortResults (non-blocking join of prior frame's jobs)
                    s_SortWaitMarker.Begin();
                    bool previousNativeJobsCompleted = CollectNativeSortResults();
                    s_SortWaitMarker.End();

                    // TEMP sub-marker (b): StartNativeSortJobs
                    if (previousNativeJobsCompleted)
                    {
                        s_SortStartMarker.Begin();
                        int jobsStarted = StartNativeSortJobs(camPosition);
                        s_SortStartMarker.End();
                        //if (jobsStarted > 0)
                        //    Debug.Log($"Started {jobsStarted} new native sort jobs");
                    }
                }
                else
                {
                    // Use Unity Task system for other platforms
                    // Non-blocking check: set a flag indicating whether previous sort tasks have finished.
                    // TEMP sub-marker (c): probe prior task completion (non-blocking — Task.WaitAll is intentionally not called)
                    s_SortWaitMarker.Begin();
                    bool previousSortTasksCompleted = true;
                    if (m_SortTasks != null)
                    {
                        int taskListSize = m_SortTasks.Length;
                        for (int i = 0; i < m_SortTasks.Length; i++)
                        {
                            var t = m_SortTasks[i];
                            if (t != null && !t.IsCompleted)
                            {
                                previousSortTasksCompleted = false;
                                break;
                            }
                        }
                    }
                    s_SortWaitMarker.End();
                    // TEMP sub-marker (b): ParallelSortVisibleNodes spawn cost (Task.Run per node fanout)
                    if (previousSortTasksCompleted)
                    {
                        s_SortStartMarker.Begin();
                        m_SortTasks = ParallelSortVisibleNodes(camPosition);
                        s_SortStartMarker.End();
                    }
                    // Now join the tasks after doing useful work on main thread (if desired)
                    // JoinParallelSortThreads(m_SortTasks);
                }
            }
            else
            {
                // Sequential path: node refs already sorted above under (a). Run inline sort now.
                // TEMP sub-marker (b): sequential sort work is a spawn+run in one step
                s_SortStartMarker.Begin();
                // Sequential path: sort outliers (background elements processed last in front-to-back)
                if (m_OthersIndices.Count > 0)
                {
                    SortOutliers(camPosition);
                }
                // Limit sorting to closest nodes per frame for better performance during camera movement
                int nodesToSort = Mathf.Min(m_VisibleNodeRefs.Count, maxSortNodesPerFrame);
                int nodesSorted = 0;
                for (int i = 0; i < m_VisibleNodeRefs.Count && nodesSorted < nodesToSort; i++) // Front-to-back processing
                {
                    var nodeRef = m_VisibleNodeRefs[i];
                    if (SortNodeSplats(nodeRef.nodeIndex, camPosition))
                    {
                        nodesSorted++;
                    }
                }
                s_SortStartMarker.End();
            }
            // TEMP sub-marker (d): append sorted per-node lists into m_VisibleSplatIndices (screen-space LOD stride, budget cap, GraphicsBuffer upload)
            using var _sortAppendScope = s_SortAppendMarker.Auto();
            // Slice 2 / Rank 1: CPU-strided-copy sub-marker opens here; closed BEFORE UpdateVisibleIndicesBuffer.
            s_SortAppendCpuMarker.Begin();
            // Append nodes in distance order (their lists now internally sorted and persistent)
            int currentIndex = 0;

            // Screen-space LOD: subsample splats in distant / on-screen-small nodes. This directly cuts
            // the DrawProcedural instance count, which is the dominant cost for multi-million-splat scenes.
            var lodSettings = GaussianSplatSettings.instance;
            bool lodOn = lodSettings != null && lodSettings.m_EnableScreenLod;
            float lodFocalPx = camera.pixelHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
            float lodFullPx = lodOn ? Mathf.Max(1f, lodSettings.m_LodFullDetailPixels) : 0f;
            int lodMaxStride = lodOn ? Mathf.Max(1, lodSettings.m_LodMaxStride) : 1;
            int lodBudget = lodOn ? Mathf.Max(0, lodSettings.m_LodSplatBudget) : 0;

            // Slice 3: strided-cache config for this frame. Cache is only useful for the strided (step>1) path;
            // step==1 already MemCpys. Byte budget is per-octree; when the visible set churns we lean on LRU.
            bool cacheOn = lodOn && lodSettings != null && lodSettings.m_EnableStridedCache;
            int cacheSlotsPerNode = cacheOn ? Mathf.Max(1, lodSettings.m_StridedCacheSlotsPerNode) : 0;
            long cacheByteBudget = 0;
            if (cacheOn)
            {
                cacheByteBudget = lodSettings.m_StridedCacheGlobalByteBudget > 0
                    ? lodSettings.m_StridedCacheGlobalByteBudget
                    : DefaultStridedCacheByteBudget();
            }
            int cacheMaxMissesPerFrame = cacheOn ? Mathf.Max(1, lodSettings.m_StridedCacheMaxMissesPerFrame) : 0;
            m_StridedCacheMissesThisFrame = 0;
            m_StridedCacheHitsThisFrame = 0;
            m_StridedCacheMissesCountThisFrame = 0;
            m_StridedCacheEvictionsThisFrame = 0;
            int currentFrame = Time.frameCount;

            // First, add node splats (front elements for front-to-back rendering)
            for (int i = 0; i < m_VisibleNodeRefs.Count; i++)
            {
                var nodeRef = m_VisibleNodeRefs[i];
                var node = m_Nodes[nodeRef.nodeIndex];
                if (node.splatIndices != null && node.splatIndices.Count > 0)
                {
                    // Ensure we have enough space
                    if (currentIndex + node.splatIndices.Count > m_VisibleSplatIndices.Length)
                    {
                        if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
                        {
                            visibleSplatCount = currentIndex;
                            s_SortAppendCpuMarker.End();
                            s_SortAppendUploadMarker.Begin();
                            UpdateVisibleIndicesBuffer();
                            s_SortAppendUploadMarker.End();
                            return;
                        }
                    }

                    // Distance-banded LOD: a node LARGER than lodFullPx on screen (near / what you look at) keeps
                    // EVERY splat (full detail); only nodes SMALLER than that (far / background) are thinned, and the
                    // smaller they are the more aggressively. This preserves the near image while cutting the
                    // far-field instance count — unlike a density target, it never thins near content.
                    int step = 1;
                    if (lodOn)
                    {
                        float dist = Vector3.Distance(node.bounds.center, camPosition);
                        Vector3 sz = node.bounds.size;
                        float worldExtent = Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
                        float projPx = worldExtent * lodFocalPx / Mathf.Max(dist, 0.001f);
                        if (projPx < lodFullPx)
                            step = Mathf.Clamp(Mathf.RoundToInt(lodFullPx / Mathf.Max(projPx, 0.01f)), 1, lodMaxStride);
                    }

                    // Slice 1 / Fix D: step==1 bulk-copy fast path.
                    // Near-view path leaves step at 1 (LOD off, or node projects >= lodFullPx), making the
                    // per-index List<int> indexer the hot loop over all visible splats. We hoist that path:
                    // List<int>.CopyTo drops the source into a reusable int[] scratch (native memmove of the
                    // list's backing), then UnsafeUtility.MemCpy writes it into m_VisibleSplatIndices as one
                    // memmove. ~10x cheaper than the per-index loop on multi-M splats. Strided LOD path is
                    // unchanged — it still needs the modulo access.
                    // Slice 1 / Fix D: step==1 bulk-copy fast path (LOD off, or node projects >= lodFullPx).
                    // In that case we memcpy the entire List<int> backing into m_VisibleSplatIndices as one
                    // op instead of iterating the List<T> indexer per splat. Strided LOD path is unchanged —
                    // an extra List.CopyTo into a scratch int[] was measured to be a net loss (append went
                    // from 3.5ms to 3.9ms) because the scratch copy costs more than the List-indexer win
                    // when only a fraction of entries are read. NOTE: in the current NEAR-view scene, LOD is
                    // on and every node's projected size is below lodFullPx, so step is always > 1 and this
                    // fast path is dormant. It becomes active in FAR views, wide FOV, or LOD-off configs.
                    int nodeCount = node.splatIndices.Count;
                    if (step == 1)
                    {
                        EnsureAppendScratch(nodeCount);
                        node.splatIndices.CopyTo(m_AppendScratch, 0);
                        unsafe
                        {
                            fixed (int* srcPtr = m_AppendScratch)
                            {
                                int* dstPtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(m_VisibleSplatIndices) + currentIndex;
                                UnsafeUtility.MemCpy(dstPtr, srcPtr, (long)nodeCount * sizeof(int));
                            }
                        }
                        currentIndex += nodeCount;
                    }
                    else
                    {
                        // Slice 3: strided-cache read/populate path.
                        //
                        // The strided per-index loop below is the hot path today (Sort.AppendCpu = 3.19 ms).
                        // We cache the strided permutation per (node, step) so warm frames hit a single
                        // UnsafeUtility.MemCpy — the same shape as the step==1 fast path above.
                        //
                        // Cache validity: node.stridedCacheEpoch[step] must equal node.sortEpoch. Every
                        // splatIndices mutation site calls MarkSplatIndicesDirty(node) (or Interlocked.Increment
                        // on the parallel path) which bumps sortEpoch, blanket-invalidating every slot for the
                        // node. Camera motion does NOT invalidate — the strided permutation is a pure function
                        // of (splatIndices contents, step). When the angular threshold triggers a re-sort, that
                        // path bumps sortEpoch and we correctly miss.
                        //
                        // Fallback on miss: same strided loop as before, run in "populate mode" — writing to
                        // BOTH the destination NativeArray AND the cache slot in one pass so the miss frame
                        // is only marginally slower than status quo (one extra store per index).
                        //
                        // Throttle: at most m_StridedCacheMaxMissesPerFrame allocations per frame. Excess misses
                        // fall through to the un-cached loop and try again next frame.
                        int strideCount = (nodeCount + step - 1) / step; // ceil(nodeCount / step)
                        bool didCacheCopy = false;

                        // Slice 3: MemCpy has meaningful per-call overhead (dozens of ns for tiny copies).
                        // Below this threshold the un-cached indexer loop beats cache-hit MemCpy in wall time,
                        // AND the cache pays alloc + bookkeeping cost. Empirically discovered on Phase2HQ:
                        // scene uses m_OctreeMaxSplatsPerLeaf=1, so most nodes hit strideCount == 1 and the
                        // cache was a 2x net LOSS (Sort 7.6 -> 10.6 ms). Skip cache for these micro nodes and
                        // fall through to the same tight loop as before.
                        const int kStrideCountCacheFloor = 32;
                        if (cacheOn && strideCount >= kStrideCountCacheFloor && (uint)step < (uint)(lodMaxStride + 1))
                        {
                            // Lazily allocate the per-node slot arrays. Length = lodMaxStride + 1 so we can
                            // index by step directly (slot 0 unused).
                            if (node.stridedCache == null || node.stridedCache.Length != lodMaxStride + 1)
                            {
                                // If lodMaxStride was resized at runtime, drop the old arrays entirely.
                                if (node.stridedCache != null)
                                {
                                    DisposeNodeStridedCache(node);
                                }
                                node.stridedCache = new NativeArray<int>[lodMaxStride + 1];
                                node.stridedCacheEpoch = new int[lodMaxStride + 1];
                                node.stridedCacheLastFrame = new int[lodMaxStride + 1];
                            }

                            var slot = node.stridedCache[step];
                            // HIT: slot allocated, epoch matches current sortEpoch, and length matches.
                            // Length check catches the rare case of a node whose splatIndices count changed
                            // (e.g. rebuild reused the OctreeNode reference) without going through MarkSplatIndicesDirty.
                            if (slot.IsCreated
                                && node.stridedCacheEpoch[step] == node.sortEpoch
                                && slot.Length == strideCount)
                            {
                                unsafe
                                {
                                    int* dstPtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(m_VisibleSplatIndices) + currentIndex;
                                    int* srcPtr = (int*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(slot);
                                    UnsafeUtility.MemCpy(dstPtr, srcPtr, (long)strideCount * sizeof(int));
                                }
                                currentIndex += strideCount;
                                node.stridedCacheLastFrame[step] = currentFrame;
                                m_StridedCacheHitsThisFrame++;
                                didCacheCopy = true;
                            }
                            else if (m_StridedCacheMissesThisFrame < cacheMaxMissesPerFrame)
                            {
                                // MISS: try to allocate the slot.
                                // (1) If slot exists but is stale (epoch mismatch OR length mismatch), free it first.
                                if (slot.IsCreated)
                                {
                                    m_StridedCacheBytes -= (long)slot.Length * sizeof(int);
                                    try { slot.Dispose(); } catch {}
                                    node.stridedCache[step] = default;
                                    node.stridedCacheEpoch[step] = 0;
                                    node.stridedCacheLastFrame[step] = 0;
                                }

                                // (2) Enforce per-node K-slots LRU. If already at K live slots for this node,
                                //     free the oldest one.
                                int liveSlots = 0;
                                int lruStep = -1;
                                int lruFrame = int.MaxValue;
                                for (int s = 1; s < node.stridedCache.Length; s++)
                                {
                                    if (!node.stridedCache[s].IsCreated) continue;
                                    liveSlots++;
                                    if (node.stridedCacheLastFrame[s] < lruFrame)
                                    {
                                        lruFrame = node.stridedCacheLastFrame[s];
                                        lruStep = s;
                                    }
                                }
                                if (liveSlots >= cacheSlotsPerNode && lruStep > 0 && lruStep != step)
                                {
                                    var lruSlot = node.stridedCache[lruStep];
                                    m_StridedCacheBytes -= (long)lruSlot.Length * sizeof(int);
                                    try { lruSlot.Dispose(); } catch {}
                                    node.stridedCache[lruStep] = default;
                                    node.stridedCacheEpoch[lruStep] = 0;
                                    node.stridedCacheLastFrame[lruStep] = 0;
                                    m_StridedCacheEvictionsThisFrame++;
                                }

                                // (3) Enforce global byte budget by cross-node LRU eviction.
                                long allocBytes = (long)strideCount * sizeof(int);
                                if (cacheByteBudget > 0 && m_StridedCacheBytes + allocBytes > cacheByteBudget)
                                {
                                    EvictStridedCacheDownTo(cacheByteBudget - allocBytes, currentFrame);
                                }

                                // (4) Allocate + populate in one pass — write to BOTH destination and cache.
                                //     If allocation fails (OOM), fall through to the un-cached loop.
                                bool allocOk = false;
                                if (cacheByteBudget == 0 || m_StridedCacheBytes + allocBytes <= cacheByteBudget)
                                {
                                    try
                                    {
                                        var newSlot = new NativeArray<int>(strideCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                                        unsafe
                                        {
                                            int* dstPtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(m_VisibleSplatIndices) + currentIndex;
                                            int* cachePtr = (int*)NativeArrayUnsafeUtility.GetUnsafePtr(newSlot);
                                            int written = 0;
                                            for (int j = 0; j < nodeCount; j += step)
                                            {
                                                int v = node.splatIndices[j];
                                                dstPtr[written] = v;
                                                cachePtr[written] = v;
                                                written++;
                                            }
                                        }
                                        node.stridedCache[step] = newSlot;
                                        node.stridedCacheEpoch[step] = node.sortEpoch;
                                        node.stridedCacheLastFrame[step] = currentFrame;
                                        m_StridedCacheBytes += allocBytes;
                                        StridedCacheRegisterOwner(nodeRef.nodeIndex);
                                        currentIndex += strideCount;
                                        m_StridedCacheMissesThisFrame++;
                                        m_StridedCacheMissesCountThisFrame++;
                                        allocOk = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        Debug.LogWarning($"Slice 3: strided-cache alloc failed: {ex.Message}");
                                    }
                                }
                                didCacheCopy = allocOk;
                            }
                            else
                            {
                                // Throttled miss — count it but fall through to the un-cached loop.
                                m_StridedCacheMissesCountThisFrame++;
                            }
                        }

                        if (!didCacheCopy)
                        {
                            // Strided LOD path — per-index copy remains correct (and cheaper than scratch+stride).
                            for (int j = 0; j < nodeCount; j += step)
                            {
                                m_VisibleSplatIndices[currentIndex] = node.splatIndices[j];
                                currentIndex++;
                            }
                        }
                    }
                }
            }
            
            // Finally, add outliers (background elements for front-to-back rendering)
            if (m_OthersIndices.Count > 0)
            {
                if (currentIndex + m_OthersIndices.Count > m_VisibleSplatIndices.Length)
                {
                    EnsureVisibleSplatIndicesCapacity(currentIndex + m_OthersIndices.Count);
                    if (!m_VisibleSplatIndicesValid || !m_VisibleSplatIndices.IsCreated)
                    {
                        visibleSplatCount = currentIndex;
                        s_SortAppendCpuMarker.End();
                        s_SortAppendUploadMarker.Begin();
                        UpdateVisibleIndicesBuffer();
                        s_SortAppendUploadMarker.End();
                        return;
                    }
                }

                for (int i = 0; i < m_OthersIndices.Count; i++)
                {
                    m_VisibleSplatIndices[currentIndex + i] = m_OthersIndices[i];
                }
                currentIndex += m_OthersIndices.Count;
            }

            // Device-tuned budget: splats were appended front-to-back, so capping the count keeps the
            // NEAREST splats and drops the farthest — a stable frame time regardless of viewpoint, without
            // ever thinning near content.
            if (lodBudget > 0 && currentIndex > lodBudget)
                currentIndex = lodBudget;

            visibleSplatCount = currentIndex;
            // Slice 3: publish per-frame cache counters as global aggregates for RendererMarkerRecorder.
            // NOTE: multiple octrees updating in the same frame will each ADD to the aggregate — this is
            // intentional so CSV sees total-work-per-frame. The recorder is responsible for resetting
            // between reads if it wants per-octree numbers.
            s_LastFrameCacheHits = m_StridedCacheHitsThisFrame;
            s_LastFrameCacheMisses = m_StridedCacheMissesCountThisFrame;
            s_LastFrameCacheEvictions = m_StridedCacheEvictionsThisFrame;
            // s_LiveCacheBytes stays authoritative — it's kept in sync at every alloc/evict/dispose site
            // (see delta arithmetic below). We simply mirror this octree's contribution here for consumers
            // that want a fresh cross-octree read after this octree's append.
            s_LiveCacheBytes = m_StridedCacheBytes;
            // Slice 2 / Rank 1: close CPU sub-marker, then measure GPU upload separately.
            s_SortAppendCpuMarker.End();
            s_SortAppendUploadMarker.Begin();
            UpdateVisibleIndicesBuffer();
            s_SortAppendUploadMarker.End();
            // (d) closed by _sortAppendScope.Dispose()
        }

        // Thread-safe per-node sorting using local scratch arrays (no shared m_DistanceSortArray)
        void SortSplatsInNodeThreadSafe(List<int> splatIndices, Vector3 camPosition)
        {
            int count = splatIndices.Count;
            if (count <= 1)
                return;
            var scratch = ArrayPool<(float distance, int index)>.Shared.Rent(Mathf.NextPowerOfTwo(count));
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int originalSplatIdx = splatIndices[i];
                    if (TryGetSplatPosition(originalSplatIdx, out float3 splatPos))
                    {
                        float distance = ((Vector3)splatPos - camPosition).sqrMagnitude;
                        scratch[i] = (distance, originalSplatIdx);
                    }
                    else
                    {
                        scratch[i] = (0f, originalSplatIdx);
                    }
                }
                System.Array.Sort(scratch, 0, count, System.Collections.Generic.Comparer<(float distance, int index)>.Create((a, b) => a.distance.CompareTo(b.distance))); // Front-to-back
                for (int i = 0; i < count; i++)
                    splatIndices[i] = scratch[i].index;
            }
            finally
            {
                ArrayPool<(float distance, int index)>.Shared.Return(scratch);
            }
        }

        bool ShouldResortOutliers(Vector3 camPosition)
        {
            if (m_OthersIndices.Count == 0)
                return false;
            if (!m_OthersSorted)
                return true;
            float baseThreshold = Mathf.Max(minOutlierResortDistance, m_OutlierRingRadius * outlierResortMoveFraction);
            float sqMove = (camPosition - m_LastOthersSortCamPos).sqrMagnitude;
            return sqMove >= baseThreshold * baseThreshold;
        }

        void SortOutliers(Vector3 camPosition)
        {
            if (!ShouldResortOutliers(camPosition))
                return;
            if (m_OthersIndices.Count > 1)
                SortSplatsInNode(m_OthersIndices, camPosition);
            // Removed native buffer sync to reduce overhead
            m_OthersSorted = true;
            m_LastOthersSortCamPos = camPosition;
        }

        public void SetOutlierResortFraction(float fraction, float minDistance = 0.05f)
        {
            outlierResortMoveFraction = Mathf.Max(0f, fraction);
            minOutlierResortDistance = Mathf.Max(0f, minDistance);
        }

        Task[] ParallelSortVisibleNodes(Vector3 camPosition)
        {
            // Slice 4 / Rank 1: pool the snapshot buffer instead of allocating a fresh
            // VisibleNodeRef[] every call. The previousSortTasksCompleted guard in the caller
            // ensures no worker is reading the previous frame's contents.
            int nodeCount = m_VisibleNodeRefs.Count;
            EnsureSortScratchSnapshot(nodeCount);
            m_SortScratchSnapshotLength = nodeCount;
            var snapshot = m_SortScratchSnapshot;
            for (int si = 0; si < nodeCount; si++)
                snapshot[si] = m_VisibleNodeRefs[si];

            // Pre-filter nodes that actually need sorting for better task utilization.
            // Slice 4 / Rank 1: reuse the pooled List<int> instead of allocating a fresh one.
            var nodesToSort = m_SortScratchNodesToSort;
            nodesToSort.Clear();
            for (int i = 0; i < nodeCount; i++)
            {
                var nodeRef = snapshot[i];
                var node = m_Nodes[nodeRef.nodeIndex];
                if (node.splatIndices != null && node.splatIndices.Count > 1)
                {
                    // Check if already sorted for this camera direction (using angular threshold)
                    bool needsSort = !node.isSorted;
                    if (!needsSort)
                    {
                        Vector3 nodeCenter = node.bounds.center;
                        Vector3 oldDirection = (nodeCenter - node.lastSortCameraPosition).normalized;
                        Vector3 newDirection = (nodeCenter - oldDirection * node.maxExtent - camPosition).normalized;
                        float cosineAngle = Vector3.Dot(oldDirection, newDirection);
                        needsSort = cosineAngle < sortDirectionThreshold;
                    }

                    if (needsSort)
                        nodesToSort.Add(i);
                }
            }

            int sortNodeCount = nodesToSort.Count;
            bool haveOutliers = m_OthersIndices.Count > 0;
            bool needOutlierResort = haveOutliers && ShouldResortOutliers(camPosition);

            // Slice 4 / Rank 2: skip Task[] alloc + Task.Run entirely when there is nothing to spawn.
            // Camera-orbit-heavy sequences below the angular threshold hit this frequently; a
            // significant fraction of chunks have sortNodeCount==0 && no outlier resort pending
            // and were previously walking into the parallel pipeline just to allocate an empty
            // Task[] and return.
            if (sortNodeCount == 0 && !needOutlierResort)
                return null;

            // Always run outlier sorting on a background task when needed
            Task CreateOutlierTaskIfNeeded()
            {
                if (!needOutlierResort)
                    return null;

                return Task.Run(() =>
                {
                    try
                    {
                        SortSplatsInNodeThreadSafe(m_OthersIndices, camPosition);
                        m_OthersSorted = true;
                        m_LastOthersSortCamPos = camPosition;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Parallel outlier sorting exception in worker task: {ex}");
                        try
                        {
                            SortSplatsInNode(m_OthersIndices, camPosition);
                            m_OthersSorted = true;
                            m_LastOthersSortCamPos = camPosition;
                        }
                        catch (Exception fallbackEx)
                        {
                            Debug.LogError($"Fallback outlier sequential sorting also failed: {fallbackEx}");
                        }
                    }
                });
            }

            if (sortNodeCount == 0)
            {
                // No nodes need sorting; just spawn outlier task if needed.
                // (needOutlierResort must be true here because the early-return above filtered
                // the sortNodeCount==0 && !needOutlierResort case.)
                var outlierTask = CreateOutlierTaskIfNeeded();
                if (outlierTask != null)
                    return new Task[] { outlierTask };
                return null;
            }

            // Clamp desired worker count based on actual work
            int workers = parallelSortThreads;
            workers = Mathf.Min(workers, sortNodeCount); // not more workers than nodes to sort
            if (workers <= 1)
            {
                // Run the filtered node sorting on a background task
                Task seqTask = null;
                if (sortNodeCount > 0)
                {
                    seqTask = Task.Run(() =>
                    {
                        // Process from front to back for front-to-back rendering (closer nodes processed first)
                        var localNodesToSort = m_SortScratchNodesToSort;
                        var localSnapshot = m_SortScratchSnapshot;
                        int localCount = localNodesToSort.Count;
                        for (int i = localCount - 1; i >= 0; i--)
                        {
                            int nodeRefIndex = localNodesToSort[i];
                            var nodeRef = localSnapshot[nodeRefIndex];
                            var node = m_Nodes[nodeRef.nodeIndex];
                            SortSplatsInNodeThreadSafe(node.splatIndices, camPosition);
                            node.isSorted = true;
                            node.lastSortCameraPosition = camPosition;
                            // Slice 3: off-thread epoch bump — Interlocked so the main-thread append loop
                            // reads a coherent (post-mutation) value.
                            Interlocked.Increment(ref node.sortEpoch);
                        }
                    });
                }

                // Create outlier task if needed
                var outlierTask = CreateOutlierTaskIfNeeded();

                // Return tasks array containing the sequential worker and optionally the outlier task
                if (seqTask != null && outlierTask != null)
                    return new Task[] { seqTask, outlierTask };
                if (seqTask != null)
                    return new Task[] { seqTask };
                if (outlierTask != null)
                    return new Task[] { outlierTask };
                return null; // No tasks to wait on
            }

            // Work-stealing parallel sort implementation
            // Slice 4 / Rank 1: hoisted workLock + nextWorkIndex to instance fields to eliminate
            // the local-function closure alloc. Serial-per-octree invocation guaranteed by the
            // previousSortTasksCompleted guard in the caller.
            m_SortNextWorkIndex = sortNodeCount - 1; // Start from the end (closest nodes)

            // Determine up-front whether we need a dedicated outlier task so we can size the tasks array correctly
            bool outlierTaskNeeded = needOutlierResort;
            int totalTasks = workers + (outlierTaskNeeded ? 1 : 0);

            // Create worker tasks that pull work as needed. Task[] itself is small (~10 refs),
            // so we do not pool it — the closure + snapshot + nodesToSort pool captures the
            // >>99% of the alloc bytes.
            Task[] tasks = new Task[totalTasks];

            for (int w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(() =>
                {
                    var localNodesToSort = m_SortScratchNodesToSort;
                    var localSnapshot = m_SortScratchSnapshot;
                    try
                    {
                        int workIndex;
                        while ((workIndex = GetNextSortWorkIndex()) != -1)
                        {
                            int nodeRefIndex = localNodesToSort[workIndex];
                            var nodeRef = localSnapshot[nodeRefIndex];
                            var node = m_Nodes[nodeRef.nodeIndex];
                            SortSplatsInNodeThreadSafe(node.splatIndices, camPosition);
                            node.isSorted = true;
                            node.lastSortCameraPosition = camPosition;
                            // Slice 3: off-thread epoch bump — Interlocked so main-thread read is coherent.
                            Interlocked.Increment(ref node.sortEpoch);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Parallel splat sorting exception in worker task: {ex}");
                        // Continue processing remaining work items with fallback method
                        int workIndex;
                        while ((workIndex = GetNextSortWorkIndex()) != -1)
                        {
                            int nodeRefIndex = localNodesToSort[workIndex];
                            var nodeRef = localSnapshot[nodeRefIndex];
                            var node = m_Nodes[nodeRef.nodeIndex];
                            try
                            {
                                SortSplatsInNode(node.splatIndices, camPosition);
                                node.isSorted = true;
                                node.lastSortCameraPosition = camPosition;
                                // Slice 3: fallback path — same off-thread epoch bump.
                                Interlocked.Increment(ref node.sortEpoch);
                            }
                            catch (Exception fallbackEx)
                            {
                                Debug.LogError($"Fallback sequential sorting also failed: {fallbackEx}");
                            }
                        }
                    }
                });
            }

            // Spawn outlier task if needed and place at the end
            if (outlierTaskNeeded)
            {
                var outlierTask = CreateOutlierTaskIfNeeded();
                tasks[workers] = outlierTask;
            }
            return tasks;
        }

        void JoinParallelSortThreads(Task[] tasks)
        {
            if (tasks == null) return;
            try
            {
                Task.WaitAll(tasks);
            }
            catch (AggregateException ex)
            {
                Debug.LogError($"One or more parallel sorting tasks threw exceptions: {ex}");
            }
        }

        /// <summary>
        /// Start native sorting jobs for WebGL platform.
        /// </summary>
        /// <returns>Number of jobs started</returns>
        int StartNativeSortJobs(Vector3 camPosition)
        {
            if (!NativeSorting.IsAvailable)
                return 0;

            int jobsStarted = 0;

            // Collect nodes that need sorting
            var nodesToSort = new List<int>();
            for (int i = 0; i < m_VisibleNodeRefs.Count; i++)
            {
                var nodeRef = m_VisibleNodeRefs[i];
                var node = m_Nodes[nodeRef.nodeIndex];
                if (node.splatIndices != null && node.splatIndices.Count > 1)
                {
                    // Check if already sorted for this camera direction
                    bool needsSort = !node.isSorted;
                    if (!needsSort)
                    {
                        Vector3 nodeCenter = node.bounds.center;
                        Vector3 oldDirection = (nodeCenter - node.lastSortCameraPosition).normalized;
                        Vector3 newDirection = (nodeCenter - oldDirection * node.maxExtent - camPosition).normalized;
                        float cosineAngle = Vector3.Dot(oldDirection, newDirection);
                        needsSort = cosineAngle < sortDirectionThreshold;
                    }

                    if (needsSort)
                    {
                        nodesToSort.Add(nodeRef.nodeIndex);
                    }
                }
            }

            // Start native sort jobs for outliers if needed
            if (ShouldResortOutliers(camPosition) && m_OthersIndices.Count > 1)
            {
                if (StartNativeOutlierSort(camPosition))
                    jobsStarted++;
            }

            // Start native jobs for nodes that need sorting
            for (int i = nodesToSort.Count - 1; i >= 0; i--)
            {
                int nodeIndex = nodesToSort[i];
                if (StartNativeNodeSort(nodeIndex, camPosition))
                    jobsStarted++;
            }
            
            return jobsStarted;
        }

        /// <summary>
        /// Start a native sort job for outlier splats.
        /// </summary>
        /// <returns>True if job was started successfully</returns>
        bool StartNativeOutlierSort(Vector3 camPosition)
        {
            if (m_OthersIndices.Count <= 1) return false;
            if (!m_AllPositionsNativeValid) return false;
            try
            {
                bool usedPersistentInput = EnsureOutlierNativeIndices();
                NativeArray<int> input;
                if (usedPersistentInput)
                {
                    input = m_OthersNativeIndices;
                }
                else
                {
                    input = new NativeArray<int>(m_OthersIndices.Count, Allocator.Persistent);
                    for (int i = 0; i < m_OthersIndices.Count; i++) input[i] = m_OthersIndices[i];
                }
                var output = new NativeArray<int>(m_OthersIndices.Count, Allocator.Persistent);
                var handle = NativeSorting.StartSortJob(input, m_AllPositionsNative, output, camPosition);
                if (!handle.IsValid)
                {
                    if (!usedPersistentInput && input.IsCreated) input.Dispose();
                    if (output.IsCreated) output.Dispose();
                    return false;
                }
                m_NativeSortJobs.Add(handle);
                m_NativeJobInfos.Add(new NativeSortJobInfo
                {
                    isOutlierJob = true,
                    nodeIndex = -1,
                    cameraPosition = camPosition,
                    inputIndices = input,
                    sortedIndices = output,
                    disposeInput = !usedPersistentInput
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start native outlier sort job: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start a native sort job for a specific node.
        /// </summary>
        /// <returns>True if job was started successfully</returns>
        bool StartNativeNodeSort(int nodeIndex, Vector3 camPosition)
        {
            if (nodeIndex < 0 || nodeIndex >= m_Nodes.Count) return false;
            var node = m_Nodes[nodeIndex];
            if (node.splatIndices == null || node.splatIndices.Count <= 1) return false;
            if (!m_AllPositionsNativeValid) return false;
            try
            {
                // Ensure persistent native splat index buffer exists for this node (lazy init)
                bool usedPersistentInput = EnsureNodeNativeIndices(nodeIndex);
                NativeArray<int> input;
                if (usedPersistentInput)
                {
                    input = node.nativeSplatIndices; // already valid & persistent
                }
                else
                {
                    // Fallback (should rarely happen) allocate one-shot buffer
                    input = new NativeArray<int>(node.splatIndices.Count, Allocator.Persistent);
                    for (int i = 0; i < node.splatIndices.Count; i++) input[i] = node.splatIndices[i];
                }
                var output = new NativeArray<int>(node.splatIndices.Count, Allocator.Persistent);
                var handle = NativeSorting.StartSortJob(input, m_AllPositionsNative, output, camPosition);
                if (!handle.IsValid)
                {
                    if (!usedPersistentInput && input.IsCreated) input.Dispose();
                    if (output.IsCreated) output.Dispose();
                    return false;
                }
                m_NativeSortJobs.Add(handle);
                m_NativeJobInfos.Add(new NativeSortJobInfo
                {
                    isOutlierJob = false,
                    nodeIndex = nodeIndex,
                    cameraPosition = camPosition,
                    inputIndices = input,
                    sortedIndices = output,
                    disposeInput = !usedPersistentInput
                });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to start native sort job for node {nodeIndex}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Collect results from completed native sort jobs.
        /// </summary>
        /// <returns>True if all native jobs are completed, false if any are still running</returns>
        bool CollectNativeSortResults()
        {
            for (int i = m_NativeSortJobs.Count - 1; i >= 0; i--)
            {
                var handle = m_NativeSortJobs[i];
                if (!handle.IsCompleted) continue;
                var info = m_NativeJobInfos[i];
                try
                {
                    if (info.isOutlierJob)
                        ApplySortedOutlierResults(info.sortedIndices, info.cameraPosition);
                    else
                        ApplySortedNodeResults(info.nodeIndex, info.sortedIndices, info.cameraPosition);
                    try { NativeSorting.CleanupJob(handle); } catch { }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"Error collecting native sort results: {ex.Message}");
                }
                try { if (info.disposeInput && info.inputIndices.IsCreated) info.inputIndices.Dispose(); } catch { }
                try { if (info.sortedIndices.IsCreated) info.sortedIndices.Dispose(); } catch { }
                m_NativeSortJobs.RemoveAt(i);
                m_NativeJobInfos.RemoveAt(i);
            }
            return m_NativeSortJobs.Count == 0;
        }
        
        /// <summary>
        /// Apply sorted results to outlier indices.
        /// </summary>
        void ApplySortedOutlierResults(NativeArray<int> sortedIndices, Vector3 camPosition)
        {
            m_OthersIndices.Clear();
            for (int i = 0; i < sortedIndices.Length; i++)
                m_OthersIndices.Add(sortedIndices[i]);
            // Removed optional native buffer refresh to avoid overhead
            m_OthersSorted = true;
            m_LastOthersSortCamPos = camPosition;
        }
        
        /// <summary>
        /// Apply sorted results to node indices.
        /// </summary>
        void ApplySortedNodeResults(int nodeIndex, NativeArray<int> sortedIndices, Vector3 camPosition)
        {
            if (nodeIndex < 0 || nodeIndex >= m_Nodes.Count)
            {
                Debug.LogWarning($"Invalid node index for native sort results: {nodeIndex}");
                return;
            }
            var node = m_Nodes[nodeIndex];
            if (node.splatIndices == null)
            {
                Debug.LogWarning($"Node {nodeIndex} has null splat indices");
                return;
            }
            node.splatIndices.Clear();
            for (int i = 0; i < sortedIndices.Length; i++)
                node.splatIndices.Add(sortedIndices[i]);
            // Removed optional native indices refresh
            node.isSorted = true;
            node.lastSortCameraPosition = camPosition;
            // Slice 3: native-sort apply is main-thread — bump epoch AFTER the swap completes so the
            // append loop's read of sortEpoch is coherent without needing Volatile/Interlocked.
            MarkSplatIndicesDirty(node);
        }

        /// <summary>
        /// Cleanup completed native jobs without applying results.
        /// </summary>
        void CleanupCompletedNativeJobs()
        {
            for (int i = m_NativeSortJobs.Count - 1; i >= 0; i--)
            {
                var handle = m_NativeSortJobs[i];
                if (!handle.IsCompleted) continue;
                try { NativeSorting.CleanupJob(handle); } catch { }
                var info = m_NativeJobInfos[i];
                try { if (info.disposeInput && info.inputIndices.IsCreated) info.inputIndices.Dispose(); } catch { }
                try { if (info.sortedIndices.IsCreated) info.sortedIndices.Dispose(); } catch { }
                m_NativeSortJobs.RemoveAt(i);
                m_NativeJobInfos.RemoveAt(i);
            }
        }

        /// <summary>
        /// Sort splats in a node and mark it as sorted for the current camera view.
        /// </summary>
        public bool SortNodeSplats(int nodeIndex, Vector3 camPosition, bool forceSort = false)
        {
            if (nodeIndex < 0 || nodeIndex >= m_Nodes.Count) return false;
            var node = m_Nodes[nodeIndex];
            if (node.splatIndices == null || node.splatIndices.Count <= 1) return false;

            if (!forceSort && node.isSorted)
            {
                Vector3 nodeCenter = node.bounds.center;
                Vector3 oldDirection = (nodeCenter - node.lastSortCameraPosition).normalized;
                Vector3 newDirection = (nodeCenter - oldDirection * node.maxExtent - camPosition).normalized;

                float cosineAngle = Vector3.Dot(oldDirection, newDirection);
                if (cosineAngle >= sortDirectionThreshold)
                    return false;
            }

            SortSplatsInNode(node.splatIndices, camPosition);
            node.isSorted = true;
            node.lastSortCameraPosition = camPosition;
            // Slice 3: sequential path is main-thread — bump epoch after mutation.
            MarkSplatIndicesDirty(node);
            return true;
        }

        /// <summary>
        /// Mark all nodes and outliers as needing re-sort.
        /// </summary>
        public void InvalidateAllSorts()
        {
            for (int i = 0; i < m_Nodes.Count; i++)
            {
                m_Nodes[i].isSorted = false;
            }
            m_OthersSorted = false;
        }

        /// <summary>
        /// Set the sort direction threshold using angle in degrees for easier configuration.
        /// </summary>
        public void SetSortDirectionThresholdDegrees(float angleDegrees)
        {
            sortDirectionThreshold = Mathf.Cos(angleDegrees * Mathf.Deg2Rad);
        }

        void SortSplatsInNode(List<int> splatIndices, Vector3 camPosition)
        {
            int count = splatIndices.Count;
            if (count <= 1) return;
            if (m_DistanceSortArray == null || m_DistanceSortArray.Length < count)
                m_DistanceSortArray = new (float distance, int index)[Mathf.NextPowerOfTwo(count)];
            for (int i = 0; i < count; i++)
            {
                int originalSplatIdx = splatIndices[i];
                if (TryGetSplatPosition(originalSplatIdx, out float3 splatPos))
                {
                    float distance = ((Vector3)splatPos - camPosition).sqrMagnitude;
                    m_DistanceSortArray[i] = (distance, originalSplatIdx);
                }
                else
                {
                    m_DistanceSortArray[i] = (0f, originalSplatIdx);
                }
            }
            System.Array.Sort(m_DistanceSortArray, 0, count, System.Collections.Generic.Comparer<(float distance, int index)>.Create((a, b) => a.distance.CompareTo(b.distance))); // Front-to-back
            for (int i = 0; i < count; i++)
                splatIndices[i] = m_DistanceSortArray[i].index;
        }

        void CollectVisibleNodesWithDistance(int nodeIndex, Plane[] frustumPlanes, Vector3 camPosition)
        {
            m_TraversalStack.Clear();
            
            // Early exit if invalid starting node
            if (nodeIndex >= m_Nodes.Count)
                return;
                
            m_TraversalStack.Push(nodeIndex);
            
            while (m_TraversalStack.Count > 0)
            {
                int currentNodeIndex = m_TraversalStack.Pop();
                
                // Bounds check
                if (currentNodeIndex >= m_Nodes.Count)
                    continue;
                    
                var node = m_Nodes[currentNodeIndex];
                
                // Frustum culling - early exit if node not visible
                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, node.bounds))
                    continue;
                
                if (node.isLeaf)
                {
                    // Add leaf node if it has splats
                    if (node.splatIndices != null && node.splatIndices.Count > 0)
                    {
                        float nodeDistance = (node.center - camPosition).sqrMagnitude;
                        m_VisibleNodeRefs.Add(new VisibleNodeRef
                        {
                            distance = nodeDistance,
                            nodeIndex = currentNodeIndex
                        });
                    }
                }
                else if (node.childIndices != null)
                {
                    // Add children to stack for traversal (reverse order for consistent traversal)
                    for (int i = node.childIndices.Count - 1; i >= 0; i--)
                    {
                        int childIndex = node.childIndices[i];
                        if (childIndex < m_Nodes.Count)
                        {
                            var childNode = m_Nodes[childIndex];
                            // Only traverse children that have content or are internal nodes
                            if ((childNode.splatIndices != null && childNode.splatIndices.Count > 0) || !childNode.isLeaf)
                            {
                                m_TraversalStack.Push(childIndex);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Sequential fallback for nodes/outliers not handled by native jobs.
        /// </summary>
        void SequentialSortFallback(Vector3 camPosition)
        {
            // Check if outliers need sequential sorting (if not already handled by native job)
            if (ShouldResortOutliers(camPosition))
            {
                SortOutliers(camPosition);
            }
            
            // Check nodes that may not have been processed by native jobs
            for (int i = 0; i < m_VisibleNodeRefs.Count; i++)
            {
                var nodeRef = m_VisibleNodeRefs[i];
                var node = m_Nodes[nodeRef.nodeIndex];
                
                // Only process nodes that aren't already sorted
                if (!node.isSorted && node.splatIndices != null && node.splatIndices.Count > 1)
                {
                    SortNodeSplats(nodeRef.nodeIndex, camPosition);
                }
            }
        }

        bool EnsureNodeNativeIndices(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= m_Nodes.Count) return false;
            var node = m_Nodes[nodeIndex];
            if (node.splatIndices == null || node.splatIndices.Count == 0) return false;
            if (!node.nativeIndicesValid || !node.nativeSplatIndices.IsCreated || node.nativeSplatIndices.Length != node.splatIndices.Count)
            {
                // Dispose previous if size mismatch
                if (node.nativeIndicesValid && node.nativeSplatIndices.IsCreated)
                {
                    try { node.nativeSplatIndices.Dispose(); } catch {}
                }
                try
                {
                    node.nativeSplatIndices = new NativeArray<int>(node.splatIndices.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    for (int i = 0; i < node.splatIndices.Count; i++)
                        node.nativeSplatIndices[i] = node.splatIndices[i];
                    node.nativeIndicesValid = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to allocate native splat index buffer for node {nodeIndex}: {ex.Message}");
                    node.nativeIndicesValid = false;
                }
            }
            return node.nativeIndicesValid;
        }

        bool EnsureOutlierNativeIndices()
        {
            if (m_OthersIndices.Count == 0) return false;
            if (!m_OthersNativeValid || !m_OthersNativeIndices.IsCreated || m_OthersNativeIndices.Length != m_OthersIndices.Count)
            {
                if (m_OthersNativeValid && m_OthersNativeIndices.IsCreated)
                {
                    try { m_OthersNativeIndices.Dispose(); } catch {}
                }
                try
                {
                    m_OthersNativeIndices = new NativeArray<int>(m_OthersIndices.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    for (int i = 0; i < m_OthersIndices.Count; i++)
                        m_OthersNativeIndices[i] = m_OthersIndices[i];
                    m_OthersNativeValid = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"Failed to allocate native outlier index buffer: {ex.Message}");
                    m_OthersNativeValid = false;
                }
            }
            return m_OthersNativeValid;
        }
    }
}
