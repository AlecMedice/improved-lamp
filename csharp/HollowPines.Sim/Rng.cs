using System;

namespace HollowPines.Sim
{
    /// <summary>
    /// Deterministic helpers so client and server generate the *same* forest from a seed.
    ///
    /// PARITY-CRITICAL: mulberry32 relies on JavaScript's 32-bit integer semantics
    /// (`| 0`, `>>> n`, `Math.imul`). The C# port uses <see cref="uint"/> arithmetic, which
    /// wraps mod 2^32 with the identical bit pattern, so it reproduces the TS stream exactly.
    /// The value-noise gradient table is <see cref="float"/> because the TS code stores it in a
    /// Float32Array — that single-precision truncation is observable and must be preserved.
    /// </summary>
    public static class Rng
    {
        /// <summary>Fast seeded PRNG. Returns a function producing doubles in [0,1).</summary>
        public static Func<double> Mulberry32(uint seed)
        {
            uint a = seed;
            return () =>
            {
                a = a + 0x6d2b79f5u;                       // JS: (a + 0x6d2b79f5) | 0
                uint t = (a ^ (a >> 15)) * (1u | a);        // JS: Math.imul(a ^ (a >>> 15), 1 | a)
                t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;   // JS: (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
                return (t ^ (t >> 14)) / 4294967296.0;      // JS: ((t ^ (t >>> 14)) >>> 0) / 2^32
            };
        }

        /// <summary>Smooth, seeded 2D value noise in ~[-1,1]. Cheap; good enough for gentle terrain.</summary>
        public static Func<double, double, double> MakeValueNoise(uint seed)
        {
            var rand = Mulberry32(seed);
            const int size = 256;
            var perm = new byte[size * 2];
            var grad = new float[size]; // Float32Array parity — single precision on purpose.
            for (int i = 0; i < size; i++)
            {
                perm[i] = (byte)i;
                grad[i] = (float)(rand() * 2 - 1);
            }
            for (int i = size - 1; i > 0; i--)
            {
                int j = (int)System.Math.Floor(rand() * (i + 1));
                byte tmp = perm[i];
                perm[i] = perm[j];
                perm[j] = tmp;
            }
            for (int i = 0; i < size; i++) perm[size + i] = perm[i];

            Func<double, double> fade = t => t * t * (3 - 2 * t);
            // grad is float; promoting to double here matches the TS read-back exactly.
            Func<int, int, double> valueAt = (ix, iy) => grad[perm[(perm[ix & 255] + (iy & 255)) & 255]];

            return (x, y) =>
            {
                int x0 = (int)System.Math.Floor(x);
                int y0 = (int)System.Math.Floor(y);
                double fx = fade(x - x0);
                double fy = fade(y - y0);
                double v00 = valueAt(x0, y0);
                double v10 = valueAt(x0 + 1, y0);
                double v01 = valueAt(x0, y0 + 1);
                double v11 = valueAt(x0 + 1, y0 + 1);
                double a = v00 + fx * (v10 - v00);
                double b = v01 + fx * (v11 - v01);
                return a + fy * (b - a);
            };
        }
    }
}
