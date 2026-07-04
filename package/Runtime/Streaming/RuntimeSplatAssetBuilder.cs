// SPDX-License-Identifier: MIT
// Runtime fast-path: InputSplatData -> GPU-ready byte blobs (no disk / AssetDatabase).

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime.Streaming
{
    public static class RuntimeSplatAssetBuilder
    {
        public struct BuiltChunk
        {
            public GaussianSplatAsset asset;
            public int splatCount;
            public int poolOffset;
        }

        const GaussianSplatAsset.VectorFormat kPosFmt = GaussianSplatAsset.VectorFormat.Norm11;
        const GaussianSplatAsset.VectorFormat kScaleFmt = GaussianSplatAsset.VectorFormat.Norm11;
        const GaussianSplatAsset.ColorFormat kColorFmt = GaussianSplatAsset.ColorFormat.Norm8x4;
        const GaussianSplatAsset.SHFormat kShFmt = GaussianSplatAsset.SHFormat.Float16;

        public static BuiltChunk BuildFromSplats(NativeArray<InputSplatData> inputSplats, string name = "runtime_chunk")
        {
            if (!inputSplats.IsCreated || inputSplats.Length == 0)
                throw new ArgumentException("inputSplats empty");

            var work = new NativeArray<InputSplatData>(inputSplats.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            work.CopyFrom(inputSplats);

            float3 boundsMin = float.PositiveInfinity;
            float3 boundsMax = float.NegativeInfinity;
            for (int i = 0; i < work.Length; i++)
            {
                float3 p = work[i].pos;
                boundsMin = math.min(boundsMin, p);
                boundsMax = math.max(boundsMax, p);
            }
            if (math.any(boundsMax - boundsMin < 1e-5f))
                boundsMax = boundsMin + new float3(1e-3f);

            int splatCount = inputSplats.Length;
            RuntimeSplatEncoding.ReorderMorton(work, boundsMin, boundsMax);
            byte[] chunkBytes = RuntimeSplatEncoding.NormalizeChunksAndEncodeTable(work);

            byte[] posBytes = RuntimeSplatEncoding.EncodePositions(work, kPosFmt);
            byte[] otherBytes = RuntimeSplatEncoding.EncodeOther(work, kScaleFmt);
            byte[] colorBytes = RuntimeSplatEncoding.EncodeColor(work, kColorFmt);
            byte[] shBytes = RuntimeSplatEncoding.EncodeSH(work, kShFmt);

            work.Dispose();

            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = name;
            asset.Initialize(splatCount, kPosFmt, kScaleFmt, kColorFmt, kShFmt, boundsMin, boundsMax, null);
            asset.SetRuntimeByteData(posBytes, otherBytes, colorBytes, shBytes, chunkBytes);
            asset.SetDataHash(new Hash128((uint)splatCount, (uint)GaussianSplatAsset.kCurrentVersion, 1, 2));

            return new BuiltChunk { asset = asset, splatCount = splatCount, poolOffset = 0 };
        }

        public static void DestroyBuiltAsset(GaussianSplatAsset asset)
        {
            if (asset != null)
                UnityEngine.Object.Destroy(asset);
        }
    }

    static class RuntimeSplatEncoding
    {
        public static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
        {
            var order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob);
            var job = new ReorderMortonJob
            {
                m_SplatData = splatData,
                m_BoundsMin = boundsMin,
                m_InvBoundsSize = 1.0f / math.max(boundsMax - boundsMin, 1e-5f),
                m_Order = order
            };
            job.Schedule(splatData.Length, 4096).Complete();

            var sorted = new NativeArray<InputSplatData>(splatData.Length, Allocator.TempJob);
            var orderArray = order.ToArray();
            Array.Sort(orderArray, (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
            for (int i = 0; i < orderArray.Length; i++)
                sorted[i] = splatData[orderArray[i].Item2];
            splatData.CopyFrom(sorted);
            sorted.Dispose();
            order.Dispose();
        }

        public static byte[] NormalizeChunksAndEncodeTable(NativeArray<InputSplatData> splatData)
        {
            int chunkCount = (splatData.Length + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
            var chunks = new NativeArray<GaussianSplatAsset.ChunkInfo>(chunkCount, Allocator.TempJob);
            var job = new CalcChunkDataJob { splatData = splatData, chunks = chunks };
            job.Schedule(chunkCount, 8).Complete();
            var bytes = chunks.Reinterpret<byte>(UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()).ToArray();
            chunks.Dispose();
            return bytes;
        }

        public static byte[] EncodePositions(NativeArray<InputSplatData> input, GaussianSplatAsset.VectorFormat fmt)
        {
            int sz = GaussianSplatAsset.GetVectorSize(fmt);
            var data = new NativeArray<byte>(input.Length * sz, Allocator.TempJob);
            var job = new CreatePositionsDataJob { m_Input = input, m_Format = fmt, m_FormatSize = sz, m_Output = data };
            job.Schedule(input.Length, 8192).Complete();
            var bytes = data.ToArray();
            data.Dispose();
            return bytes;
        }

        public static byte[] EncodeOther(NativeArray<InputSplatData> input, GaussianSplatAsset.VectorFormat scaleFmt)
        {
            int sz = GaussianSplatAsset.GetOtherSizeNoSHIndex(scaleFmt);
            var data = new NativeArray<byte>(input.Length * sz, Allocator.TempJob);
            var job = new CreateOtherDataJob { m_Input = input, m_ScaleFormat = scaleFmt, m_FormatSize = sz, m_Output = data };
            job.Schedule(input.Length, 8192).Complete();
            var bytes = data.ToArray();
            data.Dispose();
            return bytes;
        }

        public static byte[] EncodeColor(NativeArray<InputSplatData> input, GaussianSplatAsset.ColorFormat fmt)
        {
            var (width, height) = GaussianSplatAsset.CalcTextureSize(input.Length);
            var floats = new NativeArray<float4>(width * height, Allocator.TempJob);
            var fill = new CreateColorDataJob { m_Input = input, m_Output = floats };
            fill.Schedule(input.Length, 8192).Complete();

            int bpp = GaussianSplatAsset.GetColorSize(fmt);
            var data = new NativeArray<byte>(width * height * bpp, Allocator.TempJob);
            var conv = new ConvertColorJob
            {
                width = width, height = height, inputData = floats, outputData = data,
                format = fmt, formatBytesPerPixel = bpp
            };
            conv.Schedule(height, 1).Complete();
            var bytes = data.ToArray();
            floats.Dispose();
            data.Dispose();
            return bytes;
        }

        public static byte[] EncodeSH(NativeArray<InputSplatData> input, GaussianSplatAsset.SHFormat fmt)
        {
            int dataLen = (int)GaussianSplatAsset.CalcSHDataSize(input.Length, fmt);
            var data = new NativeArray<byte>(dataLen, Allocator.TempJob);
            var job = new CreateSHDataJob { m_Input = input, m_Format = fmt, m_Output = data };
            job.Schedule(input.Length, 8192).Complete();
            var bytes = data.ToArray();
            data.Dispose();
            return bytes;
        }

        public static byte[] EncodeChunkTable(NativeArray<InputSplatData> input)
        {
            return NormalizeChunksAndEncodeTable(input);
        }

        [BurstCompile]
        struct ReorderMortonJob : IJobParallelFor
        {
            const float kScaler = (float)((1 << 21) - 1);
            public float3 m_BoundsMin;
            public float3 m_InvBoundsSize;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
            public NativeArray<(ulong, int)> m_Order;

            public void Execute(int index)
            {
                float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
                uint3 ipos = (uint3)pos;
                ulong code = GaussianUtils.MortonEncode3(ipos);
                m_Order[index] = (code, index);
            }
        }

        [BurstCompile]
        struct CalcChunkDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
            public NativeArray<GaussianSplatAsset.ChunkInfo> chunks;

            public void Execute(int chunkIdx)
            {
                float3 chunkMinpos = float.PositiveInfinity;
                float3 chunkMinscl = float.PositiveInfinity;
                float4 chunkMincol = float.PositiveInfinity;
                float3 chunkMinshs = float.PositiveInfinity;
                float3 chunkMaxpos = float.NegativeInfinity;
                float3 chunkMaxscl = float.NegativeInfinity;
                float4 chunkMaxcol = float.NegativeInfinity;
                float3 chunkMaxshs = float.NegativeInfinity;

                int splatBegin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splatData.Length);
                int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splatData.Length);

                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    s.scale = GaussianUtils.ClampScaleAnisotropy(s.scale);
                    s.scale = math.pow(s.scale, 1.0f / 8.0f);
                    s.opacity = GaussianUtils.SquareCentered01(s.opacity);
                    splatData[i] = s;

                    chunkMinpos = math.min(chunkMinpos, s.pos);
                    chunkMinscl = math.min(chunkMinscl, s.scale);
                    chunkMincol = math.min(chunkMincol, new float4(s.dc0, s.opacity));
                    chunkMinshs = math.min(chunkMinshs, s.sh1);
                    chunkMinshs = math.min(chunkMinshs, s.sh2);
                    chunkMinshs = math.min(chunkMinshs, s.sh3);
                    chunkMinshs = math.min(chunkMinshs, s.sh4);
                    chunkMinshs = math.min(chunkMinshs, s.sh5);
                    chunkMinshs = math.min(chunkMinshs, s.sh6);
                    chunkMinshs = math.min(chunkMinshs, s.sh7);
                    chunkMinshs = math.min(chunkMinshs, s.sh8);
                    chunkMinshs = math.min(chunkMinshs, s.sh9);
                    chunkMinshs = math.min(chunkMinshs, s.shA);
                    chunkMinshs = math.min(chunkMinshs, s.shB);
                    chunkMinshs = math.min(chunkMinshs, s.shC);
                    chunkMinshs = math.min(chunkMinshs, s.shD);
                    chunkMinshs = math.min(chunkMinshs, s.shE);
                    chunkMinshs = math.min(chunkMinshs, s.shF);
                    chunkMaxpos = math.max(chunkMaxpos, s.pos);
                    chunkMaxscl = math.max(chunkMaxscl, s.scale);
                    chunkMaxcol = math.max(chunkMaxcol, new float4(s.dc0, s.opacity));
                    chunkMaxshs = math.max(chunkMaxshs, s.sh1);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh2);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh3);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh4);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh5);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh6);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh7);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh8);
                    chunkMaxshs = math.max(chunkMaxshs, s.sh9);
                    chunkMaxshs = math.max(chunkMaxshs, s.shA);
                    chunkMaxshs = math.max(chunkMaxshs, s.shB);
                    chunkMaxshs = math.max(chunkMaxshs, s.shC);
                    chunkMaxshs = math.max(chunkMaxshs, s.shD);
                    chunkMaxshs = math.max(chunkMaxshs, s.shE);
                    chunkMaxshs = math.max(chunkMaxshs, s.shF);
                }

                chunkMaxpos = math.max(chunkMaxpos, chunkMinpos + 1.0e-5f);
                chunkMaxscl = math.max(chunkMaxscl, chunkMinscl + 1.0e-5f);
                chunkMaxcol = math.max(chunkMaxcol, chunkMincol + 1.0e-5f);
                chunkMaxshs = math.max(chunkMaxshs, chunkMinshs + 1.0e-5f);

                GaussianSplatAsset.ChunkInfo info = default;
                info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
                info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
                info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
                info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
                info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
                info.sclZ = math.f32tof16(chunkMinscl.z) | (math.f32tof16(chunkMaxscl.z) << 16);
                info.colR = math.f32tof16(chunkMincol.x) | (math.f32tof16(chunkMaxcol.x) << 16);
                info.colG = math.f32tof16(chunkMincol.y) | (math.f32tof16(chunkMaxcol.y) << 16);
                info.colB = math.f32tof16(chunkMincol.z) | (math.f32tof16(chunkMaxcol.z) << 16);
                info.colA = math.f32tof16(chunkMincol.w) | (math.f32tof16(chunkMaxcol.w) << 16);
                info.shR = math.f32tof16(chunkMinshs.x) | (math.f32tof16(chunkMaxshs.x) << 16);
                info.shG = math.f32tof16(chunkMinshs.y) | (math.f32tof16(chunkMaxshs.y) << 16);
                info.shB = math.f32tof16(chunkMinshs.z) | (math.f32tof16(chunkMaxshs.z) << 16);
                chunks[chunkIdx] = info;

                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    s.pos = ((float3)s.pos - chunkMinpos) / (chunkMaxpos - chunkMinpos);
                    s.scale = ((float3)s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
                    s.dc0 = ((float3)s.dc0 - chunkMincol.xyz) / (chunkMaxcol.xyz - chunkMincol.xyz);
                    s.opacity = (s.opacity - chunkMincol.w) / (chunkMaxcol.w - chunkMincol.w);
                    s.sh1 = ((float3)s.sh1 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh2 = ((float3)s.sh2 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh3 = ((float3)s.sh3 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh4 = ((float3)s.sh4 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh5 = ((float3)s.sh5 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh6 = ((float3)s.sh6 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh7 = ((float3)s.sh7 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh8 = ((float3)s.sh8 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.sh9 = ((float3)s.sh9 - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shA = ((float3)s.shA - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shB = ((float3)s.shB - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shC = ((float3)s.shC - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shD = ((float3)s.shD - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shE = ((float3)s.shE - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    s.shF = ((float3)s.shF - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    splatData[i] = s;
                }
            }
        }

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplatAsset.VectorFormat m_Format;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*)m_Output.GetUnsafePtr() + index * m_FormatSize;
                SplatCodec.EmitEncodedVector(m_Input[index].pos, outputPtr, m_Format);
            }
        }

        [BurstCompile]
        struct CreateOtherDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplatAsset.VectorFormat m_ScaleFormat;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*)m_Output.GetUnsafePtr() + index * m_FormatSize;
                Quaternion rotQ = m_Input[index].rot;
                float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                // The Norm10 rotation format the GPU decodes is smallest-three packed (3 smallest
                // components in 0..1 + 2-bit largest index). The editor importers (GaussianFileReader/
                // SPZFileReader) pack via PackSmallest3Rotation before EncodeQuatToNorm10; this runtime
                // builder was missing that step and fed the raw signed quaternion straight in, so every
                // splat decoded to a garbage rotation on the GPU -> anisotropic splats rendered as
                // needle streaks. Pack here to match the editor/reference path.
                rot = GaussianUtils.PackSmallest3Rotation(rot);
                *(uint*)outputPtr = SplatCodec.EncodeQuatToNorm10(rot);
                outputPtr += 4;
                SplatCodec.EmitEncodedVector(m_Input[index].scale, outputPtr, m_ScaleFormat);
            }
        }

        [BurstCompile]
        struct CreateColorDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<float4> m_Output;

            public void Execute(int index)
            {
                var splat = m_Input[index];
                int i = SplatCodec.SplatIndexToTextureIndex((uint)index);
                m_Output[i] = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
            }
        }

        [BurstCompile]
        struct ConvertColorJob : IJobParallelFor
        {
            public int width, height;
            [ReadOnly] public NativeArray<float4> inputData;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
            public GaussianSplatAsset.ColorFormat format;
            public int formatBytesPerPixel;

            public unsafe void Execute(int y)
            {
                int srcIdx = y * width;
                byte* dstPtr = (byte*)outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
                for (int x = 0; x < width; ++x)
                {
                    float4 pix = math.saturate(inputData[srcIdx]);
                    if (format == GaussianSplatAsset.ColorFormat.Norm8x4)
                    {
                        uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                        *(uint*)dstPtr = enc;
                    }
                    srcIdx++;
                    dstPtr += formatBytesPerPixel;
                }
            }
        }

        [BurstCompile]
        struct CreateSHDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplatAsset.SHFormat m_Format;
            public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                if (m_Format != GaussianSplatAsset.SHFormat.Float16) return;
                var splat = m_Input[index];
                GaussianSplatAsset.SHTableItemFloat16 res;
                res.sh1 = new half3(splat.sh1); res.sh2 = new half3(splat.sh2); res.sh3 = new half3(splat.sh3); res.sh4 = new half3(splat.sh4);
                res.sh5 = new half3(splat.sh5); res.sh6 = new half3(splat.sh6); res.sh7 = new half3(splat.sh7); res.sh8 = new half3(splat.sh8);
                res.sh9 = new half3(splat.sh9); res.shA = new half3(splat.shA); res.shB = new half3(splat.shB); res.shC = new half3(splat.shC);
                res.shD = new half3(splat.shD); res.shE = new half3(splat.shE); res.shF = new half3(splat.shF);
                res.shPadding = default;
                ((GaussianSplatAsset.SHTableItemFloat16*)m_Output.GetUnsafePtr())[index] = res;
            }
        }
    }

    static class SplatCodec
    {
        public static uint EncodeQuatToNorm10(float4 v) =>
            (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);

        public static unsafe void EmitEncodedVector(float3 v, byte* outputPtr, GaussianSplatAsset.VectorFormat format)
        {
            v = math.saturate(v);
            if (format == GaussianSplatAsset.VectorFormat.Norm11)
            {
                uint enc = (uint)(v.x * 2047.5f) | ((uint)(v.y * 1023.5f) << 11) | ((uint)(v.z * 2047.5f) << 21);
                *(uint*)outputPtr = enc;
            }
        }

        public static int SplatIndexToTextureIndex(uint idx)
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.kTextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.kTextureWidth + x);
        }
    }
}
