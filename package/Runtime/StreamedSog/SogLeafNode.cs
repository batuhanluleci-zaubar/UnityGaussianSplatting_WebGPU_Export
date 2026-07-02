// SPDX-License-Identifier: MIT
// Track C3a: runtime flat-leaf struct produced by SogKdTree.FlattenAtLoad.
//
// Layout notes:
//   - Blittable value type so it can live in a NativeArray<SogLeafNode> and be
//     touched from Burst jobs without managed indirection.
//   - Fixed-size LOD arrays (max 8 ranks) keep the record self-contained: the
//     flat traversal doesn't have to chase a second NativeArray during the hot
//     loop.  A leaf that actually uses fewer than 8 ranks records the truth in
//     LodCount and leaves the trailing slots undefined.
//   - Field order is chosen so the struct is naturally 32-byte aligned: two
//     float3 bounds (24 B) + int LodCount (4 B) + int pad (4 B) sit together
//     in the first cache line, followed by three fixed-int[8] blocks (96 B).
//     Total = 128 B, a whole number of cache lines.
//
// Why C# `fixed` buffers rather than three NativeArrays: the whole point of
// C3a is a *flat* iteration.  Nesting NativeArrays inside a struct is not
// allowed, and splitting into three parallel NativeArrays would double the
// pointer chasing.  `unsafe` + `fixed int lodFileIdx[8]` gives us
// stack-allocated, cache-line-friendly storage that Burst is happy with.

using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Flat runtime record for one kd-tree leaf.  Interior nodes are discarded
    /// after <see cref="SogKdTree.FlattenAtLoad"/> — everything the per-frame
    /// culler needs (bounds + per-LOD file/offset/count) lives inside one
    /// blittable, cache-aligned struct.
    /// </summary>
    /// <remarks>
    /// The 8-rank cap matches the manifest schema: <c>SogLodMeta.LodLevels</c>
    /// is validated to be &gt;= 1 and we reject any leaf that claims more than
    /// <see cref="MaxLodCount"/> entries at flatten time.  Real SuperSplat
    /// assets in this repo top out at 4 ranks so 8 is comfortably future-proof.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct SogLeafNode
    {
        /// <summary>Max LOD ranks storable in one leaf record.</summary>
        public const int MaxLodCount = 8;

        // --- 32-byte header (two float3 + two ints) --------------------------

        /// <summary>Axis-aligned min corner of the leaf bound (log-space, same
        /// units as the raw manifest).  <see cref="SogKdTree.WalkVisibleLeaves"/>
        /// runs this through <see cref="SogCodebooks.InvLogTransform(float3)"/>
        /// before frustum testing.</summary>
        public float3 BoundMin;

        /// <summary>Axis-aligned max corner (log-space).</summary>
        public float3 BoundMax;

        /// <summary>Number of valid entries in the LOD arrays (1..8).</summary>
        public int LodCount;

        /// <summary>Explicit padding so the struct is a multiple of 32 B and
        /// the fixed arrays that follow start on a 4-B boundary regardless of
        /// how the C# compiler lays out `int` before an unsafe fixed buffer.</summary>
        public int _pad0;

        // --- 96 B of fixed LOD tables ---------------------------------------

        /// <summary>Per-rank index into <c>SogLodMeta.Filenames</c>.  Reading
        /// past <see cref="LodCount"/> is UB — the culler is responsible for
        /// honouring the count.</summary>
        public fixed int LodFileIdx[MaxLodCount];

        /// <summary>Per-rank splat offset inside the referenced chunk.</summary>
        public fixed int LodOffset[MaxLodCount];

        /// <summary>Per-rank splat count for that LOD (finer LODs = larger counts).</summary>
        public fixed int LodSplatCount[MaxLodCount];
    }
}
