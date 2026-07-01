# SPDX-License-Identifier: MIT
"""
Vectorised reader for Niantic/Scaniverse .SPZ gaussian-splat files (v2 and v3).

Decodes the packed streams into plain numpy arrays. Mirrors the C# reader in
package/Editor/Utils/SPZFileReader.cs (including the v3 "smallest three" quaternion).

Returned by read_spz():
    positions   float32 [N,3]   world-space centres
    scales_lin  float32 [N,3]   LINEAR per-axis scale (exp of the stored log-scale)
    quats       float32 [N,4]   rotation quaternion as (x, y, z, w)
    opacity     float32 [N]     0..1 (post-sigmoid)
    dc          float32 [N,3]   SH DC coefficient (f_dc)
    sh          float32 [N,K3]  higher SH coefficients, K3 = 3 * sh_coeffs (may be 0)
    sh_degree   int
"""
import gzip
import struct
import numpy as np

_NGSP_MAGIC = 0x5053474E  # "NGSP"
_SH_COEFFS = {0: 0, 1: 3, 2: 8, 3: 15}


def _sh_coeffs_for_degree(deg: int) -> int:
    return _SH_COEFFS.get(deg, 0)


def read_spz(path: str) -> dict:
    with gzip.open(path, "rb") as f:
        raw = f.read()

    magic, version, num_points = struct.unpack_from("<III", raw, 0)
    sh_degree, frac_bits, flags, _reserved = struct.unpack_from("<BBBB", raw, 12)
    if magic != _NGSP_MAGIC:
        raise ValueError(f"bad SPZ magic {magic:#x}")
    if version not in (2, 3):
        raise ValueError(f"unsupported SPZ version {version}")

    N = int(num_points)
    sh_coeffs = _sh_coeffs_for_degree(sh_degree)
    rot_stride = 4 if version >= 3 else 3

    off = 16
    def take(nbytes):
        nonlocal off
        b = np.frombuffer(raw, dtype=np.uint8, count=nbytes, offset=off)
        off += nbytes
        return b

    pos_b = take(N * 9).reshape(N, 3, 3).astype(np.int32)
    alpha_b = take(N).astype(np.float32)
    col_b = take(N * 3).reshape(N, 3).astype(np.float32)
    scale_b = take(N * 3).reshape(N, 3).astype(np.float32)
    rot_b = take(N * rot_stride).reshape(N, rot_stride)
    sh_b = take(N * 3 * sh_coeffs).reshape(N, 3 * sh_coeffs) if sh_coeffs else np.zeros((N, 0), np.float32)

    # --- positions: 24-bit little-endian fixed point, sign-extended, /2^frac_bits ---
    fx = pos_b[:, :, 0] | (pos_b[:, :, 1] << 8) | (pos_b[:, :, 2] << 16)
    fx = np.where((fx & 0x800000) != 0, fx - 0x1000000, fx)
    positions = (fx.astype(np.float32)) * np.float32(1.0 / (1 << frac_bits))

    # --- scales: stored byte -> log scale (b/16 - 10) -> linear = exp ---
    log_scale = scale_b / 16.0 - 10.0
    scales_lin = np.exp(log_scale).astype(np.float32)

    # --- opacity: byte/255 (already sigmoid-activated) ---
    opacity = (alpha_b / 255.0).astype(np.float32)

    # --- colour DC: (byte/255 - 0.5) / 0.15 ---
    dc = ((col_b / 255.0 - 0.5) / 0.15).astype(np.float32)

    # --- higher SH: (byte - 128) / 128 ---
    sh = ((sh_b.astype(np.float32) - 128.0) / 128.0).astype(np.float32) if sh_coeffs else sh_b

    # --- rotations ---
    if version >= 3:
        quats = _unpack_smallest_three(rot_b.astype(np.uint32))
    else:
        # v2 "first three": xyz = b/127.5 - 1, w = sqrt(max(0, 1-|xyz|^2))
        xyz = rot_b.astype(np.float32) * (1.0 / 127.5) - 1.0
        w = np.sqrt(np.clip(1.0 - np.sum(xyz * xyz, axis=1), 0.0, None))
        quats = np.column_stack([xyz, w]).astype(np.float32)

    return dict(positions=positions, scales_lin=scales_lin, quats=quats,
                opacity=opacity, dc=dc, sh=sh, sh_degree=int(sh_degree),
                version=int(version), frac_bits=int(frac_bits))


def _unpack_smallest_three(rot_b: np.ndarray) -> np.ndarray:
    """Vectorised inverse of packQuaternionSmallestThree -> quats (x,y,z,w)."""
    N = rot_b.shape[0]
    comp = (rot_b[:, 0] | (rot_b[:, 1] << 8) | (rot_b[:, 2] << 16) | (rot_b[:, 3] << 24)).astype(np.uint32)
    c_mask = np.uint32((1 << 9) - 1)  # 511
    sqrt1_2 = np.float32(0.7071067811865476)
    i_largest = (comp >> np.uint32(30)).astype(np.int64)  # 0..3

    q = np.zeros((N, 4), np.float32)
    sum_sq = np.zeros(N, np.float32)
    work = comp.copy()
    for i in range(3, -1, -1):
        active = (i_largest != i)
        mag = (work & c_mask).astype(np.float32)
        negbit = ((work >> np.uint32(9)) & np.uint32(1))
        val = sqrt1_2 * mag / np.float32(c_mask)
        val = np.where(negbit == 1, -val, val).astype(np.float32)
        q[:, i] = np.where(active, val, q[:, i])
        sum_sq = np.where(active, sum_sq + val * val, sum_sq).astype(np.float32)
        # advance only the lanes that consumed a component this step
        work = np.where(active, work >> np.uint32(10), work).astype(np.uint32)

    largest_val = np.sqrt(np.clip(1.0 - sum_sq, 0.0, None)).astype(np.float32)
    rows = np.arange(N)
    q[rows, i_largest] = largest_val
    return q


if __name__ == "__main__":
    import sys, time
    t0 = time.time()
    d = read_spz(sys.argv[1])
    n = d["positions"].shape[0]
    p = d["positions"]
    print(f"read {n:,} splats in {time.time()-t0:.1f}s  sh_degree={d['sh_degree']} sh_cols={d['sh'].shape[1]}")
    print(f"  pos  min={p.min(0)} max={p.max(0)}")
    print(f"  scale_lin  mean={d['scales_lin'].mean(0)}  opacity mean={d['opacity'].mean():.3f}")
    print(f"  quat norm mean={np.linalg.norm(d['quats'],axis=1).mean():.4f} (should be ~1.0)")
