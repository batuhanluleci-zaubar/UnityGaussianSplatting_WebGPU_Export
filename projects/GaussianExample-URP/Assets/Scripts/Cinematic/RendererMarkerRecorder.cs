// SPDX-License-Identifier: MIT
// Alt#1 diagnostic recorder — reads the two new ProfilerMarkers added in P3 gate check:
//   - GaussianSplatOctree.SortChunks    (CPU sort per frame; front-to-back visible-splat walk)
//   - GaussianSplatRenderer.SubmitDraws (CPU submit per frame; N-chunk MPB churn + DrawProcedural record)
//
// Rolls a 120-frame average and either logs to console every 2 s or writes CSV rows to
// {Application.persistentDataPath}/gsplat_perf.csv (turned on via serialized flag).
//
// Drop onto any GameObject in a test scene. Zero-alloc in the sample loop; ProfilerRecorder
// handles the Unity-side buffer. Works in editor + Development build. Disable before ship builds.
using System.Text;
using Unity.Profiling;
using UnityEngine;

namespace GsplatLod
{
    public class RendererMarkerRecorder : MonoBehaviour
    {
        const int kWindow = 120;

        [Tooltip("Every N seconds, print the 120-frame average sort/submit split to the console.")]
        [Range(0.5f, 10f)] public float logIntervalSeconds = 2f;
        [Tooltip("Also append each rolling average to gsplat_perf.csv under Application.persistentDataPath.")]
        public bool alsoWriteCsv = false;
        [Tooltip("Reset the rolling window when the streamer's [R] reset key is pressed.")]
        public KeyCode resetKey = KeyCode.T;

        ProfilerRecorder m_SortRec;
        ProfilerRecorder m_SubmitRec;
        ProfilerRecorder m_MainRec;
        readonly double[] m_SortRing = new double[kWindow];
        readonly double[] m_SubmitRing = new double[kWindow];
        readonly double[] m_MainRing = new double[kWindow];
        int m_RingIdx;
        int m_RingFill;
        float m_LastLog;
        string m_CsvPath;

        void OnEnable()
        {
            m_SortRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.SortChunks", kWindow);
            m_SubmitRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatRenderer.SubmitDraws", kWindow);
            m_MainRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", kWindow);
            m_CsvPath = System.IO.Path.Combine(Application.persistentDataPath, "gsplat_perf.csv");
            if (alsoWriteCsv && !System.IO.File.Exists(m_CsvPath))
                System.IO.File.WriteAllText(m_CsvPath, "t_seconds,sort_ms_avg,submit_ms_avg,main_ms_avg,fps_avg\n");
        }

        void OnDisable()
        {
            if (m_SortRec.Valid) m_SortRec.Dispose();
            if (m_SubmitRec.Valid) m_SubmitRec.Dispose();
            if (m_MainRec.Valid) m_MainRec.Dispose();
        }

        void LateUpdate()
        {
            if (Input.GetKeyDown(resetKey)) { m_RingIdx = 0; m_RingFill = 0; Debug.Log("[MarkerRecorder] reset window"); }

            m_SortRing[m_RingIdx] = m_SortRec.LastValue * 1e-6;      // ns -> ms
            m_SubmitRing[m_RingIdx] = m_SubmitRec.LastValue * 1e-6;
            m_MainRing[m_RingIdx] = m_MainRec.LastValue * 1e-6;
            m_RingIdx = (m_RingIdx + 1) % kWindow;
            if (m_RingFill < kWindow) m_RingFill++;

            if (Time.unscaledTime - m_LastLog < logIntervalSeconds || m_RingFill < 30) return;
            m_LastLog = Time.unscaledTime;

            double sortSum = 0, submitSum = 0, mainSum = 0;
            for (int i = 0; i < m_RingFill; i++)
            {
                sortSum += m_SortRing[i]; submitSum += m_SubmitRing[i]; mainSum += m_MainRing[i];
            }
            double sortAvg = sortSum / m_RingFill;
            double submitAvg = submitSum / m_RingFill;
            double mainAvg = mainSum / m_RingFill;
            double fpsAvg = mainAvg > 0.01 ? 1000.0 / mainAvg : 0;

            Debug.Log($"[MarkerRecorder] N={m_RingFill}  sort={sortAvg:F3}ms  submit={submitAvg:F3}ms  " +
                      $"(sort+submit={sortAvg + submitAvg:F3}ms of main={mainAvg:F2}ms  ->  fps~{fpsAvg:F0})");

            if (alsoWriteCsv)
            {
                var sb = new StringBuilder();
                sb.Append(Time.unscaledTime.ToString("F2")).Append(',');
                sb.Append(sortAvg.ToString("F4")).Append(',');
                sb.Append(submitAvg.ToString("F4")).Append(',');
                sb.Append(mainAvg.ToString("F4")).Append(',');
                sb.Append(fpsAvg.ToString("F1")).Append('\n');
                System.IO.File.AppendAllText(m_CsvPath, sb.ToString());
            }
        }
    }
}
