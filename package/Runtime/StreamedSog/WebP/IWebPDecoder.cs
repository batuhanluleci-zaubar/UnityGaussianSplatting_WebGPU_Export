// SPDX-License-Identifier: MIT
// Track C1: interface abstraction over libwebp so downstream StreamedSog code compiles
// even before the com.netpyoung.webp native plugin is landed in the project.
// Track C4b: the concrete NativeWebPDecoder now lives in NativeWebPDecoder.cs and is
// backed by com.netpyoung.webp. This file only defines the interface.

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
}
