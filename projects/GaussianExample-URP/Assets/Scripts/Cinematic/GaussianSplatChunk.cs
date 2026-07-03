// SPDX-License-Identifier: MIT
// Spatial chunk node: holds metadata (bounds, id) and five LOD child slots.
// GaussianLodStreamAsync drives which single LOD is resident; octree culling runs
// inside each active GaussianSplatRenderer on that slot.
//
// IMPORTANT: chunk GameObject transforms stay at (0,0,0). Baked GaussianSplatAsset
// positions are already in scene/world coordinates — moving the transform would
// double-offset splats and create gaps (missing floor, floating fragments).
using System;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GsplatLod
{
    [DisallowMultipleComponent]
    public class GaussianSplatChunk : MonoBehaviour
    {
        public int chunkId = -1;
        public Vector3 localCentre;
        public Vector3 localSize;

        [SerializeField] GaussianSplatLodSlot[] lodSlots = Array.Empty<GaussianSplatLodSlot>();

        public int activeLod = -1;
        public bool isResident;

        public GaussianSplatLodSlot[] LodSlots => lodSlots;
        public int LodCount => lodSlots != null ? lodSlots.Length : 0;

        public GaussianSplatLodSlot GetLod(int level)
        {
            if (lodSlots == null || level < 0 || level >= lodSlots.Length) return null;
            return lodSlots[level];
        }

        public int GetSplatCount(int level)
        {
            var slot = GetLod(level);
            return slot != null ? slot.splatCount : 0;
        }

        /// <summary>Show exactly one LOD level; disables all others and clears their GPU assets.</summary>
        public void ApplyLod(int level, GaussianSplatting.Runtime.GaussianSplatAsset asset)
        {
            if (lodSlots == null) return;
            for (int i = 0; i < lodSlots.Length; i++)
            {
                var slot = lodSlots[i];
                if (slot == null) continue;
                bool on = i == level;
                slot.SetActiveLod(on ? asset : null, on);
            }
            activeLod = asset != null ? level : -1;
            isResident = asset != null;
        }

        /// <summary>Release all LOD assets and hide every slot (chunk leaves the resident set).</summary>
        public void ClearResident()
        {
            if (lodSlots != null)
            {
                foreach (var slot in lodSlots)
                    slot?.Clear();
            }
            activeLod = -1;
            isResident = false;
        }

        public void RefreshLodSlotsFromChildren()
        {
            var found = GetComponentsInChildren<GaussianSplatLodSlot>(true);
            Array.Sort(found, (a, b) => a.lodLevel.CompareTo(b.lodLevel));
            lodSlots = found;
        }

        /// <summary>Bounds in chunk-local space (centre = localCentre metadata from manifest).</summary>
        public Bounds LocalBounds => new Bounds(localCentre, localSize);

        /// <summary>World-space AABB for editor framing / streaming cull (uses localCentre, not transform).</summary>
        public Bounds WorldBounds
        {
            get
            {
                var centre = transform.TransformPoint(localCentre);
                return new Bounds(centre, Vector3.Scale(localSize, transform.lossyScale));
            }
        }

        /// <summary>
        /// Chunk transforms must stay at origin — splat assets already contain absolute scene positions.
        /// </summary>
        public void ResetTransformToOrigin()
        {
            if (transform.localPosition.sqrMagnitude <= 0.0001f) return;
            transform.localPosition = Vector3.zero;
        }

#if UNITY_EDITOR
        public bool HasTransformOffset => transform.localPosition.sqrMagnitude > 0.0001f;

        [SerializeField, HideInInspector] int editorPreviewLod = -1;

        public int EditorPreviewLod => editorPreviewLod;

        public void SetEditorPreviewLod(int level)
        {
            if (Application.isPlaying) return;
            RefreshLodSlotsFromChildren();
            if (lodSlots == null || lodSlots.Length == 0) return;

            level = Mathf.Clamp(level, 0, lodSlots.Length - 1);
            for (int i = 0; i < lodSlots.Length; i++)
            {
                var slot = lodSlots[i];
                if (slot == null) continue;
                bool on = i == level;
                slot.SetActiveLod(on ? slot.previewAsset : null, on && slot.previewAsset != null);
            }
            editorPreviewLod = level;
            activeLod = level;
            isResident = lodSlots[level]?.previewAsset != null;
            EditorUtility.SetDirty(this);
        }
#endif
    }
}
