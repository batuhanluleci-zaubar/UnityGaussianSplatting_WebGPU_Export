#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Post-process splat-transform lod-meta.json: clip outlier leaf bounds and rebuild interior nodes.

splat-transform can emit mega-nodes (300m+ span) while actual content spans ~60m. Unity uses
root bounds for auto-frame and LOD distance; this script tightens the tree in-place.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np


def _span(bound: dict) -> float:
    mn = np.array(bound["min"], dtype=np.float64)
    mx = np.array(bound["max"], dtype=np.float64)
    return float(np.max(mx - mn))


def _clip_bound(bound: dict, clip_min: np.ndarray, clip_max: np.ndarray) -> dict:
    mn = np.maximum(np.array(bound["min"], dtype=np.float64), clip_min)
    mx = np.minimum(np.array(bound["max"], dtype=np.float64), clip_max)
    mx = np.maximum(mx, mn + 1e-6)
    return {"min": mn.tolist(), "max": mx.tolist()}


def _collect_leaf_spans(node: dict, out: list[float]) -> None:
    if "lods" in node:
        out.append(_span(node["bound"]))
        return
    for child in node.get("children") or []:
        _collect_leaf_spans(child, out)


def _union_bounds(nodes: list[dict]) -> dict:
    mns = [np.array(n["bound"]["min"], dtype=np.float64) for n in nodes]
    mxs = [np.array(n["bound"]["max"], dtype=np.float64) for n in nodes]
    return {
        "min": np.minimum.reduce(mns).tolist(),
        "max": np.maximum.reduce(mxs).tolist(),
    }


def _tighten_node(node: dict, clip_min: np.ndarray, clip_max: np.ndarray, max_leaf_span: float) -> dict:
    if "lods" in node:
        b = _clip_bound(node["bound"], clip_min, clip_max)
        if _span(b) > max_leaf_span:
            # Shrink outlier leaf to clip box (content still addressed by offset/count)
            b = {"min": clip_min.tolist(), "max": clip_max.tolist()}
        node = dict(node)
        node["bound"] = b
        return node

    children = [_tighten_node(c, clip_min, clip_max, max_leaf_span) for c in node["children"]]
    node = dict(node)
    node["children"] = children
    node["bound"] = _union_bounds(children)
    return node


def tighten_lod_meta(meta: dict, span_multiplier: float = 8.0, min_max_span: float = 80.0) -> dict:
    tree = meta.get("tree")
    if not tree:
        return meta

    spans: list[float] = []
    _collect_leaf_spans(tree, spans)
    if not spans:
        return meta

    spans.sort()
    median = spans[len(spans) // 2]
    max_leaf_span = max(median * span_multiplier, min_max_span)

    # Tight global AABB from non-outlier leaves (for clipping)
    clip_min = np.full(3, np.inf)
    clip_max = np.full(3, -np.inf)

    def walk_leaves(n: dict) -> None:
        nonlocal clip_min, clip_max
        if "lods" in n:
            if _span(n["bound"]) <= max_leaf_span:
                mn = np.array(n["bound"]["min"], dtype=np.float64)
                mx = np.array(n["bound"]["max"], dtype=np.float64)
                clip_min = np.minimum(clip_min, mn)
                clip_max = np.maximum(clip_max, mx)
            return
        for c in n.get("children") or []:
            walk_leaves(c)

    walk_leaves(tree)
    if not np.all(np.isfinite(clip_min)):
        return meta

    meta = dict(meta)
    meta["tree"] = _tighten_node(tree, clip_min, clip_max, max_leaf_span)
    return meta


def main() -> int:
    ap = argparse.ArgumentParser(description="Tighten splat-transform lod-meta.json tree bounds")
    ap.add_argument("lod_meta", type=Path, help="Path to lod-meta.json")
    ap.add_argument("--span-multiplier", type=float, default=8.0)
    ap.add_argument("--in-place", action="store_true", default=True)
    args = ap.parse_args()

    path = args.lod_meta
    meta = json.loads(path.read_text())
    before = _span(meta["tree"]["bound"])
    meta = tighten_lod_meta(meta, span_multiplier=args.span_multiplier)
    after = _span(meta["tree"]["bound"])
    path.write_text(json.dumps(meta, separators=(",", ":")))
    print(f"Tightened {path}: root span {before:.1f}m -> {after:.1f}m")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
