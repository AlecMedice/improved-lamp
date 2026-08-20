// The title card + main menu shown when the exe opens — a left-anchored column over a live 3D
// backdrop (three shots on a loop around the camp and the forest, built by WorldBuilder). This is
// the only pre-connection UI — it drives hosting and joining directly.
//
// Visible whenever no connection is up, so a disconnect (host quit, kicked, cancel) lands you
// back here automatically. JOIN is direct address (Tugboat) until Path B adds Steam invites —
// then JOIN becomes the friends/lobby flow and this menu doesn't need restructuring.
//
// It is still IMGUI, but it draws NOTHING through GUI.skin: every surface comes from MenuUI, which
// exists because the built-in skin is what made this screen look a decade old, not IMGUI itself.
// See MenuUI's header for why this didn't become a UI Toolkit canvas.
//
// Layout rules that keep it honest on a small window ([imgui-clamp]): the column is a fraction of
// the width with a pixel floor, the title block RETURNS its bottom so nothing below can overlap it,
// the settings card is clamped to the window and scrolls, and BACK sits outside that scroll.
//
// Anything that animates is advanced in Update and only READ in OnGUI — OnGUI runs at least twice a
// frame, so stepping a timer there runs it at double speed and firing an action there fires it
// twice. (Same class of bug as the [H] card that toggled itself back off, in Aug_15_Bug.)
using FishNet;
using UnityEngine;

namespace Metoh.Game
{
    public class TitleMenu : MonoBehaviour
    {
        private enum Page { Root, Solo, Join, Settings }

        private Page _page = Page.Root;
        private string _address; // seeded from the last successful join in Awake
        private float _connectStartedAt = -1f; // >=0 while a join attempt is in flight
        private string _error;                 // last hosting/joining failure, shown under the buttons
        private Vector2 _settingsScroll;       // settings page scroll (it's taller than a short window)
        private float _settingsContentH = 700f; // measured while drawing, used on the NEXT frame

        // Presentation state. Advanced in Update, read in OnGUI.
        private float _appear;                 // 0->1 fade up from black on launch
        private float _pageFade = 1f;          // 0->1 after a page change (content fades + slides in)
        private int _sel = -1;                 // highlighted row — mouse hover or keyboard
        private int _hover = -1;               // row under the cursor, recomputed every draw
        private int _rows;                     // how many rows the current page registered
        private readonly float[] _rowT = new float[8]; // per-row highlight, eased
        private bool _activate;                // Enter pressed; consumed by the selected row
        private bool _typing;                  // a text field has focus — keyboard nav stands down
        private Vector2 _lastMouse;
        private bool _devOpen = true;
        private bool _prefsDirty;              // deferred PlayerPrefs write (see Flush)

        // Enum.GetValues allocates a fresh array per call, and OnGUI is not the place for that.
        private static readonly HPAction[] Actions = (HPAction[])System.Enum.GetValues(typeof(HPAction));

        private static readonly string[] RootLabels = { "SINGLE PLAYER", "HOST GAME", "JOIN GAME", "SETTINGS", "QUIT" };
        private static readonly string[] RootHints =
        {
            "play the whole game against the CPU — no lobby, no internet",
            "open a room on this machine and hunt with up to four friends",
            "connect to a friend's host by address",
            "controls, mouse sensitivity, audio and resolution scale",
            "leave the mountain",
        };

        private void Awake()
        {
            HPSettings.Load();
            _address = HPSettings.LastJoinAddress; // pre-fill JOIN with the last address used
        }

        private void OnDestroy()
        {
            Flush();
            MenuUI.Release();
        }

        private void OnApplicationQuit()
        {
            Flush();
        }

        /// <summary>
        /// Write deferred settings to disk. HPSettings.Save ends in PlayerPrefs.Save, a disk flush —
        /// which the seed field used to do on every keystroke. Everything the menu edits lives in
        /// static fields that the game reads directly, so the write can wait for a page change, a
        /// host start, or the process ending.
        /// </summary>
        private void Flush()
        {
            if (!_prefsDirty) return;
            _prefsDirty = false;
            HPSettings.Save();
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
                GoTo(Page.Root);
#endif
            bool connected = Connected();
            SetTitleLighting(!connected); // the backdrop is lit well above gameplay dusk
            if (connected) { _connectStartedAt = -1f; return; }

            StepPresentation();

            // Join attempt timed out? (Tugboat fails quietly if nothing is listening.) This check
            // used to live inside SetTitleLighting, BELOW that method's `already in this mode` early
            // return — so it only ever ran on the single frame the lighting flipped, which is exactly
            // the frame a join can't have timed out on yet. A stuck CONNECT sat there forever.
            if (_connectStartedAt >= 0f && Time.time - _connectStartedAt > 8f)
            {
                InstanceFinder.ClientManager.StopConnection();
                _connectStartedAt = -1f;
                _error = "no answer from that address after 8s — is the host up, and the port open?";
            }

            // Drive the sky ourselves while nothing is connected.
            //
            // The palette is normally pushed every frame by GameManager.Update, but GameManager is a
            // scene NetworkObject whose whole reason to exist is a match — leaning on it to light the
            // MENU is an invisible dependency on a networking object ticking before any networking
            // has happened. When it doesn't, the world keeps whatever palette WorldBuilder.Awake
            // baked in (gameplay dusk, TitleMode off, cached by `_lastTod`), the title-mode boost
            // never lands, and the backdrop sits frozen in the wrong lighting behind the menu.
            // SetTimeOfDay early-outs when nothing changed, so this costs nothing when both run.
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.SetTimeOfDay(TitleTimeOfDay, 1);

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
        /// Advance every animated value, and resolve which row is highlighted.
        ///
        /// Selection has two drivers that must not fight: the mouse claims it whenever it MOVES (not
        /// merely whenever it is over a row, or the keyboard could never take it back from a
        /// stationary cursor), and the arrow keys claim it on a press. Unscaled time throughout —
        /// nothing in a menu should care about Time.timeScale.
        /// </summary>
        private void StepPresentation()
        {
            float dt = Time.unscaledDeltaTime;
            _appear = Mathf.MoveTowards(_appear, 1f, dt / 0.9f);
            _pageFade = Mathf.MoveTowards(_pageFade, 1f, dt / 0.16f);

#if ENABLE_INPUT_SYSTEM
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse != null)
            {
                Vector2 m = mouse.position.ReadValue();
                if ((m - _lastMouse).sqrMagnitude > 4f)
                {
                    _lastMouse = m;
                    _sel = _hover;
                }
            }

            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && !_typing && HPKeybinds.Capturing == null && _rows > 0)
            {
                int step = 0;
                if (kb.downArrowKey.wasPressedThisFrame) step = 1;
                else if (kb.upArrowKey.wasPressedThisFrame) step = -1;
                if (step != 0) _sel = _sel < 0 ? (step > 0 ? 0 : _rows - 1) : ((_sel + step + _rows) % _rows);
                if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) _activate = true;
            }
#endif
            for (int i = 0; i < _rowT.Length; i++)
                _rowT[i] = Mathf.MoveTowards(_rowT[i], i == _sel ? 1f : 0f, dt / 0.13f);
        }

        /// <summary>
        /// Where on the night the menu backdrop sits. Matches the value GameManager holds the sky at
        /// outside a match — just past dusk, night one's full moon — so handing the sky over on
        /// connect is not a visible jump.
        /// </summary>
        private const float TitleTimeOfDay = 0.05f;

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

            // Stage the actors against the shot that is actually running. Driven from here, and not
            // from TitleMenu.Update, because this method already owns the shot list — and it is also
            // the path the camp LOBBY runs through, where the actors are already gone.
            TitleActors.Tick(which, k, Time.deltaTime);

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
        private static Vector2 LerpXZ(Metoh.Sim.Vec2 a, Metoh.Sim.Vec2 b, float k)
        {
            return new Vector2(Mathf.Lerp((float)a.X, (float)b.X, k), Mathf.Lerp((float)a.Z, (float)b.Z, k));
        }

        /// <summary>Raise the world's brightness while the menu is up, and restore it on connect.</summary>
        private void SetTitleLighting(bool on)
        {
            if (WorldBuilder.TitleMode == on) return;
            WorldBuilder.TitleMode = on;
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.InvalidatePalette(); // re-applies immediately
            if (PostFX.Instance != null) PostFX.Instance.SetTitleBrightness(on);
            // The staged cast lives exactly as long as the menu does. Tied to this transition rather
            // than to Awake/OnDestroy so backing out of a match rebuilds it, and connecting disposes it
            // before the real bodies spawn — two Yetis on screen would be a memorable bug.
            //
            // This is the ONLY thing that belongs below the early return above: it is a genuine
            // edge-triggered action. The join timeout that used to sit here was level-triggered and so
            // never fired — it now lives in Update(), where it is checked every frame.
            TitleActors.SetActive(on);
        }

        private static bool Connected()
        {
            return (InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started) ||
                   (InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started);
        }

        // ------------------------------------------------------------------ drawing

        private void OnGUI()
        {
            if (Connected()) return;
            MenuUI.Begin();

            // Read once per pass: keyboard navigation stands down while a field has focus, or the
            // arrow keys would move the menu selection while you are editing an address.
            _typing = !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl());
            _hover = -1;

            float w = Screen.width, h = Screen.height;
            float appear = Mathf.SmoothStep(0f, 1f, _appear);
            float colX = Mathf.Max(40f, w * 0.085f);
            float colW = Mathf.Clamp(w * 0.36f, 280f, 470f);

            MenuUI.Alpha = appear;
            DrawScrims(w, h, colX, colW);
            float titleBottom = DrawTitle(colX, colW, h);

            // The page body fades and slides on a page change — a cut between two static screens is
            // the thing that reads as "menu from 2005" even when everything on them is modern.
            float fade = Mathf.SmoothStep(0f, 1f, _pageFade);
            MenuUI.Alpha = appear * fade;
            float slide = (1f - fade) * 14f;
            switch (_page)
            {
                case Page.Root: DrawRoot(colX, colW, Mathf.Max(titleBottom + 24f, h * 0.44f) + slide, h); break;
                case Page.Solo: DrawSolo(colX, colW, Mathf.Max(titleBottom + 20f, h * 0.40f) + slide, h); break;
                case Page.Join: DrawJoin(colX, colW, Mathf.Max(titleBottom + 24f, h * 0.44f) + slide, h); break;
                case Page.Settings: DrawSettings(colX, w, h, titleBottom + 16f + slide); break;
            }

            MenuUI.Alpha = appear;
            DrawError(colX, colW, w, h);
            DrawFooter(colX, w, h);
            DrawDevPanel(w, h);
            DrawBootProblems(w);
            MenuUI.Alpha = 1f;

            // Fade up from black. Last, so it covers everything, and shaped so the backdrop arrives
            // slightly before the UI does. It also hides the first-frame shader-variant hitch that
            // Aug_15_Bug records as "the first load after a rebuild looks broken".
            if (_appear < 1f)
                MenuUI.Fill(new Rect(0f, 0f, w, h), new Color(0f, 0f, 0f, Mathf.Pow(1f - _appear, 0.8f)));

            // Nothing claimed the keypress (an empty selection, say) — don't let it fire next frame.
            if (Event.current.type == EventType.Repaint) _activate = false;
        }

        /// <summary>
        /// The scrims. A flat wash over the whole screen would be simpler, and is what this used to
        /// do, but it flattens the one thing worth looking at: the live camera move through the
        /// forest. Instead the darkness is only where the text is — heavy down the left edge, light
        /// at the top and bottom to hold the title and the footer — and the right two-thirds of the
        /// backdrop is left alone.
        /// </summary>
        private static void DrawScrims(float w, float h, float colX, float colW)
        {
            MenuUI.Fill(new Rect(0f, 0f, w, h), new Color(MenuUI.Ink.r, MenuUI.Ink.g, MenuUI.Ink.b, 0.20f));
            MenuUI.GradientH(new Rect(0f, 0f, Mathf.Min(w, colX + colW + 300f), h), MenuUI.Fade(MenuUI.Ink, 0.88f));
            MenuUI.GradientV(new Rect(0f, 0f, w, Mathf.Min(240f, h * 0.34f)), MenuUI.Fade(MenuUI.Ink, 0.45f), true);
            float footer = Mathf.Min(170f, h * 0.28f);
            MenuUI.GradientV(new Rect(0f, h - footer, w, footer), MenuUI.Fade(MenuUI.Ink, 0.6f), false);
        }

        /// <summary>Draws the title block and returns the y its last line ends on.</summary>
        private static float DrawTitle(float colX, float colW, float h)
        {
            float y = Mathf.Max(30f, h * 0.145f);
            MenuUI.Tracked(new Rect(colX + 2f, y, colW, 14f), "1 VS 5 · HIMALAYAN HORROR", MenuUI.Tiny, 4f,
                MenuUI.Fade(MenuUI.Accent, 0.85f));
            y += 22f;

            int size = Mathf.RoundToInt(Mathf.Clamp(h * 0.105f, 40f, 88f));
            MenuUI.Display.fontSize = size;
            float titleH = size * 1.25f;
            MenuUI.Tracked(new Rect(colX, y, colW * 2f, titleH), "METOH", MenuUI.Display, size * 0.13f, MenuUI.Text);
            y += titleH + 4f;

            MenuUI.Fill(new Rect(colX + 2f, y, 44f, 2f), MenuUI.Accent);
            y += 16f;
            MenuUI.Label(new Rect(colX + 2f, y, Mathf.Min(colW, 420f), 44f),
                "five went looking for proof — something on the mountain was looking back",
                MenuUI.Body, MenuUI.TextDim);
            return y + 44f;
        }

        private void DrawRoot(float colX, float colW, float y, float h)
        {
            _rows = RootLabels.Length;
            float rowH = RowHeight(h);
            for (int i = 0; i < RootLabels.Length; i++)
            {
                if (MenuRow(new Rect(colX, y, colW, rowH), i, RootLabels[i]))
                {
                    switch (i)
                    {
                        case 0: GoTo(Page.Solo); break;
                        case 1: StartHost(); break; // StartHost disarms both solo flags
                        case 2: GoTo(Page.Join); break;
                        case 3: GoTo(Page.Settings); break;
                        default: Quit(); break;
                    }
                }
                y += rowH + 2f;
            }

            // One descriptor line under the stack, for whatever is highlighted. Five permanent
            // subtitles would be clutter; one that follows the cursor is the same information at a
            // fifth of the ink.
            if (_sel >= 0 && _sel < RootHints.Length)
                MenuUI.Label(new Rect(colX + 2f, y + 10f, colW + 160f, 20f), RootHints[_sel], MenuUI.Small,
                    MenuUI.Fade(MenuUI.TextFaint, _rowT[_sel]));
        }

        /// <summary>
        /// The Single Player page: pick which side you play against the CPU. No lobby and no
        /// internet — SoloPending tells GameManager to spawn the AI and drop you straight into night
        /// one.
        ///
        /// Both sides are live. PLAY AS YETI fields a full CPU team (GameManager.SoloSearcherBots)
        /// which, since the 2026-08-14 AI rewrite, divides the map between its members, reads the
        /// Yeti's trail for direction rather than walking to the newest print, and stages rescues
        /// instead of piling onto a body.
        /// </summary>
        private void DrawSolo(float colX, float colW, float y, float h)
        {
            _rows = 3;
            MenuUI.Tracked(new Rect(colX + 2f, y, colW, 14f), "SINGLE PLAYER", MenuUI.Tiny, 3.5f,
                MenuUI.Fade(MenuUI.Accent, 0.8f));
            y += 26f;

            // Two-line rows here rather than the root page's follow-the-cursor descriptor: there are
            // only two choices and the difference between them is the whole decision, so both
            // explanations stay on screen.
            float rowH = RowHeight(h) + 16f;
            if (MenuRow(new Rect(colX, y, colW, rowH), 0, "PLAY AS SEARCHER",
                    "find proof and get it to the duffel before the CPU Yeti takes you"))
                StartHost(solo: true);
            y += rowH + 4f;

            if (MenuRow(new Rect(colX, y, colW, rowH), 1, "PLAY AS YETI",
                    $"hunt {GameManager.SoloSearcherBots} CPU searchers — they split up, and they read tracks"))
                StartHost(solo: true, asYeti: true);
            y += rowH + 10f;

            if (MenuRow(new Rect(colX, y, colW, RowHeight(h)), 2, "BACK")) GoTo(Page.Root);
        }

        private void DrawJoin(float colX, float colW, float y, float h)
        {
            bool connecting = _connectStartedAt >= 0f;
            _rows = connecting ? 1 : 2;
            float rowH = RowHeight(h);
            float fieldW = Mathf.Min(colW, 340f);

            MenuUI.Tracked(new Rect(colX + 2f, y, colW, 14f), "HOST ADDRESS", MenuUI.Tiny, 3.5f,
                MenuUI.Fade(MenuUI.Accent, 0.8f));
            y += 24f;

            GUI.enabled = !connecting;
            _address = MenuUI.Field(new Rect(colX, y, fieldW, 34f), "addr", _address, 64);
            GUI.enabled = true;
            y += 48f;

            if (connecting)
            {
                MenuUI.Label(new Rect(colX + 2f, y, colW, 20f), "connecting" + Dots(), MenuUI.Small, MenuUI.Text);
                y += 24f;

                // An indeterminate bar — there is no progress to report (Tugboat either answers or
                // it doesn't), but a screen that shows nothing moving reads as a screen that hung.
                var track = new Rect(colX, y, fieldW, 2f);
                MenuUI.Fill(track, new Color(1f, 1f, 1f, 0.10f));
                float segW = fieldW * 0.3f;
                float sx = Mathf.Lerp(-segW, fieldW, (Time.unscaledTime * 0.55f) % 1f);
                float x0 = Mathf.Max(track.x, track.x + sx), x1 = Mathf.Min(track.xMax, track.x + sx + segW);
                if (x1 > x0) MenuUI.Fill(new Rect(x0, track.y, x1 - x0, track.height), MenuUI.Accent);
                y += 20f;

                if (MenuRow(new Rect(colX, y, colW, rowH), 0, "CANCEL"))
                {
                    InstanceFinder.ClientManager.StopConnection();
                    _connectStartedAt = -1f;
                }
                return;
            }

            if (MenuRow(new Rect(colX, y, colW, rowH), 0, "CONNECT"))
            {
                string addr = (_address ?? string.Empty).Trim();
                HPSettings.LastJoinAddress = addr; // remember it for next launch
                _prefsDirty = true;
                Flush();
                InstanceFinder.ClientManager.StartConnection(addr);
                _connectStartedAt = Time.time;
            }
            y += rowH + 2f;
            if (MenuRow(new Rect(colX, y, colW, rowH), 1, "BACK")) GoTo(Page.Root);
        }

        private static string Dots()
        {
            switch ((int)(Time.unscaledTime * 2f) % 3)
            {
                case 0: return ".";
                case 1: return "..";
                default: return "...";
            }
        }

        /// <summary>
        /// The settings page. Everything scrolls inside a card clamped to the window, with BACK
        /// pinned OUTSIDE the scroll so it can never be pushed off-screen — this page grew to eleven
        /// rebindable actions and the fixed-pixel version ran BACK past the bottom of the window,
        /// leaving no way out of the menu at all. Esc also backs out (see Update).
        ///
        /// The volume and resolution sliders APPLY as you drag them, which the identical sliders in
        /// the pause menu always did and these ones didn't (Aug_15_Bug, UI/polish): dragging a volume
        /// slider that stays silent until you leave the page reads as a broken control.
        /// </summary>
        private void DrawSettings(float colX, float w, float h, float top)
        {
            _rows = 1; // BACK
            const float pad = 18f, backH = 46f;
            float cardW = Mathf.Min(520f, w - colX - 28f);
            float cardH = Mathf.Max(180f, h - top - 74f);
            var card = new Rect(colX, top, cardW, cardH);
            MenuUI.Card(card);

            var view = new Rect(card.x + pad, card.y + pad, card.width - pad * 2f, card.height - pad * 2f - backH);
            float innerW = view.width - 10f; // room for the scroll indicator
            _settingsScroll = GUI.BeginScrollView(view, _settingsScroll,
                new Rect(0f, 0f, innerW, _settingsContentH), GUIStyle.none, GUIStyle.none);

            float y = Section(0f, innerW, "PROFILE");
            MenuUI.Label(new Rect(0f, y, innerW, 18f), "player name", MenuUI.Small, MenuUI.TextDim);
            y += 20f;
            string name = MenuUI.Field(new Rect(0f, y, Mathf.Min(240f, innerW), 30f), "name", HPSettings.PlayerName, 16);
            if (name != HPSettings.PlayerName) { HPSettings.PlayerName = name; _prefsDirty = true; }
            y += 44f;

            y = Section(y, innerW, "GAME");
            y = SliderRow(y, innerW, "mouse sensitivity", $"{HPSettings.MouseSensMul:0.00}x",
                ref HPSettings.MouseSensMul, 0.2f, 3f, 0f, false);
            y = SliderRow(y, innerW, "master volume", $"{(int)(HPSettings.MasterVolume * 100)}%",
                ref HPSettings.MasterVolume, 0f, 1f, 0f, true);
            // The single biggest frame-rate lever — same slider as the in-game pause menu.
            y = SliderRow(y, innerW, "resolution scale — lower is faster", $"{(int)(HPSettings.RenderScale * 100)}%",
                ref HPSettings.RenderScale, 0.4f, 1f, 0.05f, true);

            bool fs = MenuUI.Switch(new Rect(0f, y, innerW, 26f), "fullscreen", Screen.fullScreen);
            if (fs != Screen.fullScreen) Screen.fullScreen = fs;
            y += 38f;

            // Key rebinding (Esc and the mouse stay fixed, like the web build).
            y = Section(y, innerW, "CONTROLS");
            MenuUI.Label(new Rect(0f, y, innerW, 16f), "click a key, then press the one you want", MenuUI.Tiny,
                MenuUI.TextFaint);
            y += 22f;
            foreach (HPAction a in Actions)
            {
                bool capturing = HPKeybinds.Capturing == a;
                MenuUI.Label(new Rect(0f, y, innerW - 130f, 26f), ActionName(a), MenuUI.Small, MenuUI.TextDim);
                if (MenuUI.Chip(new Rect(innerW - 120f, y + 1f, 120f, 24f),
                        capturing ? "press a key…" : HPKeybinds.Label(a), capturing))
                    HPKeybinds.Capturing = capturing ? (HPAction?)null : a;
                y += 28f;
            }
            y += 8f;
            if (MenuUI.Chip(new Rect(0f, y, 150f, 26f), "reset to defaults", false)) HPKeybinds.ResetDefaults();
            y += 34f;

            GUI.EndScrollView();
            if (Event.current.type == EventType.Repaint) _settingsContentH = y;

            // A thin indicator rather than a scrollbar — the view scrolls on the wheel, and a stock
            // scrollbar is one of the loudest pieces of old skin left in Unity.
            if (_settingsContentH > view.height)
            {
                float thumbH = Mathf.Max(28f, view.height * (view.height / _settingsContentH));
                float p = Mathf.Clamp01(_settingsScroll.y / Mathf.Max(1f, _settingsContentH - view.height));
                float bx = view.xMax - 3f;
                MenuUI.Pill(new Rect(bx, view.y, 3f, view.height), new Color(1f, 1f, 1f, 0.07f));
                MenuUI.Pill(new Rect(bx, view.y + p * (view.height - thumbH), 3f, thumbH), new Color(1f, 1f, 1f, 0.22f));
            }

            if (MenuRow(new Rect(card.x + pad, card.yMax - backH, card.width - pad * 2f, backH - 10f), 0, "BACK"))
                CloseSettings();
        }

        /// <summary>A section header: tracked caps with a hairline running out to the right.</summary>
        private static float Section(float y, float w, string title)
        {
            MenuUI.Tracked(new Rect(0f, y, w, 14f), title, MenuUI.Tiny, 3f, MenuUI.Fade(MenuUI.Accent, 0.75f));
            float tw = MenuUI.TrackedWidth(title, MenuUI.Tiny, 3f);
            MenuUI.Fill(new Rect(tw + 10f, y + 7f, Mathf.Max(0f, w - tw - 10f), 1f), new Color(1f, 1f, 1f, 0.09f));
            return y + 26f;
        }

        /// <summary>Label + right-aligned readout above the track. <paramref name="step"/> 0 = continuous.</summary>
        private float SliderRow(float y, float w, string label, string value, ref float v,
            float min, float max, float step, bool applyLive)
        {
            MenuUI.Label(new Rect(0f, y, w * 0.72f, 18f), label, MenuUI.Small, MenuUI.TextDim);
            MenuUI.Label(new Rect(w * 0.5f, y, w * 0.5f, 18f), value, MenuUI.SmallRight, MenuUI.Text);
            y += 20f;

            float nv = MenuUI.Slider(new Rect(0f, y, w, 18f), v, min, max);
            if (step > 0f) nv = Mathf.Round(nv / step) * step;
            if (!Mathf.Approximately(nv, v))
            {
                v = nv;
                _prefsDirty = true;
                if (applyLive) HPSettings.Apply();
            }
            return y + 32f;
        }

        /// <summary>
        /// One menu item: no box, no bevel, no border. The whole highlight is an accent bar growing
        /// out of the left margin, a short gradient wash behind the text, the label brightening from
        /// dim to white, and the label sliding a few pixels right — all driven off _rowT, which
        /// Update eases. Returns true on a click OR on Enter while this row is selected.
        /// </summary>
        private bool MenuRow(Rect r, int index, string label, string hint = null)
        {
            if (r.Contains(Event.current.mousePosition)) _hover = index;
            float t = _rowT[Mathf.Clamp(index, 0, _rowT.Length - 1)];
            float e = Mathf.SmoothStep(0f, 1f, t);

            if (e > 0.002f)
            {
                MenuUI.GradientH(new Rect(r.x - 16f, r.y, r.width * 0.9f + 16f, r.height),
                    MenuUI.Fade(MenuUI.Accent, 0.13f * e));
                float barH = r.height * (0.35f + 0.65f * e);
                MenuUI.Fill(new Rect(r.x - 16f, r.y + (r.height - barH) * 0.5f, 2f, barH),
                    MenuUI.Fade(MenuUI.Accent, e));
            }

            float tx = r.x + 8f * e;
            Color labelColor = Color.Lerp(MenuUI.TextDim, MenuUI.Text, e);
            if (hint == null)
            {
                MenuUI.Tracked(new Rect(tx, r.y, r.width, r.height), label, MenuUI.Row, 2.5f, labelColor);
            }
            else
            {
                MenuUI.Tracked(new Rect(tx, r.y + 2f, r.width, r.height * 0.55f), label, MenuUI.Row, 2.5f, labelColor);
                MenuUI.Label(new Rect(tx, r.y + r.height * 0.55f, r.width, r.height * 0.42f), hint, MenuUI.Small,
                    Color.Lerp(MenuUI.TextFaint, MenuUI.TextDim, e));
            }

            bool hit = GUI.Button(r, GUIContent.none, GUIStyle.none);
            // Keyboard activation is claimed on the REPAINT pass only. OnGUI runs at least twice a
            // frame; a flag consumed on whichever pass came first would fire the action twice.
            if (!hit && _activate && index == _sel && Event.current.type == EventType.Repaint)
            {
                _activate = false;
                hit = true;
            }
            if (hit)
            {
                GUI.FocusControl(null); // a click on a row shouldn't leave a text field focused
                Click();
            }
            return hit;
        }

        private static float RowHeight(float h)
        {
            return Mathf.Clamp(h * 0.052f, 32f, 44f);
        }

        private static void Click()
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.FlashlightClick, 0.22f);
        }

        private void GoTo(Page p)
        {
            if (_page == Page.Settings) HPKeybinds.Capturing = null;
            Flush(); // settings and dev edits are written on the way out of a page
            _page = p;
            _pageFade = 0f;
            _sel = -1;
            _activate = false;
            _settingsScroll = Vector2.zero;
            for (int i = 0; i < _rowT.Length; i++) _rowT[i] = 0f;
            GUI.FocusControl(null);
        }

        /// <summary>Leave the settings page, persisting whatever was changed.</summary>
        private void CloseSettings()
        {
            _prefsDirty = true; // toggles and rebinds don't set it individually
            GoTo(Page.Root);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void DrawError(float colX, float colW, float w, float h)
        {
            if (string.IsNullOrEmpty(_error)) return;
            float bw = Mathf.Min(colW + 140f, w - colX - 24f);
            var box = new Rect(colX, Mathf.Min(h * 0.8f, h - 118f), bw, 60f);
            MenuUI.Card(box, 0.82f);
            MenuUI.Fill(new Rect(box.x + 1f, box.y + 8f, 2f, box.height - 16f), MenuUI.Warn);
            MenuUI.Label(new Rect(box.x + 14f, box.y + 9f, box.width - 26f, box.height - 18f),
                _error + "\nfull details are in the Console", MenuUI.Body, MenuUI.Warn);
        }

        private static void DrawFooter(float colX, float w, float h)
        {
            MenuUI.Fill(new Rect(colX, h - 44f, Mathf.Min(360f, w - colX - 24f), 1f), new Color(1f, 1f, 1f, 0.08f));
            MenuUI.Tracked(new Rect(colX, h - 34f, w - colX, 16f),
                "PRE-ALPHA · LOCAL / LAN BUILD · STEAM RELAY AND FRIEND INVITES COMING", MenuUI.Tiny, 2.2f,
                MenuUI.TextFaint);
        }

        /// <summary>
        /// Startup degradations, top right — out of the column, and out of the way of the dev card.
        ///
        /// The title screen is the whole reason this is drawn rather than only logged: a missing
        /// shader makes the MENU BACKDROP wrong — a flat sky, a rigid unlit forest, ground with no
        /// rock blend — which is precisely the state that gets reported later as "the title screen
        /// looks broken" with no way to tell it apart from the art simply not being finished. The
        /// backdrop IS the evidence, so the explanation belongs on top of it.
        /// </summary>
        private static void DrawBootProblems(float w)
        {
            if (!BootReport.Any) return;
            float bw = Mathf.Min(460f, w * 0.42f);
            var sb = new System.Text.StringBuilder();
            foreach (string p in BootReport.All) sb.Append("• ").Append(p).Append('\n');

            float bh = 46f + MenuUI.Body.CalcHeight(new GUIContent(sb.ToString()), bw - 28f);
            var box = new Rect(w - bw - 22f, 20f, bw, bh);
            MenuUI.Card(box, 0.85f);
            MenuUI.Fill(new Rect(box.x + 1f, box.y + 8f, 2f, box.height - 16f), MenuUI.Warn);
            MenuUI.Tracked(new Rect(box.x + 14f, box.y + 10f, bw - 28f, 14f), "STARTUP PROBLEMS", MenuUI.Tiny, 3f,
                MenuUI.Warn);
            MenuUI.Label(new Rect(box.x + 14f, box.y + 28f, bw - 28f, box.height - 38f),
                "the backdrop below is not what the game looks like\n" + sb, MenuUI.Body,
                MenuUI.Fade(MenuUI.Warn, 0.85f));
        }

        /// <summary>
        /// DEV — force the persona you'll be dealt, and pin the world seed when hosting.
        ///
        /// Casting belongs to Mara, the flash to Eli and the spare battery to Sam, so without the
        /// first one, testing any of them means restarting until the random deal happens to hand you
        /// the right character (a 1-in-5 lottery per match). The host re-validates the id, so this
        /// can't be used to invent a specialty. The seed pin exists because the forest, trails and
        /// caves all derive from it: without it, a bug you hit in one map is gone the moment you
        /// restart. F3 prints the live seed; paste it back here. Ignored when joining — the host
        /// owns the seed and replicates it.
        ///
        /// Boxed off in the corner rather than sitting under the menu: it is a tool, not part of the
        /// game, and it was the loudest thing on the old front page. It collapses, and it collapses
        /// itself on a short window ([imgui-clamp]).
        /// </summary>
        private void DrawDevPanel(float w, float h)
        {
            string[] ids = Sim.Specialties.SpecialtyIds;
            int count = ids.Length + 1; // + "random"
            const int cols = 3;
            int chipRows = Mathf.CeilToInt(count / (float)cols);

            bool open = _devOpen && h > 430f;
            float pw = Mathf.Min(300f, w - 40f);
            float ph = 34f + (open ? 22f + chipRows * 28f + 24f + 34f : 0f);
            var panel = new Rect(Mathf.Max(20f, w - pw - 22f), h - 54f - ph, pw, ph);
            MenuUI.Card(panel, 0.8f);

            var header = new Rect(panel.x, panel.y, panel.width, 34f);
            MenuUI.Tracked(new Rect(panel.x + 14f, panel.y, panel.width - 40f, 34f), "DEV TOOLS", MenuUI.Tiny, 3f,
                MenuUI.TextDim);
            MenuUI.Label(new Rect(panel.xMax - 30f, panel.y, 20f, 34f), open ? "-" : "+", MenuUI.Center, MenuUI.TextDim);
            if (GUI.Button(header, GUIContent.none, GUIStyle.none)) _devOpen = !_devOpen;
            if (!open) return;

            float x0 = panel.x + 14f, iw = panel.width - 28f;
            float y = panel.y + 34f;
            MenuUI.Label(new Rect(x0, y, iw, 16f), "force persona", MenuUI.Tiny, MenuUI.TextFaint);
            y += 20f;

            float cw = (iw - (cols - 1) * 5f) / cols;
            for (int i = 0; i < count; i++)
            {
                string id = i == 0 ? "" : ids[i - 1];
                var r = new Rect(x0 + (i % cols) * (cw + 5f), y + (i / cols) * 28f, cw, 24f);
                if (MenuUI.Chip(r, i == 0 ? "random" : FirstName(id), HPSettings.DevSpecialty == id))
                {
                    HPSettings.DevSpecialty = id;
                    _prefsDirty = true;
                }
            }
            y += chipRows * 28f + 4f;

            MenuUI.Label(new Rect(x0, y, iw, 16f), "world seed — blank is random", MenuUI.Tiny, MenuUI.TextFaint);
            y += 20f;
            string shown = HPSettings.DevWorldSeed == 0 ? "" : HPSettings.DevWorldSeed.ToString();
            string typed = MenuUI.Field(new Rect(x0, y, iw - 66f, 26f), "seed", shown, 10);
            if (typed != shown)
            {
                // Empty or unparseable => 0 => random. Never reject a keystroke: a field you can't
                // clear because it won't accept "" is worse than one that quietly means "random".
                if (!uint.TryParse(typed, out uint parsed)) parsed = 0;
                HPSettings.DevWorldSeed = parsed;
                _prefsDirty = true;
            }
            if (MenuUI.Chip(new Rect(x0 + iw - 60f, y, 60f, 26f), "clear", false))
            {
                HPSettings.DevWorldSeed = 0;
                _prefsDirty = true;
                GUI.FocusControl(null);
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
                case HPAction.Senses: return "senses (Yeti)";
                case HPAction.Map: return "map";
                case HPAction.Ping: return "stakeout ping";
                case HPAction.Flash: return "camera flash (Eli)";
                case HPAction.Binoculars: return "binoculars (on the lookout)";
                default: return a.ToString();
            }
        }

        /// <summary>
        /// Start a local host + client. Shared by SINGLE PLAYER and HOST GAME. Never fails silently: a
        /// menu button that does nothing is the worst failure mode, so a refused socket or a throw
        /// from a scene object's OnStartServer surfaces on screen instead of stranding the player.
        ///
        /// THE MODE FLAGS ARE SET HERE, TOGETHER, AND ROLLED BACK ON FAILURE. They used to be poked by
        /// each caller, which left two ways to arm the wrong game. GameManager consumes them in
        /// OnClientLoadedStartScenes — a callback a FAILED host never reaches — so a PLAY AS YETI that
        /// couldn't bind the port left SoloAsYetiPending armed, and the next HOST GAME (co-op) quietly
        /// spawned four CPU searchers into the lobby and auto-started the match on top of them.
        /// Clearing both on every entry, and again on any failure, closes it: nothing is ever armed
        /// except by the click that is actually about to be honoured.
        /// </summary>
        private void StartHost(bool solo = false, bool asYeti = false)
        {
            Flush(); // the dev persona / seed about to be consumed had better be on disk
            GameManager.SoloPending = solo && !asYeti;
            GameManager.SoloAsYetiPending = solo && asYeti;
            _error = null;
            try
            {
                if (!InstanceFinder.ServerManager.StartConnection())
                {
                    _error = "server failed to start — is port 7770 already in use?";
                }
                else if (!InstanceFinder.ClientManager.StartConnection())
                {
                    _error = "server started, but the local client could not connect";
                    // Roll the server back, or nothing on screen has a UI at all: Connected() is true
                    // (the server IS up) so TitleMenu.OnGUI early-outs, while HPHud draws nothing
                    // without a started CLIENT. That left a forest, no menu, and an error string that
                    // could never be displayed. A half-started host is not a state to sit in.
                    InstanceFinder.ServerManager.StopConnection(true);
                }
            }
            catch (System.Exception e)
            {
                _error = "start failed: " + e.GetType().Name + " — " + e.Message;
                Debug.LogException(e); // full stack trace lands in the Console
            }

            if (_error == null) return;
            GameManager.SoloPending = false;
            GameManager.SoloAsYetiPending = false;
        }
    }
}
