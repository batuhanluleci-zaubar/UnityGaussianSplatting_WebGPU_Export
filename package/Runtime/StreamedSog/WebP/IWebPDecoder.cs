// SPDX-License-Identifier: MIT
// Track C1: interface abstraction over libwebp so downstream StreamedSog code compiles
// even before the com.netpyoung.webp native plugin is landed in the project.
// The stub Native impl throws NotImplementedException — decode paths must be wired
// once the SuperSplat .sog asset is available for real end-to-end testing.

using System;
using Unity.Collections;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Minimum surface StreamedSog needs from a WebP decoder:
    /// take a byte range, produce an RGBA8 NativeArray plus width/height.
    /// Implementations are expected to be thread-safe (Burst jobs may call them).
    /// </summary>
    public interface IWebPDecoder
    {
        /// <summary>
        /// Decode a WebP payload into RGBA8. The output array is owned by the caller
        /// (Allocator.Persistent is recommended so the pixel buffer survives across
        /// job boundaries). Returns true on success.
        /// </summary>
        bool Decode(
            NativeArray<byte> encoded,
            Allocator outputAllocator,
            out NativeArray<byte> rgba,
            out int width,
            out int height);
    }

    /// <summary>
    /// Stub implementation. Present so the assembly compiles without pulling a
    /// native binary we cannot exercise end-to-end today.
    /// </summary>
    // TODO wire libwebp when SuperSplat asset landed for testing
    public sealed class NativeWebPDecoder : IWebPDecoder
    {
        public bool Decode(
            NativeArray<byte> encoded,
            Allocator outputAllocator,
            out NativeArray<byte> rgba,
            out int width,
            out int height)
        {
            throw new NotImplementedException(
                "NativeWebPDecoder is a Track-C1 stub. " +
                "Install com.netpyoung.webp (or another libwebp binding) and " +
                "replace this implementation once the SuperSplat .sog asset is available.");
        }
    }
}
