// SPDX-License-Identifier: MIT
// Track C1: POCO + parser for per-chunk meta.json (v2).
//
// Per-chunk schema (mirrors SuperSplat/PlayCanvas emitter):
//   {
//     "means":  { "mins":[x,y,z], "maxs":[x,y,z], "files":["means_l.webp","means_u.webp"] },
//     "scales": { "codebook":[float?, x256], "files":["scales.webp"] },
//     "quats":  { "files":["quats.webp"] },                          // smallest-three, no codebook
//     "sh0":    { "codebook":[float?, x256], "files":["sh0.webp"] },
//     "shN":    { "count":<int>, "bands":<1|2|3>,
//                 "codebook":[float?, x256], "files":["shN_labels.webp", "shN_centroids.webp"] }?
//   }
//
// The codebook arrays MAY contain JSON nulls — SuperSplat used to emit
// codebook[0]==null when clustering left that slot unused. Consumers must run
// SogCodebooks.PatchNullCodebook before jobs read them. We expose float? here so
// the null-vs-zero distinction survives parsing.

using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    public sealed class SogChunkMeta
    {
        public SogMeansBlock Means;
        public SogQuantBlock Scales;
        public SogQuatBlock Quats;
        public SogQuantBlock Sh0;
        public SogShNBlock  ShN;    // nullable — chunk may skip higher-order SH

        public static SogChunkMeta Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException("SogChunkMeta.Parse: input JSON is empty");

            JObject root;
            try { root = JObject.Parse(json); }
            catch (Exception ex)
            {
                throw new FormatException($"SogChunkMeta.Parse: invalid JSON — {ex.Message}", ex);
            }

            var meta = new SogChunkMeta();

            var meansTok = root["means"] as JObject
                ?? throw new FormatException("SogChunkMeta.Parse: missing 'means' block");
            meta.Means = new SogMeansBlock
            {
                Mins  = ReadVec3(meansTok["mins"] as JArray, "means.mins"),
                Maxs  = ReadVec3(meansTok["maxs"] as JArray, "means.maxs"),
                Files = ReadFiles(meansTok["files"] as JArray, "means.files"),
            };

            var scalesTok = root["scales"] as JObject
                ?? throw new FormatException("SogChunkMeta.Parse: missing 'scales' block");
            meta.Scales = new SogQuantBlock
            {
                Codebook = ReadNullableFloatArray(scalesTok["codebook"] as JArray, "scales.codebook"),
                Files    = ReadFiles(scalesTok["files"] as JArray, "scales.files"),
            };

            var quatsTok = root["quats"] as JObject
                ?? throw new FormatException("SogChunkMeta.Parse: missing 'quats' block");
            meta.Quats = new SogQuatBlock
            {
                Files = ReadFiles(quatsTok["files"] as JArray, "quats.files"),
            };

            var sh0Tok = root["sh0"] as JObject
                ?? throw new FormatException("SogChunkMeta.Parse: missing 'sh0' block");
            meta.Sh0 = new SogQuantBlock
            {
                Codebook = ReadNullableFloatArray(sh0Tok["codebook"] as JArray, "sh0.codebook"),
                Files    = ReadFiles(sh0Tok["files"] as JArray, "sh0.files"),
            };

            var shNTok = root["shN"] as JObject;
            if (shNTok != null)
            {
                int bands = (int?)shNTok["bands"] ?? 0;
                if (bands < 1 || bands > 3)
                    throw new FormatException($"SogChunkMeta.Parse: shN.bands must be 1..3 (got {bands})");
                meta.ShN = new SogShNBlock
                {
                    Count = (int?)shNTok["count"] ?? 0,
                    Bands = bands,
                    Codebook = ReadNullableFloatArray(shNTok["codebook"] as JArray, "shN.codebook"),
                    Files    = ReadFiles(shNTok["files"] as JArray, "shN.files"),
                };
            }

            return meta;
        }

        static Vector3 ReadVec3(JArray arr, string path)
        {
            if (arr == null || arr.Count < 3)
                throw new FormatException($"SogChunkMeta.Parse: expected 3-element array at '{path}'");
            return new Vector3((float)arr[0], (float)arr[1], (float)arr[2]);
        }

        static string[] ReadFiles(JArray arr, string path)
        {
            if (arr == null)
                throw new FormatException($"SogChunkMeta.Parse: missing '{path}' array");
            var files = new string[arr.Count];
            for (int i = 0; i < arr.Count; i++) files[i] = (string)arr[i];
            return files;
        }

        static float?[] ReadNullableFloatArray(JArray arr, string path)
        {
            if (arr == null) return null;
            var result = new float?[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                var tok = arr[i];
                result[i] = (tok == null || tok.Type == JTokenType.Null) ? (float?)null : (float)tok;
            }
            return result;
        }
    }

    public struct SogMeansBlock
    {
        public Vector3 Mins;
        public Vector3 Maxs;
        public string[] Files;   // typically ["means_l.webp", "means_u.webp"]
    }

    public struct SogQuantBlock
    {
        public float?[] Codebook;  // 256 entries, null slots must be patched at load
        public string[] Files;
    }

    public struct SogQuatBlock
    {
        public string[] Files;
    }

    public struct SogShNBlock
    {
        public int Count;
        public int Bands;          // 1 | 2 | 3 (=> 3/8/15 SH coeffs)
        public float?[] Codebook;
        public string[] Files;     // labels + centroid atlas

        // shCoeffs derived: {1:3, 2:8, 3:15}[Bands]. Centroid tex width MUST = 64 * shCoeffs.
        public int ShCoeffs => Bands == 1 ? 3 : (Bands == 2 ? 8 : 15);
    }
}
