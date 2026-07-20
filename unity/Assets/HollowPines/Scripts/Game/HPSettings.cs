// Persisted player settings (PlayerPrefs) — the Unity seed of the web build's core/Settings.ts.
// Kept tiny for the title-menu milestone: name, mouse sensitivity, master volume, fullscreen.
// MasterVolume drives AudioListener.volume now, so tomorrow's audio engine inherits it for free.
using UnityEngine;

namespace HollowPines.Game
{
    public static class HPSettings
    {
        private const string KeyName = "hp_name";
        private const string KeySens = "hp_sensitivity";
        private const string KeyVolume = "hp_volume";
        private const string KeyRenderScale = "hp_renderscale";

        public static string PlayerName = "";
        public static float MouseSensMul = 1f;   // multiplier over the sim's base sensitivity
        public static float MasterVolume = 0.85f;
        /// <summary>
        /// Fraction of the display resolution the 3D scene renders at (URP renderScale), upscaled to
        /// fit. This is the Unity equivalent of the web build's QUALITY.pixelRatioCap — the browser
        /// never rendered at a 4K-ish native resolution either. It is BY FAR the biggest performance
        /// lever on an integrated GPU: cost scales with the square, so 0.7 draws about half the pixels.
        /// </summary>
        public static float RenderScale = 0.7f;

        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            PlayerName = PlayerPrefs.GetString(KeyName, "");
            MouseSensMul = Mathf.Clamp(PlayerPrefs.GetFloat(KeySens, 1f), 0.2f, 3f);
            MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyVolume, 0.85f));
            RenderScale = Mathf.Clamp(PlayerPrefs.GetFloat(KeyRenderScale, 0.7f), 0.4f, 1f);
            Apply();
        }

        public static void Save()
        {
            PlayerPrefs.SetString(KeyName, PlayerName);
            PlayerPrefs.SetFloat(KeySens, MouseSensMul);
            PlayerPrefs.SetFloat(KeyVolume, MasterVolume);
            PlayerPrefs.SetFloat(KeyRenderScale, RenderScale);
            PlayerPrefs.Save();
            Apply();
        }

        public static void Apply()
        {
            AudioListener.volume = MasterVolume;
            HPQuality.ApplyRenderScale(RenderScale);
        }
    }
}
