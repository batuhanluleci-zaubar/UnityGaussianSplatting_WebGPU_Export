// SPDX-License-Identifier: MIT
// Runtime auto-fits a Cinemachine spline around whichever gsplat scene the streamer loads.
//
// Problem this solves: the tour spline was authored at edit time with knot positions
// hand-fit to a specific asset (e.g. (239.94, 36.9, 91.67) etc.). Any time the streamer
// loads a differently-sized or differently-placed scene those knots become garbage —
// camera flies through walls or misses the content entirely.
//
// This component polls GaussianLodStreamAsync until its bounds are known (streamer's Start
// finished + first chunk resolved), then generates a closed orbit spline around SceneCentre
// at radius = SceneRadius * radiusMultiplier, elevated by SceneRadius * heightMultiplier.
// A CinemachineSplineDolly attached to the same SplineContainer follows it automatically;
// if a lookAtTarget is provided, it is moved to SceneCentre so the CM camera always aims
// at the scene.
using System.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

namespace GsplatLod
{
    [RequireComponent(typeof(SplineContainer))]
    public class SplineAutoFromGsplat : MonoBehaviour
    {
        [Tooltip("Optional. Leave empty to auto-find the GaussianLodStreamAsync in the scene.")]
        public GaussianLodStreamAsync streamer;
        [Tooltip("Optional. If set, this Transform is moved to the gsplat scene centre so the CM " +
                 "camera's LookAt keeps framing the content.")]
        public Transform lookAtTarget;

        [Header("Orbit shape")]
        [Range(4, 24)] public int knotCount = 6;
        [Tooltip("Orbit radius as a multiple of the gsplat SceneRadius. INTERIOR walk-through: 0.3-0.5 " +
                 "(orbits inside a hall). OUTSIDE-orbit view: 1.2-1.5. Default 0.35 = walk-through inside.")]
        [Range(0.1f, 4f)] public float radiusMultiplier = 0.35f;
        [Tooltip("Orbit height above SceneCentre.y, as a multiple of SceneRadius. Negative pulls the camera " +
                 "TOWARD the floor of the scene (e.g. -0.08 for eye-level walk-through of a hall). Positive " +
                 "elevates for aerial/outside orbit.")]
        [Range(-1f, 1f)] public float heightMultiplier = -0.08f;
        public bool orbitClockwise = true;
        [Tooltip("Regenerate the spline if SceneCentre / SceneRadius change (e.g. streamer swaps manifests mid-run). 0 = only once at start.")]
        [Range(0f, 10f)] public float regenerateInterval = 0f;

        SplineContainer m_Container;
        Vector3 m_LastCentre = Vector3.positiveInfinity;
        float m_LastRadius = -1f;
        float m_LastRegenTime = -999f;

        IEnumerator Start()
        {
            m_Container = GetComponent<SplineContainer>();
            if (streamer == null) streamer = FindObjectOfType<GaussianLodStreamAsync>();
            if (streamer == null)
            {
                Debug.LogError("[SplineAuto] no GaussianLodStreamAsync found — assign one or add it to the scene.");
                yield break;
            }
            // Streamer's Start() sets SceneRadius as the LAST step; poll until that publishes.
            while (!streamer.BoundsReady) yield return null;
            Rebuild();
        }

        void Update()
        {
            if (regenerateInterval <= 0f || streamer == null || !streamer.BoundsReady) return;
            if (Time.time - m_LastRegenTime < regenerateInterval) return;
            if (m_LastCentre == streamer.SceneCentre && Mathf.Approximately(m_LastRadius, streamer.SceneRadius)) return;
            Rebuild();
        }

        void Rebuild()
        {
            var centre = streamer.SceneCentre;
            var sceneR = streamer.SceneRadius;
            var orbitR = sceneR * radiusMultiplier;
            var height = centre.y + sceneR * heightMultiplier;

            var spline = new Spline { Closed = true };
            float dt = 2f * Mathf.PI / knotCount;
            for (int i = 0; i < knotCount; i++)
            {
                float t = i * dt;
                if (orbitClockwise) t = -t;
                float sx = Mathf.Sin(t), sz = Mathf.Cos(t);
                var pos = new float3(centre.x + sx * orbitR, height, centre.z + sz * orbitR);

                // Tangent along the orbit: derivative (cos, -sin) scaled ~ half the knot arc length,
                // which gives a smooth continuous Bezier orbit without loops or cusps at the seams.
                float tScale = orbitR * dt * 0.5f;
                var tan = new float3(Mathf.Cos(t) * (orbitClockwise ? -1f : 1f), 0,
                                     -Mathf.Sin(t) * (orbitClockwise ? -1f : 1f)) * tScale;
                spline.Add(new BezierKnot(pos, -tan, tan, quaternion.identity));
            }

            m_Container.Spline = spline;
            if (lookAtTarget != null) lookAtTarget.position = centre;

            m_LastCentre = centre; m_LastRadius = sceneR; m_LastRegenTime = Time.time;
            Debug.Log($"[SplineAuto] rebuilt orbit spline: centre={centre} sceneR={sceneR:F1} orbitR={orbitR:F1} height={height:F1} knots={knotCount}");
        }
    }
}
