// The title card + main menu shown when the exe opens — mechanical structure borrowed from
// R.E.P.O.: a vertical stack of START NEW GAME / JOIN GAME / SETTINGS / QUIT over a live 3D
// backdrop (a slow drift around the dark camp clearing; the forest is already built by
// WorldBuilder). This is the only pre-connection UI — it drives hosting and joining directly.
//
// Visible whenever no connection is up, so a disconnect (host quit, kicked, cancel) lands you
// back here automatically. JOIN is direct address (Tugboat) until Path B adds Steam invites —
// then JOIN becomes the friends/lobby flow and this menu doesn't need restructuring.
// IMGUI like the rest of the throwaway UI; the real face goes on in R5.
using FishNet;
using UnityEngine;

namespace HollowPines.Game
{
    public class TitleMenu : MonoBehaviour
    {
        private enum Page { Root, Join, Settings }

        private Page _page = Page.Root;
        private string _address = "127.0.0.1";
        private float _connectStartedAt = -1f; // >=0 while a join attempt is in flight
        private string _error;                 // last hosting/joining failure, shown under the buttons
        private Vector2 _settingsScroll;       // settings page scroll (it's taller than a short window)
        private GUIStyle _titleStyle, _subStyle, _buttonStyle, _labelStyle, _errorStyle;

        private void Awake()
        {
            HPSettings.Load();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            HPKeybinds.UpdateCapture(); // key-rebind capture (settings page)

            // Esc always backs out to the root menu — a second way home, so a mis-sized panel can
            // never strand the player on a sub-page again. (Ignored while capturing a rebind: Esc
            // cancels the capture there, handled inside UpdateCapture.)
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame && HPKeybinds.Capturing == null && _page != Page.Root)
            {
                if (_page == Page.Settings) CloseSettings();
                else _page = Page.Root;
            }
#endif
            bool connected = Connected();
            SetTitleLighting(!connected); // the backdrop is lit well above gameplay dusk
            if (connected) { _connectStartedAt = -1f; return; }

            // At the title: keep the cursor free and drift the camera slowly around camp.
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            var cam = Camera.main;
            if (cam != null && cam.transform.parent == null) OrbitCamp(cam);
        }

        /// <summary>
        /// The title cinematic. Shared with the camp lobby (HPPlayer keeps it running there whenever
        /// the player isn't holding right-mouse to take control), so the game never drops from a
        /// moving title card to a motionless first-person shot.
        ///
        /// Three shots on a loop rather than one endless orbit. Camp is still the anchor — it opens
        /// and closes there, and it is the only lit thing in the frame — but the forest is now the
        /// thing the camera moves THROUGH and looks AT, because there is finally a forest to show:
        /// the treeline used to be a thin scatter of poles that looked better ignored.
        ///
        /// Every shot is derived from the seeded world, so the title card is this session's actual
        /// map. Cuts, not blends: a hard cut costs nothing and reads as intent, where a slow blend
        /// between two moving shots just reads as drift.
        /// </summary>
        public static void OrbitCamp(Camera cam)
        {
            const float shot = 11f; // seconds per shot
            float t = Time.time % (shot * 3f);
            int which = (int)(t / shot);
            float k = (t - which * shot) / shot; // 0..1 within the shot

            var world = WorldBuilder.EnsureWorld();
            float campY = world != null ? (float)world.GetHeight(0, 0) : 0f;
            var fire = new Vector3(0f, campY + 1.2f, 0f);

            switch (which)
            {
                case 0:
                {
                    // Shot 1 — the camp itself: a close, low orbit so the fire and the RV fill the
                    // frame with the treeline standing behind them.
                    float a = k * 1.5f;
                    cam.transform.position = fire + new Vector3(Mathf.Sin(a) * 12f, 2.0f, Mathf.Cos(a) * 12f);
                    cam.transform.LookAt(fire + new Vector3(0f, 0.6f, 0f));
                    break;
                }
                case 1:
                {
                    // Shot 2 — down a logging trail, at head height, drifting along the corridor.
                    // This is the shot that only works now: it needs walls of trunks on both sides
                    // to read as a path through anything.
                    var pts = world != null && world.Paths.Count > 0 ? world.Paths[0].Pts : null;
                    if (pts != null && pts.Count > 3)
                    {
                        float f = Mathf.Lerp(1f, Mathf.Min(5f, pts.Count - 2), k);
                        int i = Mathf.Clamp((int)f, 1, pts.Count - 2);
                        float frac = f - i;
                        // Vector2 carries world (x, z) here — .y IS the world z, not a height.
                        Vector2 here = LerpXZ(pts[i], pts[i + 1], frac);
                        Vector2 ahead = LerpXZ(pts[Mathf.Min(i + 1, pts.Count - 1)], pts[Mathf.Min(i + 2, pts.Count - 1)], frac);
                        float y = (float)world.GetHeight(here.x, here.y);
                        cam.transform.position = new Vector3(here.x, y + 1.9f, here.y);
                        cam.transform.LookAt(new Vector3(ahead.x, (float)world.GetHeight(ahead.x, ahead.y) + 1.6f, ahead.y));
                    }
                    else goto case 0; // no trails (shouldn't happen) — fall back to the camp orbit
                    break;
                }
                default:
                {
                    // Shot 3 — a high, slow push back toward camp over the canopy, so the last thing
                    // you see before the menu settles is the one warm light in a very large forest.
                    float dist = Mathf.Lerp(95f, 46f, k * k); // ease in: fast at first, settling late
                    var from = new Vector3(Mathf.Sin(2.1f) * dist, campY + 34f, Mathf.Cos(2.1f) * dist);
                    cam.transform.position = from;
                    cam.transform.LookAt(fire);
                    break;
                }
            }
        }

        /// <summary>Interpolate two sim trail points. The result packs world (x, z) into (x, y).</summary>
        private static Vector2 LerpXZ(HollowPines.Sim.Vec2 a, HollowPines.Sim.Vec2 b, float k)
        {
            return new Vector2(Mathf.Lerp((float)a.X, (float)b.X, k), Mathf.Lerp((float)a.Z, (float)b.Z, k));
        }

        /// <summary>Raise the world's brightness while the menu is up, and restore it on connect.</summary>
        private void SetTitleLighting(bool on)
        {
            if (WorldBuilder.TitleMode == on) return;
            WorldBuilder.TitleMode = on;
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.InvalidatePalette(); // re-apply now
            if (PostFX.Instance != null) PostFX.Instance.SetTitleBrightness(on);

            // Join attempt timed out? (Tugboat fails quietly if nothing is listening.)
            if (_connectStartedAt >= 0f && Time.time - _connectStartedAt > 8f)
            {
                InstanceFinder.ClientManager.StopConnection();
                _connectStartedAt = -1f;
            }
        }

        private static bool Connected()
        {
            return (InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started) ||
                   (InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started);
        }

        private void OnGUI()
        {
            if (Connected()) return;
            EnsureStyles();

            // Backdrop scrim so the buttons read over the forest.
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float cx = Screen.width / 2f;
            GUI.Label(new Rect(0, Screen.height * 0.14f, Screen.width, 90f), "HOLLOW PINES", _titleStyle);
            // Tagline on the front page only — the sub-pages need the vertical space.
            if (_page == Page.Root)
                GUI.Label(new Rect(0, Screen.height * 0.14f + 86f, Screen.width, 30f),
                    "five went looking for proof — something in the pines was looking back", _subStyle);

            switch (_page)
            {
                case Page.Root: DrawRoot(cx); break;
                case Page.Join: DrawJoin(cx); break;
                case Page.Settings: DrawSettings(cx); break;
            }

            if (!string.IsNullOrEmpty(_error))
                GUI.Label(new Rect(0f, Screen.height * 0.80f, Screen.width, 44f),
                    _error + "\n(full details in the Console)", _errorStyle);

            GUI.Label(new Rect(12f, Screen.height - 26f, 600f, 22f),
                "pre-alpha · local/LAN build · Steam relay + friend invites coming", _labelStyle);
        }

        private void DrawRoot(float cx)
        {
            float y = Screen.height * 0.42f;
            if (MenuButton(cx, ref y, "START NEW GAME"))
            {
                // Never fail silently: a menu button that does nothing is the worst failure mode.
                // StartConnection returns false on a refused socket, and a throw here (e.g. from a
                // scene NetworkObject's OnStartServer) would otherwise strand us on this screen.
                _error = null;
                try
                {
                    if (!InstanceFinder.ServerManager.StartConnection())
                        _error = "server failed to start — is port 7770 already in use?";
                    else if (!InstanceFinder.ClientManager.StartConnection())
                        _error = "server started, but the local client could not connect";
                }
                catch (System.Exception e)
                {
                    _error = "start failed: " + e.GetType().Name + " — " + e.Message;
                    Debug.LogException(e); // full stack trace lands in the Console
                }
            }
            if (MenuButton(cx, ref y, "JOIN GAME")) _page = Page.Join;
            if (MenuButton(cx, ref y, "SETTINGS")) _page = Page.Settings;
            if (MenuButton(cx, ref y, "QUIT"))
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }

            DrawDevStrip(cx, y + 6f);
        }

        /// <summary>
        /// DEV — force the persona you'll be dealt. Casting belongs to Mara, the flash to Eli and the
        /// spare battery to Sam, so without this, testing any of them means restarting until the
        /// random deal happens to hand you the right character (a 1-in-5 lottery per match).
        /// The host re-validates the id, so this can't be used to invent a specialty.
        /// </summary>
        private void DrawDevStrip(float cx, float y)
        {
            var head = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            head.normal.textColor = new Color(0.55f, 0.6f, 0.58f);
            GUI.Label(new Rect(cx - 220f, y, 440f, 18f), "DEV — force persona (testing only)", head);
            y += 20f;

            // "random" plus one button per specialty, labelled with the character's first name.
            string[] ids = Sim.Specialties.SpecialtyIds;
            int count = ids.Length + 1;
            float bw = 68f, gap = 4f;
            float total = count * bw + (count - 1) * gap;
            float x = cx - total / 2f;
            var small = new GUIStyle(GUI.skin.button) { fontSize = 11 };

            for (int i = 0; i < count; i++)
            {
                string id = i == 0 ? "" : ids[i - 1];
                string label = i == 0 ? "random" : FirstName(id);
                bool active = HPSettings.DevSpecialty == id;

                Color old = GUI.color;
                if (active) GUI.color = new Color(1f, 0.85f, 0.5f);
                if (GUI.Button(new Rect(x, y, bw, 22f), label, small))
                {
                    HPSettings.DevSpecialty = id;
                    HPSettings.Save();
                }
                GUI.color = old;
                x += bw + gap;
            }

            DrawDevSeed(cx, y + 26f);
        }

        /// <summary>
        /// DEV — pin the world seed when HOSTING (blank/0 = a fresh forest each session).
        /// The forest, trails and cave positions all derive from the seed, so without this a bug you
        /// hit in one map can never be revisited — the map is gone the moment you restart. The F3
        /// overlay prints the live seed; paste it here to get that exact forest back. Ignored when
        /// joining: the host owns the seed and replicates it.
        /// </summary>
        private void DrawDevSeed(float cx, float y)
        {
            var head = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            head.normal.textColor = new Color(0.55f, 0.6f, 0.58f);
            GUI.Label(new Rect(cx - 220f, y, 440f, 18f),
                "DEV — pin world seed when hosting (blank = random each session)", head);
            y += 20f;

            var field = new GUIStyle(GUI.skin.textField) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            string shown = HPSettings.DevWorldSeed == 0 ? "" : HPSettings.DevWorldSeed.ToString();
            string typed = GUI.TextField(new Rect(cx - 90f, y, 120f, 22f), shown, 10, field);
            if (typed != shown)
            {
                // Empty or unparseable => 0 => random. Never reject a keystroke: a field you can't
                // clear because it won't accept "" is worse than one that quietly means "random".
                if (!uint.TryParse(typed, out uint parsed)) parsed = 0;
                HPSettings.DevWorldSeed = parsed;
                HPSettings.Save();
            }
            var small = new GUIStyle(GUI.skin.button) { fontSize = 11 };
            if (GUI.Button(new Rect(cx + 36f, y, 54f, 22f), "clear", small))
            {
                HPSettings.DevWorldSeed = 0;
                HPSettings.Save();
            }
        }

        /// <summary>"Wren Castellano" -> "Wren", for the narrow dev buttons.</summary>
        private static string FirstName(string specialtyId)
        {
            if (!Sim.Specialties.CharacterName.TryGetValue(specialtyId, out string full)) return specialtyId;
            int sp = full.IndexOf(' ');
            string first = sp > 0 ? full.Substring(0, sp) : full;
            return first == "Dr." ? "Mara" : first; // "Dr. Mara Okonkwo"
        }

        private void DrawJoin(float cx)
        {
            float y = Screen.height * 0.42f;
            bool connecting = _connectStartedAt >= 0f;

            GUI.Label(new Rect(cx - 150f, y, 300f, 22f), "host address", _labelStyle);
            y += 24f;
            GUI.enabled = !connecting;
            _address = GUI.TextField(new Rect(cx - 150f, y, 300f, 34f), _address, 64);
            GUI.enabled = true;
            y += 46f;

            if (connecting)
            {
                GUI.Label(new Rect(cx - 150f, y, 300f, 30f), "connecting...", _subStyle);
                y += 40f;
                if (MenuButton(cx, ref y, "CANCEL"))
                {
                    InstanceFinder.ClientManager.StopConnection();
                    _connectStartedAt = -1f;
                }
                return;
            }

            if (MenuButton(cx, ref y, "CONNECT"))
            {
                InstanceFinder.ClientManager.StartConnection(_address.Trim());
                _connectStartedAt = Time.time;
            }
            if (MenuButton(cx, ref y, "BACK")) _page = Page.Root;
        }

        /// <summary>
        /// The settings page. Everything scrolls inside a panel clamped to the window, with BACK
        /// pinned OUTSIDE the scroll so it can never be pushed off-screen — this page grew to ten
        /// rebindable actions and the fixed-pixel version ran BACK past the bottom of the window,
        /// leaving no way out of the menu at all. Esc also backs out (see Update).
        /// </summary>
        private void DrawSettings(float cx)
        {
            const float backRow = 52f;
            float top = Screen.height * 0.14f + 124f;                  // clear of the title + subtitle
            float w = Mathf.Min(400f, Screen.width - 40f);
            float h = Mathf.Max(140f, Screen.height - top - 24f);

            GUILayout.BeginArea(new Rect(cx - w / 2f, top, w, h));
            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll, GUIStyle.none, GUI.skin.verticalScrollbar,
                GUILayout.Height(h - backRow));

            GUILayout.Label("player name", _labelStyle);
            HPSettings.PlayerName = GUILayout.TextField(HPSettings.PlayerName, 16, GUILayout.Height(28f));
            GUILayout.Space(10f);

            GUILayout.Label($"mouse sensitivity  ({HPSettings.MouseSensMul:0.00}x)", _labelStyle);
            HPSettings.MouseSensMul = GUILayout.HorizontalSlider(HPSettings.MouseSensMul, 0.2f, 3f);
            GUILayout.Space(10f);

            GUILayout.Label($"master volume  ({(int)(HPSettings.MasterVolume * 100)}%)", _labelStyle);
            HPSettings.MasterVolume = GUILayout.HorizontalSlider(HPSettings.MasterVolume, 0f, 1f);
            GUILayout.Space(10f);

            // The single biggest frame-rate lever — same slider as the in-game pause menu.
            GUILayout.Label($"resolution scale  ({(int)(HPSettings.RenderScale * 100)}%)  — lower = faster", _labelStyle);
            HPSettings.RenderScale = Mathf.Round(GUILayout.HorizontalSlider(HPSettings.RenderScale, 0.4f, 1f) * 20f) / 20f;
            GUILayout.Space(10f);

            bool fs = GUILayout.Toggle(Screen.fullScreen, " fullscreen");
            if (fs != Screen.fullScreen) Screen.fullScreen = fs;
            GUILayout.Space(12f);

            // Key rebinding (Esc and the mouse stay fixed, like the web build).
            GUILayout.Label("controls (click, then press a key)", _labelStyle);
            foreach (HPAction a in System.Enum.GetValues(typeof(HPAction)))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(ActionName(a), _labelStyle, GUILayout.Width(170f));
                string keyLabel = HPKeybinds.Capturing == a ? "press a key..." : HPKeybinds.Label(a);
                if (GUILayout.Button(keyLabel, GUILayout.Height(24f))) HPKeybinds.Capturing = a;
                GUILayout.EndHorizontal();
                GUILayout.Space(3f);
            }
            GUILayout.Space(6f);
            if (GUILayout.Button("reset defaults", GUILayout.Width(150f), GUILayout.Height(24f)))
                HPKeybinds.ResetDefaults();
            GUILayout.Space(8f);
            GUILayout.EndScrollView();

            if (GUILayout.Button("BACK", _buttonStyle, GUILayout.Height(40f))) CloseSettings();
            GUILayout.EndArea();
        }

        /// <summary>Leave the settings page, persisting whatever was changed.</summary>
        private void CloseSettings()
        {
            HPKeybinds.Capturing = null;
            HPSettings.Save();
            _page = Page.Root;
        }

        private static string ActionName(HPAction a)
        {
            switch (a)
            {
                case HPAction.Sprint: return "sprint";
                case HPAction.Jump: return "jump / vault / leap / climb";
                case HPAction.Crouch: return "crouch";
                case HPAction.Flashlight: return "flashlight";
                case HPAction.Revive: return "revive (hold)";
                case HPAction.Mark: return "trail mark (Wren)";
                case HPAction.Senses: return "senses (Bigfoot)";
                case HPAction.Map: return "map";
                case HPAction.Ping: return "stakeout ping";
                case HPAction.Flash: return "camera flash (Eli)";
                default: return a.ToString();
            }
        }

        private bool MenuButton(float cx, ref float y, string label)
        {
            bool hit = GUI.Button(new Rect(cx - 150f, y, 300f, 46f), label, _buttonStyle);
            y += 56f;
            return hit;
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            _titleStyle.normal.textColor = new Color(0.88f, 0.93f, 0.9f);
            _subStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            _subStyle.normal.textColor = new Color(0.65f, 0.72f, 0.7f);
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _labelStyle.normal.textColor = new Color(0.6f, 0.65f, 0.63f);
            _errorStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, alignment = TextAnchor.UpperCenter, wordWrap = true,
            };
            _errorStyle.normal.textColor = new Color(1f, 0.45f, 0.4f);
        }
    }
}
