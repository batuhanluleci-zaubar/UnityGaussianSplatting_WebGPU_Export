// SPDX-License-Identifier: MIT
// Shared leaf bounds math for SOG streaming (distance, LOD bands).

using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    public static class SogLeafMath
    {
        const float kRefTanHalfFov = 0.41421356237f;

        /// <summary>
        /// Tight world AABB from leaf nodes, skipping splat-transform mega-nodes whose span
        /// exceeds median×8 (splat-transform root bounds can span 300m+ while content ~60m).
        /// </summary>
        public static bool TryComputeTightSceneBounds(
            NativeArray<SogLeafNode> leaves,
            out float3 worldMin,
            out float3 worldMax)
        {
            worldMin = new float3(float.PositiveInfinity);
            worldMax = new float3(float.NegativeInfinity);
            if (!leaves.IsCreated || leaves.Length == 0)
                return false;

            int n = leaves.Length;
            var spans = new float[n];
            for (int i = 0; i < n; i++)
            {
                float3 lMin = SogCodebooks.InvLogTransform(leaves[i].BoundMin);
                float3 lMax = SogCodebooks.InvLogTransform(leaves[i].BoundMax);
                spans[i] = math.length(lMax - lMin);
            }
            Array.Sort(spans);
            float median = spans[n / 2];
            float maxSpan = math.max(median * 8f, 80f);

            bool any = false;
            for (int i = 0; i < n; i++)
            {
                float3 lMin = SogCodebooks.InvLogTransform(leaves[i].BoundMin);
                float3 lMax = SogCodebooks.InvLogTransform(leaves[i].BoundMax);
                if (math.length(lMax - lMin) > maxSpan)
                    continue;
                worldMin = math.min(worldMin, lMin);
                worldMax = math.max(worldMax, lMax);
                any = true;
            }
            return any && math.all(worldMax > worldMin);
        }

        /// <summary>Closest point on a world-space AABB to <paramref name="point"/>.</summary>
        public static float3 ClosestPointOnBounds(float3 boundsMin, float3 boundsMax, float3 point) =>
            math.clamp(point, boundsMin, boundsMax);

        /// <summary>Squared distance from <paramref name="point"/> to the leaf AABB (not centre).</summary>
        public static float ClosestDistSq(SogLeafNode leaf, float3 point)
        {
            float3 wMin = SogCodebooks.InvLogTransform(leaf.BoundMin);
            float3 wMax = SogCodebooks.InvLogTransform(leaf.BoundMax);
            float3 closest = ClosestPointOnBounds(wMin, wMax, point);
            return math.lengthsq(closest - point);
        }

        /// <summary>World-space centre of the leaf AABB.</summary>
        public static float3 WorldCentre(SogLeafNode leaf)
        {
            float3 wMin = SogCodebooks.InvLogTransform(leaf.BoundMin);
            float3 wMax = SogCodebooks.InvLogTransform(leaf.BoundMax);
            return (wMin + wMax) * 0.5f;
        }

        /// <summary>
        /// SPZ/GsplatLodScheduler parity: FOV scale + behind-camera penalty on closest distance.
        /// </summary>
        public static float EffectiveDistance(
            float closestDist,
            float3 camPos,
            float3 camFwd,
            float3 leafCentre,
            float fovScale,
            float behindPenalty)
        {
            float dist = math.max(closestDist, 0.001f);
            float3 toLeaf = leafCentre - camPos;
            float invLen = 1f / dist;
            float behindT = math.max(0f, -math.dot(camFwd, toLeaf * invLen));
            return dist * fovScale * (1f + behindT * (behindPenalty - 1f));
        }

        public static float FovScaleFromCamera(Camera cam)
        {
            if (cam == null) return 1f;
            float tanV = math.tan(cam.fieldOfView * 0.5f * math.PI / 180f);
            return math.min(tanV, tanV * cam.aspect) / kRefTanHalfFov;
        }
    }
}
