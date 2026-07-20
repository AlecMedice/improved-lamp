using System.Collections.Generic;

namespace HollowPines.Sim
{
    /// <summary>
    /// Everything the movement sim needs to run, built once from a seed. Equivalent to the TS
    /// `World` type in shared/sim/index.ts (renamed to avoid colliding with the <see cref="World"/>
    /// constants class).
    /// </summary>
    public sealed class GameWorld
    {
        public uint Seed;
        public IReadOnlyList<Cave> Caves;
        public HeightFn GetHeight;
        public List<Collider> Colliders;
        public List<Collider> Climbables; // subset of colliders with a ClimbH (structures Bigfoot can scale/perch on)
        public List<FallenLog> FallenLogs;

        /// <summary>Build the full deterministic world for a seed (terrain + caves + colliders + logs).</summary>
        public static GameWorld MakeWorld(uint seed)
        {
            // Fully qualified: the instance field `Caves` would otherwise shadow the type here.
            var caves = HollowPines.Sim.Caves.GenerateCaves(seed);
            var colliders = WorldData.BuildColliders(seed, caves);
            var climbables = new List<Collider>();
            foreach (var col in colliders)
                if (col.ClimbH.HasValue) climbables.Add(col);

            return new GameWorld
            {
                Seed = seed,
                Caves = caves,
                GetHeight = Terrain.MakeTerrain(seed),
                Colliders = colliders,
                Climbables = climbables,
                FallenLogs = WorldData.BuildFallenLogs(),
            };
        }
    }
}
