using System.Collections.Generic;

namespace HollowPines.Sim
{
    /// <summary>
    /// Old logging trails radiating out of the base camp. Ported from shared/sim/paths.ts.
    ///
    /// These are real terrain features, not decoration: <see cref="WorldData.BuildColliders"/> skips
    /// trees inside the corridor, so a path is a genuinely tree-free lane — fast to run, but with
    /// long sightlines that make you easy to film and easy to spot. Taking the trail is a
    /// speed-for-exposure trade, which is the whole reason they exist.
    /// </summary>
    public static class Paths
    {
        /// <summary>
        /// Seed-derived trail network — every client and the host lay down the identical set with
        /// nothing replicated. Each trail starts at the edge of the camp clearing and wanders outward
        /// with a bounded heading jitter until it leaves the map, so it reads as a meander rather
        /// than a spoke.
        /// </summary>
        public static List<ForestPath> GeneratePaths(uint seed)
        {
            var rand = Rng.Mulberry32(seed ^ PathGen.SeedXor);
            var outPaths = new List<ForestPath>();
            double half = World.Size / 2;

            for (int i = 0; i < PathGen.Count; i++)
            {
                // Fan the trailheads around camp so two never leave in the same direction, then
                // jitter inside that slice — evenly spaced spokes would read as man-made symmetry.
                double slice = (System.Math.PI * 2) / PathGen.Count;
                double heading = i * slice + rand() * slice;
                double x = System.Math.Cos(heading) * World.BaseCampRadius;
                double z = System.Math.Sin(heading) * World.BaseCampRadius;

                var path = new ForestPath();
                path.Pts.Add(new Vec2(x, z));
                for (int step = 0; step < PathGen.MaxSteps; step++)
                {
                    heading += (rand() * 2 - 1) * PathGen.Jitter;
                    x += System.Math.Cos(heading) * PathGen.StepLength;
                    z += System.Math.Sin(heading) * PathGen.StepLength;
                    path.Pts.Add(new Vec2(x, z));
                    if (System.Math.Abs(x) > half || System.Math.Abs(z) > half) break; // ran off the map
                }

                path.HalfWidth = PathGen.MinHalfWidth + rand() * PathGen.HalfWidthSpan;
                outPaths.Add(path);
            }
            return outPaths;
        }

        /// <summary>
        /// How far inside a trail corridor (x,z) sits: 0 = off-trail, 1 = dead centre.
        /// <paramref name="margin"/> widens the test past the corridor itself — the tree loop uses it
        /// to keep trunks from crowding the edge of a lane that is supposed to feel open.
        /// </summary>
        public static double PathDepth(IReadOnlyList<ForestPath> paths, double x, double z, double margin = 0)
        {
            double best = 0;
            if (paths == null) return best;
            foreach (var path in paths)
            {
                double w = path.HalfWidth + margin;
                for (int i = 1; i < path.Pts.Count; i++)
                {
                    Vec2 a = path.Pts[i - 1];
                    Vec2 b = path.Pts[i];
                    double dx = b.X - a.X;
                    double dz = b.Z - a.Z;
                    double len2 = dx * dx + dz * dz;
                    if (len2 == 0) len2 = 1e-6;
                    double t = ((x - a.X) * dx + (z - a.Z) * dz) / len2;
                    t = t < 0 ? 0 : (t > 1 ? 1 : t);
                    double cx = a.X + dx * t;
                    double cz = a.Z + dz * t;
                    double d = System.Math.Sqrt((x - cx) * (x - cx) + (z - cz) * (z - cz));
                    if (d < w)
                    {
                        double depth = 1 - d / w;
                        if (depth > best) best = depth;
                    }
                }
            }
            return best;
        }
    }
}
