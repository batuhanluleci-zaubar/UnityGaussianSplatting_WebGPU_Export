// SPDX-License-Identifier: MIT
// Track C1: POCO + strict parser for lod-meta.json (v1).
//
// Blueprint schema (per zaubar gsplat viewer brief):
//   {
//     "version": 1,
//     "count":   <int>,                      // total splat count across every leaf
//     "counts":  [<int>, ...],               // per-leaf counts, len == filenames.Length
//     "lodLevels": <int>,                    // number of LOD ranks per leaf (>= 1)
//     "environment": <string?>,              // optional env map path
//     "filenames": ["chunk0", "chunk1", ...],
//     "tree": <Node>
//   }
//
//   Node =
//     interior { "bound": { "min":[x,y,z], "max":[x,y,z] }, "children": [Node, Node] }
//   | leaf     { "bound": { "min":[x,y,z], "max":[x,y,z] }, "lods": { "<i>": { "file":<idx>, "offset":<int>, "count":<int> } } }
//
// Validation rules that MUST throw:
//   - version != 1
//   - any interior node whose "children" array length != 2
//   - a node that has neither "children" nor "lods"
//
// JsonUtility is intentionally NOT used: the LOD entry is a discriminated union
// (Dictionary<string, LodEntry>) that JsonUtility cannot deserialize.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    public sealed class SogLodMeta
    {
        public int Version;
        public long TotalCount;
        public int[] Counts;
        public int LodLevels;
        public string Environment;      // nullable
        public string[] Filenames;
        public SogNode Tree;

        /// <summary>
        /// Parses a lod-meta.json v1 payload. Throws <see cref="FormatException"/> with
        /// a specific message on any schema mismatch.
        /// </summary>
        public static SogLodMeta Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("SogLodMeta.Parse: input JSON is empty");

            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex)
            {
                throw new FormatException($"SogLodMeta.Parse: invalid JSON — {ex.Message}", ex);
            }

            var meta = new SogLodMeta();

            var vTok = root["version"];
            if (vTok == null || vTok.Type == JTokenType.Null)
                throw new FormatException("SogLodMeta.Parse: missing required field 'version'");
            meta.Version = (int)vTok;
            if (meta.Version != 1)
                throw new FormatException($"SogLodMeta.Parse: unsupported lod-meta version {meta.Version} (expected 1)");

            meta.TotalCount = (long?)root["count"] ?? 0L;

            var countsTok = root["counts"] as JArray;
            if (countsTok == null)
                throw new FormatException("SogLodMeta.Parse: missing required 'counts' array");
            meta.Counts = new int[countsTok.Count];
            for (int i = 0; i < countsTok.Count; i++)
                meta.Counts[i] = (int)countsTok[i];

            var lodTok = root["lodLevels"];
            if (lodTok == null || lodTok.Type == JTokenType.Null)
                throw new FormatException("SogLodMeta.Parse: missing required 'lodLevels'");
            meta.LodLevels = (int)lodTok;
            if (meta.LodLevels < 1)
                throw new FormatException($"SogLodMeta.Parse: lodLevels must be >= 1 (got {meta.LodLevels})");

            meta.Environment = (string)root["environment"];

            var filesTok = root["filenames"] as JArray;
            if (filesTok == null)
                throw new FormatException("SogLodMeta.Parse: missing required 'filenames' array");
            meta.Filenames = new string[filesTok.Count];
            for (int i = 0; i < filesTok.Count; i++)
                meta.Filenames[i] = (string)filesTok[i];

            if (meta.Counts.Length != 0 && meta.Filenames.Length != 0 &&
                meta.Counts.Length != meta.Filenames.Length)
            {
                // Not fatal in every SuperSplat build; log but keep going so we can
                // still walk the tree for debugging tools.
                Debug.LogWarning(
                    $"SogLodMeta.Parse: counts.Length ({meta.Counts.Length}) != filenames.Length " +
                    $"({meta.Filenames.Length}); some LOD entries may not resolve.");
            }

            var treeTok = root["tree"] as JObject;
            if (treeTok == null)
                throw new FormatException("SogLodMeta.Parse: missing required 'tree' node");
            meta.Tree = ParseNode(treeTok, "tree");

            return meta;
        }

        static SogNode ParseNode(JObject nodeTok, string path)
        {
            var node = new SogNode();
            var boundTok = nodeTok["bound"] as JObject;
            if (boundTok != null)
            {
                node.BoundMin = ReadVec3(boundTok["min"] as JArray, $"{path}.bound.min");
                node.BoundMax = ReadVec3(boundTok["max"] as JArray, $"{path}.bound.max");
            }

            var childrenTok = nodeTok["children"] as JArray;
            var lodsTok = nodeTok["lods"] as JObject;

            bool hasChildren = childrenTok != null && childrenTok.Count > 0;
            bool hasLods = lodsTok != null && lodsTok.Count > 0;

            if (hasChildren)
            {
                if (childrenTok.Count != 2)
                {
                    throw new FormatException(
                        $"SogLodMeta.Parse: interior node at '{path}' has {childrenTok.Count} " +
                        $"children (expected exactly 2 — the tree is binary)");
                }
                node.Children = new SogNode[2];
                node.Children[0] = ParseNode((JObject)childrenTok[0], $"{path}.children[0]");
                node.Children[1] = ParseNode((JObject)childrenTok[1], $"{path}.children[1]");
                return node;
            }

            if (hasLods)
            {
                node.Lods = new Dictionary<int, SogLodEntry>(lodsTok.Count);
                foreach (var prop in lodsTok.Properties())
                {
                    if (!int.TryParse(prop.Name, out int lodIdx))
                    {
                        throw new FormatException(
                            $"SogLodMeta.Parse: LOD key '{prop.Name}' at '{path}.lods' is not an integer");
                    }
                    var entryObj = prop.Value as JObject;
                    if (entryObj == null)
                        throw new FormatException($"SogLodMeta.Parse: LOD entry at '{path}.lods.{prop.Name}' is not an object");
                    node.Lods[lodIdx] = new SogLodEntry
                    {
                        File = (int?)entryObj["file"] ?? -1,
                        Offset = (int?)entryObj["offset"] ?? 0,
                        Count = (int?)entryObj["count"] ?? 0,
                    };
                }
                return node;
            }

            throw new FormatException(
                $"SogLodMeta.Parse: node at '{path}' has neither 'children' nor 'lods' " +
                $"(node must be either an interior binary split or a leaf with LOD entries)");
        }

        static Vector3 ReadVec3(JArray arr, string path)
        {
            if (arr == null || arr.Count < 3)
                throw new FormatException($"SogLodMeta.Parse: expected 3-element array at '{path}'");
            return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
        }

        public bool IsLeaf(SogNode n) => n?.Children == null;
    }

    /// <summary>Runtime tree node. Either Children is non-null (interior, exactly 2) or Lods is non-null (leaf).</summary>
    public sealed class SogNode
    {
        public Vector3 BoundMin;
        public Vector3 BoundMax;
        public SogNode[] Children;                       // null for leaves
        public Dictionary<int, SogLodEntry> Lods;        // null for interior
    }

    public struct SogLodEntry
    {
        public int File;      // index into SogLodMeta.Filenames
        public int Offset;    // splat offset inside the chunk
        public int Count;     // splat count at this LOD
    }
}
