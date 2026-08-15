// Where does this bot THINK its quarry is?
//
// This is the single piece that separates the new brains from the old ones, and the reason is worth
// stating plainly: a pursuit function over perfect information always produces a straight line. Both
// old brains had one. The searchers walked to the newest clue (which is wherever the Yeti is standing
// right now) and the Yeti's prowl walked to NearestSearcherRaw(). Neither was ever uncertain, so
// neither ever searched — and "it beelines at me" is what that looks like from the other side.
//
// A belief map replaces the single remembered POINT with a probability field over the whole map, and
// the field is what produces search behaviour for free:
//
//   OBSERVATION   seeing the quarry collapses the field onto one cell. Certainty.
//   NEGATIVE INFO looking somewhere and NOT finding it drives those cells toward zero. This is the
//                 one most often skipped and it is the most valuable: it is the entire reason a bot
//                 stops re-checking ground it just cleared, which is most of what "aimless" looked
//                 like. It costs a multiply per visible cell.
//   DIFFUSION     each tick, probability bleeds outward at the quarry's top speed. A ten-second-old
//                 sighting is no longer a stale waypoint — it is a disc of possibility the right size
//                 for how long ago it was. This is what makes a bot look like it is REASONING about
//                 where something went rather than remembering where it was.
//
// Together those three rules are a discrete Bayesian occupancy filter. Nothing here is clever; the
// value is entirely in applying all three instead of one.
//
// PERFORMANCE (see UNITY_NOTES [perf], which is about a build tuned for an integrated GPU). 32x32 is
// 1024 cells = 4 KB per array. Diffusion touches every cell, so it is NOT free per frame — call
// Tick() on an interval (BotBrainTuning.BeliefHz), never from Update(). No allocation after
// construction: the scratch buffer is a field, and nothing here uses LINQ or foreach over arrays.
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>
    /// A probability field over the world for "where is my quarry". One per bot — see
    /// <see cref="TeamBlackboard"/> for why belief is deliberately NOT shared between teammates.
    /// </summary>
    public sealed class BeliefMap
    {
        /// <summary>Cells per side. 32 over an 800 m world is 25 m cells — coarse enough that the
        /// diffusion is cheap, fine enough that "go to the best cell" is a meaningful instruction.</summary>
        public const int Res = 32;

        private const int Count = Res * Res;

        private readonly float[] _p = new float[Count];      // probability, sums to 1
        private readonly float[] _scratch = new float[Count]; // diffusion double-buffer

        /// <summary>Peak probability at the last <see cref="Best"/> — how sure the bot is. Drives the
        /// difference between "I know where it is" and "it could be anywhere", which is what the
        /// utility layer needs to weigh chasing against sweeping.</summary>
        public float Confidence { get; private set; }

        /// <summary>
        /// Difficulty, applied at the only two points where information enters or leaves the field:
        /// how much a cue is trusted, and how fast the picture blurs. Set from BotDifficulty by the
        /// owning brain each belief tick. Living here rather than at the dozen Rumour call sites means
        /// a new cue added later is scaled automatically instead of being accidentally exempt.
        /// </summary>
        public float RumourTrust = 1f;
        public float SpreadMul = 1f;

        public BeliefMap() { Reset(); }

        public static float CellSize => (float)Sim.World.Size / Res;
        private static float Half => (float)Sim.World.Size * 0.5f;

        // --- indexing ---------------------------------------------------------------

        public static int CellOf(float worldX, float worldZ)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt((worldX + Half) / CellSize), 0, Res - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt((worldZ + Half) / CellSize), 0, Res - 1);
            return cz * Res + cx;
        }

        /// <summary>
        /// Cell centre WITHOUT a terrain lookup. Scoring loops must use this: GetHeight is a real
        /// evaluation, and a scoring pass over all 1024 cells that called it would do 1024 terrain
        /// samples per decision, per bot. Height is only needed for the cell actually chosen.
        /// </summary>
        public static void CentreXZ(int cell, out float x, out float z)
        {
            x = (cell % Res + 0.5f) * CellSize - Half;
            z = (cell / Res + 0.5f) * CellSize - Half;
        }

        public static Vector3 CentreOf(int cell)
        {
            CentreXZ(cell, out float x, out float z);
            var world = WorldBuilder.World;
            float y = world != null ? (float)world.GetHeight(x, z) : 0f;
            return new Vector3(x, y, z);
        }

        public float ProbabilityAt(float worldX, float worldZ) => _p[CellOf(worldX, worldZ)];

        // --- the three update rules -------------------------------------------------

        /// <summary>
        /// A confirmed sighting. Collapses the field: everything else goes to zero, because the one
        /// thing we now know is that the quarry is NOT anywhere else.
        /// </summary>
        public void Sighting(Vector3 at)
        {
            for (int i = 0; i < Count; i++) _p[i] = 0f;
            _p[CellOf(at.x, at.z)] = 1f;
            Confidence = 1f;
        }

        /// <summary>
        /// A soft cue — a heard roar, a call-out, a found print. Adds a blob of probability rather
        /// than collapsing, because these say "around here" and not "here".
        ///
        /// <paramref name="weight"/> is how much to trust it relative to what we already believe; it
        /// is mixed in rather than added so a stream of weak cues can never out-shout a real sighting.
        /// </summary>
        public void Rumour(Vector3 at, float radius, float weight)
        {
            weight = Mathf.Clamp01(weight * RumourTrust);
            if (weight <= 0f) return;

            int cx = Mathf.Clamp(Mathf.FloorToInt((at.x + Half) / CellSize), 0, Res - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt((at.z + Half) / CellSize), 0, Res - 1);
            int r = Mathf.Max(1, Mathf.CeilToInt(radius / CellSize));
            float r2 = r * r;

            // Build the blob into the scratch buffer, then blend. Two passes so the blend weight is
            // applied to a normalised blob and not to raw un-normalised bumps.
            for (int i = 0; i < Count; i++) _scratch[i] = 0f;
            float sum = 0f;
            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= Res) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= Res) continue;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > r2) continue;
                    float v = 1f - Mathf.Sqrt(d2 / r2); // linear falloff to the rim
                    _scratch[z * Res + x] = v;
                    sum += v;
                }
            }
            if (sum <= 0f) return;

            float inv = 1f / sum;
            for (int i = 0; i < Count; i++) _p[i] = _p[i] * (1f - weight) + _scratch[i] * inv * weight;
        }

        /// <summary>
        /// NEGATIVE INFORMATION: we looked here and it was not here.
        ///
        /// The most under-used rule in game AI and the cheapest big win available. Without it a bot
        /// re-visits ground it just cleared, because nothing ever told it that looking was
        /// informative. <paramref name="keep"/> is not zero on purpose — vision through a night
        /// forest is imperfect, and leaving a sliver keeps the field from developing hard holes that
        /// a quarry could hide in forever.
        /// </summary>
        public void Cleared(Vector3 at, float radius, float keep = 0.03f)
        {
            int cx = Mathf.Clamp(Mathf.FloorToInt((at.x + Half) / CellSize), 0, Res - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt((at.z + Half) / CellSize), 0, Res - 1);
            int r = Mathf.Max(0, Mathf.FloorToInt(radius / CellSize));
            float r2 = Mathf.Max(1, r * r);

            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= Res) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= Res) continue;
                    if (dx * dx + dz * dz > r2) continue;
                    _p[z * Res + x] *= keep;
                }
            }
            Renormalise();
        }

        /// <summary>
        /// Advance the field by <paramref name="dt"/> seconds: the quarry could have moved, so
        /// probability spreads to neighbours at up to <paramref name="quarrySpeed"/> m/s.
        ///
        /// Call on an interval, not per frame — see the file header.
        /// </summary>
        public void Tick(float dt, float quarrySpeed)
        {
            // Fraction of a cell the quarry could cross in dt, capped well below 0.5: at 0.5 the
            // stencil sends more probability out of a cell than it keeps and the field oscillates
            // instead of spreading.
            float alpha = Mathf.Clamp(quarrySpeed * SpreadMul * dt / Mathf.Max(1f, CellSize), 0f, 0.35f);
            if (alpha <= 0f) { Renormalise(); return; }

            float stay = 1f - alpha;
            float share = alpha * 0.25f;

            for (int i = 0; i < Count; i++) _scratch[i] = 0f;

            for (int z = 0; z < Res; z++)
            {
                int row = z * Res;
                for (int x = 0; x < Res; x++)
                {
                    float v = _p[row + x];
                    if (v <= 0f) continue;
                    _scratch[row + x] += v * stay;
                    // Edge cells keep what they cannot give away, rather than leaking it out of the
                    // world — probability must be conserved or the whole field decays toward nothing
                    // at the rim and bots stop believing anything is near the map edge.
                    float spill = 0f;
                    if (x > 0)       _scratch[row + x - 1]   += v * share; else spill += share;
                    if (x < Res - 1) _scratch[row + x + 1]   += v * share; else spill += share;
                    if (z > 0)       _scratch[row - Res + x] += v * share; else spill += share;
                    if (z < Res - 1) _scratch[row + Res + x] += v * share; else spill += share;
                    if (spill > 0f) _scratch[row + x] += v * spill;
                }
            }

            System.Array.Copy(_scratch, _p, Count);
            Renormalise();
        }

        // --- queries ----------------------------------------------------------------

        /// <summary>The most likely cell right now. <see cref="Confidence"/> is refreshed alongside.</summary>
        public Vector3 Best()
        {
            int best = 0;
            float bestV = -1f;
            for (int i = 0; i < Count; i++) if (_p[i] > bestV) { bestV = _p[i]; best = i; }
            Confidence = bestV;
            return CentreOf(best);
        }

        /// <summary>
        /// The best cell to actually GO to — probability traded off against the walk, and against
        /// ground a teammate has already claimed or recently swept.
        ///
        /// This is the search-party behaviour in one function. <paramref name="coverage"/> may be
        /// null (the Yeti has no team), in which case it degrades to "likeliest cell, nearest first".
        /// </summary>
        public Vector3 BestSearchTarget(Vector3 from, TeamCoverage coverage, int askerId,
                                        float distanceWeight = 0.0016f)
        {
            int best = -1;
            float bestScore = float.MinValue;
            for (int i = 0; i < Count; i++)
            {
                CentreXZ(i, out float cxw, out float czw); // no terrain sample in the scoring loop
                float dx = cxw - from.x, dz = czw - from.z;
                float d2 = dx * dx + dz * dz;

                // Probability is the signal; distance is a cost in comparable units (the weight is
                // per square metre, so ~25 m of extra walk costs about one point of probability).
                float score = _p[i] * 100f - d2 * distanceWeight;

                // Ground the team has just swept is worth less to sweep again, and ground someone
                // else has claimed is worth much less — that claim is what fans four bots apart.
                if (coverage != null) score -= coverage.RecencyPenalty(i) + coverage.ClaimPenalty(i, askerId);

                if (score > bestScore) { bestScore = score; best = i; }
            }
            return best >= 0 ? CentreOf(best) : from;
        }

        // --- housekeeping -----------------------------------------------------------

        /// <summary>Uniform: "it could be anywhere". The honest state at match start.</summary>
        public void Reset()
        {
            float u = 1f / Count;
            for (int i = 0; i < Count; i++) _p[i] = u;
            Confidence = u;
        }

        private void Renormalise()
        {
            float sum = 0f;
            for (int i = 0; i < Count; i++) sum += _p[i];

            // Everything got cleared — the bot has searched itself into believing the quarry is
            // nowhere, which cannot be true. Falling back to uniform is not a patch over a bug; it is
            // the correct posterior once you have ruled everywhere out, and behaviourally it is
            // exactly right: give up on the stale picture and start sweeping fresh.
            if (sum < 1e-6f) { Reset(); return; }

            float inv = 1f / sum;
            float peak = 0f;
            for (int i = 0; i < Count; i++) { _p[i] *= inv; if (_p[i] > peak) peak = _p[i]; }
            Confidence = peak;
        }

        /// <summary>Raw cell read, for the F3 heatmap only.</summary>
        public float Raw(int cell) => (uint)cell < Count ? _p[cell] : 0f;
    }
}
