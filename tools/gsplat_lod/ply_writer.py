# SPDX-License-Identifier: MIT
"""
Writes a standard INRIA 3D-Gaussian-Splatting binary .ply that the Unity
org.nesnausk GaussianSplatAssetCreator imports (see GaussianFileReader.cs):

  opacity  -> stored as LOGIT   (importer applies Sigmoid)
  scale_*  -> stored as LOG      (importer applies exp / LinearScale)
  f_dc_*   -> raw SH DC          (importer applies SH0ToColor)
  rot_0..3 -> (w, x, y, z)       (importer normalises/swizzles)
  f_rest_* -> channel-major      [R0..R14, G0..G14, B0..B14]
"""
import numpy as np


def write_ply(path, positions, scales_lin, quats_xyzw, opacity, dc, sh):
    N = int(positions.shape[0])
    sh_bands = sh.shape[1] // 3 if sh.size else 0

    o = np.clip(opacity.astype(np.float64), 1e-6, 1.0 - 1e-6)
    opacity_logit = np.log(o / (1.0 - o)).astype(np.float32)[:, None]
    scale_log = np.log(np.clip(scales_lin, 1e-12, None)).astype(np.float32)

    q = quats_xyzw
    rot_wxyz = np.column_stack([q[:, 3], q[:, 0], q[:, 1], q[:, 2]]).astype(np.float32)

    # SPZ SH is coefficient-major [c0R,c0G,c0B, c1R,...]; INRIA .ply is channel-major.
    if sh_bands:
        sh_cm = sh.reshape(N, sh_bands, 3).transpose(0, 2, 1).reshape(N, sh_bands * 3).astype(np.float32)
    else:
        sh_cm = np.zeros((N, 0), np.float32)

    normals = np.zeros((N, 3), np.float32)
    blocks = [positions.astype(np.float32), normals, dc.astype(np.float32),
              sh_cm, opacity_logit, scale_log, rot_wxyz]
    data = np.concatenate(blocks, axis=1).astype(np.float32)

    names = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2"]
    names += [f"f_rest_{i}" for i in range(sh_bands * 3)]
    names += ["opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    assert data.shape[1] == len(names), (data.shape[1], len(names))

    header = "ply\nformat binary_little_endian 1.0\n"
    header += f"element vertex {N}\n"
    header += "".join(f"property float {n}\n" for n in names)
    header += "end_header\n"

    with open(path, "wb") as f:
        f.write(header.encode("ascii"))
        data.tofile(f)
    return N, len(names)
