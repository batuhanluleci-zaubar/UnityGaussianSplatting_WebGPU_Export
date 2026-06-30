// SPDX-License-Identifier: MIT
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.Splines;

namespace GsplatTour
{
    /// <summary>
    /// Drives a <see cref="CinemachineSplineDolly"/> along its spline to produce an
    /// automatic cinematic fly-through of the scene (e.g. a gaussian-splat capture).
    ///
    /// Progress is advanced in normalized [0..1] units so the same settings work for any
    /// spline length. For a seamless continuous tour use a <b>closed</b> SplineContainer with
    /// <see cref="LoopMode.Loop"/> and a linear easing curve. For an open path,
    /// <see cref="LoopMode.PingPong"/> with an ease-in/out curve reads best.
    /// </summary>
    [AddComponentMenu("Gsplat/Spline Cinematic Director")]
    [DisallowMultipleComponent]
    public class SplineCinematicDirector : MonoBehaviour
    {
        public enum LoopMode { Once, Loop, PingPong }

        [Header("Target")]
        [Tooltip("The CinemachineSplineDolly body this director advances along its spline. " +
                 "If left empty, a CinemachineSplineDolly on this same GameObject is used.")]
        [SerializeField] CinemachineSplineDolly m_Dolly;

        [Header("Playback")]
        [SerializeField] bool m_PlayOnStart = true;
        [Tooltip("Seconds for one full pass along the spline (0 -> 1).")]
        [Min(0.01f)]
        [SerializeField] float m_Duration = 30f;
        [SerializeField] LoopMode m_Loop = LoopMode.Loop;
        [Range(0f, 1f)]
        [Tooltip("Where playback starts along the spline, normalized.")]
        [SerializeField] float m_StartNormalized = 0f;
        [Tooltip("Remaps linear progress to ease the camera in/out. X and Y both span 0..1. " +
                 "Keep this linear for a seamless closed-loop tour.")]
        [SerializeField] AnimationCurve m_Easing = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        float m_Progress;     // raw linear progress 0..1
        int m_Direction = 1;  // travel direction (for ping-pong)
        bool m_Playing;

        public bool IsPlaying => m_Playing;
        public float NormalizedPosition => m_Progress;

        void Reset()
        {
            m_Dolly = GetComponent<CinemachineSplineDolly>();
        }

        void OnEnable()
        {
            if (m_Dolly == null)
                m_Dolly = GetComponent<CinemachineSplineDolly>();
            if (m_Dolly != null)
                m_Dolly.PositionUnits = PathIndexUnit.Normalized;
            m_Progress = Mathf.Clamp01(m_StartNormalized);
            m_Direction = 1;
            Apply();
        }

        void Start()
        {
            m_Playing = m_PlayOnStart;
        }

        void Update()
        {
            if (!m_Playing || m_Dolly == null || m_Duration <= 0f)
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

            Apply();
        }

        void Apply()
        {
            if (m_Dolly == null)
                return;
            float eased = m_Easing != null ? m_Easing.Evaluate(m_Progress) : m_Progress;
            m_Dolly.CameraPosition = eased;
        }

        // ---- Public control API (wire to UI buttons, input, or XR state) ----
        public void Play() => m_Playing = true;
        public void Pause() => m_Playing = false;
        public void TogglePlayPause() => m_Playing = !m_Playing;

        public void Restart()
        {
            m_Progress = Mathf.Clamp01(m_StartNormalized);
            m_Direction = 1;
            Apply();
            m_Playing = true;
        }

        public void SetNormalizedPosition(float t)
        {
            m_Progress = Mathf.Clamp01(t);
            Apply();
        }

        public void SetDuration(float seconds) => m_Duration = Mathf.Max(0.01f, seconds);
    }
}
