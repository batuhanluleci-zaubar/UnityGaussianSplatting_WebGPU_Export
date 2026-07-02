// SPDX-License-Identifier: MIT
// Track C2 (part 2): synthetic self-test for DecodeScalesJob + DecodeSh0Job.
//
// Menu path: "Tools/GaussianSplatting/StreamedSog/Run SogDecoder Scale+Sh0 Self-Test"
// Runs the Burst jobs against a tiny 2-splat fixture with hand-computed expected
// outputs. Logs Debug.Log on success or Debug.LogError on the first failing
// invariant. Uses no test-framework dependency to keep the editor asmdef clean.

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;
using GaussianSplatting.Runtime.StreamedSog;

namespace GaussianSplatting.Editor.StreamedSog
{
    public static class SogDecoderColorScaleSelfTest
    {
        const float k_Eps = 1e-5f;

        [MenuItem("Tools/GaussianSplatting/StreamedSog/Run SogDecoder ScaleSh0 Self-Test")]
        public static void Run()
        {
            var scalesCodebook = default(NativeArray<float>);
            var sh0Codebook    = default(NativeArray<float>);
            var scaleBytes     = default(NativeArray<byte>);
            var sh0Bytes       = default(NativeArray<byte>);
            var output         = default(NativeArray<InputSplatData>);
            try
            {
                // ------------------------------------------------------------------
                // Fixture: 2 splats.
                //   - scale codebook maps byte i -> log-scale = (i - 128) / 64
                //   - sh0   codebook maps byte i -> raw value = (i - 128) / 128
                //   - splat 0 picks (byte 128, 128, 128) for scale = log(1) = 0
                //     so exp(0) = 1 on each axis.
                //   - splat 1 picks (192, 96, 128) for scale = (64/64, -32/64, 0)
                //     so exp = (e, e^-0.5, 1).
                //   - sh0 splat 0: (r,g,b,a) = (128, 128, 128, 255)  -> DC=0.5, alpha=1
                //   - sh0 splat 1: (r,g,b,a) = (192, 64, 128, 128)   -> uses codebook
                // ------------------------------------------------------------------
                scalesCodebook = new NativeArray<float>(256, Allocator.TempJob);
                sh0Codebook    = new NativeArray<float>(256, Allocator.TempJob);
                for (int i = 0; i < 256; i++)
                {
                    scalesCodebook[i] = (i - 128) / 64f;
                    sh0Codebook[i]    = (i - 128) / 128f;
                }

                scaleBytes = new NativeArray<byte>(new byte[]
                {
                    128, 128, 128,   // splat 0
                    192,  96, 128,   // splat 1
                }, Allocator.TempJob);

                sh0Bytes = new NativeArray<byte>(new byte[]
                {
                    128, 128, 128, 255,   // splat 0
                    192,  64, 128, 128,   // splat 1
                }, Allocator.TempJob);

                output = new NativeArray<InputSplatData>(3, Allocator.TempJob); // slot 0 empty (splatOffset=1)

                // ------------------------------------------------------------------
                // DecodeScalesJob
                // ------------------------------------------------------------------
                var scalesJob = new DecodeScalesJob
                {
                    scales      = scaleBytes,
                    codebook    = scalesCodebook,
                    output      = output,
                    splatOffset = 1,
                };
                scalesJob.Schedule(2, 1).Complete();

                var s0 = output[1].scale;
                var s1 = output[2].scale;

                if (!Approx(s0.x, 1f) || !Approx(s0.y, 1f) || !Approx(s0.z, 1f))
                {
                    Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL: scale splat0 expected (1,1,1), got {s0}");
                    return;
                }

                float ex0 = math.exp(1f);
                float ex1 = math.exp(-0.5f);
                if (!Approx(s1.x, ex0) || !Approx(s1.y, ex1) || !Approx(s1.z, 1f))
                {
                    Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL: scale splat1 expected " +
                                   $"({ex0},{ex1},1), got {s1}");
                    return;
                }

                // ------------------------------------------------------------------
                // DecodeSh0Job (raw alpha path: storeAsLogit = false)
                // ------------------------------------------------------------------
                var sh0Job = new DecodeSh0Job
                {
                    sh0          = sh0Bytes,
                    codebook     = sh0Codebook,
                    output       = output,
                    splatOffset  = 1,
                    storeAsLogit = false,
                };
                sh0Job.Schedule(2, 1).Complete();

                var d0 = output[1];
                var d1 = output[2];

                // splat0: codebook[128] = 0 -> DC = 0.5; alpha = 255/255 = 1
                if (!Approx(d0.dc0.x, 0.5f) || !Approx(d0.dc0.y, 0.5f) || !Approx(d0.dc0.z, 0.5f) ||
                    !Approx(d0.opacity, 1f))
                {
                    Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL: sh0 splat0 expected " +
                                   $"dc=(0.5,0.5,0.5) a=1, got dc={d0.dc0} a={d0.opacity}");
                    return;
                }

                // splat1: codebook[192]=0.5, codebook[64]=-0.5, codebook[128]=0
                //   DC.x = 0.5 + 0.5 * SH_C0
                //   DC.y = 0.5 + -0.5 * SH_C0
                //   DC.z = 0.5
                //   alpha = 128/255
                float dcx = 0.5f + 0.5f * SogCodebooks.SH_C0;
                float dcy = 0.5f + -0.5f * SogCodebooks.SH_C0;
                float dcz = 0.5f;
                float aExp = 128f / 255f;
                if (!Approx(d1.dc0.x, dcx) || !Approx(d1.dc0.y, dcy) || !Approx(d1.dc0.z, dcz) ||
                    !Approx(d1.opacity, aExp))
                {
                    Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL: sh0 splat1 expected " +
                                   $"dc=({dcx},{dcy},{dcz}) a={aExp}, got dc={d1.dc0} a={d1.opacity}");
                    return;
                }

                // ------------------------------------------------------------------
                // DecodeSh0Job (logit alpha path: storeAsLogit = true)
                // Overwrite output slot 1 and rerun with only splat 0 (alpha=1).
                // Since the input alpha is 1 the helper clamps and returns logit(1-eps),
                // which must be a large positive finite float (> ~10).
                // ------------------------------------------------------------------
                var sh0LogitJob = new DecodeSh0Job
                {
                    sh0          = sh0Bytes,
                    codebook     = sh0Codebook,
                    output       = output,
                    splatOffset  = 1,
                    storeAsLogit = true,
                };
                sh0LogitJob.Schedule(1, 1).Complete();

                float aLogit = output[1].opacity;
                if (!(aLogit > 5f) || float.IsNaN(aLogit) || float.IsInfinity(aLogit))
                {
                    Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL: logit alpha for a=1 " +
                                   $"expected large finite positive, got {aLogit}");
                    return;
                }

                Debug.Log("[SogDecoderColorScaleSelfTest] PASS: DecodeScalesJob + DecodeSh0Job " +
                          $"produced expected outputs (scale exp path, dc SH_C0 path, alpha raw " +
                          $"& logit paths); SH_C0={SogCodebooks.SH_C0}.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[SogDecoderColorScaleSelfTest] FAIL (exception): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                if (scalesCodebook.IsCreated) scalesCodebook.Dispose();
                if (sh0Codebook.IsCreated)    sh0Codebook.Dispose();
                if (scaleBytes.IsCreated)     scaleBytes.Dispose();
                if (sh0Bytes.IsCreated)       sh0Bytes.Dispose();
                if (output.IsCreated)         output.Dispose();
            }
        }

        static bool Approx(float a, float b) => math.abs(a - b) <= k_Eps;
    }
}
