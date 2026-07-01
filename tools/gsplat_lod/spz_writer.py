# SPDX-License-Identifier: MIT
"""
Vectorised writer for Niantic/Scaniverse .SPZ gaussian-splat files (v3).

Exact inverse of spz_reader.py. Emits v3 by default (smallest-three quaternion, 4 bytes/rot);
Unity's SPZFileReader.cs reads v2 and v3, so v3 is the wire format of choice.

  write_spz("out.spz", positions, scales_lin, quats_xyzw, opacity, dc, sh, sh_degree=3)

positions, scales_lin, opacity, dc, sh — same units as read_spz returns.
quats_xyzw is (x, y, z, w).
"""
import gzip
import struct
import numpy as np

_NGSP_MAGIC = 0x5053474E  # "NGSP"
_SH_COEFFS = {0: 0, 1: 3, 2: 8, 3: 15}
_SQRT_HALF = np.float32(0.7071067811865476)


def _pack_positions(pos: np.ndarray, frac_bits: int) -> bytes:
    scale = float(1 << frac_bits)
    fx = np.rint(pos.astype(np.float64) * scale).astype(np.int64)
    fx = np.clip(fx, -(1 << 23), (1 << 23) - 1) & 0xFFFFFF
    N = pos.shape[0]
    out = np.empty((N, 3, 3), dtype=np.uint8)
    out[:, :, 0] = fx & 0xFF
    out[:, :, 1] = (fx >> 8) & 0xFF
    out[:, :, 2] = (fx >> 16) & 0xFF
    return out.tobytes()


def _pack_smallest_three(quats: np.ndarray) -> bytes:
    """quats [N,4] (x,y,z,w) -> [N,4] bytes (v3 smallest-three encoding).
    Wire layout: bit30..31 = index of largest component (0=x,1=y,2=z,3=w);
    bit0..9 = comp0, bit10..19 = comp1, bit20..29 = comp2 where comp0..2 are the 3 NON-max
    components in axis order; each is 1 sign bit (bit9) + 9-bit magnitude scaled to sqrt(2)."""
    N = quats.shape[0]
    q = quats.astype(np.float64)
    # normalise; force w>=0 sign for consistency with the reader's convention
    q /= np.linalg.norm(q, axis=1, keepdims=True) + 1e-12
    absq = np.abs(q)
    i_largest = np.argmax(absq, axis=1)                  # 0..3
    rows = np.arange(N)
    sign = np.sign(q[rows, i_largest])
    sign[sign == 0] = 1.0
    q *= sign[:, None]                                   # largest component is now positive
    c_mask = 511  # (1<<9)-1
    out = np.zeros(N, dtype=np.uint32)
    for k in range(4):
        active = (i_largest != k)                        # lanes where axis k is a non-max slot
        val = q[:, k].astype(np.float32) / _SQRT_HALF    # rescale from [-1/√2, 1/√2] -> [-1, 1]
        mag = np.rint(np.abs(val) * c_mask).astype(np.int64)
        mag = np.clip(mag, 0, c_mask)
        neg = (val < 0).astype(np.int64)
        code = (mag | (neg << 9)).astype(np.uint32)      # 10-bit slot value
        # Slot position for axis k depends on how many non-max slots precede k:
        # we walk axes 3->0 in the reader; encoder stores slots 0..2 in order 3,2,1,0 skipping i_largest.
        # But an equivalent simpler layout: slot index = k - (1 if k > i_largest else 0), reversed to match reader.
        # Match reader: reader reads i=3..0 and consumes a slot from the LOW end of `work` each active step.
        # So the FIRST axis it consumes (i=3) sits at bits 0..9, next active axis at bits 10..19, etc.
        # We must emit in the same order — iterate k from 3 down and push each into the next free slot.
        pass  # actual pack done in reversed loop below

    # Encode in the same iteration order the reader consumes:
    slot_idx = np.zeros(N, dtype=np.int64)               # next free 10-bit slot per row (0..2)
    for k in range(3, -1, -1):
        active = (i_largest != k)
        val = q[:, k].astype(np.float32) / _SQRT_HALF
        mag = np.rint(np.abs(val) * c_mask).astype(np.int64)
        mag = np.clip(mag, 0, c_mask)
        neg = (val < 0).astype(np.int64)
        code = (mag | (neg << 9)).astype(np.uint32)
        shift = (slot_idx.astype(np.uint32) * 10)        # 0, 10, or 20
        out = np.where(active, out | (code.astype(np.uint32) << shift), out)
        slot_idx = np.where(active, slot_idx + 1, slot_idx)

    out |= (i_largest.astype(np.uint32) << 30)
    bytes_ = np.empty((N, 4), dtype=np.uint8)
    bytes_[:, 0] = out & 0xFF
    bytes_[:, 1] = (out >> 8) & 0xFF
    bytes_[:, 2] = (out >> 16) & 0xFF
    bytes_[:, 3] = (out >> 24) & 0xFF
    return bytes_.tobytes()


def write_spz(path, positions, scales_lin, quats_xyzw, opacity, dc, sh,
              sh_degree=3, frac_bits=12, version=3):
    N = positions.shape[0]
    sh_coeffs = _SH_COEFFS[sh_degree]
    if sh.shape[1] != 3 * sh_coeffs:
        raise ValueError(f"sh has {sh.shape[1]} cols, expected {3 * sh_coeffs} for degree {sh_degree}")
    if version != 3:
        raise ValueError("only SPZ v3 is emitted (smallest-three quaternion)")

    # header (16 bytes): magic (u32), version (u32), num_points (u32),
    #                    sh_degree/frac_bits/flags/reserved (u8 x4)
    header = struct.pack("<III BBBB", _NGSP_MAGIC, version, N, sh_degree, frac_bits, 0, 0)

    pos_b = _pack_positions(positions.astype(np.float32), frac_bits)
    alpha_b = np.clip(np.rint(opacity.astype(np.float32) * 255.0), 0, 255).astype(np.uint8).tobytes()
    # colour DC: (byte/255 - 0.5) / 0.15  ->  byte = clip((dc*0.15 + 0.5) * 255, 0, 255)
    col_b = np.clip(np.rint((dc.astype(np.float32) * 0.15 + 0.5) * 255.0), 0, 255).astype(np.uint8).tobytes()
    # scales: log_scale = byte/16 - 10  ->  byte = clip((log_scale + 10) * 16, 0, 255)
    log_scale = np.log(np.maximum(scales_lin.astype(np.float32), 1e-30))
    scale_b = np.clip(np.rint((log_scale + 10.0) * 16.0), 0, 255).astype(np.uint8).tobytes()
    rot_b = _pack_smallest_three(quats_xyzw.astype(np.float32))
    if sh_coeffs:
        # higher SH: (byte - 128) / 128  ->  byte = clip(sh*128 + 128, 0, 255)
        sh_b = np.clip(np.rint(sh.astype(np.float32) * 128.0 + 128.0), 0, 255).astype(np.uint8).tobytes()
    else:
        sh_b = b""

    payload = header + pos_b + alpha_b + col_b + scale_b + rot_b + sh_b
    with gzip.open(path, "wb", compresslevel=6) as f:
        f.write(payload)


if __name__ == "__main__":
    # round-trip sanity: read a file, write it back, read again, compare.
    import sys
    from spz_reader import read_spz
    src = sys.argv[1]
    dst = sys.argv[2] if len(sys.argv) > 2 else "/tmp/roundtrip.spz"
    d = read_spz(src)
    write_spz(dst, d["positions"], d["scales_lin"], d["quats"], d["opacity"], d["dc"], d["sh"], d["sh_degree"])
    d2 = read_spz(dst)
    print("N", d["positions"].shape[0], "->", d2["positions"].shape[0])
    print("pos max err  ", np.abs(d["positions"] - d2["positions"]).max())
    print("scale max err", np.abs(d["scales_lin"] - d2["scales_lin"]).max())
    print("op max err   ", np.abs(d["opacity"] - d2["opacity"]).max())
    print("dc max err   ", np.abs(d["dc"] - d2["dc"]).max())
    if d["sh"].size:
        print("sh max err   ", np.abs(d["sh"] - d2["sh"]).max())
    # quaternion sign is ambiguous — compare rotation matrices instead
    def qR(q):
        x,y,z,w = q[:,0],q[:,1],q[:,2],q[:,3]
        R=np.empty((q.shape[0],3,3), np.float32)
        R[:,0,0]=1-2*(y*y+z*z); R[:,0,1]=2*(x*y-w*z); R[:,0,2]=2*(x*z+w*y)
        R[:,1,0]=2*(x*y+w*z); R[:,1,1]=1-2*(x*x+z*z); R[:,1,2]=2*(y*z-w*x)
        R[:,2,0]=2*(x*z-w*y); R[:,2,1]=2*(y*z+w*x); R[:,2,2]=1-2*(x*x+y*y)
        return R
    print("rotmat max err", np.abs(qR(d["quats"]) - qR(d2["quats"])).max())
