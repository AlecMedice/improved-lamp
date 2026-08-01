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
