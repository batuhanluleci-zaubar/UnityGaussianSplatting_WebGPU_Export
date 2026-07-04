// SPDX-License-Identifier: MIT
// Track C3b: decoded chunk resource holder.
//
// SogChunkResource owns the RGBA/plane byte buffers produced by decoding one
// on-disk .sog chunk (one directory containing meta.json + N *.webp files).
// The Burst decode jobs (DecodeMeansJob, DecodeQuatsJob, DecodeShNJob, ...)
// read directly out of the NativeArrays held here — no extra copy.
//
// Ownership contract:
//   * SogChunkResource is IDisposable. Dispose() frees every non-default
//     NativeArray it holds. Callers MUST NOT read the NativeArrays after
//     Dispose has run.
//   * SogChunkLoader is the sole allocator/deallocator. Consumers get a
//     SogChunkResource handle back from GetChunkResource(fileIdx) but MUST
//     go through the loader's refcount API rather than calling Dispose
//     directly, so cooldown/revive works correctly.
//
// Attribute layout follows SogChunkMeta:
//   means_l   — low  bytes of split-byte position, RGBA8 (3 channels used)
//   means_u   — high bytes of split-byte position, RGBA8 (3 channels used)
//   scales    — one byte per axis, codebook-indexed
//   quats     — smallest-three + 2-bit mode, RGBA8 (4 channels used)
//   sh0       — DC term, 4 channels (r,g,b,alpha)
//   shN_labels    — 2 bytes per splat (label lo/hi)  — optional
//   shN_centroids — RGB triplet per atlas pixel (3 bytes after load) — optional
//
// Widths/heights per attribute mirror what the WebP decoder reported so the
// Burst jobs can index (rowStride = width * strideBytes) without re-parsing.

using System;
using Unity.Collections;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Decoded per-chunk buffers plus the parsed meta.json that produced them.
    /// Held by SogChunkLoader; ref-counted and eventually disposed when the
    /// cooldown timer runs out. See file header for the ownership contract.
    /// </summary>
    public sealed class SogChunkResource : IDisposable
    {
        // ---- Parsed metadata ----------------------------------------------------

        /// <summary>Parsed per-chunk meta.json. Never null after a successful load.</summary>
        public SogChunkMeta Meta;

        /// <summary>Filesystem directory this chunk was loaded from. Kept for diagnostics/logs.</summary>
        public string DirectoryPath;

        // ---- Position (split-byte) ---------------------------------------------

        public NativeArray<byte> MeansL;
        public NativeArray<byte> MeansU;
        public int MeansWidth;
        public int MeansHeight;
        /// <summary>Bytes per pixel in <see cref="MeansL"/>/<see cref="MeansU"/>. Typically 4 (RGBA8).</summary>
        public int MeansStrideBytes;

        // ---- Quaternions --------------------------------------------------------

        public NativeArray<byte> Quats;
        public int QuatsWidth;
        public int QuatsHeight;
        public int QuatsStrideBytes;

        // ---- Scales -------------------------------------------------------------

        public NativeArray<byte> Scales;
        public int ScalesWidth;
        public int ScalesHeight;
        public int ScalesStrideBytes;

        // ---- SH0 (DC term) ------------------------------------------------------

        public NativeArray<byte> Sh0;
        public int Sh0Width;
        public int Sh0Height;
        public int Sh0StrideBytes;

        // ---- ShN (VQ, optional) -------------------------------------------------

        /// <summary>True when the chunk carries higher-order SH; false when only sh0 is available.</summary>
        public bool HasShN;

        public NativeArray<byte> ShNLabelsLo;
        public NativeArray<byte> ShNLabelsHi;
        public int ShNLabelsWidth;
        public int ShNLabelsHeight;

        public NativeArray<byte> ShNCentroids;
        public int ShNCentroidsWidth;
        public int ShNCentroidsHeight;

        // ---- Patched codebooks (float NativeArrays, ready for Burst) -----------

        public NativeArray<float> ScalesCodebook;
        public NativeArray<float> Sh0Codebook;
        public NativeArray<float> ShNCodebook;

        public void Dispose()
        {
            SafeDispose(ref MeansL);
            SafeDispose(ref MeansU);
            SafeDispose(ref Quats);
            SafeDispose(ref Scales);
            SafeDispose(ref Sh0);
            SafeDispose(ref ShNLabelsLo);
            SafeDispose(ref ShNLabelsHi);
            SafeDispose(ref ShNCentroids);

            SafeDisposeFloat(ref ScalesCodebook);
            SafeDisposeFloat(ref Sh0Codebook);
            SafeDisposeFloat(ref ShNCodebook);
        }

        static void SafeDispose(ref NativeArray<byte> arr)
        {
            if (arr.IsCreated) arr.Dispose();
            arr = default;
        }

        static void SafeDisposeFloat(ref NativeArray<float> arr)
        {
            if (arr.IsCreated) arr.Dispose();
            arr = default;
        }
    }
}
