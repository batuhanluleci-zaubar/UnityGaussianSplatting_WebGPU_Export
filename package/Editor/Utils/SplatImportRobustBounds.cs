// SPDX-License-Identifier: MIT
// Robust bounds + outlier rejection for large SPZ/PLY imports.
// A handful of extreme-coordinate floaters (|pos| >> scene extent) wreck Morton order,
// asset bounds, octree build, and camera framing — especially on VeryHigh/Float32 imports
// that skip per-chunk normalization.
using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor.Utils
{
    static class SplatImportRobustBounds
    {
        const int kMaxSamples = 65536;
        const float kLowPercentile = 0.05f;
        const float kHighPercentile = 99.95f;
        const float kMarginFraction = 0.25f;

        public struct Result
        {
            public NativeArray<InputSplatData> splats;
            public float3 boundsMin;
            public float3 boundsMax;
            public int removedOutliers;
        }

        public static Result FilterAndReport(NativeArray<InputSplatData> input)
        {
            int original = input.Length;
            var r = Filter(input);
            r.removedOutliers = original - r.splats.Length;
            if (r.removedOutliers > 0)
            {
                Debug.LogWarning(
                    $"[GaussianSplatCreator] Removed {r.removedOutliers:N0} position outlier splats " +
                    $"(outside robust scene bounds). Scene bounds now {FormatBounds(r.boundsMin, r.boundsMax)}.");
            }
            return r;
        }

        /// <summary>Keep every splat; use for pre-cleaned chunk LOD0 PLY where per-chunk filtering drops valid boundary splats.</summary>
        public static Result Passthrough(NativeArray<InputSplatData> input)
        {
            if (!input.IsCreated || input.Length == 0)
            {
                return new Result
                {
                    splats = input,
                    boundsMin = float3.zero,
                    boundsMax = float3.zero,
                };
            }
            ComputeExactBounds(input, out float3 bMin, out float3 bMax);
            return new Result { splats = input, boundsMin = bMin, boundsMax = bMax, removedOutliers = 0 };
        }

        static Result Filter(NativeArray<InputSplatData> input)
        {
            if (!input.IsCreated || input.Length == 0)
            {
                return new Result
                {
                    splats = input,
                    boundsMin = float3.zero,
                    boundsMax = float3.zero,
                };
            }

            ComputePercentileBounds(input, out float3 coreMin, out float3 coreMax);
            float3 extent = coreMax - coreMin;
            float3 margin = math.max(extent * kMarginFraction, new float3(0.05f));
            float3 keepMin = coreMin - margin;
            float3 keepMax = coreMax + margin;

            int keepCount = 0;
            for (int i = 0; i < input.Length; i++)
            {
                float3 p = input[i].pos;
                if (p.x >= keepMin.x && p.x <= keepMax.x &&
                    p.y >= keepMin.y && p.y <= keepMax.y &&
                    p.z >= keepMin.z && p.z <= keepMax.z)
                    keepCount++;
            }

            if (keepCount == input.Length)
            {
                ComputeExactBounds(input, out float3 exactMin, out float3 exactMax);
                return new Result
                {
                    splats = input,
                    boundsMin = exactMin,
                    boundsMax = exactMax,
                };
            }

            var filtered = new NativeArray<InputSplatData>(keepCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            int w = 0;
            for (int i = 0; i < input.Length; i++)
            {
                float3 p = input[i].pos;
                if (p.x >= keepMin.x && p.x <= keepMax.x &&
                    p.y >= keepMin.y && p.y <= keepMax.y &&
                    p.z >= keepMin.z && p.z <= keepMax.z)
                    filtered[w++] = input[i];
            }

            input.Dispose();
            ComputeExactBounds(filtered, out float3 bMin, out float3 bMax);
            return new Result { splats = filtered, boundsMin = bMin, boundsMax = bMax };
        }

        static string FormatBounds(float3 bMin, float3 bMax) =>
            $"[{bMin.x:F2},{bMin.y:F2},{bMin.z:F2}] .. [{bMax.x:F2},{bMax.y:F2},{bMax.z:F2}]";

        static void ComputeExactBounds(NativeArray<InputSplatData> splats, out float3 bMin, out float3 bMax)
        {
            bMin = splats[0].pos;
            bMax = splats[0].pos;
            for (int i = 1; i < splats.Length; i++)
            {
                float3 p = splats[i].pos;
                bMin = math.min(bMin, p);
                bMax = math.max(bMax, p);
            }
        }

        static void ComputePercentileBounds(NativeArray<InputSplatData> splats, out float3 bMin, out float3 bMax)
        {
            int sampleCount = math.min(splats.Length, kMaxSamples);
            int step = math.max(1, splats.Length / sampleCount);
            var xs = new float[sampleCount];
            var ys = new float[sampleCount];
            var zs = new float[sampleCount];
            int n = 0;
            for (int i = 0; i < splats.Length && n < sampleCount; i += step)
            {
                float3 p = splats[i].pos;
                xs[n] = p.x;
                ys[n] = p.y;
                zs[n] = p.z;
                n++;
            }
            Array.Sort(xs, 0, n);
            Array.Sort(ys, 0, n);
            Array.Sort(zs, 0, n);

            bMin = new float3(
                PercentileSorted(xs, n, kLowPercentile),
                PercentileSorted(ys, n, kLowPercentile),
                PercentileSorted(zs, n, kLowPercentile));
            bMax = new float3(
                PercentileSorted(xs, n, kHighPercentile),
                PercentileSorted(ys, n, kHighPercentile),
                PercentileSorted(zs, n, kHighPercentile));
        }

        static float PercentileSorted(float[] sorted, int count, float p)
        {
            if (count <= 1) return count == 1 ? sorted[0] : 0f;
            float t = math.clamp(p / 100f, 0f, 1f) * (count - 1);
            int i0 = (int)math.floor(t);
            int i1 = math.min(i0 + 1, count - 1);
            float f = t - i0;
            return math.lerp(sorted[i0], sorted[i1], f);
        }
    }
}
