#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""
Emit a minimal synthetic SuperSplat .sog asset for editor smoke-testing the
Track C SOG pipeline (SogReader -> SogChunkLoader -> Burst decode jobs ->
GaussianLodStreamAsync UpdateSog).

Layout produced (matches the runtime decoder expectations verified against
package/Runtime/StreamedSog/*):

    <out>/
        lod-meta.json                          # v1, 1 leaf x 1 LOD
        chunk_0/
            meta.json                          # v2 per-chunk meta
            means_l.webp     10x1 RGBA lossless
            means_u.webp     10x1 RGBA lossless
            scales.webp      10x1 RGBA lossless
            quats.webp       10x1 RGBA lossless
            sh0.webp         10x1 RGBA lossless

The runtime chunk loader:
  * reads means_l / means_u as RGBA WebP -> per pixel: R,G,B = x,y,z low/high
    bytes; A ignored (strideBytes = 4).
  * reads scales.webp as RGBA WebP -> per pixel R,G,B are the codebook indices
    for x,y,z. Decoder job slices with stride==3, so we pack indices in RGB and
    leave A = 0 (loader picks stride==3 for scales regardless of RGBA source).
  * reads quats.webp as RGBA WebP -> per pixel R,G,B,A = quat bytes (smallest-
    three: b0..b2 = 8-bit comps, b3 = 252 + largestIdx).
  * reads sh0.webp as RGBA WebP -> per pixel R,G,B,A = codebook indices for
    R,G,B DC and opacity byte.

Position encoding (matches DecodeMeansJob):
    log-domain lerp coord: t = clamp((sign(x)*log1p(|x|) - mins) / (maxs-mins), 0, 1)
    16-bit sample q = round(t * 65535)
    low  byte -> means_l channel
    high byte -> means_u channel
The runtime recovers world via mins + t*(maxs-mins) then sign(x)*(exp(|x|)-1).

Skip shN block entirely for this synthetic asset (bands = 0 -> loader ignores).

Usage:
    python3 emit_synthetic_sog.py --out projects/.../StreamingAssets/gsplat_lod/synthetic_sog/
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from pathlib import Path

try:
    from PIL import Image, features  # type: ignore
except ImportError:
    print("[emit_synthetic_sog] Pillow not installed. Run: pip install Pillow", file=sys.stderr)
    sys.exit(2)

if not features.check("webp"):
    print("[emit_synthetic_sog] Pillow was built WITHOUT WebP support. STOP.", file=sys.stderr)
    print("  Install a Pillow build with libwebp (macOS: pip install --force-reinstall Pillow).", file=sys.stderr)
    sys.exit(3)

SPLAT_COUNT = 10


def log_transform(x: float) -> float:
    """SuperSplat forward compression: sign(x) * log(1 + |x|)."""
    return math.copysign(math.log1p(abs(x)), x)


def build_positions(n: int) -> list[tuple[float, float, float]]:
    """Uniformly space n splats along the diagonal of the [-1,1]^3 AABB."""
    out = []
    if n == 1:
        return [(0.0, 0.0, 0.0)]
    for i in range(n):
        t = i / (n - 1)                 # 0..1
        v = -1.0 + 2.0 * t               # -1..1
        # slight per-axis phase so x,y,z aren't identical (helps eyeballing).
        x = v
        y = -1.0 + 2.0 * ((t + 1 / 3) % 1.0)
        z = -1.0 + 2.0 * ((t + 2 / 3) % 1.0)
        out.append((x, y, z))
    return out


def encode_positions_split(
    positions: list[tuple[float, float, float]],
    mins: tuple[float, float, float],
    maxs: tuple[float, float, float],
) -> tuple[list[tuple[int, int, int, int]], list[tuple[int, int, int, int]]]:
    """Return (means_l_pixels, means_u_pixels) as RGBA tuples."""
    low_pixels = []
    high_pixels = []
    for p in positions:
        low_rgba = [0, 0, 0, 255]
        high_rgba = [0, 0, 0, 255]
        for axis in range(3):
            lg = log_transform(p[axis])
            denom = maxs[axis] - mins[axis]
            t = 0.0 if denom == 0.0 else (lg - mins[axis]) / denom
            t = max(0.0, min(1.0, t))
            q = int(round(t * 65535.0))
            q = max(0, min(65535, q))
            low_rgba[axis] = q & 0xFF
            high_rgba[axis] = (q >> 8) & 0xFF
        low_pixels.append(tuple(low_rgba))
        high_pixels.append(tuple(high_rgba))
    return low_pixels, high_pixels


def build_scales_codebook() -> list[float]:
    """256 log-scale codebook. Linear ramp from log(0.01) to log(0.05)."""
    lo = math.log(0.01)
    hi = math.log(0.05)
    return [lo + (hi - lo) * (i / 255.0) for i in range(256)]


def build_sh0_codebook() -> list[float]:
    """256 sh0 codebook. Linear ramp -3..+3 (roughly the DC dynamic range)."""
    lo, hi = -3.0, 3.0
    return [lo + (hi - lo) * (i / 255.0) for i in range(256)]


def encode_scales_pixels(n: int) -> list[tuple[int, int, int, int]]:
    """Fixed index that produces a modest per-axis scale."""
    # index 200 -> log-scale ~ log(0.01) + (log(0.05)-log(0.01))*(200/255)
    # exp(...) -> ~ 0.038 which is a visible splat radius in the [-1,1] cube.
    idx = 200
    # Alpha 255 — Pillow lossless WebP corrupts RGB when A=0.
    return [(idx, idx, idx, 255) for _ in range(n)]


def encode_quats_pixels(n: int) -> list[tuple[int, int, int, int]]:
    """Identity-ish quaternion via smallest-three encoding.

    For quaternion (0, 0, 0, 1) the largest component is w (index 3).
    The three other components are 0 -> byte encoding: ((0/invNorm)+1)/2*255 = 127.5.
    Mode tag byte = 252 + 3 = 255.
    """
    return [(128, 128, 128, 255) for _ in range(n)]


def encode_sh0_pixels(n: int, codebook: list[float]) -> list[tuple[int, int, int, int]]:
    """Pick DC + opacity codebook indices.

    DecodeSh0Job produces:
        rgb = 0.5 + codebook[b] * SH_C0
        opacity = SigmoidInvOpacity(a / 255)     (storeAsLogit=true)
    We want visually visible white-ish splats -> b such that codebook[b] ~ 1.8
    (so rgb ~ 0.5 + 1.8 * 0.2820947917 ~ 1.008 -> clamped to 1.0).
    codebook range -3..+3 spans 256 entries -> index = round((1.8 - -3)/6 * 255) = 204.
    For opacity we want a fairly opaque splat -> a byte = 230 -> a=0.902 -> logit ~2.22.
    """
    # find index where codebook approx 1.8
    target = 1.8
    best = min(range(256), key=lambda i: abs(codebook[i] - target))
    return [(best, best, best, 230) for _ in range(n)]


def write_webp(path: Path, pixels: list[tuple[int, int, int, int]]) -> None:
    """Write a width x 1 RGBA lossless WebP."""
    if not pixels:
        raise ValueError(f"write_webp: empty pixel list for {path}")
    w = len(pixels)
    img = Image.new("RGBA", (w, 1))
    img.putdata(pixels)
    # lossless=True keeps byte-exact roundtrip -> decoded RGBA equals input.
    # quality=100 mirrors SuperSplat's exporter settings for these small planes.
    img.save(str(path), format="WEBP", lossless=True, quality=100, method=6)


def emit(out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    chunk_dir = out_dir / "chunk_0"
    chunk_dir.mkdir(parents=True, exist_ok=True)

    # ----- generate splats -----
    positions = build_positions(SPLAT_COUNT)

    # log-domain per-axis mins/maxs derived from actual encoded positions so
    # the quantised value always lands inside [mins, maxs] (avoids clamping).
    logged = [[log_transform(p[a]) for a in range(3)] for p in positions]
    axis_mins = tuple(min(v[a] for v in logged) for a in range(3))  # type: ignore
    axis_maxs = tuple(max(v[a] for v in logged) for a in range(3))  # type: ignore
    # Guard against zero range (all splats colinear on an axis).
    axis_maxs = tuple(
        axis_maxs[a] if axis_maxs[a] > axis_mins[a] else axis_mins[a] + 1e-4
        for a in range(3)
    )

    means_l_px, means_u_px = encode_positions_split(positions, axis_mins, axis_maxs)  # type: ignore
    scales_px = encode_scales_pixels(SPLAT_COUNT)
    quats_px = encode_quats_pixels(SPLAT_COUNT)
    sh0_codebook = build_sh0_codebook()
    sh0_px = encode_sh0_pixels(SPLAT_COUNT, sh0_codebook)

    scales_codebook = build_scales_codebook()

    # ----- write WebPs -----
    write_webp(chunk_dir / "means_l.webp", means_l_px)
    write_webp(chunk_dir / "means_u.webp", means_u_px)
    write_webp(chunk_dir / "scales.webp", scales_px)
    write_webp(chunk_dir / "quats.webp", quats_px)
    write_webp(chunk_dir / "sh0.webp", sh0_px)

    # ----- chunk meta.json (v2) -----
    chunk_meta = {
        "means": {
            "mins": list(axis_mins),
            "maxs": list(axis_maxs),
            "files": ["means_l.webp", "means_u.webp"],
        },
        "scales": {
            "codebook": scales_codebook,
            "files": ["scales.webp"],
        },
        "quats": {
            "files": ["quats.webp"],
        },
        "sh0": {
            "codebook": sh0_codebook,
            "files": ["sh0.webp"],
        },
        # shN intentionally omitted (bands=0 -> loader skips)
    }
    (chunk_dir / "meta.json").write_text(json.dumps(chunk_meta, indent=2))

    # ----- lod-meta.json (v1) -----
    lod_meta = {
        "version": 1,
        "count": SPLAT_COUNT,
        "counts": [SPLAT_COUNT],
        "lodLevels": 1,
        "environment": None,
        "filenames": ["chunk_0"],
        "tree": {
            "bound": {
                "min": [-1.0, -1.0, -1.0],
                "max": [1.0, 1.0, 1.0],
            },
            "lods": {
                "0": {"file": 0, "offset": 0, "count": SPLAT_COUNT},
            },
        },
    }
    (out_dir / "lod-meta.json").write_text(json.dumps(lod_meta, indent=2))

    print(f"[emit_synthetic_sog] wrote {SPLAT_COUNT} splats to {out_dir}")
    print(f"  axis_mins (log-domain) = {axis_mins}")
    print(f"  axis_maxs (log-domain) = {axis_maxs}")


def sanity_check(out_dir: Path) -> None:
    """Reload the manifests + open every WebP to verify the payload is readable."""
    lod_meta_path = out_dir / "lod-meta.json"
    chunk_meta_path = out_dir / "chunk_0" / "meta.json"

    lod = json.loads(lod_meta_path.read_text())
    assert lod["version"] == 1, f"lod-meta version {lod['version']} != 1"
    assert lod["lodLevels"] == 1
    assert lod["counts"] == [SPLAT_COUNT]
    assert lod["filenames"] == ["chunk_0"]

    chunk = json.loads(chunk_meta_path.read_text())
    assert set(chunk.keys()) >= {"means", "scales", "quats", "sh0"}
    assert chunk["means"]["files"] == ["means_l.webp", "means_u.webp"]
    assert len(chunk["scales"]["codebook"]) == 256
    assert len(chunk["sh0"]["codebook"]) == 256

    webps = [
        out_dir / "chunk_0" / "means_l.webp",
        out_dir / "chunk_0" / "means_u.webp",
        out_dir / "chunk_0" / "scales.webp",
        out_dir / "chunk_0" / "quats.webp",
        out_dir / "chunk_0" / "sh0.webp",
    ]
    for path in webps:
        img = Image.open(path)
        img.load()
        # Pillow may store fully-opaque RGBA as RGB on disk (WebP lossless
        # optimisation). The runtime NativeWebPDecoder always emits RGBA so we
        # normalise here before checking payload dimensions.
        rgba = img.convert("RGBA")
        assert rgba.size == (SPLAT_COUNT, 1), f"{path.name} size {rgba.size} != ({SPLAT_COUNT}, 1)"
        # Sanity: pixel count matches expected byte layout (10 splats * 4 bytes).
        pixel_bytes = rgba.tobytes()
        assert len(pixel_bytes) == SPLAT_COUNT * 4, (
            f"{path.name} decoded byte-length {len(pixel_bytes)} != {SPLAT_COUNT * 4}"
        )

    # Byte-exact roundtrip for quats.webp — this one carries a mode-tag byte in
    # alpha (255 = 252 + largestIdx=3) so a naive RGB->RGBA convert would smuggle
    # in a 255 that happens to equal the wanted value. Explicitly verify.
    quats_img = Image.open(out_dir / "chunk_0" / "quats.webp").convert("RGBA")
    quats_bytes = quats_img.tobytes()
    for i in range(SPLAT_COUNT):
        r, g, b, a = quats_bytes[i * 4:i * 4 + 4]
        assert (r, g, b, a) == (128, 128, 128, 255), (
            f"quats splat {i} roundtripped to {(r, g, b, a)} (expected identity-ish (128,128,128,255))"
        )

    print(f"[emit_synthetic_sog] sanity_check OK — {len(webps)} WebPs + 2 JSONs verified")


def main() -> int:
    ap = argparse.ArgumentParser(description="Generate a minimal synthetic SuperSplat .sog asset")
    ap.add_argument("--out", required=True, help="Output directory for the .sog contents")
    args = ap.parse_args()

    out_dir = Path(args.out).resolve()
    emit(out_dir)
    sanity_check(out_dir)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
