// SPDX-License-Identifier: MIT
// Track C2 (part 1): position + quaternion decoder helpers for SogCodebooks.
//
// This partial file lives alongside SogCodebooks.cs (SH_C0 + SigmoidInvOpacity)
// and adds the helpers required by DecodeMeansJob / DecodeQuatsJob:
//
//   PatchNullCodebook(float?[])       -> dense float[256], main-thread only
//                                        (SuperSplat pre-2025 null-slot workaround)
//   InvLogTransform(float x)          -> sign(x) * (exp(|x|) - 1)   (Burst-safe)
//   UnpackSmallestThreeQuat(b0..b3)   -> normalised quaternion or identity on
//                                        an invalid mode tag.       (Burst-safe)
//
// Formulas verbatim from the zaubar gsplat viewer blueprint:
//   - Position:     n = ((hi<<8) | lo) / 65535, world axis-wise lerp then InvLog.
//   - Quaternion:   3x 8-bit + 2-bit largest-index mode, norm = sqrt(2),
//                   mode tag byte = 252 + largestIdx, tags outside 252..255
//                   are invalid and MUST return identity.

using System;
using Unity.Burst;
using Unity.Mathematics;

namespace GaussianSplatting.Runtime.StreamedSog
{
    public static partial class SogCodebooks
    {
        /// <summary>
        /// Patches a SuperSplat null-codebook slot. If <c>codebook[0]</c> is null we
        /// extrapolate <c>codebook[0] = codebook[1] + (codebook[1] - codebook[255]) / 255</c>
        /// so downstream jobs can index it as a plain float[256]. Any other null
        /// entries are filled by linear interpolation between the nearest non-null
        /// neighbours, mirroring what SuperSplat does at encode time when the
        /// clustering leaves a mid-range slot empty.
        /// </summary>
        /// <remarks>
        /// Main-thread only — this method allocates a managed array. Call once at
        /// chunk-load time and hand the dense float[256] to Burst jobs.
        /// </remarks>
        public static float[] PatchNullCodebook(float?[] codebook)
        {
            if (codebook == null || codebook.Length == 0)
                return null;
            if (codebook.Length != 256)
                throw new ArgumentException(
                    $"PatchNullCodebook: codebook length must be 256 (got {codebook.Length})",
                    nameof(codebook));

            var dense = new float[256];
            for (int i = 0; i < 256; i++)
                dense[i] = codebook[i].GetValueOrDefault();

            // Reference JS: if slot 0 is null, extrapolate from slot 1 and slot 255.
            if (!codebook[0].HasValue && codebook[1].HasValue && codebook[255].HasValue)
            {
                float c1 = codebook[1].Value;
                float c255 = codebook[255].Value;
                dense[0] = c1 + (c1 - c255) / 255f;
            }

            // Defensive: fill remaining null holes by linear interpolation between
            // the last known value and the next non-null neighbour.
            for (int i = 1; i < 256; i++)
            {
                if (codebook[i].HasValue) continue;
                int j = i + 1;
                while (j < 256 && !codebook[j].HasValue) j++;
                if (j >= 256)
                {
                    // trailing nulls: replicate previous slot
                    dense[i] = dense[i - 1];
                    continue;
                }
                float a = dense[i - 1];
                float b = codebook[j].Value;
                dense[j] = b;
                int span = j - (i - 1);
                for (int k = i; k < j; k++)
                {
                    float t = (float)(k - (i - 1)) / span;
                    dense[k] = math.lerp(a, b, t);
                }
                i = j;
            }

            return dense;
        }

        /// <summary>
        /// Inverse of SuperSplat's log-space position compression:
        ///   world = sign(x) * (exp(|x|) - 1)
        /// Burst-safe (Unity.Mathematics only).
        /// </summary>
        [BurstCompile]
        public static float InvLogTransform(float x)
        {
            return math.sign(x) * (math.exp(math.abs(x)) - 1f);
        }

        /// <summary>
        /// Vectorised counterpart of <see cref="InvLogTransform(float)"/> for a
        /// full float3 sample.
        /// </summary>
        [BurstCompile]
        public static float3 InvLogTransform(float3 v)
        {
            return math.sign(v) * (math.exp(math.abs(v)) - 1f);
        }

        /// <summary>
        /// Decodes a smallest-three quaternion from 4 raw bytes:
        ///   b0..b2 = 8-bit components in [0..255],
        ///   b3     = mode tag (252 + largestIdx).
        /// Norm is sqrt(2), so each component maps back to [-1/sqrt(2), +1/sqrt(2)].
        /// If b3 is outside 252..255 (invalid tag) or the resulting quaternion is
        /// degenerate (zero-length), returns <see cref="quaternion.identity"/>.
        /// </summary>
        [BurstCompile]
        public static quaternion UnpackSmallestThreeQuat(byte b0, byte b1, byte b2, byte b3)
        {
            // Invalid mode tag — spec-mandated identity fallback.
            if (b3 < 252 || b3 > 255)
                return quaternion.identity;

            int largestIdx = b3 - 252;
            const float invNorm = 1f / 1.4142135623730951f;   // 1 / sqrt(2)

            // [0..255] -> [-1..+1] then scale by 1/sqrt(2).
            float a = ((b0 / 255f) * 2f - 1f) * invNorm;
            float b = ((b1 / 255f) * 2f - 1f) * invNorm;
            float c = ((b2 / 255f) * 2f - 1f) * invNorm;

            // Reconstruct the largest component from ||q|| == 1.
            float sumSq = a * a + b * b + c * c;
            float wSq = 1f - sumSq;
            if (wSq < 0f) wSq = 0f;
            float w = math.sqrt(wSq);

            // Slot back into (x,y,z,w) according to the mode tag.
            float x, y, z, ww;
            switch (largestIdx)
            {
                case 0:  x = w; y = a; z = b; ww = c; break;
                case 1:  x = a; y = w; z = b; ww = c; break;
                case 2:  x = a; y = b; z = w; ww = c; break;
                default: x = a; y = b; z = c; ww = w; break;   // largestIdx == 3
            }

            float lenSq = x * x + y * y + z * z + ww * ww;
            if (!(lenSq > 1e-8f))
                return quaternion.identity;

            return new quaternion(x, y, z, ww);
        }
    }
}
