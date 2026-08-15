// A per-bot random stream, deliberately separate from every other stream in the project.
//
// WHY THIS EXISTS. UNITY_NOTES [rng-lockstep] is about the WORLD build: the forest loop and
// WorldData.BuildColliders walk the same stream in step, so a single extra draw inside that loop
// shifts every tree after it. AI randomness is a different stream in a different phase, but the two
// are one careless `_rng.NextDouble()` away from being the same mistake — and that failure is
// silent, showing up as a world that disagrees between machines rather than as an error. Brains
// therefore never touch GameManager._rng, WorldBuilder's stream, or Sim.Rng. They come here.
//
// IT ALSO FIXES A REAL BUG, which is the better reason to have it. The old brains drew from
// UnityEngine.Random — one global stream shared by every bot. Four searchers asking that stream for
// a roam direction in the same frame get four draws from one sequence, but every other input to
// their decision was identical (same spawn box, same clue scoring), so the randomness was the ONLY
// thing separating them and it was too weak to do it. Seeding per bot from its ObjectId gives each
// brain a stable, decorrelated stream: bot 3 makes bot-3 decisions every time, and two bots facing
// the same situation reliably choose differently. Divergence by construction, not by luck.
using System;

namespace Metoh.Game
{
    /// <summary>
    /// Per-bot deterministic random. Cheap, allocation-free after construction, and never shared —
    /// see the file header for why both of those matter.
    /// </summary>
    public sealed class BotRandom
    {
        private readonly Random _r;

        public BotRandom(int seed)
        {
            // System.Random with a negative or int.MinValue seed is legal but the abs() below is not,
            // so fold the sign away rather than risk an OverflowException on a hash that happens to
            // land on int.MinValue.
            _r = new Random(seed == int.MinValue ? 1 : Math.Abs(seed));
        }

        /// <summary>Stream for <paramref name="owner"/>, stable across the whole match.</summary>
        public static BotRandom For(HPPlayer owner)
        {
            // ObjectId is unique per spawned player and stable for the object's life, which is exactly
            // the scope we want: the same bot keeps its personality all match, and a re-placed bot
            // between nights does not become a different one.
            //
            // It MUST be hashed rather than used raw. Bots are spawned in a loop, so their ObjectIds
            // are adjacent — and System.Random seeded with adjacent values yields visibly correlated
            // sequences for the first several draws. Feeding the ids in raw would hand four bots
            // near-identical "random" choices, which is precisely the correlation this file exists to
            // break. Avalanche them apart first.
            return new BotRandom(owner != null ? Mix(owner.ObjectId) : 1);
        }

        /// <summary>Integer avalanche (Knuth multiply + xorshift finalisers), folded positive.</summary>
        private static int Mix(int x)
        {
            unchecked
            {
                uint h = (uint)x * 2654435761u;
                h ^= h >> 15;
                h *= 2246822519u;
                h ^= h >> 13;
                return (int)(h & 0x7FFFFFFF);
            }
        }

        /// <summary>0..1.</summary>
        public float Value => (float)_r.NextDouble();

        /// <summary>-1..1.</summary>
        public float Signed => (float)(_r.NextDouble() * 2.0 - 1.0);

        public float Range(float a, float b) => a + (float)_r.NextDouble() * (b - a);

        public int Range(int minInclusive, int maxExclusive) =>
            maxExclusive <= minInclusive ? minInclusive : _r.Next(minInclusive, maxExclusive);

        /// <summary>True with probability <paramref name="p"/>.</summary>
        public bool Chance(float p) => _r.NextDouble() < p;
    }
}
