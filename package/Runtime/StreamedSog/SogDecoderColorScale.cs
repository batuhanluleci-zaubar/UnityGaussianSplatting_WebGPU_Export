// SPDX-License-Identifier: MIT
// Track C2 (part 2): SOG per-attribute Burst decoders — scales + sh0.
//
// The sibling C2-meansQuats agent owns SogDecoder.cs (means + quats jobs and the
// enclosing partial class opening). This file lives in the same namespace but is
// intentionally kept as its own top-level container to avoid a merge conflict on
// the class-open / #using block. The two jobs here are self-contained and only
// depend on:
//   - SogCodebooks.SH_C0                (Track C2 part 2, this workflow)
//   - SogCodebooks.SigmoidInvOpacity()  (Track C2 part 2, this workflow)
//   - InputSplatData                    (Track B0 — Runtime namespace)
//
// Decoder formulas (verbatim from the C2 blueprint):
//   Scales    : output[i].scale = exp(codebook[scaleByte])
//               3 bytes per splat (x,y,z). Codebook holds log-scales.
//   SH0 (DC)  : output[i].dc0.xyz = 0.5 + codebook[byteRGB] * SH_C0
//               output[i].opacity = byteA / 255       (if storeAsLogit == false)
//                                 = SigmoidInvOpacity(byteA / 255)  (if true)
//               4 bytes per splat (r,g,b,a).
//
// Codebook-null handling: the caller MUST run SogCodebooks.PatchNullCodebook on
// the raw meta.json float?[] before packing into a NativeArray<float>. Once the
// data reaches these jobs the codebook is a dense 256-entry float array.

using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

using GaussianSplatting.Runtime;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Reads 3 bytes per splat (x,y,z quantisation indices), looks up the log-scale
    /// codebook, applies exp() and writes the result into
    /// <see cref="InputSplatData.scale"/> starting at <c>splatOffset</c>.
    ///
    /// scales.Length MUST equal <c>output.Length * 3</c> (or at least
    /// <c>(splatOffset + iterationCount) * 3</c>); codebook.Length MUST be 256.
    /// </summary>
    [BurstCompile]
    public struct DecodeScalesJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte>  scales;      // raw quantised bytes per splat
        [ReadOnly] public NativeArray<float> codebook;    // 256 entries of log-scales
        [NativeDisableParallelForRestriction]
        public NativeArray<InputSplatData>   output;
        public int splatOffset;
        public int strideBytes;

        public void Execute(int index)
        {
            int b = index * math.max(1, strideBytes);
            byte bx = scales[b + 0];
            byte by = scales[b + 1];
            byte bz = scales[b + 2];

            float lx = codebook[bx];
            float ly = codebook[by];
            float lz = codebook[bz];

            var s = output[splatOffset + index];
            float3 scale = new float3(math.exp(lx), math.exp(ly), math.exp(lz));
            s.scale = new Vector3(scale.x, scale.y, scale.z);
            output[splatOffset + index] = s;
        }
    }

    /// <summary>
    /// Reads 4 bytes per splat (r,g,b,a quantisation indices), computes the
    /// SH band-0 DC term for RGB and the opacity for A, and writes the result
    /// into <see cref="InputSplatData.dc0"/> / <see cref="InputSplatData.opacity"/>
    /// starting at <c>splatOffset</c>.
    ///
    /// sh0.Length MUST equal <c>output.Length * 4</c> (or at least
    /// <c>(splatOffset + iterationCount) * 4</c>); codebook.Length MUST be 256.
    ///
    /// <c>storeAsLogit</c>: when true, applies SigmoidInvOpacity to the alpha byte
    /// (legacy SuperSplat V2 logit-in-InputSplatData path). The gsplat_lod baker
    /// stores post-sigmoid 0..1 in the alpha byte — pass <c>false</c> for that path
    /// so RuntimeSplatAssetBuilder.CalcChunkDataJob receives sigmoid opacity (SPZ parity).
    /// </summary>
    [BurstCompile]
    public struct DecodeSh0Job : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte>  sh0;         // raw bytes, layout [r0 g0 b0 a0 r1 g1 b1 a1 ...]
        [ReadOnly] public NativeArray<float> codebook;    // 256 entries
        [NativeDisableParallelForRestriction]
        public NativeArray<InputSplatData>   output;
        public int  splatOffset;
        public bool storeAsLogit;

        public void Execute(int index)
        {
            int b = index * 4;
            byte br = sh0[b + 0];
            byte bg = sh0[b + 1];
            byte bb = sh0[b + 2];
            byte ba = sh0[b + 3];

            float r = 0.5f + codebook[br] * SogCodebooks.SH_C0;
            float g = 0.5f + codebook[bg] * SogCodebooks.SH_C0;
            float bl = 0.5f + codebook[bb] * SogCodebooks.SH_C0;

            float a = ba * (1f / 255f);
            if (storeAsLogit)
                a = SogCodebooks.SigmoidInvOpacity(a);

            var s = output[splatOffset + index];
            s.dc0 = new Vector3(r, g, bl);
            s.opacity = a;
            output[splatOffset + index] = s;
        }
    }
}
