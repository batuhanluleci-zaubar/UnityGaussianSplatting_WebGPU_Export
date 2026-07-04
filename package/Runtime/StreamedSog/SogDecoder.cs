// SPDX-License-Identifier: MIT
// Track C2c: SOG shN (higher-order SH) VQ decoder.
//
// SuperSplat writes higher-order SH as a Vector-Quantized codebook:
//   * shN_labels.webp     : one label per splat, split across TWO RGBA channels
//                           (R = low byte, G = high byte) => 16-bit label.
//   * shN_centroids.webp  : 2D atlas of quantised centroids, indexed as
//                             u = (label % 64) * shCoeffs
//                             v =  label / 64
//                           i.e. atlas width MUST equal 64 * shCoeffs.
//   * codebook[256]       : de-quantisation LUT applied to the centroid bytes.
//                           SuperSplat may leave codebook[0]==null; consumers
//                           MUST run SogCodebooks.PatchNullCodebook before Burst.
//
// shCoeffs per band count (matches PlayCanvas/SuperSplat emitter):
//   bands=1 -> 3 coeffs (sh1..sh3)
//   bands=2 -> 8 coeffs (sh1..sh8)
//   bands=3 -> 15 coeffs (sh1..shF, fills all InputSplatData.sh* slots)
//
// This file currently only contains DecodeShNJob (part 3 of C2). Other decode
// jobs (means / quats / scales / sh0) will be added in sibling patches.

using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GaussianSplatting.Runtime.StreamedSog
{
    /// <summary>
    /// Runtime helpers shared across the SOG decode jobs.
    /// </summary>
    public static class SogDecoderConstants
    {
        /// <summary>
        /// shCoeffs = 3, 8, 15 for bands 1, 2, 3 respectively.
        /// Returns 0 for invalid inputs so callers can short-circuit.
        /// </summary>
        public static int ShCoeffsForBands(int bands)
        {
            switch (bands)
            {
                case 1: return 3;
                case 2: return 8;
                case 3: return 15;
                default: return 0;
            }
        }

        /// <summary>
        /// Expected centroid atlas width for a given band count (== 64 * shCoeffs).
        /// </summary>
        public static int ExpectedCentroidWidth(int bands) => 64 * ShCoeffsForBands(bands);

        /// <summary>
        /// Runtime sanity check: verify the decoded centroid atlas width matches
        /// the expected 64 * shCoeffs. Logs a warning if not; caller is expected
        /// to fall back to sh0-only rendering in that case.
        /// </summary>
        public static bool ValidateCentroidWidth(int bands, int actualWidth, string assetNameForLog = null)
        {
            int expected = ExpectedCentroidWidth(bands);
            if (expected == 0)
            {
                Debug.LogWarning($"[SogDecoder] shN.bands={bands} invalid (must be 1..3); degrading to sh0-only.");
                return false;
            }
            if (actualWidth != expected)
            {
                Debug.LogWarning(
                    $"[SogDecoder] shN centroid texture width mismatch " +
                    $"(asset='{assetNameForLog ?? "?"}', bands={bands}, expected={expected}, actual={actualWidth}); " +
                    "degrading to sh0-only.");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Decodes SuperSplat's Vector-Quantized higher-order SH coefficients into
    /// the runtime InputSplatData layout.
    ///
    /// One thread per output splat. Reads two label bytes (low + high) to form
    /// a 16-bit label, computes (u,v) into the centroid atlas, then dequantises
    /// each of shCoeffs * 3 values through the codebook LUT.
    ///
    /// InputSplatData ships fifteen Vector3 SH slots (sh1..shF). For bands &lt; 3
    /// the trailing slots are left at whatever the caller pre-initialised them to
    /// (typically zero-cleared); we never touch them.
    /// </summary>
    [BurstCompile]
    public struct DecodeShNJob : IJobParallelFor
    {
        // ---- Inputs (read-only) -------------------------------------------------

        /// <summary>Low byte of the 16-bit label, one entry per splat.</summary>
        [ReadOnly] public NativeArray<byte> labelsLo;

        /// <summary>High byte of the 16-bit label, one entry per splat.</summary>
        [ReadOnly] public NativeArray<byte> labelsHi;

        /// <summary>
        /// Centroid atlas as interleaved RGB bytes (3 bytes per pixel, no alpha).
        /// Layout: row v of length centroidWidth == 64 * shCoeffs pixels.
        /// Length MUST equal centroidWidth * centroidHeight * 3.
        /// </summary>
        [ReadOnly] public NativeArray<byte> centroids;

        /// <summary>Atlas width in pixels. Runtime enforces == 64 * shCoeffs.</summary>
        public int centroidWidth;

        /// <summary>Bands (1..3). Drives shCoeffs = {3, 8, 15}.</summary>
        public int bands;

        /// <summary>
        /// 256-entry codebook LUT applied to each centroid byte.
        /// Callers must have already patched any null slots via SogCodebooks.PatchNullCodebook.
        /// </summary>
        [ReadOnly] public NativeArray<float> codebook;

        /// <summary>
        /// Offset into <see cref="output"/> at which this chunk's splats begin.
        /// Enables multiple chunks to write into one shared buffer.
        /// </summary>
        public int splatOffset;

        // ---- Output (write-only per index) --------------------------------------

        [NativeDisableParallelForRestriction]
        public NativeArray<InputSplatData> output;

        public void Execute(int i)
        {
            int shCoeffs = SogDecoderConstants.ShCoeffsForBands(bands);
            if (shCoeffs == 0) return; // guarded by ValidateCentroidWidth on the CPU side

            // 16-bit label = lo | (hi << 8). u = (label % 64) * shCoeffs, v = label / 64.
            int label = labelsLo[i] | (labelsHi[i] << 8);
            int u = (label & 63) * shCoeffs;   // label % 64
            int v = label >> 6;                // label / 64

            int rowBase = v * centroidWidth + u;

            // Dequantise shCoeffs Vector3s: one atlas pixel per SH coefficient (RGB in one pixel).
            // SuperSplat SOG v2 packs each coefficient triplet into a single pixel's RGB channels;
            // u = (label % 64) * shCoeffs, v = label / 64, width = 64 * shCoeffs.

            var splat = output[splatOffset + i];

            // Unrolled per-coefficient to avoid bounds checks & register spills in Burst.
            // We only write up to 'shCoeffs' slots; the rest stay untouched.
            // Order matches InputSplatData.sh1..shF.
            for (int c = 0; c < shCoeffs; c++)
            {
                int baseIdx = (rowBase + c) * 3;
                float r = codebook[centroids[baseIdx + 0]];
                float g = codebook[centroids[baseIdx + 1]];
                float b = codebook[centroids[baseIdx + 2]];
                WriteShSlot(ref splat, c, new Vector3(r, g, b));
            }

            output[splatOffset + i] = splat;
        }

        /// <summary>
        /// Writes a single SH coefficient into the correct InputSplatData slot.
        /// Kept out-of-line to keep Execute lean; Burst inlines regardless.
        /// </summary>
        static void WriteShSlot(ref InputSplatData splat, int c, Vector3 v)
        {
            // NOTE: InputSplatData exposes sh1..shF as Vector3 fields. Burst cannot
            // switch on managed types elegantly so we fall through with a switch.
            switch (c)
            {
                case  0: splat.sh1 = v; break;
                case  1: splat.sh2 = v; break;
                case  2: splat.sh3 = v; break;
                case  3: splat.sh4 = v; break;
                case  4: splat.sh5 = v; break;
                case  5: splat.sh6 = v; break;
                case  6: splat.sh7 = v; break;
                case  7: splat.sh8 = v; break;
                case  8: splat.sh9 = v; break;
                case  9: splat.shA = v; break;
                case 10: splat.shB = v; break;
                case 11: splat.shC = v; break;
                case 12: splat.shD = v; break;
                case 13: splat.shE = v; break;
                case 14: splat.shF = v; break;
            }
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only synthetic self-test for DecodeShNJob. Constructs a
    /// bands=1 (shCoeffs=3) atlas with two labels:
    ///   label 0 -> centroid bytes {10, 20, 30}
    ///   label 1 -> centroid bytes {40, 50, 60}
    /// codebook maps byte b -> b * 0.01f, so the expected sh1 for label 0 is
    /// (0.10, 0.20, 0.30). Fails loudly via Debug.LogError if the decoder
    /// output diverges. Wired up as a menu item so it can be triggered by hand.
    /// </summary>
    public static class SogDecoderSelfTest
    {
        [UnityEditor.MenuItem("Tools/GaussianSplatting/StreamedSog/Run DecodeShN self-test")]
        public static void Run()
        {
            const int bands = 1;
            const int shCoeffs = 3; // matches bands=1
            const int width = 64 * shCoeffs; // 192
            const int height = 1;
            const int splatCount = 2;

            var labelsLo  = new NativeArray<byte>(splatCount, Allocator.TempJob);
            var labelsHi  = new NativeArray<byte>(splatCount, Allocator.TempJob);
            var centroids = new NativeArray<byte>(width * height * 3, Allocator.TempJob);
            var codebook  = new NativeArray<float>(256, Allocator.TempJob);
            var output    = new NativeArray<InputSplatData>(splatCount, Allocator.TempJob);
            try
            {
                // Labels 0 and 1
                labelsLo[0] = 0; labelsHi[0] = 0;
                labelsLo[1] = 1; labelsHi[1] = 0;

                // codebook[b] = b * 0.01f  (byte 10 -> 0.10f)
                for (int b = 0; b < 256; b++) codebook[b] = b * 0.01f;

                // label 0: u=0, pixel 0 RGB => bytes 10,20,30
                centroids[0] = 10; centroids[1] = 20; centroids[2] = 30;
                // label 1: u=3, pixel 3 RGB => bytes 40,50,60
                centroids[9] = 40; centroids[10] = 50; centroids[11] = 60;

                if (!SogDecoderConstants.ValidateCentroidWidth(bands, width, "selftest"))
                {
                    Debug.LogError("[SogDecoderSelfTest] centroid width validation unexpectedly failed");
                    return;
                }

                var job = new DecodeShNJob
                {
                    labelsLo = labelsLo,
                    labelsHi = labelsHi,
                    centroids = centroids,
                    centroidWidth = width,
                    bands = bands,
                    codebook = codebook,
                    splatOffset = 0,
                    output = output,
                };
                job.Schedule(splatCount, 1).Complete();

                var s0 = output[0];
                var s1 = output[1];

                const float eps = 1e-5f;
                bool ok0 =
                    Mathf.Abs(s0.sh1.x - 0.10f) < eps &&
                    Mathf.Abs(s0.sh1.y - 0.20f) < eps &&
                    Mathf.Abs(s0.sh1.z - 0.30f) < eps;
                bool ok1 =
                    Mathf.Abs(s1.sh1.x - 0.40f) < eps &&
                    Mathf.Abs(s1.sh1.y - 0.50f) < eps &&
                    Mathf.Abs(s1.sh1.z - 0.60f) < eps;

                if (ok0 && ok1)
                    Debug.Log($"[SogDecoderSelfTest] PASS - label0.sh1={s0.sh1}, label1.sh1={s1.sh1}");
                else
                    Debug.LogError($"[SogDecoderSelfTest] FAIL - label0.sh1={s0.sh1} (want (0.10,0.20,0.30)), label1.sh1={s1.sh1} (want (0.40,0.50,0.60))");
            }
            finally
            {
                labelsLo.Dispose();
                labelsHi.Dispose();
                centroids.Dispose();
                codebook.Dispose();
                output.Dispose();
            }
        }
    }
#endif
}
