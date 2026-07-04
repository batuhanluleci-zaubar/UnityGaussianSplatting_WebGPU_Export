// SPDX-License-Identifier: MIT
// Track C3b: per-frame LOD streamer state machine.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Per-frame LOD selection + chunk residency state machine.
    /// </summary>
    public sealed class SogStreamer : IDisposable
    {
        public float LodBaseDistance { get; set; } = 1f;
        public float LodMultiplier { get; set; } = 3f;
        public const float MinLodMultiplier = 1.2f;

        /// <summary>Multiplies LOD0 distance bands (1–10). Inspector/runtime knob.</summary>
        public float Lod0CoverageScale { get; set; } = 1f;

        /// <summary>Chunks behind the camera are demoted by this factor (SPZ parity).</summary>
        public float LodBehindPenalty { get; set; } = 5f;

        /// <summary>
        /// When effective distance is below base * this fraction * coverage scale, pin LOD0
        /// and prefetch directly to finest.
        /// </summary>
        public float VeryNearFraction { get; set; } = 0.5f;

        /// <summary>
        /// PlayCanvas lodUnderfillLimit — show finest resident LOD within
        /// [optimal .. optimal+limit] while finer levels stream in.
        /// </summary>
        public int LodUnderfillLimit { get; set; } = 3;

        readonly SogChunkLoader m_Loader;
        readonly NativeArray<SogLeafNode> m_Leaves;
        readonly int m_LodLevels;
        readonly int[] m_CurrentLod;
        readonly int[] m_PendingDecrements;
        readonly int[] m_PrefetchFileIdx;
        readonly int[] m_LastDesiredLod;
        readonly int[] m_LastOptimalLod;
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
            m_LastDesiredLod = new int[n];
            m_LastOptimalLod = new int[n];
            for (int i = 0; i < n; i++)
            {
                m_CurrentLod[i] = -1;
                m_PendingDecrements[i] = -1;
                m_PrefetchFileIdx[i] = -1;
                m_LastDesiredLod[i] = -1;
                m_LastOptimalLod[i] = -1;
            }
        }

        public bool ForceMaxQuality { get; set; }

        public int GetCurrentLod(int leafIdx) => m_CurrentLod[leafIdx];

        public int GetLastDesiredLod(int leafIdx) =>
            leafIdx >= 0 && leafIdx < m_LastDesiredLod.Length ? m_LastDesiredLod[leafIdx] : -1;

        public int GetLastOptimalLod(int leafIdx) =>
            leafIdx >= 0 && leafIdx < m_LastOptimalLod.Length ? m_LastOptimalLod[leafIdx] : -1;

        public int LeafCount => m_Leaves.Length;

        float EffectiveBaseDistance =>
            Mathf.Max(LodBaseDistance, 1e-4f) * Mathf.Clamp(Lod0CoverageScale, 0.1f, 10f);

        float VeryNearDistance => EffectiveBaseDistance * Mathf.Clamp(VeryNearFraction, 0f, 1f);

        /// <summary>Camera-aware effective distance for LOD band selection.</summary>
        public float ComputeEffectiveDistance(int leafIdx, Camera cam)
        {
            if (leafIdx < 0 || leafIdx >= m_Leaves.Length || cam == null) return float.MaxValue;
            var leaf = m_Leaves[leafIdx];
            float closestDist = math.sqrt(SogLeafMath.ClosestDistSq(leaf, cam.transform.position));
            float3 centre = SogLeafMath.WorldCentre(leaf);
            return SogLeafMath.EffectiveDistance(
                closestDist, cam.transform.position, cam.transform.forward, centre,
                SogLeafMath.FovScaleFromCamera(cam), LodBehindPenalty);
        }

        public int SelectDesiredLodFromEffectiveDistance(float effectiveDist)
        {
            if (ForceMaxQuality || effectiveDist < VeryNearDistance) return 0;

            float mult = Mathf.Max(LodMultiplier, MinLodMultiplier);
            float threshold = EffectiveBaseDistance;
            for (int i = 0; i < m_LodLevels - 1; i++)
            {
                if (effectiveDist < threshold) return i;
                threshold *= mult;
            }
            return m_LodLevels - 1;
        }

        /// <summary>Screen-error optimal LOD for a leaf (before budget / underfill).</summary>
        public int ComputeOptimalLod(int leafIdx, Camera cam)
        {
            if (ForceMaxQuality) return 0;
            if (leafIdx < 0 || leafIdx >= m_Leaves.Length) return m_LodLevels - 1;
            var leaf = m_Leaves[leafIdx];
            float effDist = ComputeEffectiveDistance(leafIdx, cam);
            int lod = SelectDesiredLodFromEffectiveDistance(effDist);
            return Mathf.Clamp(lod, 0, Math.Max(0, leaf.LodCount - 1));
        }

        /// <summary>Legacy entry — treats input as effective distance (not squared).</summary>
        public int SelectDesiredLodIndex(int leafIdx, float distSq)
        {
            if (ForceMaxQuality) return 0;
            _ = leafIdx;
            return SelectDesiredLodFromEffectiveDistance(Mathf.Sqrt(Mathf.Max(distSq, 0f)));
        }

        /// <summary>
        /// PlayCanvas selectDesiredLodIndex — finest resident within [optimal .. optimal+limit],
        /// else coarsest file-backed LOD in that range.
        /// </summary>
        public int SelectDesiredLodWithUnderfill(int leafIdx, int optimalLod)
        {
            if (leafIdx < 0 || leafIdx >= m_Leaves.Length) return optimalLod;
            var leaf = m_Leaves[leafIdx];
            int maxLod = Math.Max(0, leaf.LodCount - 1);
            optimalLod = Mathf.Clamp(optimalLod, 0, maxLod);

            if (LodUnderfillLimit <= 0) return optimalLod;

            int allowedMaxCoarse = Mathf.Min(maxLod, optimalLod + LodUnderfillLimit);

            unsafe
            {
                for (int lod = optimalLod; lod <= allowedMaxCoarse; lod++)
                {
                    int fi = leaf.LodFileIdx[lod];
                    if (fi >= 0 && m_Loader.IsResident(fi))
                        return lod;
                }

                for (int lod = allowedMaxCoarse; lod >= optimalLod; lod--)
                {
                    if (leaf.LodFileIdx[lod] >= 0)
                        return lod;
                }
            }

            return optimalLod;
        }

        bool IsLodResident(SogLeafNode leaf, int lod)
        {
            unsafe
            {
                int fi = GetLeafFileIdx(leaf, lod);
                return fi >= 0 && m_Loader.IsResident(fi);
            }
        }

        /// <summary>PlayCanvas prefetchNextLod — one step finer toward optimal per pass.</summary>
        int PrefetchTargetLodForLeaf(SogLeafNode leaf, int currentLod, int desiredLod, int optimalLod, bool veryNear)
        {
            if (ForceMaxQuality || veryNear)
            {
                if (currentLod < 0) return optimalLod >= 0 ? optimalLod : -1;
                if (currentLod <= optimalLod) return -1;
                return optimalLod;
            }

            if (desiredLod == optimalLod)
            {
                if (optimalLod >= 0 && !IsLodResident(leaf, optimalLod))
                    return optimalLod;
                return -1;
            }

            if (desiredLod > optimalLod)
                return Mathf.Max(optimalLod, desiredLod - 1);

            if (currentLod <= 0) return -1;
            return currentLod - 1;
        }

        public void ApplyLodChanges(NativeArray<int> visibleLeafIdx, int visibleCount, Camera cam)
        {
            ApplyLodChanges(visibleLeafIdx, visibleCount, cam, null);
        }

        /// <param name="budgetedOptimalLod">
        /// Per-leaf budget-adjusted optimal LOD (index = leaf id). Null = compute from distance.
        /// </param>
        public void ApplyLodChanges(
            NativeArray<int> visibleLeafIdx,
            int visibleCount,
            Camera cam,
            int[] budgetedOptimalLod)
        {
            if (cam == null)
                throw new ArgumentNullException(nameof(cam));
            ApplyLodChanges(visibleLeafIdx, visibleCount, cam.transform.position, cam, budgetedOptimalLod);
        }

        public void ApplyLodChanges(
            NativeArray<int> visibleLeafIdx,
            int visibleCount,
            float3 camPos,
            Camera cam,
            int[] budgetedOptimalLod = null)
        {
            if (!visibleLeafIdx.IsCreated)
                throw new ArgumentException("SogStreamer.ApplyLodChanges: visibleLeafIdx NativeArray must be created.", nameof(visibleLeafIdx));
            if (visibleCount < 0 || visibleCount > visibleLeafIdx.Length)
                throw new ArgumentOutOfRangeException(nameof(visibleCount));

            float3 camFwd = cam != null ? (float3)cam.transform.forward : new float3(0, 0, 1);
            float fovScale = cam != null ? SogLeafMath.FovScaleFromCamera(cam) : 1f;

            m_ChunksReferencedThisFrame.Clear();

            for (int v = 0; v < visibleCount; v++)
            {
                int leafIdx = visibleLeafIdx[v];
                if (leafIdx < 0 || leafIdx >= m_Leaves.Length) continue;

                var leaf = m_Leaves[leafIdx];
                float closestDist = math.sqrt(SogLeafMath.ClosestDistSq(leaf, camPos));
                float3 centre = SogLeafMath.WorldCentre(leaf);
                float effDist = SogLeafMath.EffectiveDistance(
                    closestDist, camPos, camFwd, centre, fovScale, LodBehindPenalty);

                bool veryNear = ForceMaxQuality || effDist < VeryNearDistance;

                int optimalLod;
                if (budgetedOptimalLod != null && leafIdx < budgetedOptimalLod.Length && budgetedOptimalLod[leafIdx] >= 0)
                    optimalLod = budgetedOptimalLod[leafIdx];
                else
                    optimalLod = SelectDesiredLodFromEffectiveDistance(effDist);

                if (optimalLod >= leaf.LodCount) optimalLod = leaf.LodCount - 1;
                if (optimalLod < 0) optimalLod = 0;
                m_LastOptimalLod[leafIdx] = optimalLod;

                int desiredLod = SelectDesiredLodWithUnderfill(leafIdx, optimalLod);
                m_LastDesiredLod[leafIdx] = optimalLod;

                int currentLod = m_CurrentLod[leafIdx];
                int currentFileIdx = currentLod >= 0 ? GetLeafFileIdx(leaf, currentLod) : -1;
                int desiredFileIdx = GetLeafFileIdx(leaf, desiredLod);

                int prefetchLod = PrefetchTargetLodForLeaf(leaf, currentLod, desiredLod, optimalLod, veryNear);
                int prefetchFileIdx = prefetchLod >= 0 ? GetLeafFileIdx(leaf, prefetchLod) : -1;

                if (desiredFileIdx != currentFileIdx)
                {
                    AcquireOnce(desiredFileIdx);

                    if (veryNear && currentLod > desiredLod)
                    {
                        for (int lod = currentLod; lod > desiredLod; lod--)
                            AcquireOnce(GetLeafFileIdx(leaf, lod - 1));
                    }

                    bool desiredResident = m_Loader.IsResident(desiredFileIdx);
                    if (desiredResident)
                    {
                        ReleasePending(leafIdx);
                        if (currentFileIdx >= 0 && currentFileIdx != desiredFileIdx)
                            m_Loader.Release(currentFileIdx);
                        m_CurrentLod[leafIdx] = desiredLod;
                    }
                    else
                    {
                        if (currentFileIdx >= 0 && m_PendingDecrements[leafIdx] < 0)
                            m_PendingDecrements[leafIdx] = currentFileIdx;
                        AcquireOnce(currentFileIdx);
                    }
                }
                else
                {
                    AcquireOnce(desiredFileIdx);
                    ReleasePending(leafIdx);
                    if (currentLod < 0) m_CurrentLod[leafIdx] = desiredLod;
                }

                if (veryNear && currentLod > optimalLod)
                {
                    for (int lod = currentLod; lod > optimalLod; lod--)
                    {
                        int fi = GetLeafFileIdx(leaf, lod - 1);
                        if (fi >= 0 && fi != desiredFileIdx && fi != currentFileIdx)
                            AcquireOnce(fi);
                    }
                }

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

            m_Loader.Tick();
        }

        static unsafe int GetLeafFileIdx(SogLeafNode leaf, int lod)
        {
            if (lod < 0 || lod >= leaf.LodCount) return -1;
            return leaf.LodFileIdx[lod];
        }

        void AcquireOnce(int fileIdx)
        {
            if (fileIdx < 0) return;
            if (!m_ChunksReferencedThisFrame.Add(fileIdx)) return;
            _ = m_Loader.AcquireAsync(fileIdx);
        }

        void ReleasePending(int leafIdx)
        {
            int pending = m_PendingDecrements[leafIdx];
            if (pending < 0) return;
            m_Loader.Release(pending);
            m_PendingDecrements[leafIdx] = -1;
        }

        public void Dispose() { }
    }
}
