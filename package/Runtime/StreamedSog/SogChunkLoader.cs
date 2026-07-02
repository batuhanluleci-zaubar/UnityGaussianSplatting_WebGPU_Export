// SPDX-License-Identifier: MIT
// Track C3b: ref-counted async chunk loader with cooldown eviction.
//
// Responsibility: turn (dirPath, IWebPDecoder) into a live SogChunkResource,
// then hand out ref-counted handles so multiple visible leaves that resolve to
// the same file share one decoded copy. Eviction is NOT LRU — it is refcount
// + a 100-frame cooldown timer so a leaf that briefly drops out of view and
// then comes back does not re-decode WebP.
//
// Threading model:
//   * LoadAsync() runs the file I/O + WebP decode + codebook patch off the
//     main thread (Task.Run). The returned Task completes on any thread; the
//     SogStreamer owns synchronising back to the main thread via ContinueWith.
//   * Acquire/Release/GetChunkResource/Tick MUST be called from the main
//     thread only. They mutate a shared Dictionary without locks by design.
//
// Cooldown semantics (mirrors PlayCanvas engine gsplat-octree.js):
//   Acquire(idx) -> ref++, remove-from-cooldown-if-present, revive resource.
//   Release(idx) -> ref--; if ref hits 0 park entry in cooldown map with
//                   cooldownFrame = currentFrame + CooldownFrames.
//   Tick()       -> increments currentFrame, drains entries whose cooldown
//                   has expired, disposes their SogChunkResource.
//
// On re-request during cooldown we revive without re-loading — this is the
// central optimisation the loader exists for.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Ref-counted async loader for SOG chunk directories. See file header for
    /// the ownership + threading contract.
    /// </summary>
    public sealed class SogChunkLoader : IDisposable
    {
        /// <summary>Default cooldown before a zero-refcount chunk is freed. Frames, not seconds.</summary>
        public const int DefaultCooldownFrames = 100;

        public int CooldownFrames { get; set; } = DefaultCooldownFrames;

        readonly string m_RootDirectory;
        readonly string[] m_Filenames;
        readonly IWebPDecoder m_Decoder;

        readonly Dictionary<int, ChunkEntry> m_Chunks = new Dictionary<int, ChunkEntry>();

        /// <summary>Monotonic frame counter driven by Tick().</summary>
        int m_CurrentFrame;

        /// <summary>
        /// Construct a loader rooted at <paramref name="rootDirectory"/>. Each entry
        /// in <paramref name="filenames"/> is treated as either an absolute path or
        /// a path relative to <paramref name="rootDirectory"/> — resolved on LoadAsync.
        /// </summary>
        public SogChunkLoader(string rootDirectory, string[] filenames, IWebPDecoder decoder)
        {
            m_RootDirectory = rootDirectory ?? string.Empty;
            m_Filenames = filenames ?? Array.Empty<string>();
            m_Decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
        }

        /// <summary>
        /// Acquire a chunk by filenames[] index. If the resource is already resident
        /// (either fully loaded or in cooldown) increments the refcount and returns
        /// synchronously; otherwise kicks off an async load. Concurrent Acquire calls
        /// for the same idx return the same task.
        /// </summary>
        public Task<SogChunkResource> AcquireAsync(int fileIdx)
        {
            if (fileIdx < 0 || fileIdx >= m_Filenames.Length)
                return Task.FromException<SogChunkResource>(
                    new ArgumentOutOfRangeException(nameof(fileIdx),
                        $"SogChunkLoader.AcquireAsync: fileIdx {fileIdx} out of range [0..{m_Filenames.Length})"));

            if (m_Chunks.TryGetValue(fileIdx, out var entry))
            {
                entry.RefCount++;
                entry.CooldownFrame = -1;   // rescue from cooldown
                if (entry.Resource != null)
                    return Task.FromResult(entry.Resource);
                return entry.LoadTask;
            }

            entry = new ChunkEntry
            {
                RefCount = 1,
                CooldownFrame = -1,
            };
            m_Chunks[fileIdx] = entry;

            string dirPath = ResolveDirectoryPath(fileIdx);
            entry.LoadTask = LoadInternalAsync(fileIdx, dirPath);
            return entry.LoadTask;
        }

        /// <summary>
        /// Decrement refcount. When it hits zero the entry moves into a cooldown
        /// window; if nothing re-acquires it before CooldownFrames elapse the
        /// resource is disposed and freed.
        /// </summary>
        public void Release(int fileIdx)
        {
            if (!m_Chunks.TryGetValue(fileIdx, out var entry))
                return;

            entry.RefCount--;
            if (entry.RefCount > 0) return;

            entry.RefCount = 0;
            entry.CooldownFrame = m_CurrentFrame + CooldownFrames;
        }

        /// <summary>
        /// Look up an already-resident resource without incrementing the refcount.
        /// Returns null if the chunk is not loaded, still loading, or has been evicted.
        /// </summary>
        public SogChunkResource GetChunkResource(int fileIdx)
        {
            if (!m_Chunks.TryGetValue(fileIdx, out var entry)) return null;
            return entry.Resource;
        }

        /// <summary>True iff fileIdx has an entry with a positive refcount and a loaded resource.</summary>
        public bool IsResident(int fileIdx)
        {
            return m_Chunks.TryGetValue(fileIdx, out var e)
                && e.RefCount > 0
                && e.Resource != null;
        }

        /// <summary>Number of chunks currently tracked (loaded + in-cooldown + in-flight).</summary>
        public int TrackedChunkCount => m_Chunks.Count;

        /// <summary>
        /// Advance the internal frame counter and evict any cooldown entries whose
        /// timer has elapsed. Call once per Update from the streamer.
        /// </summary>
        public void Tick()
        {
            m_CurrentFrame++;

            List<int> evictList = null;
            foreach (var kv in m_Chunks)
            {
                var entry = kv.Value;
                if (entry.RefCount > 0) continue;
                if (entry.CooldownFrame < 0) continue;
                if (m_CurrentFrame < entry.CooldownFrame) continue;

                (evictList ??= new List<int>()).Add(kv.Key);
            }

            if (evictList == null) return;
            for (int i = 0; i < evictList.Count; i++)
            {
                int idx = evictList[i];
                var entry = m_Chunks[idx];
                entry.Resource?.Dispose();
                m_Chunks.Remove(idx);
            }
        }

        /// <summary>Free every resident chunk. Loader is unusable afterwards.</summary>
        public void Dispose()
        {
            foreach (var kv in m_Chunks)
            {
                kv.Value.Resource?.Dispose();
            }
            m_Chunks.Clear();
        }

        // -------------------------------------------------------------------------

        string ResolveDirectoryPath(int fileIdx)
        {
            string entry = m_Filenames[fileIdx] ?? string.Empty;
            if (Path.IsPathRooted(entry)) return entry;
            return string.IsNullOrEmpty(m_RootDirectory) ? entry : Path.Combine(m_RootDirectory, entry);
        }

        async Task<SogChunkResource> LoadInternalAsync(int fileIdx, string dirPath)
        {
            // Off-thread I/O + WebP decode + codebook patch. Everything the Burst jobs
            // need is created here so the main thread only has to do the Schedule().
            SogChunkResource resource = null;
            try
            {
                resource = await Task.Run(() => LoadSync(dirPath)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Loader hangs the exception on the entry so any consumer awaiting the
                // same task sees the same failure. Remove the entry so a subsequent
                // Acquire retries from scratch.
                if (m_Chunks.TryGetValue(fileIdx, out var failed))
                {
                    failed.Resource?.Dispose();
                    m_Chunks.Remove(fileIdx);
                }
                Debug.LogError($"[SogChunkLoader] LoadAsync failed for chunk {fileIdx} at '{dirPath}': {ex.Message}");
                throw;
            }

            // Publish the resource. If the acquiring caller Release()'d during load
            // the refcount will be zero — enter cooldown immediately.
            if (!m_Chunks.TryGetValue(fileIdx, out var entry))
            {
                // Entry was evicted mid-load (should not happen with current API but
                // is defensive against future callers). Free the resource.
                resource.Dispose();
                throw new InvalidOperationException(
                    $"SogChunkLoader: chunk entry {fileIdx} disappeared during LoadAsync");
            }

            entry.Resource = resource;
            if (entry.RefCount <= 0)
            {
                entry.RefCount = 0;
                entry.CooldownFrame = m_CurrentFrame + CooldownFrames;
            }

            return resource;
        }

        /// <summary>
        /// Blocking loader — reads meta.json, decodes every referenced WebP, and
        /// patches codebooks. Runs off-thread inside <see cref="LoadInternalAsync"/>.
        /// </summary>
        SogChunkResource LoadSync(string dirPath)
        {
            if (string.IsNullOrEmpty(dirPath))
                throw new ArgumentException("SogChunkLoader.LoadSync: empty dirPath");

            string metaPath = Path.Combine(dirPath, "meta.json");
            if (!File.Exists(metaPath))
                throw new FileNotFoundException($"SogChunkLoader.LoadSync: meta.json missing at '{metaPath}'");

            var meta = SogChunkMeta.Parse(File.ReadAllText(metaPath));

            var res = new SogChunkResource
            {
                Meta = meta,
                DirectoryPath = dirPath,
            };

            // -- means_l / means_u ------------------------------------------------
            var meansFiles = meta.Means.Files;
            if (meansFiles == null || meansFiles.Length < 2)
                throw new FormatException("SogChunkLoader: means.files must have >= 2 entries (low, high)");

            DecodeWebP(dirPath, meansFiles[0], out res.MeansL, out res.MeansWidth, out res.MeansHeight);
            DecodeWebP(dirPath, meansFiles[1], out res.MeansU, out int uW, out int uH);
            if (uW != res.MeansWidth || uH != res.MeansHeight)
                throw new FormatException(
                    $"SogChunkLoader: means_l/means_u size mismatch " +
                    $"({res.MeansWidth}x{res.MeansHeight} vs {uW}x{uH})");
            res.MeansStrideBytes = 4;

            // -- quats ------------------------------------------------------------
            if (meta.Quats.Files != null && meta.Quats.Files.Length > 0)
            {
                DecodeWebP(dirPath, meta.Quats.Files[0], out res.Quats, out res.QuatsWidth, out res.QuatsHeight);
                res.QuatsStrideBytes = 4;
            }

            // -- scales -----------------------------------------------------------
            if (meta.Scales.Files != null && meta.Scales.Files.Length > 0)
            {
                DecodeWebP(dirPath, meta.Scales.Files[0], out res.Scales, out res.ScalesWidth, out res.ScalesHeight);
                res.ScalesStrideBytes = 4;
            }
            res.ScalesCodebook = PatchAndUpload(meta.Scales.Codebook, "scales.codebook");

            // -- sh0 --------------------------------------------------------------
            if (meta.Sh0.Files != null && meta.Sh0.Files.Length > 0)
            {
                DecodeWebP(dirPath, meta.Sh0.Files[0], out res.Sh0, out res.Sh0Width, out res.Sh0Height);
                res.Sh0StrideBytes = 4;
            }
            res.Sh0Codebook = PatchAndUpload(meta.Sh0.Codebook, "sh0.codebook");

            // -- shN (optional) ---------------------------------------------------
            // SogShNBlock is a value type; a chunk without shN parses to default(SogShNBlock)
            // where Bands == 0 and Files == null. Either signals "no higher-order SH".
            if (meta.ShN.Bands > 0 && meta.ShN.Files != null && meta.ShN.Files.Length >= 2)
            {
                DecodeWebP(dirPath, meta.ShN.Files[0], out var labels, out res.ShNLabelsWidth, out res.ShNLabelsHeight);
                // Split labels RGBA (R=lo, G=hi) into two byte arrays for the Burst job.
                SplitLabelsChannels(labels, res.ShNLabelsWidth * res.ShNLabelsHeight,
                    out res.ShNLabelsLo, out res.ShNLabelsHi);
                labels.Dispose();

                DecodeWebP(dirPath, meta.ShN.Files[1], out var centroidsRgba, out res.ShNCentroidsWidth, out res.ShNCentroidsHeight);
                // centroid atlas is single-channel — we only need the R byte per pixel.
                ExtractRedChannel(centroidsRgba, res.ShNCentroidsWidth * res.ShNCentroidsHeight,
                    out res.ShNCentroids);
                centroidsRgba.Dispose();

                res.ShNCodebook = PatchAndUpload(meta.ShN.Codebook, "shN.codebook");
                res.HasShN = true;
            }

            return res;
        }

        void DecodeWebP(string dirPath, string relativeFile,
            out NativeArray<byte> rgba, out int width, out int height)
        {
            string full = Path.Combine(dirPath, relativeFile);
            if (!File.Exists(full))
                throw new FileNotFoundException($"SogChunkLoader: expected WebP '{full}' but it is missing");

            byte[] managed = File.ReadAllBytes(full);
            var encoded = new NativeArray<byte>(managed.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                NativeArray<byte>.Copy(managed, encoded, managed.Length);
                if (!m_Decoder.Decode(encoded, Allocator.Persistent, out rgba, out width, out height))
                    throw new InvalidOperationException($"SogChunkLoader: WebP decode returned false for '{full}'");
            }
            finally
            {
                encoded.Dispose();
            }
        }

        static void SplitLabelsChannels(NativeArray<byte> rgba, int pixelCount,
            out NativeArray<byte> lo, out NativeArray<byte> hi)
        {
            lo = new NativeArray<byte>(pixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            hi = new NativeArray<byte>(pixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < pixelCount; i++)
            {
                int off = i * 4;
                lo[i] = rgba[off + 0];   // R channel = label low byte
                hi[i] = rgba[off + 1];   // G channel = label high byte
            }
        }

        static void ExtractRedChannel(NativeArray<byte> rgba, int pixelCount, out NativeArray<byte> r)
        {
            r = new NativeArray<byte>(pixelCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < pixelCount; i++)
                r[i] = rgba[i * 4];
        }

        /// <summary>
        /// Patches null slots via SogCodebooks.PatchNullCodebook (if available) and
        /// uploads the 256-float codebook into a Persistent NativeArray so Burst
        /// jobs can read it. Missing codebook -> empty NativeArray (zero-length).
        /// </summary>
        static NativeArray<float> PatchAndUpload(float?[] codebook, string label)
        {
            if (codebook == null || codebook.Length == 0)
                return default;

            if (codebook.Length != 256)
                Debug.LogWarning(
                    $"[SogChunkLoader] {label} has {codebook.Length} entries (expected 256); " +
                    "runtime may misindex.");

            // SuperSplat pre-2025 null-patch: if slot 0 is null, extrapolate from slots 1 and 255.
            // We inline the patch here so we do not require SogCodebooks.PatchNullCodebook
            // (which is added in a sibling C2 patch) to already be compiled.
            var patched = new float[codebook.Length];
            for (int i = 0; i < codebook.Length; i++)
                patched[i] = codebook[i].GetValueOrDefault(0f);

            if (codebook.Length > 255 && !codebook[0].HasValue
                && codebook[1].HasValue && codebook[255].HasValue)
            {
                float c1 = codebook[1].Value;
                float c255 = codebook[255].Value;
                patched[0] = c1 + (c1 - c255) / 255f;
            }

            var na = new NativeArray<float>(patched.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            NativeArray<float>.Copy(patched, na, patched.Length);
            return na;
        }

        // -------------------------------------------------------------------------

        sealed class ChunkEntry
        {
            public int RefCount;

            /// <summary>Frame the entry becomes eligible for eviction, or -1 while live.</summary>
            public int CooldownFrame;

            public Task<SogChunkResource> LoadTask;
            public SogChunkResource Resource;
        }
    }
}
