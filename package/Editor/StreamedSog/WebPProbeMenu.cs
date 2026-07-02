// SPDX-License-Identifier: MIT
// Track C4b: editor menu that lets us verify libwebp is wired without spinning
// up a whole scene. Loads a synthetic 1-byte payload through the real decoder
// probe and logs Pass/Fail with the same fail-loud message the runtime gate uses.

using GaussianSplatting.Runtime.StreamedSog;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor.StreamedSog
{
    static class WebPProbeMenu
    {
        [MenuItem("Tools/GaussianSplatting/Test/WebP Probe")]
        static void RunProbe()
        {
            var decoder = new NativeWebPDecoder();
            bool ok = decoder.Probe();
            if (ok)
                Debug.Log("[SOG] WebP Probe: Pass — libwebp is reachable via com.netpyoung.webp.");
            else
                Debug.LogError(
                    "[SOG] WebP Probe: Fail — libwebp not available. " +
                    "Ensure com.netpyoung.webp resolves and native binaries deployed.");
        }
    }
}
