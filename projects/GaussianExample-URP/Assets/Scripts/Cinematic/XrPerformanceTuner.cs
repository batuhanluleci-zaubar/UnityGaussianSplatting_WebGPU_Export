// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace GsplatTour
{
    /// <summary>
    /// Applies mobile/XR performance settings at startup when running on an XR device
    /// (Android XR / OpenXR). Gaussian-splat rendering is dominated by fill-rate — many
    /// overlapping transparent quads blended per pixel — so the biggest lever by far is the
    /// per-eye render resolution. Foveation and a throttled cull/sort add further headroom.
    ///
    /// All values are inspector-tunable; on-device profiling should drive the final numbers.
    /// </summary>
    [AddComponentMenu("Gsplat/XR Performance Tuner")]
    [DefaultExecutionOrder(-90)] // after XrSceneBootstrap (-100), before first render
    public class XrPerformanceTuner : MonoBehaviour
    {
        [Tooltip("Only apply when an XR device is active (recommended; leaves desktop/WebGL untouched).")]
        [SerializeField] bool m_OnlyUnderXR = true;

        [Header("Per-eye render resolution — biggest fill-rate lever")]
        [Tooltip("Scales the eye render targets (the splat RT inherits this). 0.7 ≈ half the pixels of 1.0. Lower = faster, softer.")]
        [Range(0.5f, 1.0f)] [SerializeField] float m_EyeResolutionScale = 0.7f;

        [Header("Foveated rendering (Adreno fixed foveation)")]
        [Tooltip("0 = off, 1 = maximum peripheral reduction. Benefits the URP/composite passes; the splat RT pass may need explicit VRS wiring to fully benefit (see GSPLAT_TOUR_XR_SETUP.md).")]
        [Range(0f, 1f)] [SerializeField] float m_FoveationLevel = 1.0f;

        [Header("Splat cull / sort throttle")]
        [SerializeField] bool m_EnableOctreeCulling = true;
        [Tooltip("Re-cull + re-sort every N frames. 2-3 throttles the GPU radix sort with little visible cost during slow head motion.")]
        [Range(1, 6)] [SerializeField] int m_CullingUpdateInterval = 2;
        [Tooltip("Stochastic skips the depth sort (faster, slightly noisier); AlphaBlend keeps sorted quality.")]
        [SerializeField] TransparencyMode m_Transparency = TransparencyMode.AlphaBlend;

        [Header("Screen-space LOD (dominant lever for big scenes)")]
        [Tooltip("Subsample distant/on-screen-small splats. This cuts the DrawProcedural instance count, the #1 cost for multi-million-splat scenes.")]
        [SerializeField] bool m_EnableScreenLod = true;
        [Tooltip("Full-detail pixel threshold: nodes larger than this on screen stay full detail, smaller ones thin. Lower = more aggressive; mobile ~120-180.")]
        [Range(20f, 600f)] [SerializeField] float m_LodFullDetailPixels = 150f;
        [Range(1, 64)] [SerializeField] int m_LodMaxStride = 20;
        [Tooltip("Hard cap on rendered splats on-device (0 = unlimited). Keeps the nearest splats, drops the farthest — set from measured device frame time.")]
        [Min(0)] [SerializeField] int m_LodSplatBudget = 1200000;

        void Start()
        {
            bool xr = XRSettings.isDeviceActive;
            if (m_OnlyUnderXR && !xr)
                return;

            if (xr)
                XRSettings.eyeTextureResolutionScale = m_EyeResolutionScale;

            ApplyFoveation(m_FoveationLevel);

            var gs = GaussianSplatSettings.instance;
            if (gs != null)
            {
                gs.m_EnableOctreeCulling = m_EnableOctreeCulling;
                gs.m_OctreeCullingUpdateInterval = Mathf.Max(1, m_CullingUpdateInterval);
                gs.m_Transparency = m_Transparency;
                gs.m_EnableScreenLod = m_EnableScreenLod;
                gs.m_LodFullDetailPixels = m_LodFullDetailPixels;
                gs.m_LodMaxStride = m_LodMaxStride;
                gs.m_LodSplatBudget = m_LodSplatBudget;
            }

            Debug.Log($"[XrPerformanceTuner] XR={xr} eyeScale={(xr ? m_EyeResolutionScale : 1f)} " +
                      $"foveation={m_FoveationLevel} cullInterval={m_CullingUpdateInterval} transparency={m_Transparency}");
        }

        static void ApplyFoveation(float level)
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (var d in displays)
            {
                if (d == null || !d.running)
                    continue;
                try { d.foveatedRenderingLevel = level; }
                catch (System.Exception e) { Debug.LogWarning($"[XrPerformanceTuner] foveation not applied: {e.Message}"); }
            }
        }
    }
}
