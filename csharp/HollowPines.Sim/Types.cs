namespace HollowPines.Sim
{
    /// <summary>Terrain-height sampler for a seed: ground height at (x,z).</summary>
    public delegate double HeightFn(double x, double z);

    /// <summary>A 2D point in the world XZ plane.</summary>
    public struct Vec2
    {
        public double X;
        public double Z;
        public Vec2(double x, double z) { X = x; Z = z; }
    }

    /// <summary>A cave entrance — Bigfoot's lair and a node of its fast-travel network.</summary>
    public struct Cave
    {
        public double X;
        public double Z;
        public Cave(double x, double z) { X = x; Z = z; }
    }

    /// <summary>Where a traveller emerges from a cave mouth: a point + heading. Mirrors caveEmergePoint's return.</summary>
    public struct EmergePoint
    {
        public double X;
        public double Z;
        public double Yaw;
    }

    /// <summary>
    /// A circle collider. <see cref="ClimbH"/> (nullable) marks a climbable structure: solid from
    /// the side, walkable on top at that many metres above its base terrain. Null = a plain solid.
    /// </summary>
    public struct Collider
    {
        public double X;
        public double Z;
        public double R;
        public double? ClimbH;

        public Collider(double x, double z, double r, double? climbH = null)
        {
            X = x; Z = z; R = r; ClimbH = climbH;
        }
    }

    /// <summary>A fallen-log obstacle (slows hunters, not Bigfoot). A capsule in the XZ plane.</summary>
    public struct FallenLog
    {
        public double Cx;
        public double Cz;      // centre
        public double Ax;
        public double Az;      // unit axis along the trunk (world XZ)
        public double HalfLen; // half-length of the trunk
        public double R;       // trunk radius
    }
}
