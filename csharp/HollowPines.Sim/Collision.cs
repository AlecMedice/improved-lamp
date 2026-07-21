using System.Collections.Generic;

namespace HollowPines.Sim
{
    /// <summary>Result of <see cref="Collision.ClimbSupport"/>: a climbable's top, and whether over its footprint.</summary>
    public struct ClimbSupportResult
    {
        public double Top;
        public bool Over;
    }

    public static class Collision
    {
        /// <summary>A player within this vertical slop of a climbable's top counts as "on top".</summary>
        private const double ClimbTopEps = 0.06;

        /// <summary>
        /// Push an (x,z) point out of any solid circle collider it overlaps. Returns the resolved point.
        /// Climbable structures are solid from the side but walkable on top: if <paramref name="feetY"/>/
        /// <paramref name="getHeight"/> are supplied and the point is at/above a climbable's top, that
        /// collider is skipped so the player can stand on it. Called without feetY it treats every
        /// collider as a plain full-height solid.
        /// </summary>
        public static Vec2 ResolveCollision(
            IReadOnlyList<Collider> colliders, double x, double z, double radius,
            double feetY = double.NegativeInfinity, HeightFn getHeight = null)
        {
            double nx = x;
            double nz = z;
            foreach (var t in colliders)
            {
                if (t.ClimbH.HasValue && getHeight != null &&
                    feetY >= getHeight(t.X, t.Z) + t.ClimbH.Value - ClimbTopEps) continue;
                double min = t.R + radius;
                double dx = nx - t.X;
                double dz = nz - t.Z;
                double d2 = dx * dx + dz * dz;
                if (d2 < min * min && d2 > 1e-6)
                {
                    double d = System.Math.Sqrt(d2);
                    double push = min - d;
                    nx += (dx / d) * push;
                    nz += (dz / d) * push;
                }
            }
            return new Vec2(nx, nz);
        }

        /// <summary>
        /// Ground height at (x,z): the terrain, raised to a climbable's top when standing over its
        /// footprint (so a player who has climbed onto a structure walks on its top surface).
        /// </summary>
        public static double GroundHeightAt(IReadOnlyList<Collider> climbables, HeightFn getHeight, double x, double z)
        {
            double g = getHeight(x, z);
            foreach (var t in climbables)
            {
                double dx = x - t.X;
                double dz = z - t.Z;
                if (dx * dx + dz * dz <= t.R * t.R)
                    g = System.Math.Max(g, getHeight(t.X, t.Z) + (t.ClimbH ?? 0));
            }
            return g;
        }

        /// <summary>
        /// The top of a climbable structure Bigfoot can scale here (clinging to the side, or over the
        /// top), or null.
        /// </summary>
        public static ClimbSupportResult? ClimbSupport(
            IReadOnlyList<Collider> climbables, HeightFn getHeight, double x, double z, double radius, double reach)
        {
            ClimbSupportResult? best = null;
            foreach (var t in climbables)
            {
                double d = SimMath.Hypot(x - t.X, z - t.Z);
                bool over;
                if (d <= t.R) over = true;                       // over the footprint (on/above the top)
                else if (d <= t.R + radius + reach) over = false; // within reach of the side
                else continue;
                double top = getHeight(t.X, t.Z) + (t.ClimbH ?? 0);
                if (best == null || top > best.Value.Top)
                    best = new ClimbSupportResult { Top = top, Over = over };
            }
            return best;
        }

        /// <summary>True if a solid collider blocks the straight (XZ) line between two points.</summary>
        public static bool LineBlocked(IReadOnlyList<Collider> colliders, Vec2 a, Vec2 b)
        {
            double dx = b.X - a.X;
            double dz = b.Z - a.Z;
            double len2 = dx * dx + dz * dz;
            if (len2 == 0) len2 = 1e-6;
            foreach (var t in colliders)
            {
                double s = ((t.X - a.X) * dx + (t.Z - a.Z) * dz) / len2;
                s = System.Math.Max(0, System.Math.Min(1, s));
                double cx = a.X + dx * s;
                double cz = a.Z + dz * s;
                double ddx = t.X - cx;
                double ddz = t.Z - cz;
                if (ddx * ddx + ddz * ddz < t.R * t.R) return true;
            }
            return false;
        }

        /// <summary>
        /// Push an (x,z) point out of every fallen log it overlaps — logs are SOLID to a grounded hunter.
        ///
        /// Called only for grounded hunters: Bigfoot strides over logs untouched, and a hunter
        /// mid-vault is airborne, so the trunk passes harmlessly beneath. That is the whole mechanic —
        /// a log is a wall you go around or spend stamina to clear, not a patch of mud you wade through.
        /// </summary>
        public static Vec2 ResolveLogs(IReadOnlyList<FallenLog> logs, double x, double z, double radius)
        {
            double nx = x;
            double nz = z;
            foreach (var log in logs)
            {
                double dx = nx - log.Cx;
                double dz = nz - log.Cz;
                double t = System.Math.Max(-log.HalfLen, System.Math.Min(log.HalfLen, dx * log.Ax + dz * log.Az));
                double px = log.Cx + t * log.Ax;
                double pz = log.Cz + t * log.Az;
                double ox = nx - px;
                double oz = nz - pz;
                double min = log.R + radius;
                double d = System.Math.Sqrt(ox * ox + oz * oz);
                if (d >= min) continue;
                if (d > 1e-6)
                {
                    ox /= d;
                    oz /= d;
                }
                else
                {
                    // Dead on the centreline (landed astride it): shove out along the trunk's normal
                    // rather than dividing by zero. Fixed choice, so every client resolves it the same.
                    ox = -log.Az;
                    oz = log.Ax;
                }
                double push = min - d;
                nx += ox * push;
                nz += oz * push;
            }
            return new Vec2(nx, nz);
        }

        /// <summary>Capsule overlap against all fallen logs, 0 = clear, 1 = fully inside (drives the vault prompt).</summary>
        public static double LogOverlap(IReadOnlyList<FallenLog> logs, double x, double z, double playerRadius)
        {
            double best = 0;
            foreach (var log in logs)
            {
                double dx = x - log.Cx;
                double dz = z - log.Cz;
                double t = System.Math.Max(-log.HalfLen, System.Math.Min(log.HalfLen, dx * log.Ax + dz * log.Az));
                double nx = log.Cx + t * log.Ax;
                double nz = log.Cz + t * log.Az;
                double dist = SimMath.Hypot(x - nx, z - nz);
                double threshold = log.R + playerRadius;
                if (dist < threshold) best = System.Math.Max(best, 1 - dist / threshold);
            }
            return best;
        }

        /// <summary>How deep into the lake the point is, 0 = outside, 1 = dead centre (wading slow).</summary>
        public static double LakeDepth(double x, double z)
        {
            double nx = (x - WorldData.Lake.X) / WorldData.Lake.Rx;
            double nz = (z - WorldData.Lake.Z) / WorldData.Lake.Rz;
            double d2 = nx * nx + nz * nz;
            return d2 < 1 ? System.Math.Max(0, 1 - System.Math.Sqrt(d2)) : 0;
        }
    }
}
