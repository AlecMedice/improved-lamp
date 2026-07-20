// Rebindable action -> key map (PlayerPrefs) — the Unity seed of the web build's core/Keybinds.ts.
// Esc and the mouse buttons stay fixed, like the web build. The title screen's settings page does
// the rebinding UI (click an action, press a key; Esc cancels a capture).
using System.Collections.Generic;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
#endif

namespace HollowPines.Game
{
    public enum HPAction { Sprint, Jump, Crouch, Flashlight, Revive, Mark, Senses, Map, Ping, Flash }

    public static class HPKeybinds
    {
#if ENABLE_INPUT_SYSTEM
        private static readonly Dictionary<HPAction, Key> Defaults = new Dictionary<HPAction, Key>
        {
            { HPAction.Sprint, Key.LeftShift },   // sprint — both roles (Bigfoot's is faster)
            { HPAction.Jump, Key.Space },         // jump / vault / leap / (hold) climb
            { HPAction.Crouch, Key.LeftCtrl },
            { HPAction.Flashlight, Key.F },
            { HPAction.Revive, Key.E },
            { HPAction.Mark, Key.T },             // Wren's trail mark
            { HPAction.Senses, Key.V },           // Bigfoot's senses overlay
            { HPAction.Map, Key.Tab },            // map overlay — you keep walking while it's up
            { HPAction.Ping, Key.Q },             // searcher stakeout ping at your feet
            { HPAction.Flash, Key.G },            // Eli's camera flash
        };

        private static Dictionary<HPAction, Key> _map;

        /// <summary>Action currently waiting for a key press in the settings UI (null = none).</summary>
        public static HPAction? Capturing;

        private static void EnsureLoaded()
        {
            if (_map != null) return;
            _map = new Dictionary<HPAction, Key>();
            foreach (var kv in Defaults)
            {
                int stored = PlayerPrefs.GetInt("hp_key_" + kv.Key, (int)kv.Value);
                _map[kv.Key] = (Key)stored;
            }
        }

        public static Key Get(HPAction a)
        {
            EnsureLoaded();
            return _map[a];
        }

        public static void Set(HPAction a, Key key)
        {
            EnsureLoaded();
            _map[a] = key;
            PlayerPrefs.SetInt("hp_key_" + a, (int)key);
            PlayerPrefs.Save();
        }

        public static void ResetDefaults()
        {
            foreach (var kv in Defaults) Set(kv.Key, kv.Value);
        }

        public static string Label(HPAction a)
        {
            Key k = Get(a);
            switch (k)
            {
                case Key.LeftShift: return "Shift";
                case Key.LeftCtrl: return "Ctrl";
                case Key.Space: return "Space";
                case Key.Tab: return "Tab";
                default: return k.ToString();
            }
        }

        public static bool Down(Keyboard kb, HPAction a) => kb[Get(a)].isPressed;
        public static bool Pressed(Keyboard kb, HPAction a) => kb[Get(a)].wasPressedThisFrame;

        /// <summary>Poll while a capture is pending (called from TitleMenu.Update). Esc cancels.</summary>
        public static void UpdateCapture()
        {
            if (Capturing == null) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.escapeKey.wasPressedThisFrame) { Capturing = null; return; }
            foreach (KeyControl kc in kb.allKeys)
            {
                if (!kc.wasPressedThisFrame) continue;
                if (kc.keyCode != Key.Escape) Set(Capturing.Value, kc.keyCode);
                Capturing = null;
                return;
            }
        }
#else
        public static string Label(HPAction a) => a.ToString();
#endif
    }
}
