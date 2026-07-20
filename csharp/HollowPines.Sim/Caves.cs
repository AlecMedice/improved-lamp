using System;
using System.Collections.Generic;

namespace HollowPines.Sim
{
    public static class Caves
    {
        /// <summary>
        /// Cave entrances — Bigfoot's lairs and the nodes of its fast-travel network. Seed-derived
        /// so the client and the host generate the IDENTICAL set. Five caves spread across the
        /// outer ring (150..340 m from centre), >=120 m apart. Ported from shared/sim/caves.ts.
        /// </summary>
        public static List<Cave> GenerateCaves(uint seed)
        {
            var rand = Rng.Mulberry32(seed ^ CaveGen.SeedXor);
            var outList = new List<Cave>();
            for (int attempt = 0; outList.Count < CaveGen.Count && attempt < CaveGen.MaxAttempts; attempt++)
            {
                double angle = rand() * System.Math.PI * 2;
                double r = CaveGen.MinRadius + rand() * CaveGen.RadiusSpan;
                double x = SimMath.JsRound(System.Math.Cos(angle) * r); // JS Math.round semantics
                double z = SimMath.JsRound(System.Math.Sin(angle) * r);
                bool farEnough = true;
                foreach (var c in outList)
                {
                    if (SimMath.Hypot(c.X - x, c.Z - z) < CaveGen.MinSpacing) { farEnough = false; break; }
                }
                if (farEnough) outList.Add(new Cave(x, z));
            }
            return outList;
        }

        /// <summary>
        /// Index of a cave whose mouth (x,z) is within <see cref="CaveRules.TriggerRadius"/>, or -1.
        /// One place for client + host.
        /// </summary>
        public static int NearestCaveIndex(IReadOnlyList<Cave> caves, double x, double z)
        {
            double r2 = CaveRules.TriggerRadius * CaveRules.TriggerRadius;
            for (int i = 0; i < caves.Count; i++)
            {
                double dx = caves[i].X - x;
                double dz = caves[i].Z - z;
                if (dx * dx + dz * dz <= r2) return i;
            }
            return -1;
        }

        /// <summary>
        /// Where a traveller emerges from a cave mouth: <see cref="CaveRules.EmergeOffset"/> metres
        /// toward map centre (outside the boulder horseshoe), facing back into the forest. Shared so the
        /// host's authoritative caveTravel and the client's fade-in land on the exact same spot + heading.
        /// </summary>
        public static EmergePoint CaveEmergePoint(Cave cave)
        {
            double dl = SimMath.Hypot(cave.X, cave.Z);
            if (dl == 0) dl = 1;
            return new EmergePoint
            {
                X = cave.X - (cave.X / dl) * CaveRules.EmergeOffset,
                Z = cave.Z - (cave.Z / dl) * CaveRules.EmergeOffset,
                Yaw = System.Math.Atan2(cave.X, cave.Z),
            };
        }
    }
}
