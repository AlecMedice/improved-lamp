// DEV — the F3 diagnostics overlay: frame cost, what the world is made of, and live switches for
// the expensive things.
//
// Why this exists: the owner's machine is an Intel Iris Plus 655 driving 2560x1600, and the forest
// just went from 700 trees to ~2,400. "It felt slow" is not an actionable bug report — it doesn't
// say whether the cost is bloom, the realtime point lights, the undergrowth or the trees, and those
// four have wildly different fixes. The toggles below let a play-tester bisect it in ten seconds
// without a rebuild, in the same spirit as the renderScale slider in the pause menu.
//
// It also prints the WORLD SEED. The forest is rolled per hosting session now, so without the seed
// on screen a bug found in one map is unreproducible — the map is gone the moment you restart.
// Type that number into the title screen's dev field to get the exact forest back.
//
// IMGUI like the rest of the throwaway UI. Costs nothing when hidden (OnGUI early-outs).
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HollowPines.Game
{
    public class HPDebug : MonoBehaviour
    {
        public static HPDebug Instance { get; private set; }

        /// <summary>Attach once. WorldBuilder bootstraps this alongside PostFX/HPAudio.</summary>
        public static void Ensure(GameObject host)
        {
            if (Instance != null) return;
            Instance = host.AddComponent<HPDebug>();
        }

        private bool _open;

        // Frame timing, exponentially smoothed. A raw per-frame number is unreadable and a 1-second
        // average hides exactly the hitches you're hunting, so: smoothed average PLUS the worst
        // frame in the last second, which is what actually makes a game feel bad.
        private float _smoothedMs = 16f;
        private float _worstMs;
        private float _worstResetAt;

        // Live lever states. Defaults match the shipped configuration.
        private bool _bloom = true;
        private bool _propLights = true;
        private bool _undergrowth = true;
        private bool _shadows = true;

        private GUIStyle _style;

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb.f3Key.wasPressedThisFrame) _open = !_open;
            if (!_open) return;

            // Number keys flip the levers. Only while the overlay is up, so they can't collide with
            // anything the game binds — and the overlay is the only place they're documented.
            if (kb.digit1Key.wasPressedThisFrame) SetBloom(!_bloom);
            if (kb.digit2Key.wasPressedThisFrame) SetPropLights(!_propLights);
            if (kb.digit3Key.wasPressedThisFrame) SetUndergrowth(!_undergrowth);
            if (kb.digit4Key.wasPressedThisFrame) SetShadows(!_shadows);
#endif
        }

        private void LateUpdate()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _smoothedMs = Mathf.Lerp(_smoothedMs, ms, 0.08f);
            if (ms > _worstMs) _worstMs = ms;
            if (Time.unscaledTime - _worstResetAt > 1f) { _worstMs = ms; _worstResetAt = Time.unscaledTime; }
        }

        private void SetBloom(bool on)
        {
            _bloom = on;
            if (PostFX.Instance != null) PostFX.Instance.SetBloomEnabled(on);
        }

        private void SetPropLights(bool on)
        {
            _propLights = on;
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.SetPropLightsEnabled(on);
        }

        private void SetUndergrowth(bool on)
        {
            _undergrowth = on;
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.SetUndergrowthVisible(on);
        }

        private void SetShadows(bool on)
        {
            _shadows = on;
            // Distance, not the light's shadow flag: 0 disables shadow rendering wholesale, and it's
            // the same knob HPQuality tunes, so this stays consistent with the shipped lever.
            QualitySettings.shadowDistance = on ? 35f : 0f;
        }

        private void OnGUI()
        {
            if (!_open) return;
            EnsureStyles();

            var world = WorldBuilder.World;
            var gm = GameManager.Instance;
            var wb = WorldBuilder.Instance;

            float fps = _smoothedMs > 0.001f ? 1000f / _smoothedMs : 0f;
            string seed = world != null ? world.Seed.ToString() : "—";
            int trees = world != null ? world.Colliders.Count - world.Climbables.Count : 0;
            int trails = world != null ? world.Paths.Count : 0;

            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"fps {fps:0}   frame {_smoothedMs:0.0} ms   worst/s {_worstMs:0.0} ms");
            lines.AppendLine($"renderScale {HPSettings.RenderScale:0.00}   {Screen.width}x{Screen.height}");
            lines.AppendLine();
            lines.AppendLine($"seed {seed}   trees {trees}   trails {trails}");
            if (wb != null)
                lines.AppendLine($"undergrowth meshes {wb.UndergrowthMeshCount}   prop lights {wb.PropLightCount}");
            if (gm != null)
            {
                string phase = gm.MatchPhase.Value == GameManager.PhasePlaying ? "playing"
                    : gm.MatchPhase.Value == GameManager.PhaseLobby ? "lobby" : "results";
                lines.AppendLine($"phase {phase}   night {gm.NightNumber.Value}/{gm.TotalNights.Value}   " +
                                 $"proof {gm.StoredProof}/{gm.VideosRequired.Value}");
            }
            lines.AppendLine($"players {HPPlayer.All.Count}   tick {TickRateLabel()}");
            lines.AppendLine();
            lines.AppendLine("COST LEVERS — in the order §7 says to pull them");
            lines.AppendLine($"  [1] bloom        {OnOff(_bloom)}   (most expensive single effect)");
            lines.AppendLine($"  [2] prop lights  {OnOff(_propLights)}");
            lines.AppendLine($"  [3] undergrowth  {OnOff(_undergrowth)}");
            lines.AppendLine($"  [4] shadows      {OnOff(_shadows)}");
            lines.AppendLine();
            lines.Append("[F3] closes  ·  renderScale lives in the Esc pause menu");

            string text = lines.ToString();
            var size = _style.CalcSize(new GUIContent(text));
            var rect = new Rect(8f, 8f, Mathf.Min(size.x + 18f, Screen.width - 16f), size.y + 14f);

            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f), text, _style);
        }

        private static string OnOff(bool b) => b ? "ON " : "off";

        private static string TickRateLabel()
        {
            var tm = FishNet.InstanceFinder.TimeManager;
            return tm != null ? $"{tm.TickRate} Hz" : "—";
        }

        private void EnsureStyles()
        {
            if (_style != null) return;
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                alignment = TextAnchor.UpperLeft,
                font = Font.CreateDynamicFontFromOSFont("Consolas", 12),
            };
            _style.normal.textColor = new Color(0.72f, 0.95f, 0.78f);
        }
    }
}
