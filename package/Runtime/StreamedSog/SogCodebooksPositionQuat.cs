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
using UnityEngine;

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
        /// Forward log-space position compression (inverse of <see cref="InvLogTransform"/>).
        /// splat-transform tree bounds are world-space; sog_baker emits log-space.
        /// </summary>
        [BurstCompile]
        public static float LogTransform(float x)
        {
            return math.sign(x) * math.log(math.abs(x) + 1f);
        }

        /// <summary>
        /// Vectorised counterpart of <see cref="LogTransform(float)"/>.
        /// </summary>
        [BurstCompile]
        public static float3 LogTransform(float3 v)
        {
            return math.sign(v) * math.log(math.abs(v) + 1f);
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
        /// splat-transform SOG uses PlayCanvas RDF; Unity SPZ import is Y-up with +Z forward.
        /// Empirical parity: negate Y and Z (X unchanged) on positions decoded from splat-transform.
        /// NOTE: the official SOG spec states the file is RUB (x:right,y:up,z:back) — RUB->RUF would
        /// negate ONLY Z. This RDF (negate Y,Z) is the shipped behavior; revisit alongside the
        /// unresolved needle-orientation bug (see gsplat-webgpu-sog-needle-bug memory).
        /// </summary>
        [BurstCompile]
        public static float3 PlayCanvasToUnityPos(float3 p) => new float3(p.x, -p.y, -p.z);

        /// <summary>Quaternion companion for <see cref="PlayCanvasToUnityPos"/> (flipQ = 1,-1,-1).</summary>
        [BurstCompile]
        public static quaternion PlayCanvasToUnityQuat(quaternion q) =>
            new quaternion(q.value.x, -q.value.y, -q.value.z, q.value.w);

        /// <summary>Flip PlayCanvas world AABB into Unity SPZ space (min/max swap on Y/Z).</summary>
        public static void PlayCanvasToUnityBounds(ref Vector3 bMin, ref Vector3 bMax)
        {
            float yLo = -bMax.y;
            float yHi = -bMin.y;
            float zLo = -bMax.z;
            float zHi = -bMin.z;
            bMin.y = yLo;
            bMax.y = yHi;
            bMin.z = zLo;
            bMax.z = zHi;
        }

        /// <summary>
        /// Raises the smallest scale axes so aspect ratio stays bounded. Preserves
        /// anisotropy up to maxAspect (needed for flat surfaces) without needle streaks.
        /// </summary>
        [BurstCompile]
        public static float3 ClampScaleAnisotropy(float3 scale, float maxAspect = 8f)
        {
            float maxS = math.max(scale.x, math.max(scale.y, scale.z));
            if (!(maxS > 0f) || maxAspect <= 1f)
                return scale;
            float floorS = maxS / maxAspect;
            return math.max(scale, new float3(floorS, floorS, floorS));
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

            // [0..255] -> [-1..+1] then scale by 1/sqrt(2)  =>  [-sqrt(2)/2, +sqrt(2)/2].
            float a = ((b0 / 255f) * 2f - 1f) * invNorm;
            float b = ((b1 / 255f) * 2f - 1f) * invNorm;
            float c = ((b2 / 255f) * 2f - 1f) * invNorm;

            // Reconstruct the omitted (largest) component from ||q|| == 1 (assumed non-negative).
            float sumSq = a * a + b * b + c * c;
            float big = math.sqrt(math.max(0f, 1f - sumSq));

            // PlayCanvas SOG spec: components are ordered (w, x, y, z). The mode tag
            // (A - 252) indexes the largest/omitted component as 0=w, 1=x, 2=y, 3=z, and the
            // three kept bytes (b0,b1,b2) fill the remaining slots in that same (w,x,y,z) order.
            // (Previous code assumed an (x,y,z,w) order + 0=x mode mapping, which mis-slotted
            // every component and produced randomly-oriented anisotropic splats = needle streaks.)
            float qw, qx, qy, qz;
            switch (largestIdx)
            {
                case 0:  qw = big; qx = a;   qy = b;   qz = c;   break; // w omitted
                case 1:  qx = big; qw = a;   qy = b;   qz = c;   break; // x omitted
                case 2:  qy = big; qw = a;   qx = b;   qz = c;   break; // y omitted
                default: qz = big; qw = a;   qx = b;   qy = c;   break; // z omitted
            }

            float lenSq = qx * qx + qy * qy + qz * qz + qw * qw;
            if (!(lenSq > 1e-8f))
                return quaternion.identity;

            // Unity quaternion component order is (x, y, z, w).
            return new quaternion(qx, qy, qz, qw);
        }
    }
}
