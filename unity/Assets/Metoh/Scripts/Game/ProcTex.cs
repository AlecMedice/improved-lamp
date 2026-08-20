// Procedurally generated surface maps, built once at runtime.
//
// WHY THIS EXISTS. Everything in this game is generated — there is not a single texture file in the
// repo, and adding one would break the "clone it and it runs" property the whole project is built
// on. But flat-coloured URP/Lit is exactly what makes a scene read as "polygons" rather than as
// material: with no normal detail, a surface has only one normal per face, so it takes one shade of
// light and dies there. Snow in particular is almost entirely READ through its microstructure —
// the grain that scatters moonlight into glitter, the wind ripple, the way a footprint's edge
// catches. A flat white triangle cannot do any of that no matter how you colour it.
//
// So the maps are synthesized here from tileable value noise, the same technique the meshes and the
// audio already use. A 256px normal map costs ~256 KB and a few milliseconds to build, once.
//
// THE NOISE MUST TILE. These are tiled across an 800 m terrain, so a non-periodic hash would put a
// visible seam every repeat. The lattice hash below wraps its integer coordinates modulo the period,
// which makes the field genuinely periodic rather than approximately so.
using UnityEngine;

namespace Metoh.Game
{
    public static class ProcTex
    {
        private static Texture2D _snow, _rock, _bark, _ice, _fabric, _snowDetail, _fur;

        /// <summary>Fine wind-packed grain — the sparkle surface. Tiled small and often.</summary>
        public static Texture2D SnowNormal => _snow ??= BuildNormal(256, "SnowNormal", 2.4f,
            (x, y) => Fbm(x, y, 256, 8, 5, 0.55f) * 0.65f + Ridge(x, y, 256, 3) * 0.35f);

        /// <summary>A second, much finer grain layered as URP's DETAIL normal, so close-up snow keeps
        /// structure after the base map has tiled out to mush.</summary>
        public static Texture2D SnowDetailNormal => _snowDetail ??= BuildNormal(128, "SnowDetail", 1.6f,
            (x, y) => Fbm(x, y, 128, 24, 3, 0.5f));

        /// <summary>Coarse fracture and grit for granite, scree and boulders.</summary>
        public static Texture2D RockNormal => _rock ??= BuildNormal(256, "RockNormal", 3.2f,
            (x, y) => Ridge(x, y, 256, 6) * 0.7f + Fbm(x, y, 256, 12, 4, 0.6f) * 0.3f);

        /// <summary>Vertical striation for trunks and timber. Stretched along Y by sampling anisotropically.</summary>
        public static Texture2D BarkNormal => _bark ??= BuildNormal(256, "BarkNormal", 2.8f,
            (x, y) => Fbm(x * 4f, y * 0.35f, 256, 10, 4, 0.6f));

        /// <summary>Near-flat with occasional pressure cracks — the tarn surface.</summary>
        public static Texture2D IceNormal => _ice ??= BuildNormal(256, "IceNormal", 1.5f,
            (x, y) => Ridge(x, y, 256, 2) * 0.8f + Fbm(x, y, 256, 6, 3, 0.5f) * 0.2f);

        /// <summary>
        /// Coarse matted fur. Stretched hard along one axis so it reads as strands lying in a
        /// direction rather than as generic lumps — that directionality is most of what separates
        /// "an animal" from "a grey capsule" at the distance you usually see the Yeti from.
        /// </summary>
        public static Texture2D FurNormal => _fur ??= BuildNormal(256, "FurNormal", 2.2f,
            (x, y) => Fbm(x * 6f, y * 0.5f, 256, 14, 4, 0.62f) * 0.75f + Ridge(x * 3f, y, 256, 20) * 0.25f);

        /// <summary>Woven canvas for the expedition tents.</summary>
        public static Texture2D FabricNormal => _fabric ??= BuildNormal(128, "FabricNormal", 1.2f,
            (x, y) => (Mathf.Sin(x * 1.4f) * Mathf.Sin(y * 1.4f)) * 0.5f + 0.5f);

        private static Texture2D _packed, _metal;

        /// <summary>
        /// Trodden trail snow — and the reason trails stopped reading as decals.
        ///
        /// The packed trail used to share <see cref="SnowNormal"/> with virgin powder, just tiled
        /// differently. That is why it read as "snow of a different colour" rather than as a different
        /// material: identical micro-relief, so it caught the moon and the torch in exactly the same
        /// way as the drift beside it, and the eye reads lighting response long before it reads albedo.
        ///
        /// Packed snow is physically the opposite of powder. Powder is a loose fractal of ice crystals
        /// — fine, uniform, sparkly. Packed snow has been crushed by boots into overlapping DISHES,
        /// partially melted and refrozen, so it is broadly smoother but pitted at bootprint scale, with
        /// hard little rims where one press cut into another. So: cellular dishes carry the shape,
        /// scuff striations run along it, and the fine crystal grain is mostly gone (0.18, against
        /// SnowNormal's 0.65) because that is exactly what walking on it destroys.
        /// </summary>
        public static Texture2D PackedSnowNormal => _packed ??= BuildNormal(256, "PackedSnowNormal", 1.9f,
            (x, y) => Cells(x, y, 256, 7) * 0.55f                       // boot dishes
                    + Fbm(x * 3.5f, y * 0.6f, 256, 9, 3, 0.5f) * 0.27f  // scuff, dragged along the path
                    + Fbm(x, y, 256, 20, 3, 0.5f) * 0.18f);             // what little crystal survives

        /// <summary>
        /// Dented, scratched steel panel for the wreck. Broad hail-and-age dents from the cell
        /// function, then fine anisotropic scratches — the scratches are what sell it as metal rather
        /// than as painted stone, because they catch a moving torch beam as bright streaks.
        /// </summary>
        public static Texture2D MetalNormal => _metal ??= BuildNormal(256, "MetalNormal", 2.6f,
            (x, y) => (1f - Cells(x, y, 256, 5)) * 0.5f                 // shallow dents
                    + Fbm(x * 9f, y * 0.35f, 256, 16, 3, 0.55f) * 0.34f // scratches
                    + Fbm(x, y, 256, 4, 2, 0.5f) * 0.16f);              // panel warp

        /// <summary>
        /// The searcher's headtorch, as a spot-light cookie.
        ///
        /// WHY. The torch is the single most-looked-at light in the game — a searcher spends the whole
        /// night reading the valley through it — and it projects a perfect mathematical disc. Real
        /// hand-and-head torches never do: a reflector has facets, the lens picks up frost and grease
        /// within minutes at this altitude, and the pool that lands on the ground is a soft, mottled,
        /// slightly off-round patch with a ragged edge. A geometrically perfect circle sliding over
        /// snow is one of the loudest remaining "this is a game" tells left in the frame, and it is
        /// loudest precisely where the player is looking.
        ///
        /// It also earns its keep in gameplay. [legibility] made the terrain readable by giving it a
        /// value range; the torch pool is what a searcher reads that range *through*, and a flat disc
        /// carries no information about the surface it is falling on. Break-up in the beam makes the
        /// ground's own relief legible where the two interact.
        ///
        /// The shape is deliberately gentle. The light already has a real angular falloff
        /// (`innerSpotAngle` 38 inside `spotAngle` 62) and a cookie MULTIPLIES that, so an aggressive
        /// edge here would darken the rim twice and shrink the usable pool — a stealth nerf to the
        /// searchers' main tool dressed up as an art change. The core therefore stays at full
        /// brightness and everything this does happens in the outer third.
        ///
        /// Clamp wrapping is not optional: URP packs cookies into an atlas, and a Repeat cookie bleeds
        /// its opposite edge into its neighbours' tiles.
        /// </summary>
        private static Texture2D _torchCookie;
        public static Texture2D TorchCookie
        {
            get
            {
                if (_torchCookie != null) return _torchCookie;
                const int size = 256;
                _torchCookie = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
                {
                    name = "TorchCookie",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    anisoLevel = 2,
                };

                var px = new Color32[size * size];
                float c = (size - 1) * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = (x - c) / c, dy = (y - c) / c;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);

                        // Base pool: flat out to 0.55, then eased to nothing by the border. The last
                        // few texels are forced to zero — an atlased cookie that is still lit at its
                        // own edge shows the tile as a hard square in the light.
                        float v = 1f - Mathf.SmoothStep(0.55f, 1.0f, d);

                        // Reflector facets: a slow angular ripple, so the pool is faintly polygonal
                        // rather than round. Weighted outward, because the centre of a beam is the one
                        // part a reflector actually collimates cleanly.
                        float ang = Mathf.Atan2(dy, dx);
                        v *= 1f + 0.06f * Mathf.Sin(ang * 7f) * Mathf.SmoothStep(0.2f, 0.9f, d);

                        // Frost and grease on the lens: low-frequency mottle across the whole pool,
                        // plus a finer layer. Both are subtle — this is a dirty lens, not a gobo.
                        float grime = Fbm(x, y, size, 3, 3, 0.55f) * 0.7f + Fbm(x, y, size, 9, 2, 0.5f) * 0.3f;
                        v *= 0.90f + 0.10f * grime;

                        // A single dim halo outside the main pool — the spill every real torch throws
                        // past its own cone edge. It is what stops the beam looking like a cut-out.
                        v += 0.05f * (1f - Mathf.SmoothStep(0.6f, 1.0f, d));

                        byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * 255f), 0, 255);
                        px[y * size + x] = new Color32(b, b, b, b);
                    }
                }
                _torchCookie.SetPixels32(px);
                _torchCookie.Apply(true, true);
                return _torchCookie;
            }
        }

        /// <summary>
        /// A soft round dot — the sprite every particle in the game is drawn with (snow, breath,
        /// spindrift, motes in the torch beam).
        ///
        /// Without it a particle is a flat quad, and a flat quad reads as a SQUARE the instant it is
        /// big enough to see: falling snow becomes falling confetti. This is a colour texture, not a
        /// normal map, so it is the one thing here that does not go through BuildNormal.
        ///
        /// The falloff is squared rather than linear because a linear ramp still shows a definite
        /// circular edge; squaring puts most of the fade near the rim, which is what makes a dot read
        /// as out-of-focus rather than as a drawn circle.
        /// </summary>
        private static Texture2D _dot;
        public static Texture2D SoftDot
        {
            get
            {
                if (_dot != null) return _dot;
                const int size = 32;
                _dot = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
                {
                    name = "SoftDot",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                var px = new Color32[size * size];
                float c = (size - 1) * 0.5f;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                        float a = Mathf.Clamp01(1f - d);
                        a *= a;
                        px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                    }
                }
                _dot.SetPixels32(px);
                _dot.Apply(true, true);
                return _dot;
            }
        }

        // ---------------------------------------------------------------- generation

        /// <summary>
        /// Height field -> tangent-space normal map, via central differences (a Sobel-lite).
        ///
        /// Sampling the height function WRAPPED keeps the derivative continuous across the seam, so
        /// the normals tile as cleanly as the heights do. <paramref name="strength"/> scales the
        /// gradient before normalising: it is the bumpiness dial, not a post-multiply.
        /// </summary>
        private static Texture2D BuildNormal(int size, string name, float strength, System.Func<float, float, float> height)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) // linear: normals are data, not colour
            {
                name = name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
            };

            // Cache the height field so each texel's four neighbours aren't recomputed from scratch.
            var h = new float[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    h[y * size + x] = height(x, y);

            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                int ym = ((y - 1) + size) % size, yp = (y + 1) % size;
                for (int x = 0; x < size; x++)
                {
                    int xm = ((x - 1) + size) % size, xp = (x + 1) % size;
                    float dx = (h[y * size + xp] - h[y * size + xm]) * strength;
                    float dy = (h[yp * size + x] - h[ym * size + x]) * strength;
                    Vector3 n = new Vector3(-dx, -dy, 1f).normalized;
                    px[y * size + x] = new Color32(
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.x * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.y * 0.5f + 0.5f) * 255f), 0, 255),
                        (byte)Mathf.Clamp(Mathf.RoundToInt((n.z * 0.5f + 0.5f) * 255f), 0, 255),
                        255);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true); // mipmaps on, then release the CPU copy
            return tex;
        }

        // ---------------------------------------------------------------- noise

        /// <summary>Tileable integer-lattice hash. Wrapping the coordinates is what makes it periodic.</summary>
        private static float Hash(int x, int y, int period)
        {
            x = ((x % period) + period) % period;
            y = ((y % period) + period) % period;
            int n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            return ((n ^ (n >> 16)) & 0x7fffffff) / (float)0x7fffffff;
        }

        /// <summary>Smoothed value noise on a lattice of `period` cells across the texture.</summary>
        private static float Value(float x, float y, int size, int period)
        {
            float fx = x / size * period, fy = y / size * period;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0, ty = fy - y0;
            // Smoothstep the interpolant so the lattice doesn't show as a grid of creases.
            tx = tx * tx * (3f - 2f * tx);
            ty = ty * ty * (3f - 2f * ty);
            float a = Mathf.Lerp(Hash(x0, y0, period), Hash(x0 + 1, y0, period), tx);
            float b = Mathf.Lerp(Hash(x0, y0 + 1, period), Hash(x0 + 1, y0 + 1, period), tx);
            return Mathf.Lerp(a, b, ty);
        }

        /// <summary>
        /// Tileable cellular (Worley-lite) noise: distance to the nearest of one jittered feature
        /// point per lattice cell, normalised to 0..1.
        ///
        /// Fbm cannot make this shape. Fractal noise is smooth blobs at every scale, so it gives
        /// rolling dunes; what a bootprint or a dent needs is a DISH with a rim — a field that falls
        /// away from scattered centres and creases where two of them meet. That crease is the whole
        /// visual point, and it is what makes packed snow read as pressed rather than merely rougher.
        ///
        /// Only the 3x3 neighbourhood is searched, which is exact for one point per cell: a nearer
        /// point cannot live two cells away. Coordinates wrap through <see cref="Hash"/>, so this
        /// tiles as cleanly as everything else here.
        /// </summary>
        private static float Cells(float x, float y, int size, int period)
        {
            float fx = x / size * period, fy = y / size * period;
            int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
            float best = 4f;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int cx = x0 + dx, cy = y0 + dy;
                    // Two decorrelated hashes place the feature point inside its cell. Reusing one
                    // hash for both axes would line every point up on the cell diagonal.
                    float px = cx + Hash(cx, cy, period);
                    float py = cy + Hash(cx + 977, cy + 331, period);
                    float ddx = fx - px, ddy = fy - py;
                    float d2 = ddx * ddx + ddy * ddy;
                    if (d2 < best) best = d2;
                }
            }
            // sqrt then clamp: distances beyond one cell are already the flat "between dishes" region.
            return Mathf.Clamp01(Mathf.Sqrt(best));
        }

        /// <summary>Fractal sum — the general-purpose "surface roughness" field.</summary>
        private static float Fbm(float x, float y, int size, int basePeriod, int octaves, float gain)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            int period = basePeriod;
            for (int i = 0; i < octaves; i++)
            {
                sum += Value(x, y, size, period) * amp;
                norm += amp;
                amp *= gain;
                period *= 2;
            }
            return sum / Mathf.Max(norm, 1e-5f);
        }

        /// <summary>
        /// Ridged noise — 1 - |2v-1| folded, which turns smooth blobs into creases. This is what makes
        /// rock read as fractured rather than lumpy, and gives ice its pressure lines.
        /// </summary>
        private static float Ridge(float x, float y, int size, int period)
        {
            float v = Value(x, y, size, period);
            float r = 1f - Mathf.Abs(v * 2f - 1f);
            return r * r;
        }
    }
}
