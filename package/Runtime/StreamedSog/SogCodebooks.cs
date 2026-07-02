// SPDX-License-Identifier: MIT
// Track C2 (part 2): SOG codebook helpers — SH_C0 constant + SigmoidInvOpacity.
//
// This file is a partial class so the sibling C2-meansQuats agent can extend it
// with additional helpers (PatchNullCodebook, InvLogTransform, UnpackSmallestThreeQuat,
// etc.) without editing this file. Only add helpers required by the scale + sh0
// decoders here.
//
// Public constants / helpers:
//   SH_C0                = 0.28209479177387814  (spherical-harmonic band-0 factor)
//   SigmoidInvOpacity(a) = logit(a) with numerical clamp — inverse of sigmoid
//                          used to lift SuperSplat's stored sigmoided alpha back
//                          to the logit space that InputSplatData.opacity expects
//                          when the runtime path wants raw pre-sigmoid values.

using Unity.Burst;

namespace GaussianSplatting.Runtime.StreamedSog
{
    public static partial class SogCodebooks
    {
        /// <summary>
        /// SH band-0 normalisation constant: 1 / (2 * sqrt(pi)).
        /// Matches PlayCanvas gsplat and SuperSplat's DC term.
        /// </summary>
        public const float SH_C0 = 0.28209479177387814f;

        /// <summary>
        /// Inverse of the standard sigmoid: logit(a) = ln(a / (1 - a)).
        /// Clamps the input to (eps, 1-eps) to keep the result finite when
        /// SuperSplat stored a saturated 0 or 1 alpha.
        /// </summary>
        [BurstCompile]
        public static float SigmoidInvOpacity(float a)
        {
            const float k_Eps = 1e-6f;
            if (a < k_Eps) a = k_Eps;
            else if (a > 1f - k_Eps) a = 1f - k_Eps;
            return Unity.Mathematics.math.log(a / (1f - a));
        }
    }
}
