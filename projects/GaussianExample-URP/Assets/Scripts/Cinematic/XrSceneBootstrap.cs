// SPDX-License-Identifier: MIT
using UnityEngine;

namespace GsplatTour
{
    /// <summary>
    /// Selects the active camera setup at startup so a single scene serves both targets:
    ///  - On an XR device (Android XR / OpenXR): enables the XR Origin rig + AR Session and
    ///    disables the desktop Cinemachine tour camera (head tracking owns the camera there).
    ///  - On desktop / WebGL (no XR loader): keeps the Cinemachine cinematic tour and disables
    ///    the XR rig and AR Session.
    ///
    /// This avoids the classic conflict of a CinemachineBrain and a head-tracking TrackedPoseDriver
    /// both writing the same camera transform.
    /// </summary>
    [AddComponentMenu("Gsplat/XR Scene Bootstrap")]
    [DefaultExecutionOrder(-100)]
    public class XrSceneBootstrap : MonoBehaviour
    {
        [Header("XR (Android XR / OpenXR)")]
        [SerializeField] GameObject m_XrOrigin;
        [SerializeField] GameObject m_ArSession;

        [Header("Desktop / WebGL")]
        [Tooltip("The Main Camera that carries the CinemachineBrain for the cinematic tour.")]
        [SerializeField] GameObject m_DesktopTourCamera;

        [Tooltip("Force a mode in the Editor for testing, instead of auto-detecting the XR loader.")]
        [SerializeField] Mode m_EditorOverride = Mode.Auto;
        public enum Mode { Auto, ForceXR, ForceDesktop }

        void Awake()
        {
            bool xr = ResolveXrActive();

            if (m_XrOrigin != null) m_XrOrigin.SetActive(xr);
            if (m_ArSession != null) m_ArSession.SetActive(xr);
            if (m_DesktopTourCamera != null) m_DesktopTourCamera.SetActive(!xr);

            Debug.Log($"[XrSceneBootstrap] Starting in {(xr ? "XR (Android XR)" : "Desktop/WebGL")} mode.");
        }

        bool ResolveXrActive()
        {
            if (m_EditorOverride == Mode.ForceXR) return true;
            if (m_EditorOverride == Mode.ForceDesktop) return false;

            // Core UnityEngine.XR signal — no XR-package assembly reference needed. With
            // "Initialize XR on Startup" (default), the device is already active by Awake on-device.
            return UnityEngine.XR.XRSettings.isDeviceActive;
        }
    }
}
