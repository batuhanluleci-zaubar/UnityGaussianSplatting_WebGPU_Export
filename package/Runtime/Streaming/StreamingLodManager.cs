// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime.Streaming
{
    /// <summary>
    /// Bridges scheduler desired LODs to pool residency with never-drop-visible semantics.
    /// </summary>
    public sealed class StreamingLodManager
    {
        readonly GpuBufferPool m_Pool;
        readonly Dictionary<int, int> m_CurrentLod = new();
        readonly Dictionary<int, int> m_PendingLod = new();

        public StreamingLodManager(GpuBufferPool pool) => m_Pool = pool;

        public GpuBufferPool pool => m_Pool;

        public bool RequestChunk(int leafId, int lodLevel, NativeArray<InputSplatData> splats)
            => RequestChunk(leafId, lodLevel, splats, null, null);

        /// <param name="camPos">When set with <paramref name="getLeafCentre"/>, evicts farthest resident leaf first.</param>
        /// <param name="getLeafCentre">World-space centre for a leaf id (for distance eviction).</param>
        public bool RequestChunk(int leafId, int lodLevel, NativeArray<InputSplatData> splats,
            Vector3? camPos, System.Func<int, Vector3> getLeafCentre)
        {
            if (!splats.IsCreated) return false;
            if (TryAddChunkInternal(leafId, lodLevel, splats))
                return true;

            var evict = new List<int>(m_CurrentLod.Count);
            foreach (var kv in m_CurrentLod)
                if (kv.Key != leafId) evict.Add(kv.Key);

            if (camPos.HasValue && getLeafCentre != null)
            {
                var cam = camPos.Value;
                evict.Sort((a, b) =>
                {
                    float da = (getLeafCentre(a) - cam).sqrMagnitude;
                    float db = (getLeafCentre(b) - cam).sqrMagnitude;
                    return db.CompareTo(da);
                });
            }

            foreach (int other in evict)
            {
                EvictChunk(other);
                if (TryAddChunkInternal(leafId, lodLevel, splats))
                    return true;
            }
            return false;
        }

        bool TryAddChunkInternal(int leafId, int lodLevel, NativeArray<InputSplatData> splats)
        {
            if (!m_Pool.TryAddChunk(leafId, lodLevel, splats)) return false;
            m_CurrentLod[leafId] = lodLevel;
            m_PendingLod.Remove(leafId);
            return true;
        }

        public void EvictChunk(int leafId)
        {
            m_Pool.RemoveChunk(leafId);
            m_CurrentLod.Remove(leafId);
            m_PendingLod.Remove(leafId);
        }

        public int GetCurrentLod(int leafId) =>
            m_CurrentLod.TryGetValue(leafId, out int lod) ? lod : -1;

        public void MarkPending(int leafId, int lod) => m_PendingLod[leafId] = lod;

        public bool IsResident(int leafId) => m_Pool.TryGetSlot(leafId, out var s) && s.active;

        public GaussianSplatAsset RebuildIfNeeded() =>
            m_Pool.needsRebuild ? m_Pool.RebuildMergedAsset() : m_Pool.mergedAsset;
    }
}
