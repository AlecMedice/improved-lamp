// Things that went wrong during startup, said OUT LOUD.
//
// WHY THIS EXISTS. Nearly every asset lookup in this project has a graceful fallback, and every one
// of those fallbacks produces plausible-looking output:
//
//   Metoh/Snowpack missing   -> flat snow. No rock blend, no basin tint, no wind scour.
//   Metoh/NightSky missing   -> a flat solid-colour sky. No moon, no stars, no ridgeline.
//   Metoh/TreeSway missing   -> a rigid, unlit-looking forest.
//   Metoh/TorchBeam missing  -> a torch with no visible shaft.
//
// None of those throw. None of them stop the game. They just make it look like the art was never
// done — which is exactly how UNITY_NOTES describes the Snowpack parse error surviving an entire
// pass, and the render-scale default hiding the whole realism pass before that. The rule that came
// out of those is already written down: **if a Shader.Find fallback fires, treat it as an error, not
// as a degraded mode.** Until now nothing enforced it, because the only place it was recorded was a
// warning in a Console nobody reads during a play-test.
//
// So: fallbacks report here, and the title screen draws them over the menu. A broken import now says
// what it is on the first screen you see, instead of being described three days later as "the
// graphics look off".
//
// Deliberately dependency-free and static — it is written to from WorldBuilder.Awake, which runs
// before anything else exists, and read from IMGUI.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    public static class BootReport
    {
        private static readonly List<string> Problems = new List<string>();

        /// <summary>Everything that degraded at startup. Empty is the healthy case.</summary>
        public static IReadOnlyList<string> All => Problems;

        public static bool Any => Problems.Count > 0;

        /// <summary>
        /// Record a startup degradation. De-duplicated, because the shader lookups run per material
        /// and a missing one would otherwise report itself sixty times.
        /// </summary>
        public static void Problem(string what)
        {
            if (string.IsNullOrEmpty(what) || Problems.Contains(what)) return;
            Problems.Add(what);
            // Console + the play-test log as well, so it is in the same timeline as everything else.
            Debug.LogError("[boot] " + what);
            HPLog.Event("BOOT", what);
        }

        /// <summary>
        /// A missing shader, phrased as the thing to actually go and do about it. The live Unity
        /// project is a separate tree from the repo, and a sync that copies Scripts/ but not Shaders/
        /// is the single most common way to arrive here (UNITY_NOTES [workflow]).
        /// </summary>
        public static void MissingShader(string shaderName, string consequence)
        {
            Problem($"shader '{shaderName}' not found — {consequence}. " +
                    "Sync Assets/Metoh/Shaders into the live project and re-import.");
        }

        /// <summary>Cleared on a world rebuild so a reseed re-tests rather than showing stale news.</summary>
        public static void Reset() => Problems.Clear();
    }
}
