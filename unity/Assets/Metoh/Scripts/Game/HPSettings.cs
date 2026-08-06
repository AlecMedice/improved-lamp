// Persisted player settings (PlayerPrefs) — the Unity seed of the web build's core/Settings.ts.
// Kept tiny for the title-menu milestone: name, mouse sensitivity, master volume, fullscreen.
// MasterVolume drives AudioListener.volume now, so tomorrow's audio engine inherits it for free.
using UnityEngine;

namespace Metoh.Game
{
    public static class HPSettings
    {
        private const string KeyName = "hp_name";
        private const string KeySens = "hp_sensitivity";
        private const string KeyVolume = "hp_volume";
        private const string KeyRenderScale = "hp_renderscale";
        private const string KeyVersion = "hp_settings_version";
        private const string KeyDevSpecialty = "hp_dev_specialty";
        private const string KeyDevWorldSeed = "hp_dev_worldseed";
        private const string KeyLastJoin = "hp_last_join";

        public static string PlayerName = "";
        public static float MouseSensMul = 1f;   // multiplier over the sim's base sensitivity
        public static float MasterVolume = 0.85f;
        /// <summary>
        /// Fraction of the display resolution the 3D scene renders at (URP renderScale), upscaled to
        /// fit. This is the Unity equivalent of the web build's QUALITY.pixelRatioCap — the browser
        /// never rendered at a 4K-ish native resolution either. It is BY FAR the biggest performance
        /// lever on an integrated GPU: cost scales with the square, so 0.7 draws about half the pixels.
        ///
        /// DEFAULTS TO NATIVE (1.0) as of the legibility pass, and that is a deliberate reversal.
        /// Shipping at 0.7 meant every screenshot and every first impression of the game was a 70%
        /// buffer stretched back up — soft edges, crawling stair-steps on every trunk, and the fine
        /// normal-map grain the whole realism pass is built on resolving to mush before it ever
        /// reached a pixel. That reads as "old game" far more loudly than any amount of polygon
        /// budget does, and no amount of material work can be seen through it. The slider is still
        /// right there in the pause menu for anyone who needs the frames back; it is just no longer
        /// the thing you get without asking.
        /// </summary>
        public static float RenderScale = 1f;

        /// <summary>
        /// DEV ONLY — force which searcher persona you're dealt ("" = the normal random deal).
        /// Exists because several systems are gated behind a single character (casting is Mara's,
        /// the flash is Eli's, the battery is Sam's), so testing them by rerolling matches is a
        /// 1-in-5 lottery. Persisted, so it survives the restarts a test session is made of.
        /// The host still re-validates the id; an unknown value just falls back to a random deal.
        /// </summary>
        public static string DevSpecialty = "";

        /// <summary>
        /// DEV ONLY — force the world seed when HOSTING (0 = roll a fresh forest each session).
        /// The forest, the trail network and the cave positions all derive from this, so without an
        /// override a bug you hit in one session's map is unreproducible: the map is gone the moment
        /// you restart. Set it to the seed the F3 overlay printed and you get that exact forest back.
        /// Ignored when joining — the host owns the seed and replicates it.
        /// </summary>
        public static uint DevWorldSeed;

        /// <summary>The last address a JOIN was attempted against, so a returning player doesn't retype
        /// their friend's IP every launch. Defaults to loopback for same-PC testing.</summary>
        public static string LastJoinAddress = "127.0.0.1";

        private static bool _loaded;

        /// <summary>
        /// Bump when a DEFAULT changes in a way an existing player must actually receive.
        ///
        /// PlayerPrefs is stickier than it looks: the code default only applies to a machine that has
        /// never saved, so raising <see cref="RenderScale"/> to 1.0 would have changed nothing at all
        /// for the one person who has been playing this the whole time. Anyone who had ever opened the
        /// pause menu was pinned at 0.7 forever, which is precisely the state the pass was fixing.
        /// Version 2 = the legibility pass's native-resolution default.
        /// </summary>
        private const int SettingsVersion = 2;

        /// <summary>The pre-v2 shipping default, and the only value v2's migration will overwrite.</summary>
        private const float LegacyRenderScale = 0.7f;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            int version = PlayerPrefs.GetInt(KeyVersion, 1);
            PlayerName = PlayerPrefs.GetString(KeyName, "");
            MouseSensMul = Mathf.Clamp(PlayerPrefs.GetFloat(KeySens, 1f), 0.2f, 3f);
            MasterVolume = Mathf.Clamp01(PlayerPrefs.GetFloat(KeyVolume, 0.85f));
            RenderScale = Mathf.Clamp(PlayerPrefs.GetFloat(KeyRenderScale, RenderScale), 0.4f, 1f);

            // v1 -> v2: lift a saved 0.7 to native. Deliberately narrow — it moves the value ONLY if
            // it is exactly the old default, so a deliberate 0.5 chosen to buy frames is left alone.
            // An inherited default and a considered choice are indistinguishable in the store, so the
            // tie is broken toward "never override something the player might have typed".
            if (version < 2)
            {
                if (Mathf.Approximately(RenderScale, LegacyRenderScale))
                {
                    RenderScale = 1f;
                    Debug.Log("[HPSettings] Migrated render scale 0.70 -> 1.00 (native). " +
                              "Pause menu -> quality slider if you need the frames back.");
                }
                PlayerPrefs.SetInt(KeyVersion, SettingsVersion);
            }
            DevSpecialty = PlayerPrefs.GetString(KeyDevSpecialty, "");
            // Stored as a string: PlayerPrefs has no uint, and seeds run past int.MaxValue.
            uint.TryParse(PlayerPrefs.GetString(KeyDevWorldSeed, "0"), out DevWorldSeed);
            LastJoinAddress = PlayerPrefs.GetString(KeyLastJoin, "127.0.0.1");
            Apply();
        }

        public static void Save()
        {
            PlayerPrefs.SetString(KeyName, PlayerName);
            PlayerPrefs.SetFloat(KeySens, MouseSensMul);
            PlayerPrefs.SetFloat(KeyVolume, MasterVolume);
            PlayerPrefs.SetFloat(KeyRenderScale, RenderScale);
            PlayerPrefs.SetString(KeyDevSpecialty, DevSpecialty);
            PlayerPrefs.SetString(KeyDevWorldSeed, DevWorldSeed.ToString());
            PlayerPrefs.SetString(KeyLastJoin, LastJoinAddress);
            PlayerPrefs.SetInt(KeyVersion, SettingsVersion);
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
