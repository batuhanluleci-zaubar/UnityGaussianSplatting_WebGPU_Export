// SPDX-License-Identifier: MIT
// Track C2a: in-editor synthetic self-test for DecodeMeansJob + DecodeQuatsJob.
//
// Menu path: "Tools/GaussianSplatting/StreamedSog/Run DecodeMeans+Quats self-test"
//
// Position test:
//   mins = (-1, -1, -1), maxs = (1, 1, 1),
//   per-axis lo = 127, hi = 127  =>  raw16 = (127<<8) | 127 = 32639,
//     n = 32639 / 65535 ~= 0.49803...
//     lerp(-1, 1, n)  ~= -0.00393...
//   InvLogTransform of that lerp is ~0 (sign * (exp(|x|)-1) with |x|<<1).
//   We accept |world.x/y/z| < 5e-3 as pass.
//
// Quaternion test:
//   Encode identity as (a,b,c) = (0,0,0) mapped from bytes (127,127,127) which is
//   1/255 shy of true zero; largest-index mode = 3 (w-largest) => tag byte = 255.
//   Decoded quaternion should be very close to (0,0,0,1) after normalisation.
//
// Invalid tag test:
//   b3 = 0 (out of the 252..255 range) MUST decode to identity.
//
// Logs Debug.Log on success, Debug.LogError on any failure.
//
// NOTE: NativeArray does not allow indexer assignment via a C# 'using var'
// (CS1654 — "cannot modify members of a using variable"), so we scope the
// arrays with try/finally + Dispose() instead.

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using GaussianSplatting.Runtime;
using GaussianSplatting.Runtime.StreamedSog;

namespace GaussianSplatting.Editor.StreamedSog
{
    public static class SogDecoderMeansQuatsSelfTest
    {
        [MenuItem("Tools/GaussianSplatting/StreamedSog/Run DecodeMeans+Quats self-test")]
        public static void Run()
        {
            bool positionOk = RunPositionTest();
            bool quatOk     = RunQuatTest();
            bool invalidOk  = RunInvalidQuatTagTest();

            if (positionOk && quatOk && invalidOk)
                Debug.Log("[SogDecoderMeansQuatsSelfTest] PASS - position + quaternion decoders match expected values.");
            else
                Debug.LogError($"[SogDecoderMeansQuatsSelfTest] FAIL - position={positionOk}, quat={quatOk}, invalidQuat={invalidOk}");
        }

        static bool RunPositionTest()
        {
            const int splatCount = 1;
            const int stride = 3;   // RGB8 plane, 3 bytes per splat

            var meansL = new NativeArray<byte>(splatCount * stride, Allocator.TempJob);
            var meansU = new NativeArray<byte>(splatCount * stride, Allocator.TempJob);
            var output = new NativeArray<InputSplatData>(splatCount, Allocator.TempJob);
            try
            {
                for (int i = 0; i < splatCount * stride; i++)
                {
                    meansL[i] = 127;
                    meansU[i] = 127;
                }

                new DecodeMeansJob
                {
                    meansL = meansL,
                    meansU = meansU,
                    mins = new float3(-1f, -1f, -1f),
                    maxs = new float3( 1f,  1f,  1f),
                    strideBytes = stride,
                    splatOffset = 0,
                    output = output,
                }.Schedule(splatCount, 1).Complete();

                var pos = output[0].pos;
                // The 16-bit sample (127<<8)|127 = 32639 gives n ~= 0.49803, which lerps to
                // about -0.00394 in [-1,1] and InvLogTransform keeps it near zero.
                const float tol = 5e-3f;
                bool ok = Mathf.Abs(pos.x) < tol && Mathf.Abs(pos.y) < tol && Mathf.Abs(pos.z) < tol;
                if (!ok)
                    Debug.LogError($"[SogDecoderMeansQuatsSelfTest] position FAIL: got {pos}, expected near (0,0,0)");
                return ok;
            }
            finally
            {
                meansL.Dispose();
                meansU.Dispose();
                output.Dispose();
            }
        }

        static bool RunQuatTest()
        {
            const int splatCount = 1;
            var quats = new NativeArray<byte>(splatCount * 4, Allocator.TempJob);
            var output = new NativeArray<InputSplatData>(splatCount, Allocator.TempJob);
            try
            {
                // Encode identity: (a,b,c)=(0,0,0) with w largest (idx=3) -> tag = 255.
                // Byte 127 -> ((127/255)*2 - 1) * (1/sqrt(2)) is *nearly* zero (1/255 offset),
                // so the decoded quaternion is approximately (0,0,0,1).
                quats[0] = 127;
                quats[1] = 127;
                quats[2] = 127;
                quats[3] = 255;   // mode tag for largestIdx=3

                new DecodeQuatsJob
                {
                    quats = quats,
                    splatOffset = 0,
                    output = output,
                }.Schedule(splatCount, 1).Complete();

                var r = output[0].rot;
                const float tol = 1e-2f;   // 1/255 error propagates ~0.004, keep slack
                bool ok = Mathf.Abs(r.x) < tol && Mathf.Abs(r.y) < tol && Mathf.Abs(r.z) < tol && Mathf.Abs(r.w - 1f) < tol;
                if (!ok)
                    Debug.LogError($"[SogDecoderMeansQuatsSelfTest] quat FAIL: got ({r.x},{r.y},{r.z},{r.w}), expected ~(0,0,0,1)");
                return ok;
            }
            finally
            {
                quats.Dispose();
                output.Dispose();
            }
        }

        static bool RunInvalidQuatTagTest()
        {
            const int splatCount = 1;
            var quats = new NativeArray<byte>(splatCount * 4, Allocator.TempJob);
            var output = new NativeArray<InputSplatData>(splatCount, Allocator.TempJob);
            try
            {
                // Any tag byte outside 252..255 must decode to identity (0,0,0,1).
                quats[0] = 200; quats[1] = 200; quats[2] = 200; quats[3] = 0;

                new DecodeQuatsJob
                {
                    quats = quats,
                    splatOffset = 0,
                    output = output,
                }.Schedule(splatCount, 1).Complete();

                var r = output[0].rot;
                bool ok = r.x == 0f && r.y == 0f && r.z == 0f && Mathf.Approximately(r.w, 1f);
                if (!ok)
                    Debug.LogError($"[SogDecoderMeansQuatsSelfTest] invalid-tag FAIL: got ({r.x},{r.y},{r.z},{r.w}), expected exactly (0,0,0,1)");
                return ok;
            }
            finally
            {
                quats.Dispose();
                output.Dispose();
            }
        }
    }
}
