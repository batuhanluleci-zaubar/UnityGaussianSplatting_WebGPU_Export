// SPDX-License-Identifier: MIT
// Track C2a: Burst-compiled position + quaternion decoders.
//
// Runs one job per SOG attribute, producing InputSplatData rows in place:
//
//   DecodeMeansJob  — reads two WebP planes (means_l = low bytes, means_u = high
//                     bytes). Per splat samples 3 low + 3 high bytes, forms a
//                     16-bit normalised value per axis (n = ((hi << 8) | lo) / 65535),
//                     lerps between [mins, maxs], then applies InvLogTransform to
//                     recover the world-space position and writes it into
//                     output[splatOffset + i].pos.
//
//   DecodeQuatsJob  — reads a 4-byte-per-splat texture, calls
//                     SogCodebooks.UnpackSmallestThreeQuat and writes into
//                     output[splatOffset + i].rot. Invalid mode tags fall back to
//                     identity per spec.
//
// Both jobs are IJobParallelFor + [BurstCompile]. They use Unity.Mathematics only
// (no System.Math) and take NativeArray<byte> inputs so they are safe to schedule
// against job dependencies without managed allocations.
//
// Kept as top-level structs (mirroring DecodeShNJob) so callers can reference them
// via `SogGaussianSplatting.Runtime.StreamedSog.DecodeMeansJob` directly, matching
// the pattern already established in SogDecoder.cs.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Decodes SuperSplat's split-byte position texture pair into world-space float3
    /// positions inside <see cref="InputSplatData"/> rows.
    ///
    /// Byte layout assumed: two textures with <c>strideBytes</c> bytes per splat
    /// (3 for RGB8 planes, 4 for RGBA8). Only the first three components of each
    /// pixel are read (x,y,z). The 16-bit normalised value <c>n = ((hi &lt;&lt; 8) | lo) / 65535</c>
    /// is lerped between mins/maxs and then run through <see cref="SogCodebooks.InvLogTransform"/>.
    /// </summary>
    [BurstCompile]
    public struct DecodeMeansJob : IJobParallelFor
    {
        /// <summary>Low-byte plane. Length must be &gt;= splatCount * strideBytes.</summary>
        [ReadOnly] public NativeArray<byte> meansL;

        /// <summary>High-byte plane. Length must equal <see cref="meansL"/>'s length.</summary>
        [ReadOnly] public NativeArray<byte> meansU;

        /// <summary>World-space AABB min from the chunk meta.</summary>
        public float3 mins;

        /// <summary>World-space AABB max from the chunk meta.</summary>
        public float3 maxs;

        /// <summary>3 for RGB8-planes, 4 for RGBA8-planes.</summary>
        public int strideBytes;

        /// <summary>
        /// Offset into <see cref="output"/> at which this chunk's splats begin.
        /// </summary>
        public int splatOffset;

        [NativeDisableParallelForRestriction]
        public NativeArray<InputSplatData> output;

        /// <summary>splat-transform SOG → Unity SPZ frame (see SogCodebooks.PlayCanvasToUnityPos).</summary>
        public bool playCanvasToUnity;

        public void Execute(int index)
        {
            int b = index * strideBytes;

            // 16-bit normalised sample per axis: n = ((hi << 8) | lo) / 65535.
            float nx = ((meansU[b + 0] << 8) | meansL[b + 0]) / 65535f;
            float ny = ((meansU[b + 1] << 8) | meansL[b + 1]) / 65535f;
            float nz = ((meansU[b + 2] << 8) | meansL[b + 2]) / 65535f;

            float3 lerped = math.lerp(mins, maxs, new float3(nx, ny, nz));
            float3 world  = SogCodebooks.InvLogTransform(lerped);
            if (playCanvasToUnity)
                world = SogCodebooks.PlayCanvasToUnityPos(world);

            int outIdx = splatOffset + index;
            var s = output[outIdx];
            s.pos = new Vector3(world.x, world.y, world.z);
            output[outIdx] = s;
        }
    }

    /// <summary>
    /// Decodes a 4-byte-per-splat smallest-three quaternion texture into
    /// <c>InputSplatData.rot</c>. Delegates the actual unpack to
    /// <see cref="SogCodebooks.UnpackSmallestThreeQuat"/>, which returns identity
    /// on any invalid mode tag.
    /// </summary>
    [BurstCompile]
    public struct DecodeQuatsJob : IJobParallelFor
    {
        /// <summary>Raw quaternion bytes. Length must be &gt;= splatCount * 4.</summary>
        [ReadOnly] public NativeArray<byte> quats;

        /// <summary>
        /// Offset into <see cref="output"/> at which this chunk's splats begin.
        /// </summary>
        public int splatOffset;

        [NativeDisableParallelForRestriction]
        public NativeArray<InputSplatData> output;

        public bool playCanvasToUnity;

        public void Execute(int index)
        {
            int b = index * 4;
            quaternion q = SogCodebooks.UnpackSmallestThreeQuat(
                quats[b + 0],
                quats[b + 1],
                quats[b + 2],
                quats[b + 3]);
            if (playCanvasToUnity)
                q = SogCodebooks.PlayCanvasToUnityQuat(q);

            int outIdx = splatOffset + index;
            var s = output[outIdx];
            s.rot = new Quaternion(q.value.x, q.value.y, q.value.z, q.value.w);
            output[outIdx] = s;
        }
    }
}
