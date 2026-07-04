// SPDX-License-Identifier: MIT
// Port of playcanvas/engine src/core/block-allocator.js — fixed-size splat slot allocator.

using System;
using System.Collections.Generic;

namespace GaussianSplatting.Runtime.Streaming
{
    /// <summary>
    /// First-fit block allocator over a fixed splat-capacity pool.
    /// Each allocation is an integer splat index range [offset, offset+count).
    /// </summary>
    public sealed class BlockAllocator
    {
        readonly int m_Capacity;
        readonly List<(int offset, int size)> m_Free = new();

        public int capacity => m_Capacity;
        public int allocatedSplats { get; private set; }

        public BlockAllocator(int splatCapacity)
        {
            m_Capacity = Math.Max(1, splatCapacity);
            m_Free.Add((0, m_Capacity));
        }

        public bool TryAllocate(int splatCount, out int offset)
        {
            offset = -1;
            if (splatCount <= 0) return false;
            for (int i = 0; i < m_Free.Count; i++)
            {
                var (off, size) = m_Free[i];
                if (size < splatCount) continue;
                offset = off;
                allocatedSplats += splatCount;
                if (size == splatCount)
                    m_Free.RemoveAt(i);
                else
                    m_Free[i] = (off + splatCount, size - splatCount);
                return true;
            }
            return false;
        }

        public void Free(int offset, int splatCount)
        {
            if (splatCount <= 0) return;
            allocatedSplats = Math.Max(0, allocatedSplats - splatCount);
            m_Free.Add((offset, splatCount));
            Coalesce();
        }

        void Coalesce()
        {
            m_Free.Sort((a, b) => a.offset.CompareTo(b.offset));
            for (int i = m_Free.Count - 2; i >= 0; i--)
            {
                var a = m_Free[i];
                var b = m_Free[i + 1];
                if (a.offset + a.size == b.offset)
                {
                    m_Free[i] = (a.offset, a.size + b.size);
                    m_Free.RemoveAt(i + 1);
                }
            }
        }

        public void Reset()
        {
            m_Free.Clear();
            m_Free.Add((0, m_Capacity));
            allocatedSplats = 0;
        }
    }
}
