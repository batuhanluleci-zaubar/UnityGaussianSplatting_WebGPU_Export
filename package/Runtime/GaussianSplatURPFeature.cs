// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        [Header("Gaussian Splat Fixed Resolution Override")]
        [SerializeField] bool m_OverrideResolution = false;
        [SerializeField] int m_MaxSize = 1280;

        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            const string GaussianMotionRTName = "_GaussianSplatMotionRT";
            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle GaussianSplatMotionRT;
                internal int GaussianSplatWidth;
                internal int GaussianSplatHeight;
            }

            public GSRenderPass()
            {
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var settings = GaussianSplatSettings.instance;
                var usingRT = !settings.isDebugRender;
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                passData.CameraData = cameraData;

                if (usingRT)
                {
                    RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                    rtDesc.depthBufferBits = 0;
                    rtDesc.msaaSamples = 1;
                    rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;

                    var colorHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);
                    passData.GaussianSplatRT = colorHandle;
                    passData.GaussianSplatWidth = rtDesc.width;
                    passData.GaussianSplatHeight = rtDesc.height;
                    builder.UseTexture(colorHandle, AccessFlags.Write);

                    // create a motion target (RG16 float) used by temporal filter
                    var motionDesc = cameraData.cameraTargetDescriptor;
                    motionDesc.depthBufferBits = 0;
                    motionDesc.msaaSamples = 1;
                    motionDesc.graphicsFormat = GraphicsFormat.R16G16_SFloat;
                    var motionHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, motionDesc, GaussianMotionRTName, true);
                    passData.GaussianSplatMotionRT = motionHandle;
                    builder.UseTexture(motionHandle, AccessFlags.Write);
                }
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);

                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var system = GaussianSplatRenderSystem.instance;
                    system.EnsureMaterials();
                    var matComposite = system.m_MatComposite;
                    if (matComposite == null)
                        return;

                    var settings = GaussianSplatSettings.instance;
                    var usingRT = !settings.isDebugRender;
                    // XR multi-pass: the temporal filter relies on a single history buffer and per-eye
                    // motion vectors it doesn't produce, so it would ghost across eyes. Disable under XR.
                    var temporalOn = settings.m_TemporalFilter != TemporalFilter.None && !UnityEngine.XR.XRSettings.isDeviceActive;

                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);

                    if (usingRT)
                    {
                        // bind both color and motion targets as global textures
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatRT, data.GaussianSplatRT);
                        commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.GaussianSplatMotionRT, data.GaussianSplatMotionRT);
                        
                        if (temporalOn)
                        {
                            // Render to both color and motion RTs
                            CoreUtils.SetRenderTarget(commandBuffer,
                                new RenderTargetIdentifier[] { data.GaussianSplatRT, data.GaussianSplatMotionRT },
                                data.SourceDepth, ClearFlag.None);
                        }
                        else
                        {
                            // Render only to color RT
                            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.None);
                        }
                    }
                    else
                    {
                        CoreUtils.SetRenderTarget(commandBuffer, data.SourceTexture, data.SourceDepth, ClearFlag.None);
                    }
                    system.RenderAllSplats(data.CameraData.camera, commandBuffer);
                    if (usingRT)
                    {
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        if (temporalOn)
                        {
                            // use temporal filter to composite; pass the render graph texture handles directly
                            system.GetTemporalFilter().Render(commandBuffer, data.CameraData.camera, matComposite, 1,
                                data.GaussianSplatRT, data.SourceTexture,
                                data.GaussianSplatWidth, data.GaussianSplatHeight,
                                settings.m_FrameInfluence, settings.m_VarianceClampScale,
                                data.GaussianSplatMotionRT);
                        }
                        else
                        {
                            Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.SourceTexture, matComposite, 0);
                        }
                         commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    }
                });
            }
        }

        GSRenderPass m_Pass;
        bool m_HasCamera;

        public override void Create()
        {
            m_Pass = new GSRenderPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };

            // Structural fix: previously mutated GraphicsSettings.currentRenderPipeline.renderScale here,
            // which writes to the SHARED UniversalRenderPipelineAsset ScriptableObject and dirties the
            // on-disk .asset every Play at a different Game view size. That caused Medium_PipelineAsset.asset
            // m_RenderScale to drift (0.7365 -> 1.0711), producing 2.116x pixel work and visible overdraw
            // streaks + FPS collapse. Rely on statically-authored pipeline asset variants instead.
            // Note: m_OverrideResolution / m_MaxSize are retained as serialized fields for backwards
            // compatibility with existing feature configurations but no longer apply dynamic scaling.
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!m_HasCamera)
                return;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            // no restore of pipeline asset
            m_Pass = null;
        }
    }
}

#endif // #if GS_ENABLE_URP
