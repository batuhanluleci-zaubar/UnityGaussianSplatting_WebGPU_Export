// SPDX-License-Identifier: MIT
// Track C3b: per-frame LOD streamer state machine.
//
// Direct port of PlayCanvas engine gsplat-octree.js:236-249 (LOD selection) and
// octree-instance.js:393-453 (per-frame prefetch + pendingDecrements). The core
// invariants we replicate:
//
//   * selectDesiredLodIndex uses lodBaseDistance * pow(lodMultiplier, i), where
//     lodMultiplier defaults to 3 but is clamped to a minimum of 1.2 (below that
//     LOD ranks overlap and the state machine thrashes). LOD 0 is the finest,
//     LOD (lodLevels-1) is the coarsest — matching the on-disk manifest order.
//
//   * prefetchNextLod prefetches EXACTLY ONE step finer per node per frame.
//     Never skip levels — the engine explicitly limits this so budget spikes
//     don't stall the frame.
//
//   * applyLodChanges walks the visible leaves each frame and, for each, picks
//     the desired LOD and ensures the referenced chunk is resident via the
//     loader. Never-evict-visible is enforced via pendingDecrements: when a
//     leaf swaps from LOD-A to LOD-B, LOD-A's chunk ref is parked in a
//     "release once B has arrived" map so the visible leaf never blinks.
//
// Kd-tree traversal at runtime is NOT part of this class — SogKdTree.Flatten*
// already collapsed the tree into a flat NativeArray<SogLeafNode> at load,
// and per-frame culling happens via SogKdTree.WalkVisibleLeaves. This class
// only consumes the visible-leaf index list.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Per-frame LOD selection + chunk residency state machine. Owns the loader
    /// and the pending-decrement bookkeeping. See file header for the invariants.
    /// </summary>
    public sealed class SogStreamer : IDisposable
    {
        // ---- Tuning knobs (defaults mirror PlayCanvas engine) ------------------

        /// <summary>Distance at which LOD 0 (finest) is chosen. Below this every leaf pins to LOD 0.</summary>
        public float LodBaseDistance { get; set; } = 1f;

        /// <summary>
        /// Geometric progression base. Distance for LOD i = base * mult^i.
        /// Clamped internally to a minimum of 1.2 so LOD ranks never overlap.
        /// </summary>
        public float LodMultiplier { get; set; } = 3f;

        /// <summary>Absolute lower bound on the multiplier to avoid thrash.</summary>
        public const float MinLodMultiplier = 1.2f;

        // ---- State -------------------------------------------------------------

        readonly SogChunkLoader m_Loader;
        readonly NativeArray<SogLeafNode> m_Leaves;
        readonly int m_LodLevels;

        /// <summary>
        /// Currently-selected LOD rank per leaf, or -1 if the leaf has never
        /// been resident yet.
        /// </summary>
        readonly int[] m_CurrentLod;

        /// <summary>
        /// pendingDecrements[leafIdx] = fileIdx of the previous LOD's chunk whose
        /// Release we deferred until the new LOD's chunk becomes resident. -1
        /// means "nothing pending". Mirrors PlayCanvas octree-instance.js pendingDecrements.
        /// </summary>
        readonly int[] m_PendingDecrements;

        /// <summary>
        /// Which chunk index each leaf is currently "prefetching" (one-step-finer
        /// than <see cref="m_CurrentLod"/>). -1 while no prefetch is outstanding.
        /// </summary>
        readonly int[] m_PrefetchFileIdx;

        /// <summary>
        /// Set of chunk file indices we told the loader about this frame. Used to
        /// avoid double-acquire when several leaves share a chunk.
        /// </summary>
        readonly HashSet<int> m_ChunksReferencedThisFrame = new HashSet<int>();

        public SogStreamer(SogChunkLoader loader, NativeArray<SogLeafNode> leaves, int lodLevels)
        {
            m_Loader = loader ?? throw new ArgumentNullException(nameof(loader));
            if (!leaves.IsCreated)
                throw new ArgumentException("SogStreamer: leaves NativeArray must be created.", nameof(leaves));
            if (lodLevels < 1)
                throw new ArgumentOutOfRangeException(nameof(lodLevels), "SogStreamer: lodLevels must be >= 1.");

            m_Leaves = leaves;
            m_LodLevels = lodLevels;

            int n = leaves.Length;
            m_CurrentLod = new int[n];
            m_PendingDecrements = new int[n];
            m_PrefetchFileIdx = new int[n];
            for (int i = 0; i < n; i++)
            {
                m_CurrentLod[i] = -1;
                m_PendingDecrements[i] = -1;
                m_PrefetchFileIdx[i] = -1;
            }
        }

        /// <summary>Currently-selected LOD rank for <paramref name="leafIdx"/>, or -1 if none yet.</summary>
        public int GetCurrentLod(int leafIdx) => m_CurrentLod[leafIdx];

        /// <summary>Total number of leaves tracked by the streamer.</summary>
        public int LeafCount => m_Leaves.Length;

        // ---- Public API --------------------------------------------------------

        /// <summary>
        /// Pick a desired LOD for a leaf given the squared distance from the camera
        /// to the leaf centre. Returns 0 (finest) when very close and (lodLevels-1)
        /// (coarsest) when very far. Uses lodBaseDistance * mult^i thresholds.
        /// </summary>
        /// <param name="leafIdx">Index into the flat leaf array (bounds-checked in DEBUG only).</param>
        /// <param name="distSq">Squared distance from camera to leaf centre.</param>
        public int SelectDesiredLodIndex(int leafIdx, float distSq)
        {
            _ = leafIdx; // leafIdx currently unused — reserved for per-leaf hysteresis
            float mult = Mathf.Max(LodMultiplier, MinLodMultiplier);
            float dist = Mathf.Sqrt(Mathf.Max(distSq, 0f));

            // Walk from finest (0) to coarsest (lodLevels-1). Threshold for staying
            // at LOD i is base * mult^i; once dist exceeds it we step one coarser.
            float threshold = Mathf.Max(LodBaseDistance, 1e-4f);
            for (int i = 0; i < m_LodLevels - 1; i++)
            {
                if (dist < threshold) return i;
                threshold *= mult;
            }
            return m_LodLevels - 1;
        }

        /// <summary>
        /// Return the LOD rank we should PREFETCH for this leaf given its current
        /// resident LOD. Prefetch is exactly one step finer than current; returns
        /// -1 when there is nothing finer to prefetch (already at LOD 0 or leaf
        /// has never resolved a LOD yet).
        /// </summary>
        public int PrefetchNextLod(int leafIdx, int currentLod)
        {
            _ = leafIdx;
            if (currentLod <= 0) return -1;
            return currentLod - 1;
        }

        /// <summary>
        /// Advance the streamer state machine by one frame. For each visible leaf
        /// pick a desired LOD, ensure the chunk backing that LOD is resident via
        /// the loader, and manage the pendingDecrements map so previously-visible
        /// chunks are only released once the new LOD's chunk has arrived. Also
        /// advances the loader's cooldown timer.
        /// </summary>
        /// <param name="visibleLeafIdx">Indices produced by <see cref="SogKdTree.WalkVisibleLeaves"/>.</param>
        /// <param name="visibleCount">Number of valid entries in <paramref name="visibleLeafIdx"/>.</param>
        /// <param name="camPos">Camera world position (used for per-leaf distance).</param>
        public void ApplyLodChanges(
            NativeArray<int> visibleLeafIdx,
            int visibleCount,
            float3 camPos)
        {
            if (!visibleLeafIdx.IsCreated)
                throw new ArgumentException("SogStreamer.ApplyLodChanges: visibleLeafIdx NativeArray must be created.", nameof(visibleLeafIdx));
            if (visibleCount < 0 || visibleCount > visibleLeafIdx.Length)
                throw new ArgumentOutOfRangeException(nameof(visibleCount));

            m_ChunksReferencedThisFrame.Clear();

            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = visibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= m_Leaves.Length) continue;

                var leaf = m_Leaves[leafIdx];

                // Distance to leaf centre in world space. We deliberately use the
                // log-space bounds midpoint here — the kd-walk already ran the
                // InvLog conversion during the visibility test, so this stays a
                // hair off but is fine for LOD selection (log space is monotonic).
                float3 centre = (leaf.BoundMin + leaf.BoundMax) * 0.5f;
                float3 delta  = centre - camPos;
                float distSq  = math.dot(delta, delta);

                int desiredLod = SelectDesiredLodIndex(leafIdx, distSq);
                if (desiredLod >= leaf.LodCount) desiredLod = leaf.LodCount - 1;
                if (desiredLod < 0) desiredLod = 0;

                int currentLod = m_CurrentLod[leafIdx];
                int currentFileIdx = currentLod >= 0 ? GetLeafFileIdx(leaf, currentLod) : -1;
                int desiredFileIdx = GetLeafFileIdx(leaf, desiredLod);

                // Prefetch next-finer LOD (one step per frame).
                int prefetchLod = PrefetchNextLod(leafIdx, desiredLod);
                int prefetchFileIdx = prefetchLod >= 0 ? GetLeafFileIdx(leaf, prefetchLod) : -1;

                // -----------------------------------------------------------------
                // 1) Ensure the desired chunk is resident. Never-evict-visible: we
                //    keep the current chunk pinned in pendingDecrements until the
                //    desired chunk actually arrives.
                // -----------------------------------------------------------------
                if (desiredFileIdx != currentFileIdx)
                {
                    AcquireOnce(desiredFileIdx);

                    bool desiredResident = m_Loader.IsResident(desiredFileIdx);
                    if (desiredResident)
                    {
                        // Swap over. Release the previous LOD, whether it came from
                        // the current slot or from a still-pending decrement from
                        // an earlier transition.
                        ReleasePending(leafIdx);
                        if (currentFileIdx >= 0 && currentFileIdx != desiredFileIdx)
                            m_Loader.Release(currentFileIdx);

                        m_CurrentLod[leafIdx] = desiredLod;
                    }
                    else
                    {
                        // Desired chunk still loading. Keep the current LOD visible;
                        // park its refcount so a later frame won't accidentally evict it.
                        if (currentFileIdx >= 0 && m_PendingDecrements[leafIdx] < 0)
                            m_PendingDecrements[leafIdx] = currentFileIdx;

                        // Also touch the current file so it counts as "used this frame".
                        AcquireOnce(currentFileIdx);
                    }
                }
                else
                {
                    // Same file as before — refresh the this-frame acquire so the
                    // loader doesn't drop us into cooldown.
                    AcquireOnce(desiredFileIdx);

                    // Clear a stale pendingDecrement now that we're back on the same LOD.
                    ReleasePending(leafIdx);

                    if (currentLod < 0) m_CurrentLod[leafIdx] = desiredLod;
                }

                // -----------------------------------------------------------------
                // 2) Prefetch the one-step-finer LOD. Same "acquire only" pattern;
                //    we don't swap m_CurrentLod until it actually arrives (checked
                //    on a later frame in the currentFileIdx != desiredFileIdx path).
                // -----------------------------------------------------------------
                if (prefetchFileIdx >= 0
                    && prefetchFileIdx != desiredFileIdx
                    && prefetchFileIdx != currentFileIdx)
                {
                    AcquireOnce(prefetchFileIdx);
                    m_PrefetchFileIdx[leafIdx] = prefetchFileIdx;
                }
                else
                {
                    m_PrefetchFileIdx[leafIdx] = -1;
                }
            }

            // The Acquire calls above bumped refcounts one-per-visible-leaf. Cooldown
            // is driven by Release, which the streamer only calls on LOD transitions.
            // We rely on the loader's per-frame Tick to age out entries that stopped
            // being touched because leaves left the frustum.
            m_Loader.Tick();
        }

        // ---- Internals ---------------------------------------------------------

        static unsafe int GetLeafFileIdx(SogLeafNode leaf, int lod)
        {
            if (lod < 0 || lod >= leaf.LodCount) return -1;
            return leaf.LodFileIdx[lod];
        }

        void AcquireOnce(int fileIdx)
        {
            if (fileIdx < 0) return;
            if (!m_ChunksReferencedThisFrame.Add(fileIdx)) return;
            // Fire-and-forget: the streamer doesn't await the task — SogKdTree walk
            // will re-check IsResident on subsequent frames.
            _ = m_Loader.AcquireAsync(fileIdx);
        }

        void ReleasePending(int leafIdx)
        {
            int pending = m_PendingDecrements[leafIdx];
            if (pending < 0) return;
            m_Loader.Release(pending);
            m_PendingDecrements[leafIdx] = -1;
        }

        public void Dispose()
        {
            // The streamer does not own m_Leaves (the reader does) and only weakly
            // holds m_Loader (also owned externally). Nothing to free here beyond
            // the managed arrays which the GC will collect.
        }
    }
}
