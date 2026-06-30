// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GsplatTour
{
    /// <summary>
    /// Computes an outlier-robust world-space content centre + radius for a gaussian splat.
    ///
    /// Gaussian-splat captures frequently contain a sprinkling of stray "floater" splats far from
    /// the real scene; these inflate the asset's axis-aligned bounds, which is why naive framing
    /// (orbit the bounds centre) puts the camera inside the walls. We instead work from the
    /// per-chunk position bounds (256-splat Morton-coherent groups), take the per-axis median of
    /// the chunk centres for the centre, and a percentile of their spread for the radius — so the
    /// few sparse outlier chunks are discarded. Falls back to the asset bounds if no chunk data.
    /// </summary>
    public static class GsplatAutoFramer
    {
        public struct Frame
        {
            public Vector3 center;   // robust content centre, world space
            public float radius;     // robust content radius, world units
            public bool fromChunks;  // true if derived from chunk data, false if bounds fallback
        }

        public static bool TryCompute(GaussianSplatRenderer splat, out Frame frame, float radiusPercentile = 0.92f)
        {
            frame = default;
            if (splat == null || splat.m_Asset == null)
                return false;

            var asset = splat.m_Asset;
            var t = splat.transform;

            // Fallback: full (outlier-inflated) bounds.
            Vector3 boundsCenter = t.TransformPoint(((Vector3)asset.boundsMin + (Vector3)asset.boundsMax) * 0.5f);
            float boundsRadius = Vector3.Scale((Vector3)asset.boundsMax - (Vector3)asset.boundsMin, t.lossyScale).magnitude * 0.5f;

            if (asset.chunkData == null || asset.chunkData.dataSize == 0)
            {
                frame = new Frame { center = boundsCenter, radius = Mathf.Max(boundsRadius, 0.01f), fromChunks = false };
                return true;
            }

            NativeArray<GaussianSplatAsset.ChunkInfo> chunks = asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>();
            int n = chunks.Length;
            if (n == 0)
            {
                frame = new Frame { center = boundsCenter, radius = Mathf.Max(boundsRadius, 0.01f), fromChunks = false };
                return true;
            }

            var centers = new List<Vector3>(n);
            var xs = new List<float>(n);
            var ys = new List<float>(n);
            var zs = new List<float>(n);
            for (int i = 0; i < n; i++)
            {
                GaussianSplatAsset.ChunkInfo c = chunks[i];
                // posX/posY/posZ are float2(min, max) of the chunk's positions in asset-local space.
                float3 localCenter = new float3((c.posX.x + c.posX.y) * 0.5f,
                                                (c.posY.x + c.posY.y) * 0.5f,
                                                (c.posZ.x + c.posZ.y) * 0.5f);
                Vector3 w = t.TransformPoint(localCenter);
                centers.Add(w);
                xs.Add(w.x); ys.Add(w.y); zs.Add(w.z);
            }

            Vector3 median = new Vector3(Median(xs), Median(ys), Median(zs));

            var dists = new List<float>(n);
            for (int i = 0; i < centers.Count; i++)
                dists.Add((centers[i] - median).magnitude);
            dists.Sort();
            int idx = Mathf.Clamp(Mathf.RoundToInt(radiusPercentile * (dists.Count - 1)), 0, dists.Count - 1);
            float radius = Mathf.Max(dists[idx], 0.01f);

            frame = new Frame { center = median, radius = radius, fromChunks = true };
            return true;
        }

        static float Median(List<float> v)
        {
            v.Sort();
            int m = v.Count / 2;
            return (v.Count & 1) == 1 ? v[m] : 0.5f * (v[m - 1] + v[m]);
        }
    }
}
