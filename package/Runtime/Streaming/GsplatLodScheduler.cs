// SPDX-License-Identifier: MIT
// Shared LOD scheduler — screen-error bands, budget damper, behind-camera penalty.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime.Streaming
{
    public sealed class GsplatLodScheduler
    {
        public const int kBuckets = SogBudgetBalancer.kBuckets;
        const float kRefTanHalfFov = 0.41421356237f;
        const float kBudgetDeadZone = 0.4f;
        const float kBudgetBlend = 0.3f;

        readonly List<List<int>> m_Buckets = new(kBuckets);

        public float lodBaseDistance = 15f;
        public float lodMultiplier = 3f;
        public float lodBehindPenalty = 2.5f;
        public float budgetScale = 1f;
        public long deviceBudget = 250_000;
        public bool forceMaxQuality;

        public GsplatLodScheduler()
        {
            for (int i = 0; i < kBuckets; i++)
                m_Buckets.Add(new List<int>(32));
        }

        public struct ChunkView
        {
            public int id;
            public bool visible;
            public float distance;
            public Vector3 worldCentre;
            public int lodCount;
            public int optimalLod;
            public int desiredLod;
            public Func<int, int> splatCountAtLod;
        }

        public void EvaluateVisible(Camera cam, List<ChunkView> chunks, List<int> visibleIds, long envFloorSplats)
        {
            visibleIds.Clear();
            if (cam == null) return;

            var planes = GeometryUtility.CalculateFrustumPlanes(cam);
            Vector3 camPos = cam.transform.position;
            Vector3 camFwd = cam.transform.forward;
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float fovScale = Mathf.Min(tanV, tanV * cam.aspect) / kRefTanHalfFov;
            float baseDist = Mathf.Max(0.01f, lodBaseDistance * budgetScale);
            float maxDist = 0.001f;

            for (int i = 0; i < chunks.Count; i++)
            {
                var c = chunks[i];
                int k1 = Math.Max(0, c.lodCount - 1);
                c.visible = GeometryUtility.TestPlanesAABB(planes, new Bounds(c.worldCentre, Vector3.one * 0.01f));
                c.distance = Vector3.Distance(camPos, c.worldCentre);
                if (c.distance > maxDist) maxDist = c.distance;

                if (!c.visible) { c.optimalLod = k1; chunks[i] = c; continue; }
                if (forceMaxQuality) { c.optimalLod = 0; c.desiredLod = 0; chunks[i] = c; visibleIds.Add(i); continue; }

                Vector3 toChunk = c.worldCentre - camPos;
                float invLen = 1f / Mathf.Max(c.distance, 0.001f);
                float behindT = Mathf.Max(0f, -Vector3.Dot(camFwd, toChunk * invLen));
                float effDist = c.distance * fovScale * (1f + behindT * (lodBehindPenalty - 1f));

                int lv = 0;
                float thr = baseDist;
                while (lv < k1 && effDist >= thr) { thr *= lodMultiplier; lv++; }
                c.optimalLod = lv;
                c.desiredLod = lv;
                chunks[i] = c;
                visibleIds.Add(i);
            }

            visibleIds.Sort((a, b) => chunks[a].distance.CompareTo(chunks[b].distance));

            long lodBudget = deviceBudget > 0 ? Math.Max(1, deviceBudget - envFloorSplats) : 0;
            if (lodBudget <= 0 || forceMaxQuality) return;

            var entries = new List<SogBudgetBalancer.Entry>(visibleIds.Count);
            for (int vi = 0; vi < visibleIds.Count; vi++)
            {
                int i = visibleIds[vi];
                var c = chunks[i];
                entries.Add(new SogBudgetBalancer.Entry
                {
                    id = vi,
                    distance = c.distance,
                    desiredLod = c.desiredLod,
                    currentLod = c.desiredLod,
                    maxLod = Math.Max(0, c.lodCount - 1),
                    splatCountForLod = c.splatCountAtLod
                });
            }

            SogBudgetBalancer.Balance(entries, lodBudget, m_Buckets);

            for (int vi = 0; vi < entries.Count; vi++)
            {
                int i = visibleIds[vi];
                var c = chunks[i];
                c.desiredLod = entries[vi].desiredLod;
                chunks[i] = c;
            }

            long total = 0;
            foreach (int i in visibleIds)
                total += chunks[i].splatCountAtLod(chunks[i].desiredLod);

            ApplyBudgetDamper(visibleIds, chunks, lodBudget);
        }

        /// <summary>
        /// SOG path: visible leaves already have optimalLod/desiredLod/distance set.
        /// Runs 64-bucket budget balancer + damper only (no frustum re-test).
        /// </summary>
        public void BalanceLodBudget(List<ChunkView> visibleChunks, long envFloorSplats)
        {
            if (visibleChunks == null || visibleChunks.Count == 0) return;

            long lodBudget = deviceBudget > 0 ? Math.Max(1, deviceBudget - envFloorSplats) : 0;
            if (lodBudget <= 0 || forceMaxQuality) return;

            var visibleIds = new List<int>(visibleChunks.Count);
            for (int i = 0; i < visibleChunks.Count; i++)
                visibleIds.Add(i);

            visibleIds.Sort((a, b) => visibleChunks[a].distance.CompareTo(visibleChunks[b].distance));

            var entries = new List<SogBudgetBalancer.Entry>(visibleIds.Count);
            for (int vi = 0; vi < visibleIds.Count; vi++)
            {
                int i = visibleIds[vi];
                var c = visibleChunks[i];
                entries.Add(new SogBudgetBalancer.Entry
                {
                    id = vi,
                    distance = c.distance,
                    desiredLod = c.desiredLod,
                    currentLod = c.desiredLod,
                    maxLod = Math.Max(0, c.lodCount - 1),
                    splatCountForLod = c.splatCountAtLod
                });
            }

            SogBudgetBalancer.Balance(entries, lodBudget, m_Buckets);

            for (int vi = 0; vi < entries.Count; vi++)
            {
                int i = visibleIds[vi];
                var c = visibleChunks[i];
                c.desiredLod = entries[vi].desiredLod;
                visibleChunks[i] = c;
            }

            ApplyBudgetDamperIndices(visibleIds, visibleChunks, lodBudget);
        }

        void ApplyBudgetDamper(List<int> visibleIds, List<ChunkView> chunks, long lodBudget)
        {
            long total = 0;
            foreach (int i in visibleIds)
                total += chunks[i].splatCountAtLod(chunks[i].desiredLod);
            ApplyBudgetDamperFromTotal(total, lodBudget);
        }

        void ApplyBudgetDamperIndices(List<int> visibleIds, List<ChunkView> chunks, long lodBudget)
        {
            long total = 0;
            foreach (int i in visibleIds)
                total += chunks[i].splatCountAtLod(chunks[i].desiredLod);
            ApplyBudgetDamperFromTotal(total, lodBudget);
        }

        void ApplyBudgetDamperFromTotal(long total, long lodBudget)
        {
            if (lodBudget <= 0) return;
            float ratio = (float)total / lodBudget;
            if (ratio < 1f - kBudgetDeadZone || ratio > 1f + kBudgetDeadZone)
            {
                float target = 1f / Mathf.Sqrt(Mathf.Max(ratio, 1e-3f));
                budgetScale = Mathf.Clamp(budgetScale * (1f + (target - 1f) * kBudgetBlend), 0.05f, 1f);
            }
        }
    }
}
