// SPDX-License-Identifier: MIT
// PlayCanvas gsplat-budget-balancer.js port — 64 sqrt-distance buckets, far-first degrade.

using System;
using System.Collections.Generic;

namespace GaussianSplatting.Runtime.Streaming
{
    public static class SogBudgetBalancer
    {
        public const int kBuckets = 64;

        public struct Entry
        {
            public int id;
            public float distance;
            public int desiredLod;
            public int currentLod;
            public int maxLod;
            public int splatCountAtLod;
            public Func<int, int> splatCountForLod;
        }

        /// <summary>
        /// Adjust desiredLod per entry to fit lodBudget. Degrades far buckets first.
        /// </summary>
        public static void Balance(List<Entry> visible, long lodBudget, List<List<int>> bucketScratch)
        {
            if (lodBudget <= 0 || visible.Count == 0) return;

            long total = 0;
            foreach (var e in visible)
                total += e.splatCountForLod(e.desiredLod);

            if (total <= lodBudget) return;

            float maxDist = 0.001f;
            foreach (var e in visible)
                if (e.distance > maxDist) maxDist = e.distance;

            for (int b = 0; b < kBuckets; b++)
                bucketScratch[b].Clear();

            float invMax = (kBuckets - 1) / (float)Math.Sqrt(maxDist);
            foreach (var e in visible)
            {
                int b = Math.Clamp((int)(Math.Sqrt(e.distance) * invMax), 0, kBuckets - 1);
                bucketScratch[b].Add(e.id);
            }

            int guard = visible.Count * 8;
            bool moved = true;
            while (total > lodBudget && moved && guard-- > 0)
            {
                moved = false;
                for (int b = kBuckets - 1; b >= 0 && total > lodBudget; b--)
                {
                    foreach (int id in bucketScratch[b])
                    {
                        var e = visible[id];
                        if (e.desiredLod < e.maxLod)
                        {
                            int oldC = e.splatCountForLod(e.desiredLod);
                            int newLod = e.desiredLod + 1;
                            int newC = e.splatCountForLod(newLod);
                            total += newC - oldC;
                            e.desiredLod = newLod;
                            visible[id] = e;
                            moved = true;
                            if (total <= lodBudget) break;
                        }
                    }
                }
            }
        }
    }
}
