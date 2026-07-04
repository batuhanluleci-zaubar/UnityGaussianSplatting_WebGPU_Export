#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Remove needle/floater splats from SPZ before splat-transform rebake."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from chunk_lod import apply_prune_mask, prune_report, prune_subset
from spz_reader import read_spz
from spz_writer import write_spz


def main() -> int:
    ap = argparse.ArgumentParser(description="Prune high-aspect splats from SPZ")
    ap.add_argument("input", type=Path)
    ap.add_argument("output", type=Path)
    ap.add_argument(
        "--prune-aspect-ratio",
        type=float,
        default=30.0,
        help="Drop splats with max(scale)/min(scale) above this (default 30, sog_baker parity)",
    )
    args = ap.parse_args()
    if not args.input.is_file():
        print(f"Input not found: {args.input}", file=sys.stderr)
        return 1

    d = read_spz(str(args.input))
    n0 = d["positions"].shape[0]
    keep = apply_prune_mask(d, prune_aspect_ratio=args.prune_aspect_ratio)
    n1 = prune_report(n0, keep, f"aspect<={args.prune_aspect_ratio:g}")
    if n1 == 0:
        print("ERROR: prune removed all splats", file=sys.stderr)
        return 1

    d = prune_subset(d, keep)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    write_spz(
        str(args.output),
        d["positions"],
        d["scales_lin"],
        d["quats"],
        d["opacity"],
        d["dc"],
        d["sh"],
        d["sh_degree"],
    )
    print(f"Wrote {args.output} ({n1:,} splats)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
