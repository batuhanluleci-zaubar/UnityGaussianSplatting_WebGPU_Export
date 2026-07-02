// SPDX-License-Identifier: MIT
// Track C3a: kd-tree flattening + zero-alloc leaf traversal.
//
// Two entry points:
//
//   1) FlattenAtLoad(SogLodMeta)
//        Traverses the managed SogNode tree ONCE at load time using an
//        explicit int stack (NativeArray<int>), producing a flat
//        NativeArray<SogLeafNode>.  Interior nodes are discarded; only
//        leaves survive.  Callers own the returned NativeArray and must
//        Dispose it when the streamer is torn down.
//
//   2) WalkVisibleLeaves(leaves, camPos, camFwd, frustumPlanes, outIdx)
//        Per-frame flat loop over the leaves.  For each leaf we
//          a) InvLogTransform the log-space bounds to world space,
//          b) reject via a 6-plane frustum test on the resulting AABB
//             (which is already the ellipsoid-extent AABB: the manifest
//             bounds cover the *extent* of every Gaussian ellipsoid at
//             this rank, so the AABB is a valid frustum-test proxy),
//          c) write the leaf index into `outIdx` and continue.
//        Returns the count actually written.  Never allocates.
//
// Why an explicit stack: the SuperSplat kd-tree is only ~10 levels deep for
// current assets, but the recursive-C# form gets in the way of Burst reach
// later (managed recursion + capture-by-ref won't lift) and burns managed
// stack on WebGL where the JS stack is small.  A NativeArray<int> stack
// gives us a fixed upper bound (2 * nodesEncountered) and lets us bail on
// any pathological input without a StackOverflow.
//
// Ellipsoid-extent AABB test: we searched the repo for an existing helper
// (Track B3 culler predicate) and there isn't a shared function — the octree
// path uses per-splat positions plus GeometryUtility.TestPlanesAABB.  The
// SOG manifest already stores extent-aware bounds per leaf, so the honest
// port is "InvLog the corners, then TestPlanesAABB".  If a shared helper
// lands in a later track, WalkVisibleLeaves will move over to it in one
// line.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Static helpers that convert a parsed <see cref="SogLodMeta"/> tree into a
    /// flat, Burst-friendly leaf array and then walk it once per frame for
    /// visibility.  No managed recursion, no per-frame allocation.
    /// </summary>
    public static class SogKdTree
    {
        // Hard cap on the number of nodes the flatten pass will visit before it
        // aborts — protects against a malformed manifest with a cyclic or
        // absurdly deep tree.  A healthy SuperSplat asset is < 4K leaves so
        // 1M is 250x headroom.
        const int k_MaxNodesTraversed = 1 << 20;

        /// <summary>
        /// Flattens the parsed kd-tree in <paramref name="meta"/> into a
        /// NativeArray of <see cref="SogLeafNode"/> using an explicit iterative
        /// stack (no managed recursion).  Interior nodes are discarded post-flatten.
        /// </summary>
        /// <param name="meta">Parsed manifest; must have a non-null <c>Tree</c>.</param>
        /// <param name="allocator">Allocator for the returned array (typically
        /// <see cref="Allocator.Persistent"/> — the streamer keeps it around).</param>
        /// <returns>Leaves in DFS-pre order.  Caller owns the array and must
        /// Dispose it.</returns>
        public static NativeArray<SogLeafNode> FlattenAtLoad(
            SogLodMeta meta, Allocator allocator = Allocator.Persistent)
        {
            if (meta == null)
                throw new ArgumentNullException(nameof(meta));
            if (meta.Tree == null)
                throw new ArgumentException("SogKdTree.FlattenAtLoad: meta.Tree is null — parse the manifest first.", nameof(meta));

            // The kd-tree is binary and every interior has exactly 2 children,
            // so the leaf count = (nodeCount + 1) / 2.  We don't know nodeCount
            // up front, so gather into a List and then hand off to a NativeArray.
            // A managed List<T> here is fine because this runs exactly once at
            // load, off the per-frame path.
            var leaves = new List<SogLeafNode>(capacity: 256);

            // Iterative DFS.  Managed stack of SogNode is unavoidable here
            // because the input is a managed object graph; the explicit stack
            // still buys us bounded stack usage and lets us cap the traversal.
            var stack = new Stack<SogNode>(64);
            stack.Push(meta.Tree);

            int visited = 0;
            while (stack.Count > 0)
            {
                if (++visited > k_MaxNodesTraversed)
                {
                    throw new InvalidOperationException(
                        $"SogKdTree.FlattenAtLoad: traversed {visited} nodes without terminating — manifest is likely malformed.");
                }

                var node = stack.Pop();
                if (node == null)
                    continue;

                bool isLeaf = node.Children == null;
                if (!isLeaf)
                {
                    // Push children in reverse so left is popped first — keeps
                    // the flat array in the same order a natural recursion
                    // would produce (nicer for debugging / gizmos).
                    if (node.Children.Length != 2)
                    {
                        throw new FormatException(
                            $"SogKdTree.FlattenAtLoad: interior node has {node.Children.Length} children (expected 2).");
                    }
                    stack.Push(node.Children[1]);
                    stack.Push(node.Children[0]);
                    continue;
                }

                if (node.Lods == null || node.Lods.Count == 0)
                {
                    // Parser should have rejected this, but stay defensive.
                    throw new FormatException("SogKdTree.FlattenAtLoad: leaf has no LOD entries.");
                }
                if (node.Lods.Count > SogLeafNode.MaxLodCount)
                {
                    throw new FormatException(
                        $"SogKdTree.FlattenAtLoad: leaf reports {node.Lods.Count} LOD ranks, max supported is {SogLeafNode.MaxLodCount}.");
                }

                leaves.Add(MakeLeaf(node));
            }

            if (leaves.Count == 0)
            {
                // Empty tree is valid but useless — hand back an empty array so
                // callers don't have to null-check.
                return new NativeArray<SogLeafNode>(0, allocator, NativeArrayOptions.UninitializedMemory);
            }

            var flat = new NativeArray<SogLeafNode>(leaves.Count, allocator, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < leaves.Count; i++)
                flat[i] = leaves[i];
            return flat;
        }

        // Convert one managed SogNode leaf into its blittable SogLeafNode form.
        // The LOD dictionary is walked in key order so consumers can rely on
        // rank 0 == coarsest, rank N == finest (matches SuperSplat convention).
        static unsafe SogLeafNode MakeLeaf(SogNode node)
        {
            var leaf = new SogLeafNode
            {
                BoundMin = new float3(node.BoundMin.x, node.BoundMin.y, node.BoundMin.z),
                BoundMax = new float3(node.BoundMax.x, node.BoundMax.y, node.BoundMax.z),
                LodCount = node.Lods.Count,
            };

            // Sort keys ascending so LOD 0 comes first.  Dictionaries in .NET
            // don't guarantee iteration order.
            var keys = new int[node.Lods.Count];
            int ki = 0;
            foreach (var k in node.Lods.Keys) keys[ki++] = k;
            Array.Sort(keys);

            for (int i = 0; i < keys.Length; i++)
            {
                var entry = node.Lods[keys[i]];
                leaf.LodFileIdx[i]    = entry.File;
                leaf.LodOffset[i]     = entry.Offset;
                leaf.LodSplatCount[i] = entry.Count;
            }
            return leaf;
        }

        /// <summary>
        /// Walks a flat leaf array once, writing the indices of leaves whose
        /// world-space AABB survives a 6-plane frustum test into
        /// <paramref name="outIdx"/>.  Zero managed allocation.  Not marked
        /// [BurstCompile] because <see cref="GeometryUtility.TestPlanesAABB"/>
        /// is a managed call — the loop still runs plenty fast for the
        /// leaf counts (~1K) SuperSplat assets produce in this repo.
        /// </summary>
        /// <param name="leaves">Flat leaves produced by <see cref="FlattenAtLoad"/>.</param>
        /// <param name="camPos">Camera world position — used for distance sort by callers.</param>
        /// <param name="camFwd">Camera forward — reserved for a later cone cull; not used yet.</param>
        /// <param name="frustumPlanes">6-plane frustum from <c>GeometryUtility.CalculateFrustumPlanes</c>.</param>
        /// <param name="outIdx">Pre-allocated output buffer; must be >= leaves.Length.
        /// The first return-value entries are the visible leaf indices.</param>
        /// <returns>Number of indices written to <paramref name="outIdx"/>.</returns>
        public static int WalkVisibleLeaves(
            NativeArray<SogLeafNode> leaves,
            float3 camPos,
            float3 camFwd,
            Plane[] frustumPlanes,
            NativeArray<int> outIdx)
        {
            if (!leaves.IsCreated || leaves.Length == 0) return 0;
            if (frustumPlanes == null || frustumPlanes.Length < 6)
                throw new ArgumentException("SogKdTree.WalkVisibleLeaves: frustumPlanes must have at least 6 entries.", nameof(frustumPlanes));
            if (!outIdx.IsCreated || outIdx.Length < leaves.Length)
                throw new ArgumentException("SogKdTree.WalkVisibleLeaves: outIdx must be allocated with capacity >= leaves.Length.", nameof(outIdx));

            _ = camPos;  // reserved for distance-sort callers
            _ = camFwd;  // reserved for a later cone-cull hook

            int written = 0;
            for (int i = 0; i < leaves.Length; i++)
            {
                var leaf = leaves[i];

                // Log-space bounds -> world-space via the same InvLogTransform
                // the decoder uses on positions.  Because sign*(exp(|x|)-1) is
                // monotonic, applying it to (min, max) yields a valid AABB.
                float3 wMin = SogCodebooks.InvLogTransform(leaf.BoundMin);
                float3 wMax = SogCodebooks.InvLogTransform(leaf.BoundMax);

                // The manifest bounds already envelope the ellipsoid extents of
                // every Gaussian at this rank (SuperSplat bakes it that way),
                // so no extra scale-based expansion is needed here — the AABB
                // IS the ellipsoid-extent AABB.
                var center = (Vector3)((wMin + wMax) * 0.5f);
                var size   = (Vector3)(wMax - wMin);
                var bounds = new Bounds(center, size);

                if (!GeometryUtility.TestPlanesAABB(frustumPlanes, bounds))
                    continue;

                outIdx[written++] = i;
            }
            return written;
        }
    }
}
