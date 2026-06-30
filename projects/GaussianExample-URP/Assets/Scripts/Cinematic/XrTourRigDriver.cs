// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Splines;

namespace GsplatTour
{
    /// <summary>
    /// Optional "guided tour" for XR: moves an XR Origin (the tracked rig root) along the same
    /// SplineContainer the desktop Cinemachine tour uses, while head tracking still owns the
    /// camera's local orientation. Disabled by default — an auto-moving rig can cause discomfort,
    /// so enable it only for a deliberate on-rails AR fly-through.
    ///
    /// This drives the rig transform directly (independent of Cinemachine, which must not fight
    /// the head-tracked camera in XR).
    /// </summary>
    [AddComponentMenu("Gsplat/XR Tour Rig Driver")]
    [DisallowMultipleComponent]
    public class XrTourRigDriver : MonoBehaviour
    {
        public enum LoopMode { Once, Loop, PingPong }

        [Header("References")]
        [Tooltip("The path to follow (the same one the desktop Cinemachine tour uses).")]
        [SerializeField] SplineContainer m_Spline;
        [Tooltip("The XR Origin / rig root to move. If empty, this GameObject's transform is moved.")]
        [SerializeField] Transform m_Rig;
        [Tooltip("Optional point the rig yaws toward (e.g. the splat centre). Pitch is left to head tracking.")]
        [SerializeField] Transform m_FaceTarget;

        [Header("Playback")]
        [SerializeField] bool m_PlayOnEnable = false;
        [Min(0.01f)]
        [Tooltip("Seconds for one full pass along the spline.")]
        [SerializeField] float m_Duration = 45f;
        [SerializeField] LoopMode m_Loop = LoopMode.Loop;
        [Tooltip("How quickly the rig's yaw eases toward the face target (deg/sec-ish). 0 = snap.")]
        [SerializeField] float m_YawDamping = 4f;

        float m_Progress;
        int m_Direction = 1;
        bool m_Playing;

        public bool IsPlaying => m_Playing;

        void OnEnable()
        {
            if (m_Rig == null) m_Rig = transform;
            m_Playing = m_PlayOnEnable;
        }

        void Update()
        {
            if (!m_Playing || m_Spline == null || m_Rig == null || m_Duration <= 0f)
                return;

            m_Progress += m_Direction * (Time.deltaTime / m_Duration);
            switch (m_Loop)
            {
                case LoopMode.Once:
                    if (m_Progress >= 1f) { m_Progress = 1f; m_Playing = false; }
                    else if (m_Progress <= 0f) { m_Progress = 0f; m_Playing = false; }
                    break;
                case LoopMode.Loop:
                    if (m_Progress >= 1f) m_Progress -= 1f;
                    else if (m_Progress < 0f) m_Progress += 1f;
                    break;
                case LoopMode.PingPong:
                    if (m_Progress >= 1f) { m_Progress = 1f; m_Direction = -1; }
                    else if (m_Progress <= 0f) { m_Progress = 0f; m_Direction = 1; }
                    break;
            }

            // SplineContainer evaluates in world space (applies its transform).
            Vector3 pos = m_Spline.EvaluatePosition(Mathf.Clamp01(m_Progress));
            m_Rig.position = pos;

            if (m_FaceTarget != null)
            {
                Vector3 flatDir = m_FaceTarget.position - pos;
                flatDir.y = 0f;
                if (flatDir.sqrMagnitude > 1e-4f)
                {
                    Quaternion want = Quaternion.LookRotation(flatDir.normalized, Vector3.up);
                    m_Rig.rotation = m_YawDamping > 0f
                        ? Quaternion.Slerp(m_Rig.rotation, want, 1f - Mathf.Exp(-m_YawDamping * Time.deltaTime))
                        : want;
                }
            }
        }

        // ---- Public control API (wire to an XR controller button or UI) ----
        public void Play() => m_Playing = true;
        public void Pause() => m_Playing = false;
        public void TogglePlayPause() => m_Playing = !m_Playing;
        public void Restart() { m_Progress = 0f; m_Direction = 1; m_Playing = true; }
    }
}
