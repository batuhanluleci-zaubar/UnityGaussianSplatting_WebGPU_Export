// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime.Streaming
{
    /// <summary>
    /// Tracks resident leaf slots and rebuilds a single merged GaussianSplatAsset for unified draw.
    /// PlayCanvas GpuBufferPool semantics with a pragmatic merge path (desktop 250K cap).
    /// </summary>
    public sealed class GpuBufferPool : IDisposable
    {
        public struct Slot
        {
            public int leafId;
            public int lodLevel;
            public int splatCount;
            public bool active;
        }

        readonly BlockAllocator m_Allocator;
        readonly int m_MaxSplats;
        readonly Dictionary<int, NativeArray<InputSplatData>> m_SplatData = new();
        readonly Dictionary<int, Slot> m_Slots = new();

        GaussianSplatAsset m_MergedAsset;
        int m_MergedSplatCount;

        public int maxSplats => m_MaxSplats;
        public int residentSplats => m_Allocator.allocatedSplats;
        public GaussianSplatAsset mergedAsset => m_MergedAsset;
        public int mergedSplatCount => m_MergedSplatCount;
        public bool needsRebuild { get; private set; }

        /// <summary>Live splat total across active pool slots (accurate before RebuildMergedAsset).</summary>
        public int ActiveSplatCount()
        {
            int n = 0;
            foreach (var kv in m_Slots)
                if (kv.Value.active) n += kv.Value.splatCount;
            return n;
        }

        public GpuBufferPool(int maxSplats)
        {
            m_MaxSplats = Math.Max(256, maxSplats);
            m_Allocator = new BlockAllocator(m_MaxSplats);
        }

        public bool TryAddChunk(int leafId, int lodLevel, NativeArray<InputSplatData> splats)
        {
            if (!splats.IsCreated || splats.Length == 0) return false;

            if (m_Slots.TryGetValue(leafId, out var existing) && existing.active
                && existing.lodLevel == lodLevel && existing.splatCount == splats.Length)
                return true;

            int projected = residentSplats;
            if (m_Slots.TryGetValue(leafId, out var prev) && prev.active)
                projected -= prev.splatCount;
            if (projected + splats.Length > m_MaxSplats) return false;

            RemoveChunk(leafId, freeAllocator: false, markRebuild: false);

            var copy = new NativeArray<InputSplatData>(splats.Length, Allocator.Persistent);
            copy.CopyFrom(splats);
            m_SplatData[leafId] = copy;
            m_Slots[leafId] = new Slot { leafId = leafId, lodLevel = lodLevel, splatCount = splats.Length, active = true };
            RebuildAllocatorFromSlots();
            needsRebuild = true;
            return true;
        }

        void RebuildAllocatorFromSlots()
        {
            m_Allocator.Reset();
            foreach (var kv in m_Slots)
                if (kv.Value.active)
                    m_Allocator.TryAllocate(kv.Value.splatCount, out _);
        }

        public void RemoveChunk(int leafId, bool freeAllocator = true, bool markRebuild = true)
        {
            if (!m_Slots.TryGetValue(leafId, out var slot) || !slot.active) return;
            if (freeAllocator)
                m_Allocator.Free(0, slot.splatCount); // logical tracking only
            if (m_SplatData.TryGetValue(leafId, out var arr))
            {
                if (arr.IsCreated) arr.Dispose();
                m_SplatData.Remove(leafId);
            }
            slot.active = false;
            m_Slots[leafId] = slot;
            RebuildAllocatorFromSlots();
            if (markRebuild)
                needsRebuild = true;
        }

        public bool TryGetSlot(int leafId, out Slot slot) =>
            m_Slots.TryGetValue(leafId, out slot) && slot.active;

        public IEnumerable<Slot> ActiveSlots()
        {
            foreach (var kv in m_Slots)
                if (kv.Value.active) yield return kv.Value;
        }

        public GaussianSplatAsset RebuildMergedAsset()
        {
            if (m_MergedAsset != null)
            {
                UnityEngine.Object.Destroy(m_MergedAsset);
                m_MergedAsset = null;
            }
            m_MergedSplatCount = 0;
            foreach (var kv in m_Slots)
                if (kv.Value.active) m_MergedSplatCount += kv.Value.splatCount;
            if (m_MergedSplatCount == 0) { needsRebuild = false; return null; }

            var merged = new NativeArray<InputSplatData>(m_MergedSplatCount, Allocator.TempJob);
            int dst = 0;
            foreach (var kv in m_Slots)
            {
                if (!kv.Value.active || !m_SplatData.TryGetValue(kv.Key, out var src)) continue;
                NativeArray<InputSplatData>.Copy(src, 0, merged, dst, src.Length);
                dst += src.Length;
            }
            var built = RuntimeSplatAssetBuilder.BuildFromSplats(merged, "pool_merged");
            merged.Dispose();
            m_MergedAsset = built.asset;
            needsRebuild = false;
            return m_MergedAsset;
        }

        public void Dispose()
        {
            if (m_MergedAsset != null)
                UnityEngine.Object.Destroy(m_MergedAsset);
            foreach (var kv in m_SplatData)
                if (kv.Value.IsCreated) kv.Value.Dispose();
            m_SplatData.Clear();
            m_Slots.Clear();
        }
    }
}
