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

    /// <summary>
    /// A trail through the forest: a polyline of centreline points walked outward from camp,
    /// plus the half-width of the tree-free corridor around it. Mirrors TS `ForestPath`.
    /// (A class, not a struct: it owns a list and gets passed around by reference.)
    /// </summary>
    public sealed class ForestPath
    {
        /// <summary>Centreline points, camp end first.</summary>
        public System.Collections.Generic.List<Vec2> Pts = new System.Collections.Generic.List<Vec2>();
        /// <summary>Half-width of the cleared corridor (m). Trees inside it are skipped.</summary>
        public double HalfWidth;
    }

    /// <summary>A fallen-log obstacle (solid to hunters on foot, not to Bigfoot). A capsule in the XZ plane.</summary>
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
