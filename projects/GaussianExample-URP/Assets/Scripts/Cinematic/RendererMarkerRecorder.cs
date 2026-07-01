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
        // TEMP: sub-stage recorders for the SortVisibleSplatsByDepth breakdown (remove with the octree markers)
        ProfilerRecorder m_SortCollectRec;
        ProfilerRecorder m_SortStartRec;
        ProfilerRecorder m_SortWaitRec;
        ProfilerRecorder m_SortAppendRec;
        // Slice 2 / Rank 1: split-append markers
        ProfilerRecorder m_SortAppendCpuRec;
        ProfilerRecorder m_SortAppendUploadRec;
        // Slice 3: strided-cache counters — Unity's ProfilerCounter<T> API isn't reliably available in
        // this Unity version, so we read directly from GaussianSplatOctree's static aggregate fields
        // (public static s_LastFrameCacheHits / Misses / Evictions / s_LiveCacheBytes). Same rolling
        // window semantics as the profiler recorders above.
        readonly double[] m_SortRing = new double[kWindow];
        readonly double[] m_SubmitRing = new double[kWindow];
        readonly double[] m_MainRing = new double[kWindow];
        readonly double[] m_SortCollectRing = new double[kWindow];
        readonly double[] m_SortStartRing = new double[kWindow];
        readonly double[] m_SortWaitRing = new double[kWindow];
        readonly double[] m_SortAppendRing = new double[kWindow];
        readonly double[] m_SortAppendCpuRing = new double[kWindow];
        readonly double[] m_SortAppendUploadRing = new double[kWindow];
        // Slice 3 rings — hits/misses are per-frame counts; bytes is instantaneous.
        readonly double[] m_CacheHitsRing = new double[kWindow];
        readonly double[] m_CacheMissesRing = new double[kWindow];
        readonly double[] m_CacheEvictionsRing = new double[kWindow];
        readonly double[] m_CacheBytesRing = new double[kWindow];
        int m_RingIdx;
        int m_RingFill;
        float m_LastLog;
        string m_CsvPath;

        void OnEnable()
        {
            m_SortRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.SortChunks", kWindow);
            m_SubmitRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatRenderer.SubmitDraws", kWindow);
            m_MainRec = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", kWindow);
            m_SortCollectRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.Collect", kWindow);
            m_SortStartRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.Start", kWindow);
            m_SortWaitRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.Wait", kWindow);
            m_SortAppendRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.Append", kWindow);
            m_SortAppendCpuRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.AppendCpu", kWindow);
            m_SortAppendUploadRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "GaussianSplatOctree.Sort.AppendUpload", kWindow);
            m_CsvPath = System.IO.Path.Combine(Application.persistentDataPath, "gsplat_perf.csv");
            if (alsoWriteCsv && !System.IO.File.Exists(m_CsvPath))
                System.IO.File.WriteAllText(m_CsvPath, "t_seconds,sort_ms_avg,submit_ms_avg,main_ms_avg,fps_avg,collect_ms,start_ms,wait_ms,append_ms,append_cpu_ms,append_upload_ms,cache_hits,cache_misses,cache_evict,cache_bytes\n");
        }

        void OnDisable()
        {
            if (m_SortRec.Valid) m_SortRec.Dispose();
            if (m_SubmitRec.Valid) m_SubmitRec.Dispose();
            if (m_MainRec.Valid) m_MainRec.Dispose();
            if (m_SortCollectRec.Valid) m_SortCollectRec.Dispose();
            if (m_SortStartRec.Valid) m_SortStartRec.Dispose();
            if (m_SortWaitRec.Valid) m_SortWaitRec.Dispose();
            if (m_SortAppendRec.Valid) m_SortAppendRec.Dispose();
            if (m_SortAppendCpuRec.Valid) m_SortAppendCpuRec.Dispose();
            if (m_SortAppendUploadRec.Valid) m_SortAppendUploadRec.Dispose();
        }

        void LateUpdate()
        {
            if (Input.GetKeyDown(resetKey)) { m_RingIdx = 0; m_RingFill = 0; Debug.Log("[MarkerRecorder] reset window"); }

            m_SortRing[m_RingIdx] = m_SortRec.LastValue * 1e-6;      // ns -> ms
            m_SubmitRing[m_RingIdx] = m_SubmitRec.LastValue * 1e-6;
            m_MainRing[m_RingIdx] = m_MainRec.LastValue * 1e-6;
            m_SortCollectRing[m_RingIdx] = m_SortCollectRec.LastValue * 1e-6;
            m_SortStartRing[m_RingIdx] = m_SortStartRec.LastValue * 1e-6;
            m_SortWaitRing[m_RingIdx] = m_SortWaitRec.LastValue * 1e-6;
            m_SortAppendRing[m_RingIdx] = m_SortAppendRec.LastValue * 1e-6;
            m_SortAppendCpuRing[m_RingIdx] = m_SortAppendCpuRec.LastValue * 1e-6;
            m_SortAppendUploadRing[m_RingIdx] = m_SortAppendUploadRec.LastValue * 1e-6;
            // Slice 3 counters — read the GaussianSplatOctree static aggregates directly.
            m_CacheHitsRing[m_RingIdx] = GaussianSplatting.Runtime.GaussianSplatOctree.s_LastFrameCacheHits;
            m_CacheMissesRing[m_RingIdx] = GaussianSplatting.Runtime.GaussianSplatOctree.s_LastFrameCacheMisses;
            m_CacheEvictionsRing[m_RingIdx] = GaussianSplatting.Runtime.GaussianSplatOctree.s_LastFrameCacheEvictions;
            m_CacheBytesRing[m_RingIdx] = GaussianSplatting.Runtime.GaussianSplatOctree.s_LiveCacheBytes;
            m_RingIdx = (m_RingIdx + 1) % kWindow;
            if (m_RingFill < kWindow) m_RingFill++;

            if (Time.unscaledTime - m_LastLog < logIntervalSeconds || m_RingFill < 30) return;
            m_LastLog = Time.unscaledTime;

            double sortSum = 0, submitSum = 0, mainSum = 0;
            double collectSum = 0, startSum = 0, waitSum = 0, appendSum = 0;
            double appendCpuSum = 0, appendUploadSum = 0;
            double hitsSum = 0, missesSum = 0, evictSum = 0, bytesSum = 0;
            for (int i = 0; i < m_RingFill; i++)
            {
                sortSum += m_SortRing[i]; submitSum += m_SubmitRing[i]; mainSum += m_MainRing[i];
                collectSum += m_SortCollectRing[i]; startSum += m_SortStartRing[i];
                waitSum += m_SortWaitRing[i]; appendSum += m_SortAppendRing[i];
                appendCpuSum += m_SortAppendCpuRing[i]; appendUploadSum += m_SortAppendUploadRing[i];
                hitsSum += m_CacheHitsRing[i]; missesSum += m_CacheMissesRing[i];
                evictSum += m_CacheEvictionsRing[i]; bytesSum += m_CacheBytesRing[i];
            }
            double sortAvg = sortSum / m_RingFill;
            double submitAvg = submitSum / m_RingFill;
            double mainAvg = mainSum / m_RingFill;
            double collectAvg = collectSum / m_RingFill;
            double startAvg = startSum / m_RingFill;
            double waitAvg = waitSum / m_RingFill;
            double appendAvg = appendSum / m_RingFill;
            double appendCpuAvg = appendCpuSum / m_RingFill;
            double appendUploadAvg = appendUploadSum / m_RingFill;
            double hitsAvg = hitsSum / m_RingFill;
            double missesAvg = missesSum / m_RingFill;
            double evictAvg = evictSum / m_RingFill;
            double bytesAvg = bytesSum / m_RingFill;
            double hitRatio = (hitsAvg + missesAvg) > 0.0 ? hitsAvg / (hitsAvg + missesAvg) : 0.0;
            double fpsAvg = mainAvg > 0.01 ? 1000.0 / mainAvg : 0;

            Debug.Log($"[MarkerRecorder] N={m_RingFill}  sort={sortAvg:F3}ms  submit={submitAvg:F3}ms  " +
                      $"(sort+submit={sortAvg + submitAvg:F3}ms of main={mainAvg:F2}ms  ->  fps~{fpsAvg:F0})");
            Debug.Log($"[MarkerRecorder-Sort] collect={collectAvg:F3}ms  start={startAvg:F3}ms  wait={waitAvg:F3}ms  append={appendAvg:F3}ms  " +
                      $"(cpu={appendCpuAvg:F3}ms upload={appendUploadAvg:F3}ms)  " +
                      $"(sum={collectAvg + startAvg + waitAvg + appendAvg:F3}ms vs total sort={sortAvg:F3}ms)");
            Debug.Log($"[MarkerRecorder-Cache] hits/frame={hitsAvg:F1}  misses/frame={missesAvg:F1}  " +
                      $"hit_ratio={hitRatio * 100.0:F1}%  evict/frame={evictAvg:F1}  bytes={bytesAvg / (1024.0 * 1024.0):F2}MB");

            if (alsoWriteCsv)
            {
                var sb = new StringBuilder();
                sb.Append(Time.unscaledTime.ToString("F2")).Append(',');
                sb.Append(sortAvg.ToString("F4")).Append(',');
                sb.Append(submitAvg.ToString("F4")).Append(',');
                sb.Append(mainAvg.ToString("F4")).Append(',');
                sb.Append(fpsAvg.ToString("F1")).Append(',');
                sb.Append(collectAvg.ToString("F4")).Append(',');
                sb.Append(startAvg.ToString("F4")).Append(',');
                sb.Append(waitAvg.ToString("F4")).Append(',');
                sb.Append(appendAvg.ToString("F4")).Append(',');
                sb.Append(appendCpuAvg.ToString("F4")).Append(',');
                sb.Append(appendUploadAvg.ToString("F4")).Append(',');
                sb.Append(hitsAvg.ToString("F2")).Append(',');
                sb.Append(missesAvg.ToString("F2")).Append(',');
                sb.Append(evictAvg.ToString("F2")).Append(',');
                sb.Append(bytesAvg.ToString("F0")).Append('\n');
                System.IO.File.AppendAllText(m_CsvPath, sb.ToString());
            }
        }
    }
}
