namespace HollowPines.Sim
{
    public static class Terrain
    {
        /// <summary>
        /// Build the terrain-height sampler for a seed. fBm value-noise (4 octaves) scaled by
        /// hillHeight, flattened to ground level near the base camp. Ported verbatim from
        /// shared/sim/terrain.ts so the collision mesh and the sim agree exactly.
        /// </summary>
        public static HeightFn MakeTerrain(uint seed)
        {
            var noise = Rng.MakeValueNoise(seed);
            return (x, z) =>
            {
                double nx = x * 0.0065;
                double nz = z * 0.0065;
                double h = 0;
                double amp = 1;
                double freq = 1;
                double norm = 0;
                for (int o = 0; o < 4; o++)
                {
                    h += noise(nx * freq, nz * freq) * amp;
                    norm += amp;
                    amp *= 0.5;
                    freq *= 2;
                }
                h = (h / norm) * World.HillHeight;
                double d = System.Math.Sqrt(x * x + z * z);
                double flat = SimMath.Smoothstep(d, World.BaseCampRadius, World.BaseCampRadius + 12);
                return h * flat;
            };
        }
    }
}
