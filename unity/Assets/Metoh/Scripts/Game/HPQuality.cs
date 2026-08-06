// Performance knobs for the URP pipeline asset — the Unity counterpart of the web build's QUALITY
// block in config.ts. The browser build capped its device pixel ratio so it never rendered at a
// laptop panel's full native resolution; Unity WILL, and on integrated graphics at 2560x1600 that
// alone is the difference between choppy and smooth. Fill rate scales with the square of the
// resolution, so renderScale 0.7 costs about half as many pixels as 1.0.
//
// Applied at startup (HPSettings.Apply) and live whenever the quality slider moves, so the owner
// can trade sharpness for frame rate without leaving the game.
//
// The render-scale slider doubles as the DETAIL TIER. The realism pass (normal-mapped surfaces, soft
// shadows, a bounce-lit ambient) is not free, and the machine this is developed on has integrated
// graphics, so the expensive half is gated: anyone who has already pulled the slider down to buy
// frames should not silently be paying for soft shadows and a longer shadow distance as well.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Metoh.Game
{
    public static class HPQuality
    {
        /// <summary>Shadow draw distance in metres, on the tier that can afford it. The world is
        /// fogged, so beyond this a shadow is invisible anyway and pure waste.</summary>
        private const float ShadowDistanceHigh = 55f;
        private const float ShadowDistanceLow = 30f;

        /// <summary>
        /// Render scale at or above which the expensive lighting is switched on.
        ///
        /// **This used to be a bug, and it hid the entire realism pass.** The test was
        /// `HighDetail = renderScale > 0.7f` while the shipping default was *exactly* 0.7 — so the
        /// comparison came out false on a clean install and the game booted into the cheap tier every
        /// single time. Hard shadows and a 30 m shadow distance were not a fallback anyone had chosen;
        /// they were what everybody got, and the soft shadows the material pass was tuned against had
        /// never once been on screen.
        ///
        /// The threshold now sits at 0.8, strictly BETWEEN the two scales anyone actually runs (0.7
        /// cheap, 1.0 native), so no default can ever land on the boundary again. Never set this equal
        /// to a value the slider can produce.
        /// </summary>
        private const float HighTierScale = 0.8f;

        private static bool _appliedOnce;

        /// <summary>True when the current tier is paying for the expensive lighting.</summary>
        public static bool HighDetail { get; private set; } = true;

        /// <summary>Set the URP render scale (0.4..1). Cheap to call; safe if URP isn't active yet.</summary>
        public static void ApplyRenderScale(float scale)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) return;

            urp.renderScale = Mathf.Clamp(scale, 0.4f, 1f);
            HighDetail = urp.renderScale >= HighTierScale;

            // Shadow distance is the cheapest big lever on a shadow budget: cost scales with the
            // volume the cascades have to cover, not with what ends up on screen.
            urp.shadowDistance = HighDetail ? ShadowDistanceHigh : ShadowDistanceLow;

            ApplyShadowQuality();

            if (!_appliedOnce)
            {
                _appliedOnce = true;
                urp.msaaSampleCount = 1; // MSAA is a luxury this GPU can't afford
                Debug.Log($"[HPQuality] renderScale {urp.renderScale:0.00}, MSAA off, " +
                          $"detail {(HighDetail ? "HIGH" : "LOW")}, shadow distance {urp.shadowDistance} m " +
                          $"(screen {Screen.width}x{Screen.height}).");
            }
        }

        /// <summary>
        /// Push the current tier's shadow setting onto the moon.
        ///
        /// Public because the light is rebuilt on every world reseed, so WorldBuilder has to be able
        /// to re-assert this after BuildLighting — otherwise a reseed silently resets the look. Soft
        /// shadows matter more here than they would over a forest floor: a hard-edged shadow on open
        /// snowpack is one of the loudest "this is a game" tells there is.
        /// </summary>
        public static void ApplyShadowQuality()
        {
            var moon = WorldBuilder.MoonLight;
            if (moon == null) return;
            moon.shadows = HighDetail ? LightShadows.Soft : LightShadows.Hard;
        }
    }
}
