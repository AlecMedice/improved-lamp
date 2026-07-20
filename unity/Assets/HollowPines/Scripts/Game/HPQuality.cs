// Performance knobs for the URP pipeline asset — the Unity counterpart of the web build's QUALITY
// block in config.ts. The browser build capped its device pixel ratio so it never rendered at a
// laptop panel's full native resolution; Unity WILL, and on integrated graphics at 2560x1600 that
// alone is the difference between choppy and smooth. Fill rate scales with the square of the
// resolution, so renderScale 0.7 costs about half as many pixels as 1.0.
//
// Applied at startup (HPSettings.Apply) and live whenever the quality slider moves, so the owner
// can trade sharpness for frame rate without leaving the game.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HollowPines.Game
{
    public static class HPQuality
    {
        /// <summary>Shadow draw distance in metres. The forest is dense and fogged — 50 m was wasted work.</summary>
        private const float ShadowDistance = 35f;

        private static bool _appliedOnce;

        /// <summary>Set the URP render scale (0.4..1). Cheap to call; safe if URP isn't active yet.</summary>
        public static void ApplyRenderScale(float scale)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (urp == null) return;

            urp.renderScale = Mathf.Clamp(scale, 0.4f, 1f);

            if (!_appliedOnce)
            {
                _appliedOnce = true;
                urp.msaaSampleCount = 1;             // MSAA is a luxury this GPU can't afford
                urp.shadowDistance = ShadowDistance;
                Debug.Log($"[HPQuality] renderScale {urp.renderScale:0.00}, MSAA off, shadow distance {ShadowDistance} m " +
                          $"(screen {Screen.width}x{Screen.height}).");
            }
        }
    }
}
