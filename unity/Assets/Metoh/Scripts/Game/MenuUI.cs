// The menu look-and-feel — a small IMGUI drawing kit that never touches GUI.skin.
//
// IMGUI is not what makes a menu look old; the BUILT-IN SKIN is. `GUI.skin.button` is the grey
// bevelled 2005 button, `GUI.skin.horizontalSlider` the 2005 slider, and every screen that draws
// through them inherits that decade whatever the layout does. So nothing here does: every pixel is
// a tinted white texture we generated ourselves, and the only stock control still used is the one
// whose *behaviour* is worth keeping (text fields, slider dragging) — dressed in styles with no
// background at all.
//
// Why IMGUI at all, when UNITY_NOTES [imgui-clamp] earmarks UI Toolkit for the R5 pass: a canvas of
// prefabs and font assets would have to live in the live project (C:\Users\amedi\Metoh_port), which
// is not a git repo and is reached only by robocopy. A script-only menu stays in this repo, syncs
// with the other three trees, and is proven by the headless rebuild like everything else.
//
// Everything is built once and cached — OnGUI runs at least twice a frame, so a `new GUIStyle` or a
// `new Texture2D` in a draw path is 60–120 allocations a second (the churn Aug_15_Bug records in
// HPHud). Textures are HideFlags.DontSave and are released with Release(); the null checks in
// EnsureBuilt() cover the editor destroying them on play-mode exit.
using UnityEngine;

namespace Metoh.Game
{
    public static class MenuUI
    {
        // ------------------------------------------------------------------ palette
        // Cold and desaturated — the Himalayan re-theme. The accent is the only chroma on screen, so
        // it carries all the "this is interactive" signalling by itself.
        public static readonly Color Text = new Color(0.90f, 0.94f, 0.96f);
        public static readonly Color TextDim = new Color(0.60f, 0.67f, 0.71f);
        public static readonly Color TextFaint = new Color(0.40f, 0.46f, 0.50f);
        public static readonly Color Accent = new Color(0.55f, 0.83f, 0.92f);
        public static readonly Color Warn = new Color(0.95f, 0.47f, 0.40f);
        public static readonly Color Ink = new Color(0.024f, 0.035f, 0.047f);

        /// <summary>
        /// Global opacity multiplier folded into every colour this class draws. The whole menu fades
        /// in through this rather than through GUI.color, because the helpers below SET GUI.color
        /// absolutely (they have to — they tint a white texture), so an ambient GUI.color would be
        /// overwritten by the first thing drawn.
        /// </summary>
        public static float Alpha = 1f;

        // ------------------------------------------------------------------ styles
        public static GUIStyle Display { get; private set; } // the title word; caller sets fontSize
        public static GUIStyle Row { get; private set; }     // a menu item
        public static GUIStyle Body { get; private set; }    // wrapped prose
        public static GUIStyle Small { get; private set; }
        public static GUIStyle SmallRight { get; private set; }
        public static GUIStyle Tiny { get; private set; }
        public static GUIStyle Center { get; private set; }  // chip labels
        public static GUIStyle FieldText { get; private set; }

        private static Texture2D _disc, _round, _gradH, _gradTop, _gradBottom;
        private static GUIStyle _roundStyle, _sliderThumb;
        private static Font _font;
        private static bool _fontTried;

        // One string per ASCII char, so per-character tracking doesn't allocate on every draw.
        private static readonly string[] Chars = new string[128];
        private static readonly GUIContent Content = new GUIContent();

        /// <summary>Call once at the top of OnGUI. Rebuilds anything the editor destroyed.</summary>
        public static void Begin()
        {
            Alpha = 1f;
            if (_round != null && _roundStyle != null) return;
            Build();
        }

        /// <summary>Drop the generated textures (the owning MonoBehaviour's OnDestroy).</summary>
        public static void Release()
        {
            Kill(ref _disc);
            Kill(ref _round);
            Kill(ref _gradH);
            Kill(ref _gradTop);
            Kill(ref _gradBottom);
            _roundStyle = null;
        }

        private static void Kill(ref Texture2D t)
        {
            if (t == null) return;
            Object.Destroy(t);
            t = null;
        }

        public static Color Fade(Color c, float a)
        {
            return new Color(c.r, c.g, c.b, c.a * a);
        }

        private static Color Tint(Color c)
        {
            return new Color(c.r, c.g, c.b, c.a * Alpha);
        }

        // ------------------------------------------------------------------ primitives

        /// <summary>A hard-edged rectangle — hairlines, rules, scrim washes.</summary>
        public static void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>A rounded rectangle (9-sliced, 6 px corners) — cards and chips.</summary>
        public static void Round(Rect r, Color c)
        {
            if (r.width < 2f || r.height < 2f) return;
            Color old = GUI.color;
            GUI.color = Tint(c);
            GUI.Box(r, GUIContent.none, _roundStyle);
            GUI.color = old;
        }

        /// <summary>A translucent card: 1 px light border, dark fill.</summary>
        public static void Card(Rect r, float opacity = 0.86f)
        {
            Round(r, new Color(1f, 1f, 1f, 0.10f));
            Round(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), new Color(Ink.r, Ink.g, Ink.b, opacity));
        }

        public static void Disc(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            GUI.DrawTexture(r, _disc);
            GUI.color = old;
        }

        /// <summary>A fully-rounded bar — slider tracks, switches, scroll indicators. Either axis.</summary>
        public static void Pill(Rect r, Color c)
        {
            float d = Mathf.Min(r.width, r.height);
            if (d <= 2f) { Fill(r, c); return; }
            if (r.width >= r.height)
            {
                Disc(new Rect(r.x, r.y, d, d), c);
                Disc(new Rect(r.xMax - d, r.y, d, d), c);
                Fill(new Rect(r.x + d * 0.5f, r.y, Mathf.Max(0f, r.width - d), r.height), c);
            }
            else
            {
                Disc(new Rect(r.x, r.y, d, d), c);
                Disc(new Rect(r.x, r.yMax - d, d, d), c);
                Fill(new Rect(r.x, r.y + d * 0.5f, r.width, Mathf.Max(0f, r.height - d)), c);
            }
        }

        /// <summary>Opaque at the left edge, gone by the right. The scrim that frames the column.</summary>
        public static void GradientH(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            GUI.DrawTexture(r, _gradH);
            GUI.color = old;
        }

        /// <summary>Vertical scrim — opaque at the top edge or the bottom one.</summary>
        public static void GradientV(Rect r, Color c, bool opaqueTop)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            GUI.DrawTexture(r, opaqueTop ? _gradTop : _gradBottom);
            GUI.color = old;
        }

        // ------------------------------------------------------------------ text

        public static void Label(Rect r, string text, GUIStyle style, Color c)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            Content.text = text;
            GUI.Label(r, Content, style);
            GUI.color = old;
        }

        /// <summary>
        /// Letter-spaced text, drawn a character at a time.
        ///
        /// IMGUI has no tracking, and tracking is most of the difference between a title that reads as
        /// a heading and one that reads as a label in a bigger font. The cost is one GUI.Label per
        /// character, which is nothing at the handful of places this is used (the title, the eyebrow,
        /// menu rows, the footer) and would be worth watching anywhere else.
        /// </summary>
        public static void Tracked(Rect r, string text, GUIStyle style, float track, Color c)
        {
            Color old = GUI.color;
            GUI.color = Tint(c);
            float x = r.x;
            for (int i = 0; i < text.Length; i++)
            {
                Content.text = Char(text[i]);
                float w = style.CalcSize(Content).x;
                GUI.Label(new Rect(x, r.y, w + 2f, r.height), Content, style);
                x += w + track;
            }
            GUI.color = old;
        }

        public static float TrackedWidth(string text, GUIStyle style, float track)
        {
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                Content.text = Char(text[i]);
                w += style.CalcSize(Content).x + track;
            }
            return Mathf.Max(0f, w - track);
        }

        private static string Char(char c)
        {
            if (c >= Chars.Length) return c.ToString();
            return Chars[c] ?? (Chars[c] = c.ToString());
        }

        // ------------------------------------------------------------------ controls

        /// <summary>
        /// Track, fill and knob drawn by hand; the DRAG is still Unity's, because slider dragging is
        /// fiddly hot-control bookkeeping that has no visual footprint. The stock control draws
        /// nothing (both styles are backgroundless) — the knob position below is computed with the
        /// same maths Unity uses, so what you see is exactly where the mouse has it.
        /// </summary>
        public static float Slider(Rect r, float value, float min, float max)
        {
            const float knob = 13f;
            float cy = r.y + r.height * 0.5f;
            var track = new Rect(r.x, cy - 2f, r.width, 4f);
            Pill(track, new Color(1f, 1f, 1f, 0.13f));

            float t = Mathf.Clamp01(Mathf.InverseLerp(min, max, value));
            float kx = r.x + t * (r.width - knob); // Unity runs the thumb's LEFT edge over this range
            float fillW = kx + knob * 0.5f - track.x;
            if (fillW > 4f) Pill(new Rect(track.x, track.y, fillW, track.height), Fade(Accent, 0.85f));
            Disc(new Rect(kx, cy - knob * 0.5f, knob, knob), Text);

            return GUI.HorizontalSlider(r, value, min, max, GUIStyle.none, _sliderThumb);
        }

        /// <summary>A switch, not a tick-box. Returns the new value; the whole row is clickable.</summary>
        public static bool Switch(Rect r, string label, bool on)
        {
            bool over = r.Contains(Event.current.mousePosition);
            var sw = new Rect(r.x, r.y + (r.height - 18f) * 0.5f, 34f, 18f);
            Pill(sw, on ? Fade(Accent, 0.55f) : new Color(1f, 1f, 1f, over ? 0.17f : 0.10f));
            Disc(new Rect(on ? sw.xMax - 16f : sw.x + 2f, sw.y + 2f, 14f, 14f), on ? Text : new Color(0.72f, 0.78f, 0.81f));
            Label(new Rect(sw.xMax + 12f, r.y, r.width - 46f, r.height), label, Small, on || over ? Text : TextDim);
            return GUI.Button(r, GUIContent.none, GUIStyle.none) ? !on : on;
        }

        /// <summary>A small pill button. <paramref name="active"/> = currently the chosen one.</summary>
        public static bool Chip(Rect r, string label, bool active)
        {
            bool over = r.Contains(Event.current.mousePosition);
            Round(r, active ? Fade(Accent, 0.22f) : new Color(1f, 1f, 1f, over ? 0.13f : 0.06f));
            if (active) Round(r, Fade(Accent, 0.35f)); // a second pass reads as a border at this alpha
            Label(r, label, Center, active ? Accent : over ? Text : TextDim);
            return GUI.Button(r, GUIContent.none, GUIStyle.none);
        }

        /// <summary>
        /// A text field with no box — a tinted well and an underline that lights up on focus. The
        /// control name is required (focus is what the underline reads), so callers must pass a
        /// unique one per field on screen.
        /// </summary>
        public static string Field(Rect r, string controlName, string text, int maxLength)
        {
            Round(r, new Color(1f, 1f, 1f, 0.06f));
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            Fill(new Rect(r.x + 1f, r.yMax - 1f, r.width - 2f, 1f), focused ? Accent : new Color(1f, 1f, 1f, 0.16f));

            Color old = GUI.color;
            GUI.color = Tint(Text);
            GUI.SetNextControlName(controlName);
            string result = GUI.TextField(r, text ?? string.Empty, maxLength, FieldText);
            GUI.color = old;
            return result;
        }

        // ------------------------------------------------------------------ construction

        private static void Build()
        {
            _disc = MakeDisc(32);
            _round = MakeRounded(6);
            _gradH = MakeRampH(64);
            _gradTop = MakeRampV(64, true);
            _gradBottom = MakeRampV(64, false);

            _roundStyle = new GUIStyle { border = new RectOffset(6, 6, 6, 6) };
            _roundStyle.normal.background = _round;
            // A real size on the thumb, and no texture on it: Unity maps the value over
            // (width - fixedWidth), which is the range Slider() draws its knob across.
            _sliderThumb = new GUIStyle { fixedWidth = 13f, fixedHeight = 13f };

            if (!_fontTried)
            {
                _fontTried = true;
                _font = PickFont();
            }

            Display = Style(64, FontStyle.Bold, TextAnchor.MiddleLeft);
            Row = Style(19, FontStyle.Bold, TextAnchor.MiddleLeft);
            Body = Style(14, FontStyle.Normal, TextAnchor.UpperLeft);
            Body.wordWrap = true;
            Small = Style(13, FontStyle.Normal, TextAnchor.MiddleLeft);
            SmallRight = Style(13, FontStyle.Normal, TextAnchor.MiddleRight);
            Tiny = Style(11, FontStyle.Normal, TextAnchor.MiddleLeft);
            Center = Style(12, FontStyle.Normal, TextAnchor.MiddleCenter);
            FieldText = Style(14, FontStyle.Normal, TextAnchor.MiddleLeft);
            FieldText.padding = new RectOffset(10, 10, 0, 0);
        }

        /// <summary>
        /// Every style is white and gets its colour from the caller — one style serves a dozen
        /// colours, so nothing allocates a style to change a shade.
        /// </summary>
        private static GUIStyle Style(int size, FontStyle weight, TextAnchor align)
        {
            var s = new GUIStyle
            {
                fontSize = size,
                fontStyle = weight,
                alignment = align,
                richText = false,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            s.normal.textColor = Color.white;
            if (_font != null) s.font = _font;
            return s;
        }

        /// <summary>
        /// Borrow a face from the OS. Unity's built-in font is Arial-ish, which is the single most
        /// dated thing on screen after the skin. The list degrades — a missing family costs nothing
        /// and the last stop is what we would have had anyway.
        /// </summary>
        private static Font PickFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[] { "Bahnschrift", "Segoe UI Semibold", "Segoe UI", "Helvetica Neue", "Arial" }, 24);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[menu] no OS font available, falling back to the built-in one: " + e.Message);
                return null;
            }
        }

        private static Texture2D New(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.DontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
        }

        /// <summary>An antialiased white disc — pill ends, slider knobs, switch handles.</summary>
        private static Texture2D MakeDisc(int n)
        {
            var t = New(n, n);
            var px = new Color32[n * n];
            float c = n * 0.5f, rad = c - 0.5f;
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - c) * (x + 0.5f - c) + (y + 0.5f - c) * (y + 0.5f - c));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(rad - d + 0.5f) * 255f));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>
        /// A white rounded rectangle sized for 9-slicing: `radius` corners around a 4 px middle that
        /// stretches. Alpha ramps over the last pixel of the corner arc, which is where the
        /// antialiasing comes from — IMGUI has no shape drawing of its own.
        /// </summary>
        private static Texture2D MakeRounded(int radius)
        {
            int n = radius * 2 + 4;
            var t = New(n, n);
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // Nearest point of the inner (corner-centre) rectangle, so the distance field is 0
                // everywhere in the middle band and a circle radius at each corner.
                float cx = Mathf.Clamp(x + 0.5f, radius, n - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, n - radius);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>Horizontal alpha ramp, opaque at x = 0. Column 0 draws at the rect's left edge.</summary>
        private static Texture2D MakeRampH(int n)
        {
            var t = New(n, 1);
            var px = new Color32[n];
            for (int i = 0; i < n; i++)
                px[i] = new Color32(255, 255, 255, (byte)(Mathf.SmoothStep(1f, 0f, i / (n - 1f)) * 255f));
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>
        /// Vertical alpha ramp. Row 0 of a texture is its BOTTOM row and draws at the bottom of the
        /// rect, so "opaque at the top" means the ramp runs the other way through the array.
        /// </summary>
        private static Texture2D MakeRampV(int n, bool opaqueTop)
        {
            var t = New(1, n);
            var px = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                float u = i / (n - 1f);              // 0 at the bottom of the drawn rect
                float a = opaqueTop ? u : 1f - u;    // 1 at the opaque edge
                px[i] = new Color32(255, 255, 255, (byte)(Mathf.SmoothStep(0f, 1f, a) * 255f));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
