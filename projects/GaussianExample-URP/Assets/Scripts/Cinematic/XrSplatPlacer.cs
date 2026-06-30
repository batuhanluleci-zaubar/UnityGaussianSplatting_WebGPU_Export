// SPDX-License-Identifier: MIT
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

namespace GsplatTour
{
    /// <summary>
    /// On an XR device, rescales the gaussian splat to a comfortable room/tabletop size and places
    /// it in front of the user — raw captures can be hundreds of metres across, so without this you
    /// start buried inside the cloud. Uses <see cref="GsplatAutoFramer"/> for an outlier-robust
    /// content centre + radius. Call <see cref="Recenter"/> (e.g. from a controller button) to bring
    /// the content back in front of the current head pose.
    /// </summary>
    [AddComponentMenu("Gsplat/XR Splat Placer")]
    public class XrSplatPlacer : MonoBehaviour
    {
        [SerializeField] GaussianSplatRenderer m_Splat;
        [Tooltip("The XR head/camera. If empty, Camera.main is used.")]
        [SerializeField] Transform m_Head;

        [Header("Placement (metres)")]
        [Tooltip("Content radius after rescale — roughly a tabletop model.")]
        [SerializeField] float m_TargetRadius = 0.75f;
        [Tooltip("Distance in front of the user to put the content centre.")]
        [SerializeField] float m_Distance = 1.5f;
        [Tooltip("Vertical offset of the content centre relative to the head.")]
        [SerializeField] float m_HeightOffset = 0f;

        [SerializeField] bool m_OnlyUnderXR = true;
        [Tooltip("Place once on Start. Tracking may not be settled yet — re-trigger Recenter() when ready.")]
        [SerializeField] bool m_PlaceOnStart = true;

        void Start()
        {
            if (m_OnlyUnderXR && !XRSettings.isDeviceActive)
                return;
            if (m_PlaceOnStart)
                Recenter();
        }

        public void Recenter()
        {
            if (m_Splat == null) m_Splat = FindAnyObjectByType<GaussianSplatRenderer>();
            if (m_Splat == null) return;

            Transform head = m_Head != null ? m_Head : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;

            if (!GsplatAutoFramer.TryCompute(m_Splat, out var frame))
                return;

            Transform st = m_Splat.transform;

            // content centre in splat-local space, captured BEFORE rescaling
            Vector3 localCenter = st.InverseTransformPoint(frame.center);

            // uniform rescale (keeps axis signs, e.g. mirrored X) so the content radius hits the target
            float factor = m_TargetRadius / Mathf.Max(frame.radius, 1e-4f);
            st.localScale = st.localScale * factor;

            // target world position: in front of the head at eye height, on the horizontal plane
            Vector3 fwd = head.forward; fwd.y = 0f;
            fwd = fwd.sqrMagnitude < 1e-4f ? Vector3.forward : fwd.normalized;
            Vector3 target = head.position + fwd * m_Distance + Vector3.up * m_HeightOffset;

            // shift the splat so the (rescaled) content centre lands on the target
            Vector3 centerAfterScale = st.TransformPoint(localCenter);
            st.position += target - centerAfterScale;

            Debug.Log($"[XrSplatPlacer] recentred: scale×{factor:F4}, radius {frame.radius:F1}→{m_TargetRadius}m, " +
                      $"centre@{target}, robust={frame.fromChunks}");
        }
    }
}
