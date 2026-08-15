// What the CPU searchers tell each other — and, just as importantly, what they don't.
//
// THE LINE THIS DRAWS IS THE WHOLE DESIGN. It is tempting to give a bot team one shared belief map:
// it is less code, and the bots instantly look competent. It is also wrong, and wrong in a way that
// reads on screen. A team with pooled perception is a hive mind — four bodies with one pair of eyes,
// converging on things no individual could know. That is the same legibility failure as omniscience
// (UNITY_NOTES [searcher-bots] records the original version of this bug: an unbounded revive scan
// meant one grab summoned the entire team from across the valley and handed the Yeti all of them).
//
// So the split is:
//   BELIEF  is PRIVATE. Each bot owns a BeliefMap built only from what it personally sensed.
//   INTENT  is SHARED. Claims, call-outs and swept ground go here — the things a real team would say
//           out loud. "I'll take the north ridge", "Wren's down by the tarn", "I've got this one".
//
// Everything a bot learns from this object is therefore something a person could have said into a
// radio, which is the test any new field here has to pass.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>
    /// Ground the team has swept, and who has claimed what. Shared and legitimate: dividing a search
    /// is exactly the thing a search party coordinates out loud.
    /// </summary>
    public sealed class TeamCoverage
    {
        private const int Res = BeliefMap.Res;
        private const int Count = Res * Res;

        private readonly float[] _sweptAt = new float[Count];
        private readonly int[] _claimedBy = new int[Count];
        private readonly float[] _claimUntil = new float[Count];

        /// <summary>Seconds before swept ground is worth sweeping again. Shorter than a night, so a
        /// long match re-covers the map rather than declaring it done and idling.</summary>
        public const float SweepMemory = 75f;

        /// <summary>How strongly a claim repels other bots. Large enough to dominate any plausible
        /// probability-minus-distance score, because a half-honoured claim is worse than none — two
        /// bots half-avoiding each other still end up walking together.</summary>
        private const float ClaimRepel = 400f;

        public void MarkSwept(Vector3 at, float radius)
        {
            int r = Mathf.Max(0, Mathf.FloorToInt(radius / BeliefMap.CellSize));
            int c = BeliefMap.CellOf(at.x, at.z);
            int cx = c % Res, cz = c / Res;
            float now = Time.time;
            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= Res) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= Res) continue;
                    if (dx * dx + dz * dz > r * r) continue;
                    _sweptAt[z * Res + x] = now;
                }
            }
        }

        /// <summary>Cost of re-sweeping ground someone covered recently; fades to nothing over
        /// <see cref="SweepMemory"/>.</summary>
        public float RecencyPenalty(int cell)
        {
            float age = Time.time - _sweptAt[cell];
            if (age >= SweepMemory) return 0f;
            return Mathf.Lerp(60f, 0f, age / SweepMemory);
        }

        /// <summary>Reserve a cell (and its neighbourhood) for one bot for a while.</summary>
        public void Claim(Vector3 at, int botId, float seconds, float radius)
        {
            int r = Mathf.Max(0, Mathf.FloorToInt(radius / BeliefMap.CellSize));
            int c = BeliefMap.CellOf(at.x, at.z);
            int cx = c % Res, cz = c / Res;
            float until = Time.time + seconds;
            for (int dz = -r; dz <= r; dz++)
            {
                int z = cz + dz;
                if (z < 0 || z >= Res) continue;
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx;
                    if (x < 0 || x >= Res) continue;
                    if (dx * dx + dz * dz > r * r) continue;
                    int i = z * Res + x;
                    // Never steal a live claim from someone else; first come wins until it lapses.
                    if (_claimedBy[i] != 0 && _claimedBy[i] != botId && Time.time < _claimUntil[i]) continue;
                    _claimedBy[i] = botId;
                    _claimUntil[i] = until;
                }
            }
        }

        public float ClaimPenalty(int cell, int askerId)
        {
            if (_claimedBy[cell] == 0 || _claimedBy[cell] == askerId) return 0f;
            return Time.time < _claimUntil[cell] ? ClaimRepel : 0f;
        }

        public void Reset()
        {
            for (int i = 0; i < Count; i++) { _sweptAt[i] = -999f; _claimedBy[i] = 0; _claimUntil[i] = 0f; }
        }
    }

    /// <summary>
    /// The searchers' shared intent. Host-side only and reset per match — nothing here replicates,
    /// because bots exist only on the host (see HPPlayer.ServerBecomeBot).
    /// </summary>
    public static class TeamBlackboard
    {
        public static readonly TeamCoverage Coverage = new TeamCoverage();

        // --- task claims ------------------------------------------------------------
        // One job, one owner. Without this, four bots hearing one call-out all walk to the same body,
        // which is the exact failure [searcher-bots] describes: the rescue becomes the ambush.

        private struct Claim { public int OwnerId; public float Until; }

        private static readonly Dictionary<int, Claim> _reviveClaims = new Dictionary<int, Claim>();
        private static readonly Dictionary<int, Claim> _pileClaims = new Dictionary<int, Claim>();
        private static readonly Dictionary<int, Claim> _overwatchClaims = new Dictionary<int, Claim>();

        /// <summary>
        /// Try to become the one who handles <paramref name="jobId"/>. Returns true if this bot owns
        /// it. Re-asserting your own claim always succeeds, so the owner keeps it by continuing to
        /// want it and loses it by going quiet for <paramref name="holdSeconds"/> — no explicit
        /// release, which means a bot that gets grabbed mid-rescue frees the job automatically.
        /// </summary>
        private static bool TryClaim(Dictionary<int, Claim> book, int jobId, int botId, float holdSeconds)
        {
            if (book.TryGetValue(jobId, out Claim c) && c.OwnerId != botId && Time.time < c.Until) return false;
            book[jobId] = new Claim { OwnerId = botId, Until = Time.time + holdSeconds };
            return true;
        }

        /// <summary>Claim the rescue of a downed teammate.</summary>
        public static bool ClaimRevive(int victimObjectId, int botId) =>
            TryClaim(_reviveClaims, victimObjectId, botId, 3f);

        /// <summary>
        /// Claim the OVERWATCH slot on a rescue: the second searcher, who does not revive but stands
        /// off with a torch on the Yeti. A rescue with one reviver and one dazzler is a play; a rescue
        /// with four bodies in a heap is a free double grab.
        /// </summary>
        public static bool ClaimOverwatch(int victimObjectId, int botId) =>
            TryClaim(_overwatchClaims, victimObjectId, botId, 3f);

        /// <summary>Claim a spilled proof pile, so two bots don't both cross the map for one bag.</summary>
        public static bool ClaimPile(int pileObjectId, int botId) =>
            TryClaim(_pileClaims, pileObjectId, botId, 4f);

        // --- call-outs --------------------------------------------------------------

        /// <summary>Where the Yeti was last reported by ANY teammate, and when. This is a radio call,
        /// not shared perception: it carries one position and a timestamp, and every bot that acts on
        /// it still has to go and look.</summary>
        public static Vector3 LastYetiCallout { get; private set; }
        public static float LastYetiCalloutAt { get; private set; } = -999f;

        public static void CallOutYeti(Vector3 at)
        {
            LastYetiCallout = at;
            LastYetiCalloutAt = Time.time;
        }

        public static bool CalloutFresh(float within) => Time.time - LastYetiCalloutAt < within;

        public static void ResetMatch()
        {
            Coverage.Reset();
            _reviveClaims.Clear();
            _pileClaims.Clear();
            _overwatchClaims.Clear();
            LastYetiCalloutAt = -999f;
        }
    }
}
