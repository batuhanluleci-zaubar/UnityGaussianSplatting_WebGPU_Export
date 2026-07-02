// SPDX-License-Identifier: MIT
// Track C4: public entry point for SOG reader — LoadManifest + ReadLeafLod.
//
// SogReader bridges the parsed lod-meta.json + flat-leaf array (built by
// SogLodMeta.Parse + SogKdTree.FlattenAtLoad) to the higher-level streamer
// consumed by GaussianLodStreamAsync. It is the SPZ<->SOG parity surface:
// GaussianLodStreamAsync only ever calls the small factory + LoadManifest +
// ReadLeafLod triple and never touches the chunk decoder jobs directly.
//
// Responsibilities:
//   * LoadManifest(path):
//       Accepts either a directory containing lod-meta.json OR the
//       lod-meta.json path itself. Reads + parses the file, flattens the
//       kd-tree into a NativeArray<SogLeafNode> and returns a SogManifest
//       handle bundling parsed meta + flat leaves + root dir. The manifest
//       owns the NativeArray; call Dispose() at teardown.
//
//   * ReadLeafLod(manifest, leafIdx, lodIdx, decoder):
//       Ensures the (offset,count) slice of the referenced chunk is resident
//       (async via SogChunkLoader), then schedules the five Burst decode jobs
//       (means / quats / scales / sh0 / shN when present) writing into a
//       fresh NativeArray<InputSplatData>. Returns the buffer to the caller,
//       who owns it and Dispose()s when the derived GaussianSplatAsset is
//       built. First call for a chunk pays the WebP decode; subsequent calls
//       for other LODs of the same chunk reuse the cached resource via the
//       loader's refcount.
//
// Auto-detect helper: SogReader.IsSogPath returns true when the caller-supplied
// path ends with ".sog" OR is a directory containing lod-meta.json. Anything
// else (bare .spz blob, a manifest.json legacy path) returns false and the
// GaussianLodStreamAsync factory routes to the SPZ Addressables path.

using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Runtime handle to a parsed lod-meta.json + flattened leaf array. Owns the
    /// NativeArray of leaves; callers must Dispose() at teardown.
    /// </summary>
    public sealed class SogManifest : IDisposable
    {
        /// <summary>Parsed manifest from lod-meta.json.</summary>
        public SogLodMeta Meta;

        /// <summary>Root directory where the .sog contents live (parent of lod-meta.json).</summary>
        public string RootDirectory;

        /// <summary>Flat leaf array produced by SogKdTree.FlattenAtLoad. Persistent-allocated.</summary>
        public NativeArray<SogLeafNode> Leaves;

        /// <summary>Number of LOD ranks (mirrors SogLodMeta.LodLevels for convenience).</summary>
        public int LodLevels;

        /// <summary>Absolute filesystem paths for each entry in Meta.Filenames — resolved once at load.</summary>
        public string[] ChunkDirectories;

        public void Dispose()
        {
            if (Leaves.IsCreated) Leaves.Dispose();
            Leaves = default;
            // Meta + ChunkDirectories are managed and let go with the GC.
        }
    }

    /// <summary>
    /// Public SOG reader entry points used by GaussianLodStreamAsync. This class
    /// is intentionally stateless — it composes SogLodMeta + SogKdTree +
    /// SogChunkLoader + the Burst decoders without holding any state of its own.
    /// </summary>
    public static class SogReader
    {
        /// <summary>
        /// Returns true when <paramref name="path"/> looks like a SOG asset:
        /// either ends in <c>.sog</c> or is a directory that contains a
        /// <c>lod-meta.json</c> file. Anything else (bare <c>.spz</c> blob,
        /// legacy <c>manifest.json</c> path) returns false.
        /// </summary>
        public static bool IsSogPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            // .sog suffix — treat as a directory or archive named .sog.
            if (path.EndsWith(".sog", StringComparison.OrdinalIgnoreCase)) return true;
            // Directory containing lod-meta.json is unambiguously SOG.
            if (Directory.Exists(path) && File.Exists(Path.Combine(path, "lod-meta.json"))) return true;
            // Direct path to a lod-meta.json.
            if (File.Exists(path) &&
                string.Equals(Path.GetFileName(path), "lod-meta.json", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// Load + flatten a SOG manifest. Accepts either a directory (contents
        /// must include <c>lod-meta.json</c>) or the lod-meta.json path directly.
        /// Throws <see cref="FileNotFoundException"/> if the manifest cannot be
        /// located and <see cref="FormatException"/> on schema errors.
        /// </summary>
        public static SogManifest LoadManifest(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("SogReader.LoadManifest: path is null/empty", nameof(path));

            string metaPath;
            string rootDir;
            if (File.Exists(path) &&
                string.Equals(Path.GetFileName(path), "lod-meta.json", StringComparison.OrdinalIgnoreCase))
            {
                metaPath = path;
                rootDir  = Path.GetDirectoryName(path) ?? string.Empty;
            }
            else if (Directory.Exists(path))
            {
                metaPath = Path.Combine(path, "lod-meta.json");
                rootDir  = path;
                if (!File.Exists(metaPath))
                    throw new FileNotFoundException(
                        $"SogReader.LoadManifest: '{path}' has no lod-meta.json", metaPath);
            }
            else
            {
                throw new FileNotFoundException(
                    $"SogReader.LoadManifest: '{path}' is neither a lod-meta.json file nor a directory", path);
            }

            string json = File.ReadAllText(metaPath);
            SogLodMeta meta = SogLodMeta.Parse(json);
            NativeArray<SogLeafNode> leaves = SogKdTree.FlattenAtLoad(meta, Allocator.Persistent);

            // Resolve chunk directories up-front so per-leaf reads never have to
            // touch Path.Combine on the hot path.
            string[] chunkDirs = null;
            if (meta.Filenames != null)
            {
                chunkDirs = new string[meta.Filenames.Length];
                for (int i = 0; i < meta.Filenames.Length; i++)
                {
                    string fn = meta.Filenames[i] ?? string.Empty;
                    chunkDirs[i] = Path.IsPathRooted(fn) ? fn : Path.Combine(rootDir, fn);
                }
            }

            return new SogManifest
            {
                Meta = meta,
                RootDirectory = rootDir,
                Leaves = leaves,
                LodLevels = meta.LodLevels,
                ChunkDirectories = chunkDirs,
            };
        }

        /// <summary>
        /// Read the (leafIdx, lodIdx) slice out of the SOG asset, returning a
        /// freshly-allocated NativeArray&lt;InputSplatData&gt; that the caller
        /// owns. Ensures the referenced chunk is resident via the supplied
        /// <paramref name="loader"/> (or a scratch loader when <c>null</c>) and
        /// then schedules the Burst decode jobs for the (offset, count) window.
        /// </summary>
        /// <param name="manifest">Manifest returned by <see cref="LoadManifest"/>.</param>
        /// <param name="leafIdx">Index into <see cref="SogManifest.Leaves"/>.</param>
        /// <param name="lodIdx">LOD rank (0 = finest .. LodCount-1 = coarsest for this leaf).</param>
        /// <param name="decoder">WebP decoder implementation.</param>
        /// <param name="loader">Optional shared chunk loader. When null, a
        /// throw-away loader is created for this single read (used by the unit
        /// tests and one-shot editor tools).</param>
        public static async Task<NativeArray<InputSplatData>> ReadLeafLod(
            SogManifest manifest, int leafIdx, int lodIdx,
            IWebPDecoder decoder, SogChunkLoader loader = null)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (!manifest.Leaves.IsCreated || manifest.Leaves.Length == 0)
                throw new InvalidOperationException("SogReader.ReadLeafLod: manifest.Leaves is empty");
            if (leafIdx < 0 || leafIdx >= manifest.Leaves.Length)
                throw new ArgumentOutOfRangeException(nameof(leafIdx),
                    $"SogReader.ReadLeafLod: leafIdx {leafIdx} out of range [0..{manifest.Leaves.Length})");

            SogLeafNode leaf = manifest.Leaves[leafIdx];
            if (lodIdx < 0 || lodIdx >= leaf.LodCount)
                throw new ArgumentOutOfRangeException(nameof(lodIdx),
                    $"SogReader.ReadLeafLod: lodIdx {lodIdx} out of range [0..{leaf.LodCount})");

            int fileIdx;
            int offset;
            int count;
            unsafe
            {
                fileIdx = leaf.LodFileIdx[lodIdx];
                offset  = leaf.LodOffset[lodIdx];
                count   = leaf.LodSplatCount[lodIdx];
            }
            if (count <= 0)
                return new NativeArray<InputSplatData>(0, Allocator.Persistent);

            // Ensure the chunk is resident. Own the loader if the caller did
            // not pass one — the unit-test path uses this.
            bool ownsLoader = false;
            if (loader == null)
            {
                loader = new SogChunkLoader(manifest.RootDirectory, manifest.Meta.Filenames, decoder);
                ownsLoader = true;
            }

            SogChunkResource resource;
            try
            {
                resource = await loader.AcquireAsync(fileIdx).ConfigureAwait(true);
            }
            catch
            {
                if (ownsLoader) loader.Dispose();
                throw;
            }
            if (resource == null)
            {
                if (ownsLoader) loader.Dispose();
                throw new InvalidOperationException(
                    $"SogReader.ReadLeafLod: chunk {fileIdx} resource is null after AcquireAsync");
            }

            var output = new NativeArray<InputSplatData>(count, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            try
            {
                DecodeSlice(resource, offset, count, output);
            }
            catch
            {
                output.Dispose();
                if (ownsLoader)
                {
                    loader.Release(fileIdx);
                    loader.Dispose();
                }
                throw;
            }

            // The scratch-loader path releases the chunk immediately — production
            // callers pass a shared loader and manage ref lifetime themselves.
            if (ownsLoader)
            {
                loader.Release(fileIdx);
                loader.Dispose();
            }
            return output;
        }

        // ---------------------------------------------------------------------

        /// <summary>
        /// Track C4b: public shim so <see cref="GsplatLod.GaussianLodStreamAsync"/> can
        /// drive the same Burst decode pipeline as <see cref="ReadLeafLod"/> without
        /// having to await the loader (it already ensured residency and holds the
        /// refcount). Caller owns <paramref name="output"/> and MUST size it to
        /// <paramref name="count"/> splats. Throws if the resource is missing
        /// required buffers (means_l/u, meta) rather than silently producing
        /// zero-splat output.
        /// </summary>
        public static void DecodeChunkSliceForStreamer(
            SogChunkResource resource, int offset, int count,
            NativeArray<InputSplatData> output)
        {
            if (resource == null)
                throw new ArgumentNullException(nameof(resource));
            if (offset < 0 || count < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(offset), $"SogReader.DecodeChunkSliceForStreamer: offset {offset} count {count} must be non-negative");
            if (!output.IsCreated || output.Length < count)
                throw new ArgumentException(
                    $"SogReader.DecodeChunkSliceForStreamer: output NativeArray must be created with length >= {count} (has {output.Length}).",
                    nameof(output));

            DecodeSlice(resource, offset, count, output);
        }

        /// <summary>
        /// Schedules the five Burst decode jobs (means / quats / scales / sh0
        /// and, when present, shN) writing splat rows into <paramref name="output"/>.
        /// The caller owns <paramref name="output"/> and passes count = slice length.
        /// The <paramref name="offset"/> is the splat index INSIDE the chunk.
        /// </summary>
        static void DecodeSlice(SogChunkResource resource, int offset, int count,
            NativeArray<InputSplatData> output)
        {
            if (resource.Meta == null)
                throw new InvalidOperationException("SogReader.DecodeSlice: resource.Meta is null");

            // The decoder jobs are per-splat and index into the chunk arrays as
            // (offset + i). To decode a mid-chunk slice we build tiny slice
            // NativeArrays over the raw byte NativeArrays and pass splatOffset=0
            // on the write side (output is exactly `count` splats).
            var meansL = SliceBytes(resource.MeansL, offset * resource.MeansStrideBytes,
                                    count * resource.MeansStrideBytes);
            var meansU = SliceBytes(resource.MeansU, offset * resource.MeansStrideBytes,
                                    count * resource.MeansStrideBytes);

            JobHandle prev = default;

            // Means / positions -----------------------------------------------
            prev = new DecodeMeansJob
            {
                meansL = meansL,
                meansU = meansU,
                mins   = new Unity.Mathematics.float3(
                            resource.Meta.Means.Mins.x,
                            resource.Meta.Means.Mins.y,
                            resource.Meta.Means.Mins.z),
                maxs   = new Unity.Mathematics.float3(
                            resource.Meta.Means.Maxs.x,
                            resource.Meta.Means.Maxs.y,
                            resource.Meta.Means.Maxs.z),
                strideBytes = resource.MeansStrideBytes,
                splatOffset = 0,
                output = output,
            }.Schedule(count, 512, prev);

            // Quats ------------------------------------------------------------
            if (resource.Quats.IsCreated && resource.QuatsStrideBytes > 0)
            {
                var quats = SliceBytes(resource.Quats, offset * resource.QuatsStrideBytes,
                                        count * resource.QuatsStrideBytes);
                prev = new DecodeQuatsJob
                {
                    quats = quats,
                    splatOffset = 0,
                    output = output,
                }.Schedule(count, 512, prev);
            }

            // Scales -----------------------------------------------------------
            if (resource.Scales.IsCreated && resource.ScalesCodebook.IsCreated)
            {
                // Scales layout is 3 bytes per splat (x,y,z) — stride is fixed
                // at 3 regardless of ScalesStrideBytes (RGBA vs RGB pack was
                // already dealt with by the loader on the codebook side).
                int scaleStride = 3;
                var scales = SliceBytes(resource.Scales, offset * scaleStride, count * scaleStride);
                prev = new DecodeScalesJob
                {
                    scales   = scales,
                    codebook = resource.ScalesCodebook,
                    output   = output,
                    splatOffset = 0,
                }.Schedule(count, 512, prev);
            }

            // sh0 (DC + opacity) -----------------------------------------------
            if (resource.Sh0.IsCreated && resource.Sh0Codebook.IsCreated)
            {
                // 4 bytes per splat (r,g,b,a).
                var sh0 = SliceBytes(resource.Sh0, offset * 4, count * 4);
                prev = new DecodeSh0Job
                {
                    sh0      = sh0,
                    codebook = resource.Sh0Codebook,
                    output   = output,
                    splatOffset = 0,
                    storeAsLogit = true,
                }.Schedule(count, 512, prev);
            }

            // shN (VQ, optional) -----------------------------------------------
            if (resource.HasShN && resource.ShNLabelsLo.IsCreated && resource.ShNLabelsHi.IsCreated
                && resource.ShNCentroids.IsCreated && resource.ShNCodebook.IsCreated
                && resource.Meta.ShN.Bands > 0)
            {
                int bands = resource.Meta.ShN.Bands;
                bool centroidsOk = SogDecoderConstants.ValidateCentroidWidth(
                    bands, resource.ShNCentroidsWidth, resource.DirectoryPath);
                if (centroidsOk)
                {
                    var lo = SliceBytes(resource.ShNLabelsLo, offset, count);
                    var hi = SliceBytes(resource.ShNLabelsHi, offset, count);
                    prev = new DecodeShNJob
                    {
                        labelsLo = lo,
                        labelsHi = hi,
                        centroids = resource.ShNCentroids,
                        centroidWidth = resource.ShNCentroidsWidth,
                        bands = bands,
                        codebook = resource.ShNCodebook,
                        splatOffset = 0,
                        output = output,
                    }.Schedule(count, 512, prev);
                }
            }

            prev.Complete();
        }

        /// <summary>
        /// Cheap NativeArray slice using GetSubArray. Returns default when the
        /// source is not created or the requested window is out of range —
        /// callers check IsCreated before scheduling.
        /// </summary>
        static NativeArray<byte> SliceBytes(NativeArray<byte> source, int start, int length)
        {
            if (!source.IsCreated || length <= 0) return default;
            if (start < 0 || start + length > source.Length)
                throw new ArgumentOutOfRangeException(nameof(start),
                    $"SogReader.SliceBytes: window [{start}..{start + length}) out of source length {source.Length}");
            return source.GetSubArray(start, length);
        }
    }

    /// <summary>
    /// Cross-format read surface. Both SPZ and SOG paths implement this via
    /// thin adapters so <see cref="GsplatLod"/> code stays format-agnostic.
    /// Currently used as a marker interface — the factory returns a concrete
    /// reader-token by discriminated union rather than polymorphic dispatch
    /// (see GaussianLodStreamAsync.StreamFormat + AutoDetectFormat).
    /// </summary>
    public interface ISplatChunkReader
    {
        /// <summary>Kind of underlying asset for diagnostics/logging.</summary>
        string FormatName { get; }
    }

    /// <summary>Thin adapter around <see cref="SogManifest"/> for the shared reader interface.</summary>
    public sealed class SogChunkReader : ISplatChunkReader, IDisposable
    {
        public SogManifest Manifest { get; }
        public string FormatName => "SOG";

        public SogChunkReader(SogManifest manifest)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        }

        public void Dispose() => Manifest.Dispose();
    }

    /// <summary>Placeholder marker so the SPZ path also satisfies <see cref="ISplatChunkReader"/>.</summary>
    public sealed class SpzChunkReader : ISplatChunkReader
    {
        public string FormatName => "SPZ";
        public string ManifestPath { get; }
        public SpzChunkReader(string manifestPath) { ManifestPath = manifestPath; }
    }

#if UNITY_EDITOR
    /// <summary>
    /// C4 bonus: hand-crafted synthetic SOG manifest self-test. Exercises
    /// SogLodMeta.Parse + SogKdTree.FlattenAtLoad end-to-end (no WebP decode)
    /// so the parse pipeline is smoke-tested without needing an actual asset.
    /// Wired via a Unity menu item — logs pass/fail via Debug.Log/LogError.
    /// </summary>
    public static class SogSyntheticParseTest
    {
        [UnityEditor.MenuItem("Tools/GaussianSplatting/Test/Synthetic SOG Parse")]
        public static void Run()
        {
            // Hand-crafted 2-leaf tree: root is interior with 2 leaves, each
            // exposes 2 LOD ranks. This is the smallest structure that
            // exercises interior parsing, binary-split rule, LOD dict parse,
            // filenames index, and FlattenAtLoad's ordering.
            const string kJson = @"{
              ""version"": 1,
              ""count"": 1000,
              ""counts"": [400, 600],
              ""lodLevels"": 2,
              ""environment"": null,
              ""filenames"": [""chunk_0"", ""chunk_1""],
              ""tree"": {
                ""bound"": { ""min"": [-1.0, -1.0, -1.0], ""max"": [1.0, 1.0, 1.0] },
                ""children"": [
                  {
                    ""bound"": { ""min"": [-1.0, -1.0, -1.0], ""max"": [0.0, 1.0, 1.0] },
                    ""lods"": {
                      ""0"": { ""file"": 0, ""offset"": 0,   ""count"": 300 },
                      ""1"": { ""file"": 0, ""offset"": 300, ""count"": 100 }
                    }
                  },
                  {
                    ""bound"": { ""min"": [0.0, -1.0, -1.0], ""max"": [1.0, 1.0, 1.0] },
                    ""lods"": {
                      ""0"": { ""file"": 1, ""offset"": 0,   ""count"": 400 },
                      ""1"": { ""file"": 1, ""offset"": 400, ""count"": 200 }
                    }
                  }
                ]
              }
            }";

            // Optional: a fake per-chunk meta.json string that exercises the
            // SogChunkMeta parser (means/scales/quats/sh0/shN). No WebP decode
            // happens — this stops after Parse succeeds.
            const string kChunkJson = @"{
              ""means"":  { ""mins"":[-1,-1,-1], ""maxs"":[1,1,1], ""files"":[""means_l.webp"",""means_u.webp""] },
              ""scales"": { ""codebook"":[null, 0.1, 0.2, 0.3], ""files"":[""scales.webp""] },
              ""quats"":  { ""files"":[""quats.webp""] },
              ""sh0"":    { ""codebook"":[null, 0.5, 0.4, 0.3], ""files"":[""sh0.webp""] },
              ""shN"":    { ""count"":1000, ""bands"":1, ""codebook"":[null, 0.01, 0.02], ""files"":[""shN_labels.webp"",""shN_centroids.webp""] }
            }";

            bool pass = true;
            string failure = null;

            try
            {
                var meta = SogLodMeta.Parse(kJson);
                if (meta.Version != 1)  { pass = false; failure = $"version {meta.Version} != 1"; }
                if (meta.LodLevels != 2) { pass = false; failure = $"lodLevels {meta.LodLevels} != 2"; }
                if (meta.Counts == null || meta.Counts.Length != 2)
                { pass = false; failure = $"counts.Length {meta.Counts?.Length} != 2"; }
                if (meta.Filenames == null || meta.Filenames.Length != 2)
                { pass = false; failure = $"filenames.Length {meta.Filenames?.Length} != 2"; }
                if (meta.Tree == null || meta.Tree.Children == null || meta.Tree.Children.Length != 2)
                { pass = false; failure = "root tree is not a binary interior"; }

                var leaves = SogKdTree.FlattenAtLoad(meta, Allocator.Temp);
                try
                {
                    if (leaves.Length != 2)
                    { pass = false; failure = $"flattened leaves.Length {leaves.Length} != 2"; }
                    else
                    {
                        unsafe
                        {
                            var l0 = leaves[0];
                            var l1 = leaves[1];
                            if (l0.LodCount != 2 || l1.LodCount != 2)
                            { pass = false; failure = $"leaf LodCount {l0.LodCount}/{l1.LodCount} != 2"; }
                            if (l0.LodFileIdx[0] != 0 || l0.LodFileIdx[1] != 0)
                            { pass = false; failure = "leaf0 fileIdx != 0"; }
                            if (l1.LodFileIdx[0] != 1 || l1.LodFileIdx[1] != 1)
                            { pass = false; failure = "leaf1 fileIdx != 1"; }
                            if (l0.LodSplatCount[0] != 300 || l0.LodSplatCount[1] != 100)
                            { pass = false; failure = "leaf0 splat counts wrong"; }
                            if (l1.LodSplatCount[0] != 400 || l1.LodSplatCount[1] != 200)
                            { pass = false; failure = "leaf1 splat counts wrong"; }
                            if (l0.LodOffset[1] != 300 || l1.LodOffset[1] != 400)
                            { pass = false; failure = "leaf offsets wrong"; }
                        }
                    }
                }
                finally { leaves.Dispose(); }

                var chunkMeta = SogChunkMeta.Parse(kChunkJson);
                if (chunkMeta.Means.Files == null || chunkMeta.Means.Files.Length != 2)
                { pass = false; failure = "chunk means.files bad"; }
                if (chunkMeta.ShN.Bands != 1) { pass = false; failure = $"chunk shN.bands {chunkMeta.ShN.Bands} != 1"; }
                if (chunkMeta.Scales.Codebook == null || chunkMeta.Scales.Codebook.Length < 1
                    || chunkMeta.Scales.Codebook[0].HasValue)
                { pass = false; failure = "chunk scales.codebook[0] should have been null (pre-patch)"; }
            }
            catch (Exception ex)
            {
                pass = false;
                failure = $"exception: {ex.GetType().Name} — {ex.Message}";
            }

            if (pass) Debug.Log("[SogSyntheticParseTest] PASS — lod-meta + chunk meta parse + flatten end-to-end.");
            else      Debug.LogError($"[SogSyntheticParseTest] FAIL — {failure}");
        }
    }
#endif
}
