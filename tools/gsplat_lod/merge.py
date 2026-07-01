# SPDX-License-Identifier: MIT
"""
Quality-preserving voxel merge for 3D gaussian splats (moment matching).

Splats whose centres fall in the same voxel are merged into ONE gaussian that has
the same 0th/1st/2nd moments as the group (mean + full covariance), so the merged
blob covers the same volume — not a subsample. Larger voxel = fewer, bigger splats.

Merged gaussian:
  weight   w_i    = opacity_i                       (contribution weight)
  mean     mu     = sum(w_i * mu_i) / sum(w_i)
  cov      Sigma  = sum(w_i * (Sigma_i + (mu_i-mu)(mu_i-mu)^T)) / sum(w_i)   (parallel axis)
                    Sigma_i = R_i diag(s_i^2) R_i^T
  scale/rot from eigen-decomposition Sigma = R diag(lambda) R^T -> s = sqrt(lambda)
  opacity  = 1 - prod(1 - o_i)  clipped   (alpha coverage of overlapping blobs)
  colour/SH= sum(w_i * c_i) / sum(w_i)     (opacity-weighted mean)
"""
import numpy as np


def _quat_to_R(q):  # q: [N,4] (x,y,z,w) -> [N,3,3]
    x, y, z, w = q[:, 0], q[:, 1], q[:, 2], q[:, 3]
    R = np.empty((q.shape[0], 3, 3), np.float64)
    R[:, 0, 0] = 1 - 2 * (y * y + z * z); R[:, 0, 1] = 2 * (x * y - w * z); R[:, 0, 2] = 2 * (x * z + w * y)
    R[:, 1, 0] = 2 * (x * y + w * z); R[:, 1, 1] = 1 - 2 * (x * x + z * z); R[:, 1, 2] = 2 * (y * z - w * x)
    R[:, 2, 0] = 2 * (x * z - w * y); R[:, 2, 1] = 2 * (y * z + w * x); R[:, 2, 2] = 1 - 2 * (x * x + y * y)
    return R


def _R_to_quat(R):  # [M,3,3] -> [M,4] (x,y,z,w), numerically stable branchless-ish
    M = R.shape[0]
    t = R[:, 0, 0] + R[:, 1, 1] + R[:, 2, 2]
    q = np.empty((M, 4), np.float64)
    # case w largest (t>0)
    s = np.sqrt(np.maximum(t + 1.0, 1e-12)) * 2.0
    qw = 0.25 * s; qx = (R[:, 2, 1] - R[:, 1, 2]) / s; qy = (R[:, 0, 2] - R[:, 2, 0]) / s; qz = (R[:, 1, 0] - R[:, 0, 1]) / s
    q[:] = np.stack([qx, qy, qz, qw], axis=1)
    # fallback for t<=0: pick the largest diagonal (rare; use scipy-free per-row)
    bad = t <= 0
    if np.any(bad):
        Rb = R[bad]
        out = np.empty((Rb.shape[0], 4), np.float64)
        for k in range(Rb.shape[0]):
            m = Rb[k]; d = np.array([m[0, 0], m[1, 1], m[2, 2]])
            i = int(np.argmax(d)); j = (i + 1) % 3; kk = (i + 2) % 3
            s2 = np.sqrt(max(1.0 + m[i, i] - m[j, j] - m[kk, kk], 1e-12)) * 2.0
            qi = 0.25 * s2
            qj = (m[j, i] + m[i, j]) / s2
            qk = (m[kk, i] + m[i, kk]) / s2
            qw2 = (m[kk, j] - m[j, kk]) / s2
            v = np.zeros(4); v[i] = qi; v[j] = qj; v[kk] = qk; v[3] = qw2
            out[k] = v
        q[bad] = out
    q /= np.linalg.norm(q, axis=1, keepdims=True) + 1e-12
    return q


def _segsum(values, seg_starts):
    # sum rows of `values` within contiguous segments defined by seg_starts (add.reduceat)
    return np.add.reduceat(values, seg_starts, axis=0)


def voxel_merge(d, voxel_size, prune_opacity=0.0, prune_min_scale=0.0,
                prune_max_scale=0.0, prune_aspect_ratio=0.0, op_boost=1.0, op_cap=0.999):
    pos = d["positions"].astype(np.float64)
    scl = d["scales_lin"].astype(np.float64)
    quat = d["quats"].astype(np.float64)
    op = d["opacity"].astype(np.float64)
    dc = d["dc"].astype(np.float64)
    sh = d["sh"].astype(np.float64)

    # ---- prune negligible splats first ----
    keep = np.ones(pos.shape[0], bool)
    if prune_opacity > 0:
        keep &= op >= prune_opacity
    if prune_min_scale > 0:
        keep &= scl.max(axis=1) >= prune_min_scale
    if prune_max_scale > 0:
        keep &= scl.max(axis=1) <= prune_max_scale  # kills giant-scale floater ellipsoids
    if prune_aspect_ratio > 0:
        # kills needle-shaped anisotropic floaters (long thin splats that render as visible streaks).
        # In noisy captures these are 90 % of the visible "haze": e.g. the Festsaal capture has
        # median aspect ratio 8.9 but 99 %-ile 2180 — the top few % are extreme needles.
        keep &= (scl.max(axis=1) / np.maximum(scl.min(axis=1), 1e-6)) <= prune_aspect_ratio
    if not keep.all():
        pos, scl, quat, op, dc, sh = pos[keep], scl[keep], quat[keep], op[keep], dc[keep], sh[keep]

    N = pos.shape[0]
    # ---- voxel assignment, sorted so each voxel is a contiguous segment ----
    vox = np.floor(pos / voxel_size).astype(np.int64)
    # order by voxel key
    order = np.lexsort((vox[:, 2], vox[:, 1], vox[:, 0]))
    vox_s = vox[order]
    new_seg = np.ones(N, bool)
    new_seg[1:] = np.any(vox_s[1:] != vox_s[:-1], axis=1)
    seg_starts = np.flatnonzero(new_seg)
    M = seg_starts.shape[0]
    cluster_id = np.cumsum(new_seg) - 1  # 0..M-1 per sorted splat

    p = pos[order]; s = scl[order]; qq = quat[order]; o = op[order]; c = dc[order]; H = sh[order]
    w = o.copy()  # weight = opacity
    Wc = _segsum(w, seg_starts)                        # [M]
    Wc_safe = np.maximum(Wc, 1e-12)

    # mean
    mu = _segsum(w[:, None] * p, seg_starts) / Wc_safe[:, None]      # [M,3]
    mu_per = mu[cluster_id]                                          # [N,3]

    # per-splat covariance Sigma_i = R diag(s^2) R^T
    R = _quat_to_R(qq)                                               # [N,3,3]
    s2 = s * s                                                       # [N,3]
    Sig_i = np.einsum("nij,nj,nkj->nik", R, s2, R)                   # [N,3,3]
    dmu = p - mu_per
    outer = dmu[:, :, None] * dmu[:, None, :]                        # [N,3,3]
    term = w[:, None, None] * (Sig_i + outer)                       # [N,3,3]
    Sig_c = _segsum(term.reshape(N, 9), seg_starts).reshape(M, 3, 3) / Wc_safe[:, None, None]

    # eigen-decompose -> scale (sqrt eig) + rotation
    lam, V = np.linalg.eigh(Sig_c)          # ascending eigenvalues, V columns = eigenvectors
    lam = np.clip(lam, 1e-12, None)
    merged_scale = np.sqrt(lam).astype(np.float64)          # [M,3]
    # ensure right-handed rotation (det +1)
    dets = np.linalg.det(V)
    V[dets < 0, :, 0] *= -1.0
    merged_quat = _R_to_quat(V)                            # [M,4] (x,y,z,w)

    # opacity: coverage union 1 - prod(1 - o), in log space (avoids underflow).
    # op_boost (>1) makes heavily-merged coarse LODs read solid; op_cap avoids hard opaque edges.
    o_eff = np.clip(o * op_boost, 0.0, 0.999)
    log1m = np.log(np.clip(1.0 - o_eff, 1e-6, 1.0))
    merged_op = 1.0 - np.exp(_segsum(log1m, seg_starts))
    merged_op = np.clip(merged_op, 0.02, op_cap)

    # colour + SH: opacity-weighted mean
    merged_dc = _segsum(w[:, None] * c, seg_starts) / Wc_safe[:, None]
    merged_sh = (_segsum(w[:, None] * H, seg_starts) / Wc_safe[:, None]) if H.shape[1] else H[:M]

    return dict(
        positions=mu.astype(np.float32),
        scales_lin=merged_scale.astype(np.float32),
        quats=merged_quat.astype(np.float32),
        opacity=merged_op.astype(np.float32),
        dc=merged_dc.astype(np.float32),
        sh=merged_sh.astype(np.float32),
        sh_degree=d.get("sh_degree", 3),
    )
