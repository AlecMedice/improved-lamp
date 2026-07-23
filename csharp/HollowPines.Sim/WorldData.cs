using System.Collections.Generic;

namespace HollowPines.Sim
{
    /// <summary>
    /// Fixed world geometry + deterministic collider construction. Ported from shared/sim/world.ts.
    /// </summary>
    public static class WorldData
    {
        /// <summary>Lake: an ellipse SW of camp. Speed penalty + shore colouring read from this.</summary>
        public static class Lake
        {
            public const double X = -120;
            public const double Z = -110;
            public const double Rx = 60;
            public const double Rz = 45;
        }

        /// <summary>Fixed wooden fire-lookout tower location (a single circle collider).</summary>
        public static class Lookout
        {
            public const double X = 220;
            public const double Z = -230;
            public const double R = 2.4;
        }

        /// <summary>Research RV parked beside the campfire (3 circle colliders along the body).</summary>
        public static class Rv
        {
            public const double X = 9;
            public const double Z = -4;
            public const double Ry = -0.5;
        }

        /// <summary>
        /// Fallen-log obstacles (slow hunters, not Bigfoot). [cx, cz, angle(rad), length(m)].
        /// After the trunk is laid flat and turned by `angle`, its long axis in world XZ is
        /// (cos(angle), -sin(angle)); trunk radius is 0.38.
        /// </summary>
        private static readonly double[][] LogTable =
        {
            new double[] { 120, -160, 0.3, 11 },
            new double[] { 60, -90, 1.8, 9 },
            new double[] { -60, -70, 0.7, 10 },
            new double[] { -170, -80, -0.4, 12 },
            new double[] { -200, 30, 1.1, 10 },
            new double[] { 160, 80, -0.8, 9 },
            new double[] { 240, -160, 0.5, 13 },
            new double[] { -100, 200, 1.4, 10 },
            new double[] { 50, 260, -0.6, 11 },
            new double[] { -280, 120, 0.9, 9 },
        };

        public static List<FallenLog> BuildFallenLogs()
        {
            var logs = new List<FallenLog>();
            foreach (var row in LogTable)
            {
                double cx = row[0], cz = row[1], angle = row[2], len = row[3];
                logs.Add(new FallenLog
                {
                    Cx = cx,
                    Cz = cz,
                    Ax = System.Math.Cos(angle),
                    Az = -System.Math.Sin(angle),
                    HalfLen = len / 2,
                    R = 0.38,
                });
            }
            return logs;
        }

        private static bool NearCave(IReadOnlyList<Cave> caves, double x, double z, double r)
        {
            foreach (var c in caves)
            {
                if ((c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z) < r * r) return true;
            }
            return false;
        }

        /// <summary>
        /// Is (x,z) inside the lake (optionally plus a shoreline margin)? Trees are rejected here so
        /// trunks don't stand in open water. Deliberately a local copy of the ellipse test rather
        /// than calling <see cref="Collision.LakeDepth"/> — mirrors the TS side, which keeps it local
        /// to avoid an import cycle.
        /// </summary>
        private static bool InLake(double x, double z, double margin)
        {
            double nx = (x - Lake.X) / (Lake.Rx + margin);
            double nz = (z - Lake.Z) / (Lake.Rz + margin);
            return nx * nx + nz * nz < 1;
        }

        /// <summary>
        /// Deterministically build the circle colliders (trees, RV, caves, lookout tower) for a
        /// seed + cave set. The tree loop preserves the exact rand() call order (incl. the rotation
        /// draw it discards) so the collider positions match the rendered tree instances exactly.
        ///
        /// Every rejection below is a `continue` placed BEFORE the scale/rotation draws, so adding
        /// one never shifts the RNG stream for later candidates — that invariant is what lets the
        /// renderer re-walk this loop and land its trunks exactly on these colliders.
        /// </summary>
        public static List<Collider> BuildColliders(uint seed, IReadOnlyList<Cave> caves, IReadOnlyList<ForestPath> paths = null)
        {
            var colliders = new List<Collider>();

            // Trees — mirror buildForest()'s placement loop exactly.
            var rand = Rng.Mulberry32(seed ^ 0x9e3779b9u);
            double half = World.Size / 2 - 6;
            for (int i = 0; i < World.TreeCount; i++)
            {
                double x = (rand() * 2 - 1) * half;
                double z = (rand() * 2 - 1) * half;
                if (System.Math.Sqrt(x * x + z * z) < World.BaseCampRadius + 4) continue; // keep clearing open
                if (NearCave(caves, x, z, 7)) continue;                                    // keep cave mouths clear
                if (InLake(x, z, 3)) continue;                        // trees don't grow in open water
                if (Paths.PathDepth(paths, x, z, PathGen.TreeMargin) > 0) continue; // keep the trails walkable
                double s = 0.7 + rand() * 0.9;
                rand(); // rotation draw — discarded, but consumed to keep the sequence aligned
                colliders.Add(new Collider(x, z, 0.45 * s));
            }

            // RV — 3 circles along the body, rotated by Rv.Ry about Y. Flat roof is a low perch.
            double c = System.Math.Cos(Rv.Ry);
            double sn = System.Math.Sin(Rv.Ry);
            foreach (double lx in new double[] { -2.2, 0, 2.2 })
            {
                colliders.Add(new Collider(Rv.X + lx * c, Rv.Z + -lx * sn, 1.6, 2.8));
            }

            // Caves — horseshoe of boulders; side + back solid, the mouth (toward centre) open.
            // The boulders are climbable — Bigfoot can perch on them above its lair.
            foreach (var cave in caves)
            {
                double dl = SimMath.Hypot(cave.X, cave.Z);
                if (dl == 0) dl = 1;
                double dx = -cave.X / dl;
                double dz = -cave.Z / dl;
                double px = -dz;
                double pz = dx;
                colliders.Add(new Collider(cave.X - dx * 3, cave.Z - dz * 3, 1.8, 2.4));
                colliders.Add(new Collider(cave.X + px * 3.0, cave.Z + pz * 3.0, 1.5, 2.4));
                colliders.Add(new Collider(cave.X - px * 3.0, cave.Z - pz * 3.0, 1.5, 2.4));
            }

            // Lookout tower — the tallest climb; its render platform sits at ~10 m.
            colliders.Add(new Collider(Lookout.X, Lookout.Z, Lookout.R, 9.5));

            return colliders;
        }
    }
}
