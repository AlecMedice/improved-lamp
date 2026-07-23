// The fullscreen top-down map (M) — Unity port of client/src/ui/MapView.ts plus the map wiring in
// core/Game.ts. IMGUI like the rest of this milestone's UI; the terrain background is baked once
// into a Texture2D straight from the shared sim, so it agrees with the world everyone walks on.
//
// What each role sees (the web build's rules, verbatim):
//   both      — self (position + heading), camp, cave mouths, landmarks, the 100 m grid
//   searchers — teammates, stakeout pings, Wren's trail marks, and the recent clue trail ONLY
//               while "in contact": Bigfoot within hearRange (Theo hears farther) or a fresh clue
//               within evidenceSight (Wren sees farther, and keeps tracks visible longer)
//   Bigfoot   — standing in a cave mouth, clicking a DIFFERENT cave fast-travels there (the server
//               re-validates everything; see GameManager.TryCaveTravel)
// Clicking anywhere else on the map as a searcher drops a stakeout ping at that spot.
using HollowPines.Sim;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HollowPines.Game
{
    public class MapView : MonoBehaviour
    {
        /// <summary>Open state — HPPlayer checks this so the mouse isn't re-captured under the map.</summary>
        public static bool IsOpen { get; private set; }

        // Map readout tuning (client/src/config.ts MAP) — specialty multipliers scale these.
        // Public so the dusk briefing can quote each persona's REAL numbers instead of duplicating them.
        public const float ClueWindow = 15f;    // only tracks from the last N seconds show
        public const float HearRange = 35f;     // Bigfoot this close counts as "heard nearby"
        public const float EvidenceSight = 18f; // a clue this close counts as "sees recent evidence"

        private const int BgRes = 256; // baked background resolution

        private static Texture2D _bg, _dot, _ring;

        /// <summary>
        /// Drop the baked terrain image so the next open re-bakes it. Called by WorldBuilder.SetSeed:
        /// the background is baked from the world's heightfield, so a reseed leaves it showing the
        /// PREVIOUS session's ridges under this session's markers — the same class of silent
        /// inconsistency as the mirrored-map bug (see UNITY_PORT_NOTES §2).
        /// </summary>
        public static void InvalidateBackground()
        {
            _bg = null;
        }
        private float _half;
        private Rect _frame;

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            if (HPPlayer.Local != null && !HPHud.PauseOpen && HPKeybinds.Pressed(kb, HPAction.Map))
            {
                if (IsOpen) Close(); else Open();
            }
            // Q drops a ping where you stand, without opening the map (the web build's hotkey).
            if (!IsOpen && !HPHud.PauseOpen && HPKeybinds.Pressed(kb, HPAction.Ping))
            {
                var me = HPPlayer.Local;
                if (me != null && !me.IsBigfoot && Cursor.lockState == CursorLockMode.Locked)
                    me.RequestPing(me.transform.position.x, me.transform.position.z);
            }
#endif
        }

        private static void Open()
        {
            IsOpen = true;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>Close and hand the mouse back to the game (also called on cave travel).</summary>
        public static void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            var gm = GameManager.Instance;
            if (!HPHud.PauseOpen && gm != null && gm.MatchPhase.Value == GameManager.PhasePlaying)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void OnGUI()
        {
            if (!IsOpen) return;
            var me = HPPlayer.Local;
            var gm = GameManager.Instance;
            if (me == null || gm == null) { IsOpen = false; return; }
            GUI.depth = -1; // over the HUD

            var world = WorldBuilder.EnsureWorld();
            _half = (float)Sim.World.Size / 2f;
            EnsureTextures(world);

            // Square frame, centred in the space LEFT OVER after reserving room for the HUD's top bar
            // (which stays visible under the overlay) and the hint line beneath the map. Centring on
            // the whole screen ran the map's title straight through the night/proof bar.
            const float topReserve = 62f;    // clears the HUD bar (y 8..34) plus the map title
            const float bottomReserve = 46f; // the "click the map to drop a ping" hint
            float avail = Mathf.Max(160f, Screen.height - topReserve - bottomReserve);
            // Leave the flanks of the screen clear so peripheral vision survives while walking.
            float side = Mathf.Min(Screen.width * 0.58f, avail);
            _frame = new Rect((Screen.width - side) / 2f, topReserve + (avail - side) / 2f, side, side);
            // Light scrim only: you can still walk with this open, so the world around the map has to
            // stay legible enough to not blunder into Bigfoot while reading it.
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.32f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(_frame, _bg, ScaleMode.StretchToFill);
            GUI.color = new Color(0f, 0f, 0f, 0.3f); // veil so markers stay readable over the terrain
            GUI.DrawTexture(_frame, Texture2D.whiteTexture);
            GUI.color = old;

            DrawGrid();
            DrawLandmarks(world);

            bool searcher = !me.IsBigfoot;
            if (searcher)
            {
                if (ClueVisionActive(me, world)) DrawClueTrail(me);
                DrawCastablePrints();
                DrawMarks();
                DrawPings();
                DrawTeammates(me);
                DrawProofPiles(); // last of the searcher layers — a spill outranks everything under it
            }

            int currentCave = Caves.NearestCaveIndex(world.Caves, me.transform.position.x, me.transform.position.z);
            DrawCaves(world, me, currentCave);
            DrawSelf(me);
            DrawCompass();
            DrawLegend(me, currentCave);

            HandleClicks(me, searcher);
        }

        // ------------------------------------------------------------------ layers

        private void DrawGrid()
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.06f);
            float step = _frame.width * (100f / (_half * 2f)); // 100 world metres
            for (float x = _frame.x + step; x < _frame.xMax; x += step)
                GUI.DrawTexture(new Rect(x, _frame.y, 1f, _frame.height), Texture2D.whiteTexture);
            for (float y = _frame.y + step; y < _frame.yMax; y += step)
                GUI.DrawTexture(new Rect(_frame.x, y, _frame.width, 1f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private void DrawLandmarks(GameWorld world)
        {
            // The lake ellipse and camp clearing are baked into the background; these are the labels
            // plus the glyphs that make the map navigable (matches the web build's LANDMARKS).
            Blob(ToMap(0f, 0f), 16f, new Color(1f, 0.6f, 0.25f, 0.55f));
            Label(ToMap(0f, 0f) + new Vector2(0f, -16f), "CAMP", new Color(1f, 0.85f, 0.6f));
            Label(ToMap((float)WorldData.Lookout.X, (float)WorldData.Lookout.Z), "TOWER", new Color(0.85f, 0.78f, 0.55f));
            Label(ToMap((float)WorldData.Lake.X, (float)WorldData.Lake.Z), "LAKE", new Color(0.6f, 0.8f, 0.95f));
            Dot(ToMap((float)WorldData.Rv.X, (float)WorldData.Rv.Z), 4f, MeshUtil.Rgb(0xd9d3c2));

            // The duffel — where proof becomes permanent. Every searcher's destination, so it's
            // labelled rather than left as scenery.
            Vector2 duffel = ToMap(WorldBuilder.DuffelPosition());
            Blob(duffel, 12f, new Color(1f, 0.85f, 0.5f, 0.35f));
            Dot(duffel, 4f, new Color(1f, 0.88f, 0.6f));
            Label(duffel + new Vector2(0f, 13f), "DUFFEL", new Color(1f, 0.9f, 0.65f));
        }

        /// <summary>The clue trail is gated on "contact" — this mirrors Game.ts's clueVisionActive().</summary>
        private static bool ClueVisionActive(HPPlayer me, GameWorld world)
        {
            string spec = me.Specialty.Value;
            Vector3 p = me.transform.position;

            float hear = HearRange * (float)Specialties.HearRangeMul(spec); // Theo hears farther
            foreach (var other in HPPlayer.All)
            {
                if (other == null || !other.IsBigfoot) continue;
                // "In contact" via hearing — so a crouching Bigfoot doesn't trip it, even Theo's.
                // The evidence-sight half of the check below still works: it left tracks earlier.
                if (other.Crouched.Value) continue;
                float dx = other.transform.position.x - p.x, dz = other.transform.position.z - p.z;
                if (dx * dx + dz * dz < hear * hear) return true;
            }

            float sight = EvidenceSight * (float)Specialties.EvidenceSightMul(spec); // Wren sees farther
            float window = ClueWindow * (float)Specialties.ClueWindowMul(spec);
            foreach (var c in ClueMarker.All)
            {
                if (c == null || Time.time - c.Born > window) continue;
                float dx = c.transform.position.x - p.x, dz = c.transform.position.z - p.z;
                if (dx * dx + dz * dz < sight * sight) return true;
            }
            return false;
        }

        private void DrawClueTrail(HPPlayer me)
        {
            float window = ClueWindow * (float)Specialties.ClueWindowMul(me.Specialty.Value);
            var trail = new System.Collections.Generic.List<ClueMarker>();
            foreach (var c in ClueMarker.All)
                if (c != null && Time.time - c.Born <= window) trail.Add(c);
            trail.Sort((a, b) => a.Born.CompareTo(b.Born));

            // A faint breadcrumb line through the tracks, then the tracks themselves.
            for (int i = 1; i < trail.Count; i++)
            {
                Line(ToMap(trail[i - 1].transform.position), ToMap(trail[i].transform.position),
                    new Color(0.9f, 0.71f, 0.47f, 0.35f), 1.5f);
            }
            foreach (var c in trail)
                Dot(ToMap(c.transform.position), 2.4f, new Color(0.92f, 0.75f, 0.51f, 0.9f));
        }

        /// <summary>
        /// Workable (castable) prints — shown to the whole team even outside clue contact, because
        /// they're the objective, not a tracking hint. The team can always see where the work is; the
        /// cost is the walk out to it and the long stationary cast once Mara arrives.
        /// </summary>
        private void DrawCastablePrints()
        {
            float pulse = 0.55f + 0.45f * Mathf.Sin(Time.time * 2.2f);
            foreach (var c in ClueMarker.Castables)
            {
                if (c == null) continue;
                Vector2 p = ToMap(c.transform.position);
                Blob(p, 9f, new Color(0.90f, 0.82f, 0.55f, 0.26f + 0.22f * pulse));
                Dot(p, 3.2f, new Color(0.96f, 0.90f, 0.66f));
            }

            // Hair samples share the workable-evidence language, in a cooler tone so the team can tell
            // at a glance which ones need Mara and which anyone can go and take.
            foreach (var c in ClueMarker.Hairs)
            {
                if (c == null) continue;
                Vector2 p = ToMap(c.transform.position);
                Blob(p, 8f, new Color(0.62f, 0.80f, 0.72f, 0.24f + 0.20f * pulse));
                Dot(p, 3f, new Color(0.76f, 0.93f, 0.85f));
            }
        }

        /// <summary>
        /// Spilled packs. Shown to the whole team unconditionally and drawn loudest on the map: this
        /// is proof that has already been paid for once, it is on a timer, and it is the only marker
        /// here that represents someone else's bad night waiting to be rescued.
        /// </summary>
        private void DrawProofPiles()
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 3.4f);
            foreach (var pile in ProofPile.All)
            {
                if (pile == null) continue;
                Vector2 p = ToMap(pile.transform.position);
                Blob(p, 13f, new Color(1f, 0.72f, 0.32f, 0.20f + 0.28f * pulse));
                Dot(p, 4.2f, new Color(1f, 0.82f, 0.42f));

                var st = new GUIStyle(GUI.skin.label) { fontSize = 10, alignment = TextAnchor.MiddleCenter };
                st.normal.textColor = new Color(1f, 0.86f, 0.55f);
                GUI.Label(new Rect(p.x - 30f, p.y + 5f, 60f, 14f), $"pack ×{pile.Total}", st);
            }
        }

        private void DrawMarks()
        {
            foreach (var m in TrailMark.All)
            {
                if (m == null) continue;
                Vector2 p = ToMap(m.transform.position);
                // Amber diamond — deliberately distinct from the pulsing pings.
                Matrix4x4 mat = GUI.matrix;
                GUIUtility.RotateAroundPivot(45f, p);
                Color old = GUI.color;
                GUI.color = MeshUtil.Rgb(0xffb347);
                GUI.DrawTexture(new Rect(p.x - 3.5f, p.y - 3.5f, 7f, 7f), Texture2D.whiteTexture);
                GUI.color = old;
                GUI.matrix = mat;
            }
        }

        private void DrawPings()
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 3f);
            foreach (var pg in PingBeacon.All)
            {
                if (pg == null) continue;
                Vector2 p = ToMap(pg.transform.position);
                float r = 7f + pulse * 3.5f;
                Color old = GUI.color;
                GUI.color = new Color(1f, 0.89f, 0.29f, 0.45f + 0.4f * pulse);
                GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2f, r * 2f), _ring);
                GUI.color = old;
                Dot(p, 2.5f, MeshUtil.Rgb(0xffe24a));
            }
        }

        private void DrawTeammates(HPPlayer me)
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p == me || p.IsBigfoot) continue;
                Vector2 at = ToMap(p.transform.position);
                bool down = p.Status.Value == HPPlayer.StatusIncap;
                Color c = down ? new Color(1f, 0.45f, 0.4f) : MeshUtil.Rgb(0x7ad1ff);
                Blob(at, 13f, new Color(c.r, c.g, c.b, 0.45f));
                Dot(at, 3f, c);
                if (p.CharacterName.Value != "")
                    Label(at + new Vector2(0f, 11f), ShortName(p.CharacterName.Value), new Color(0.8f, 0.9f, 1f));
            }
        }

        /// <summary>
        /// Cave mouths: Bigfoot's own fast-travel network, and for searchers only the mouths the team
        /// has actually walked up to (GameManager.CavesFound). An undiscovered lair draws nothing at
        /// all — not a greyed-out marker, which would still tell you where to go.
        /// </summary>
        private void DrawCaves(GameWorld world, HPPlayer me, int currentCave)
        {
            bool travelMode = me.IsBigfoot && me.Status.Value == HPPlayer.StatusActive &&
                              currentCave >= 0 && me.CaveReadyIn <= 0f;
            var gm = GameManager.Instance;

            for (int i = 0; i < world.Caves.Count; i++)
            {
                if (!me.IsBigfoot && (gm == null || !gm.IsCaveFound(i))) continue; // not found yet
                Vector2 p = ToMap((float)world.Caves[i].X, (float)world.Caves[i].Z);
                var r = new Rect(p.x - 11f, p.y - 11f, 22f, 22f);
                bool isCurrent = i == currentCave;
                bool selectable = travelMode && !isCurrent;

                Color old = GUI.color;
                GUI.color = isCurrent ? new Color(0.55f, 0.95f, 0.7f) : selectable ? Color.white : new Color(0.75f, 0.75f, 0.8f, 0.75f);
                if (selectable)
                {
                    if (GUI.Button(r, (i + 1).ToString())) me.RequestCaveTravel(i);
                }
                else
                {
                    GUI.Box(r, (i + 1).ToString());
                }
                GUI.color = old;
            }
        }

        private void DrawSelf(HPPlayer me)
        {
            Vector2 p = ToMap(me.transform.position);
            float yaw = me.SimYawFromTransform();
            // Sim forward at yaw 0 is -Z = up on the map. The map's x axis is mirrored (see ToMap),
            // which flips the direction of turn on screen — hence +yaw, not -yaw.
            Matrix4x4 mat = GUI.matrix;
            GUIUtility.RotateAroundPivot(yaw * Mathf.Rad2Deg, p);
            var style = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter };
            Color old = GUI.color;
            GUI.color = Color.white;
            GUI.Label(new Rect(p.x - 14f, p.y - 15f, 28f, 30f), "▲", style);
            GUI.color = old;
            GUI.matrix = mat;
        }

        private void DrawCompass()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.48f, 0.43f);
            GUI.Label(new Rect(_frame.center.x - 10f, _frame.y + 4f, 20f, 20f), "N", style);
            GUI.color = new Color(1f, 1f, 1f, 0.75f);
            GUI.Label(new Rect(_frame.center.x - 10f, _frame.yMax - 24f, 20f, 20f), "S", style);
            GUI.Label(new Rect(_frame.xMax - 22f, _frame.center.y - 10f, 20f, 20f), "E", style);
            GUI.Label(new Rect(_frame.x + 4f, _frame.center.y - 10f, 20f, 20f), "W", style);
            GUI.color = old;
        }

        private void DrawLegend(HPPlayer me, int currentCave)
        {
            string hint;
            if (me.IsBigfoot)
            {
                hint = currentCave < 0
                    ? "stand in a cave mouth to fast-travel"
                    : me.CaveReadyIn > 0f
                        ? $"cave system on cooldown ({me.CaveReadyIn:0.0}s)"
                        : "click a cave to emerge there";
            }
            else
            {
                // Say how many lairs are still out there. Without this the blank map is ambiguous —
                // a searcher can't tell "no caves found yet" from "this map doesn't show caves".
                var gm = GameManager.Instance;
                int total = WorldBuilder.EnsureWorld().Caves.Count;
                int found = 0;
                for (int i = 0; i < total; i++) if (gm != null && gm.IsCaveFound(i)) found++;
                hint = found < total
                    ? $"caves found {found}/{total} — walk up to a mouth to map it  ·  " +
                      $"click the map to drop a stakeout ping"
                    : "every cave mapped  ·  click the map to drop a stakeout ping  ·  " +
                      $"[{HPKeybinds.Label(HPAction.Ping)}] pings where you stand";
            }
            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(_frame.x, _frame.yMax + 4f, _frame.width, 20f), hint, style);
            GUI.Label(new Rect(_frame.x, _frame.y - 24f, _frame.width, 20f),
                $"HOLLOW PINES — [{HPKeybinds.Label(HPAction.Map)}] closes the map · you can keep moving",
                new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter });
        }

        /// <summary>Left-click on open map: searchers drop a ping there. (Cave buttons eat their own clicks.)</summary>
        private void HandleClicks(HPPlayer me, bool searcher)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return;
            if (!_frame.Contains(e.mousePosition)) { Close(); return; } // click outside = close
            if (!searcher) return;

            float u = (e.mousePosition.x - _frame.x) / _frame.width;
            float v = (e.mousePosition.y - _frame.y) / _frame.height;
            // Inverse of ToMap — x is mirrored, so it subtracts rather than adds.
            me.RequestPing(_half - u * _half * 2f, v * _half * 2f - _half);
            e.Use();
        }

        // ------------------------------------------------------------------ drawing helpers

        private Vector2 ToMap(Vector3 world) => ToMap(world.x, world.z);

        /// <summary>
        /// World (x,z) → screen. The x axis is MIRRORED on purpose: screen-up is world -Z (the sim's
        /// forward at yaw 0), so screen-right must be the player's visual right, which in Unity's
        /// mirrored XZ plane is world -X (see the handedness note in HPPlayer.StepSim). Without the
        /// mirror, strafing right would slide your dot LEFT across the map.
        /// </summary>
        private Vector2 ToMap(float x, float z)
        {
            return new Vector2(
                _frame.x + ((_half - x) / (_half * 2f)) * _frame.width,
                _frame.y + ((z + _half) / (_half * 2f)) * _frame.height);
        }

        private static void Dot(Vector2 p, float r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2f, r * 2f), _dot);
            GUI.color = old;
        }

        private static void Blob(Vector2 p, float r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(p.x - r, p.y - r, r * 2f, r * 2f), _dot);
            GUI.color = old;
        }

        private static void Line(Vector2 a, Vector2 b, Color c, float width)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            Matrix4x4 mat = GUI.matrix;
            Color old = GUI.color;
            GUI.color = c;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
            GUI.DrawTexture(new Rect(a.x, a.y - width / 2f, len, width), Texture2D.whiteTexture);
            GUI.color = old;
            GUI.matrix = mat;
        }

        private static void Label(Vector2 p, string text, Color c)
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleCenter };
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(new Rect(p.x - 50f, p.y - 9f, 100f, 18f), text, style); // cheap drop shadow
            GUI.color = c;
            GUI.Label(new Rect(p.x - 51f, p.y - 10f, 100f, 18f), text, style);
            GUI.color = old;
        }

        private static string ShortName(string full)
        {
            int sp = full.IndexOf(' ');
            return sp > 0 ? full.Substring(0, sp) : full;
        }

        // ------------------------------------------------------------------ baked background

        /// <summary>
        /// Bake the terrain once from the sim: height-shaded forest floor, the camp clearing, and the
        /// lake — the Unity answer to Environment.generateMapCanvas(). Lazy, so it costs nothing until
        /// the first time someone opens the map.
        /// </summary>
        private void EnsureTextures(GameWorld world)
        {
            if (_dot == null) _dot = RadialTex(32, false);
            if (_ring == null) _ring = RadialTex(32, true);
            if (_bg != null) return;

            var heights = new float[BgRes * BgRes];
            float min = float.MaxValue, max = float.MinValue;
            for (int j = 0; j < BgRes; j++)
            {
                for (int i = 0; i < BgRes; i++)
                {
                    float x = (i + 0.5f) / BgRes * (_half * 2f) - _half;
                    float z = (j + 0.5f) / BgRes * (_half * 2f) - _half;
                    float h = (float)world.GetHeight(x, z);
                    heights[j * BgRes + i] = h;
                    if (h < min) min = h;
                    if (h > max) max = h;
                }
            }

            var tex = new Texture2D(BgRes, BgRes, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var px = new Color[BgRes * BgRes];
            Color low = MeshUtil.Rgb(0x1e2a18), high = MeshUtil.Rgb(0x3c5228);
            Color lake = MeshUtil.Rgb(0x2a5a7a), camp = MeshUtil.Rgb(0x466030);
            float span = Mathf.Max(0.001f, max - min);
            float campR = (float)Sim.World.BaseCampRadius;

            for (int j = 0; j < BgRes; j++)
            {
                for (int i = 0; i < BgRes; i++)
                {
                    float x = (i + 0.5f) / BgRes * (_half * 2f) - _half;
                    float z = (j + 0.5f) / BgRes * (_half * 2f) - _half;
                    Color c = Color.Lerp(low, high, (heights[j * BgRes + i] - min) / span);

                    // Camp clearing — a soft lighter disc around the campfire.
                    float campD = Mathf.Sqrt(x * x + z * z) / (campR * 2.4f);
                    if (campD < 1f) c = Color.Lerp(camp, c, campD * campD);

                    // Lake — the shared ellipse, so the map matches the water you can walk into.
                    float lx = (x - (float)WorldData.Lake.X) / (float)WorldData.Lake.Rx;
                    float lz = (z - (float)WorldData.Lake.Z) / (float)WorldData.Lake.Rz;
                    float ld = lx * lx + lz * lz;
                    if (ld < 1f) c = Color.Lerp(lake, c, Mathf.Clamp01((ld - 0.75f) / 0.25f));

                    // Write in the SAME orientation ToMap uses, or the terrain ends up mirrored under
                    // the markers drawn on top of it (the lake sat opposite its own label).
                    //   rows: texture rows run bottom-up and the map wants +Z downward → flip j.
                    //   cols: ToMap mirrors x (screen-right = world -X, matching Unity's left-handed
                    //         frame — see HPPlayer.StepSim) → flip i to match.
                    px[(BgRes - 1 - j) * BgRes + (BgRes - 1 - i)] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply();
            _bg = tex;
        }

        /// <summary>A soft radial dot (or ring) used for markers and halos.</summary>
        private static Texture2D RadialTex(int s, bool ring)
        {
            var t = new Texture2D(s, s, TextureFormat.RGBA32, false);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float dx = (x - s / 2f) / (s / 2f), dy = (y - s / 2f) / (s / 2f);
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = ring
                        ? Mathf.Clamp01(1f - Mathf.Abs(d - 0.8f) / 0.2f)
                        : Mathf.Clamp01(1f - d);
                    if (!ring) a *= a;
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            t.Apply();
            return t;
        }
    }
}
