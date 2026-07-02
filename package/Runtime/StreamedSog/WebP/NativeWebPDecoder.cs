// SPDX-License-Identifier: MIT
// Track C4b: real libwebp-backed decoder replacing the C1 stub.
//
// Backed by com.netpyoung.webp (OpenUPM 0.3.22). We deliberately bypass
// Texture2DExt.LoadRGBAFromWebP because that helper Y-flips the output
// (negative stride into a bottom-anchored buffer) — SOG codebook-indexed
// atlases must land in row-0-first layout so pixel (0,0) is splat 0.
// So we go straight to WebPDecodeRGBAInto with positive stride and decode
// directly into a caller-owned NativeArray<byte>.
//
// Threading: NativeLibwebp is thread-safe (libwebp is). LoadInternalAsync
// on SogChunkLoader runs this off the main thread; Probe() is main-thread.
//
// Fail-loud gate: Probe() catches the two exception shapes that surface a
// missing native binary — DllNotFoundException (Unity/Mono probe failed to
// find webp.bundle/libwebp.so/etc.) and EntryPointNotFoundException (bundle
// loaded but symbol layout mismatch). Any other exception is genuine bad
// input, which is exactly what a good probe should NOT swallow — those
// escape so the caller sees them.

using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using unity.libwebp;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// libwebp-backed <see cref="IWebPDecoder"/> for SOG chunk atlases.
    /// Uses com.netpyoung.webp's <c>NativeLibwebp</c> P/Invoke surface.
    /// </summary>
    public sealed unsafe class NativeWebPDecoder : IWebPDecoder
    {
        /// <summary>Cached probe result so we only exercise libwebp once per process.</summary>
        static bool s_ProbedOnce;
        static bool s_ProbeResult;

        /// <summary>
        /// Convenience shape used by higher-level callers (managed byte[]) — takes a
        /// managed WebP payload, returns a tightly-packed RGBA8 NativeArray with the
        /// given allocator. Width/height are queried from the decoded output. Throws
        /// on failure so the caller learns fast when a chunk file is malformed.
        /// </summary>
        public NativeArray<byte> Decode(byte[] webpBytes, Allocator alloc)
        {
            if (webpBytes == null) throw new ArgumentNullException(nameof(webpBytes));
            if (webpBytes.Length == 0) throw new ArgumentException("Empty WebP payload", nameof(webpBytes));

            int width, height;
            fixed (byte* srcPtr = webpBytes)
            {
                if (NativeLibwebp.WebPGetInfo(srcPtr, (UIntPtr)webpBytes.Length, &width, &height) == 0)
                    throw new FormatException("NativeWebPDecoder.Decode: WebPGetInfo failed (invalid WebP header)");
            }

            long byteCount = (long)width * height * 4;
            if (byteCount <= 0 || byteCount > int.MaxValue)
                throw new FormatException($"NativeWebPDecoder.Decode: implausible dimensions {width}x{height}");

            var rgba = new NativeArray<byte>((int)byteCount, alloc, NativeArrayOptions.UninitializedMemory);
            try
            {
                byte* dstPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(rgba);
                DecodeInto(webpBytes, dstPtr, (int)byteCount, width, height);
            }
            catch
            {
                rgba.Dispose();
                throw;
            }
            return rgba;
        }

        /// <summary>
        /// Existing IWebPDecoder surface used by SogChunkLoader — takes an already-native
        /// encoded buffer, allocates the RGBA output, returns width/height. Returns false
        /// only for the "invalid header" case that the loader treats as a soft failure;
        /// everything else surfaces as an exception so failures do not go silent.
        /// </summary>
        public bool Decode(
            NativeArray<byte> encoded,
            Allocator outputAllocator,
            out NativeArray<byte> rgba,
            out int width,
            out int height)
        {
            rgba = default;
            width = 0;
            height = 0;

            if (!encoded.IsCreated || encoded.Length == 0)
                throw new ArgumentException("NativeWebPDecoder.Decode: empty encoded buffer");

            byte* srcPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(encoded);
            int w, h;
            if (NativeLibwebp.WebPGetInfo(srcPtr, (UIntPtr)encoded.Length, &w, &h) == 0)
                return false;

            long byteCount = (long)w * h * 4;
            if (byteCount <= 0 || byteCount > int.MaxValue)
                throw new FormatException($"NativeWebPDecoder.Decode: implausible dimensions {w}x{h}");

            rgba = new NativeArray<byte>((int)byteCount, outputAllocator, NativeArrayOptions.UninitializedMemory);
            try
            {
                int stride = 4 * w;
                byte* dstPtr = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(rgba);
                byte* result = NativeLibwebp.WebPDecodeRGBAInto(
                    srcPtr, (UIntPtr)encoded.Length,
                    dstPtr, (UIntPtr)byteCount, stride);
                if (result == null)
                    throw new InvalidOperationException("NativeWebPDecoder.Decode: WebPDecodeRGBAInto returned NULL");
            }
            catch
            {
                rgba.Dispose();
                rgba = default;
                throw;
            }

            width = w;
            height = h;
            return true;
        }

        /// <summary>
        /// One-shot runtime probe. Feeds libwebp a deliberately-invalid 1-byte payload
        /// and only cares whether the native library can be reached at all — we expect
        /// WebPGetInfo to return 0 (not enough data), NOT to throw. If the DLL/bundle
        /// cannot be loaded we get <see cref="DllNotFoundException"/> or
        /// <see cref="EntryPointNotFoundException"/>. Any other outcome is treated as
        /// "libwebp is reachable"; a genuine mis-linked binary would raise one of the
        /// two above.
        /// </summary>
        public bool Probe()
        {
            if (s_ProbedOnce) return s_ProbeResult;

            try
            {
                byte fakeByte = 0;
                int w, h;
                _ = NativeLibwebp.WebPGetInfo(&fakeByte, (UIntPtr)1, &w, &h);
                s_ProbeResult = true;
            }
            catch (DllNotFoundException)
            {
                s_ProbeResult = false;
            }
            catch (EntryPointNotFoundException)
            {
                s_ProbeResult = false;
            }
            catch (Exception ex)
            {
                // Anything else (AccessViolation, TypeInitializationException…) is
                // also an environment problem for our purposes — fail loud.
                Debug.LogWarning($"[NativeWebPDecoder] Probe raised unexpected {ex.GetType().Name}: {ex.Message}");
                s_ProbeResult = false;
            }

            s_ProbedOnce = true;
            return s_ProbeResult;
        }

        // -------------------------------------------------------------------------

        static void DecodeInto(byte[] webpBytes, byte* dstPtr, int dstBytes, int width, int height)
        {
            int stride = 4 * width;
            fixed (byte* srcPtr = webpBytes)
            {
                byte* result = NativeLibwebp.WebPDecodeRGBAInto(
                    srcPtr, (UIntPtr)webpBytes.Length,
                    dstPtr, (UIntPtr)dstBytes, stride);
                if (result == null)
                    throw new InvalidOperationException("NativeWebPDecoder.DecodeInto: WebPDecodeRGBAInto returned NULL");
            }
        }
    }
}
