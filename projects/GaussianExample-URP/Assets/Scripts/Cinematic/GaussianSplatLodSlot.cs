// SPDX-License-Identifier: MIT
// One discrete LOD level inside a spatial chunk. The streamer activates exactly one slot
// per chunk at a time; each slot owns a GaussianSplatRenderer whose octree handles
// frustum cull + depth sort for that LOD asset independently.
using UnityEngine;
using GaussianSplatting.Runtime;

namespace GsplatLod
{
    [DisallowMultipleComponent]
    public class GaussianSplatLodSlot : MonoBehaviour
    {
        [Tooltip("LOD index: 0 = finest, higher = coarser.")]
        public int lodLevel;

        [Tooltip("Addressables key (asset name without extension) used at runtime.")]
        public string addressableKey;

        [Tooltip("Splat count from manifest — used by the budget balancer.")]
        public int splatCount;

        [Tooltip("Optional direct asset reference for editor preview / non-Addressables paths.")]
        public GaussianSplatAsset previewAsset;

        GaussianSplatRenderer m_Renderer;

        public GaussianSplatRenderer Renderer
        {
            get
            {
                if (m_Renderer == null)
                    m_Renderer = GetComponent<GaussianSplatRenderer>();
                return m_Renderer;
            }
        }

        public void WirePreviewToRenderer()
        {
            var r = Renderer;
            if (r == null || previewAsset == null) return;
            if (r.m_Asset == previewAsset) return;
            r.m_Asset = previewAsset;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                UnityEditor.EditorUtility.SetDirty(r);
#endif
        }

        public void SetActiveLod(GaussianSplatAsset asset, bool active)
        {
            var r = Renderer;
            if (r == null) return;

            if (active && asset != null)
            {
                r.m_Asset = asset;
                gameObject.SetActive(true);
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    r.EditorForceReloadAsset();
#endif
            }
            else
            {
                gameObject.SetActive(false);
                // Runtime: drop GPU asset on inactive slots. Editor: keep m_Asset wired for Inspector.
                if (Application.isPlaying)
                    r.m_Asset = null;
            }
        }

        public void Clear()
        {
            var r = Renderer;
            if (r != null) r.m_Asset = null;
            gameObject.SetActive(false);
        }

#if UNITY_EDITOR
        void Reset()
        {
            if (Renderer == null)
                gameObject.AddComponent<GaussianSplatRenderer>();
        }
#endif
    }
}
