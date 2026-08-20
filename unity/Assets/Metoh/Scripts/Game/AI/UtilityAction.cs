// Action selection by score instead of by ladder.
//
// WHAT WAS WRONG WITH THE LADDER. A fixed priority chain (FLEE > REVIVE > BANK > FILM > COLLECT >
// INVESTIGATE > EXPLORE) answers every situation with the same ranking, so the bot cannot express
// "the Yeti is close, but only just, and I'm one clue from a full bag" — the rung either fires or it
// doesn't. Every trade-off has to be smuggled into a threshold constant, and thresholds are exactly
// where "if-else AI" comes from. Scoring lets the same nine behaviours combine continuously: a
// slightly-scary Yeti competes with a nearly-full bag and the closer call wins.
//
// THE TWO WAYS THIS GOES WRONG, both designed against here rather than discovered later:
//
//  1. DITHERING. Two actions scoring 0.51 and 0.49 swap every time the numbers wobble, and the bot
//     vibrates between them. Fixed by CommitBonus (the running action gets a handicap in its favour)
//     plus MinDwell (it cannot be replaced at all for a moment), and by only re-deciding at
//     BotBrainTuning.DecideHz rather than every frame. Steering still runs every frame — deciding
//     slowly and moving smoothly are different problems.
//
//  2. ILLEGIBILITY. When a bot does something odd, "why" has to be answerable. Every score is kept
//     for the frame and the F3 overlay prints the top three with their numbers, so a wrong decision
//     is a wrong WEIGHT you can see rather than a mystery. This is why the class stores scores at
//     all instead of just returning an argmax.
using System.Collections.Generic;
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>Shared cadence + feel constants for both brains. One place, so they can't drift.</summary>
    public static class BotBrainTuning
    {
        /// <summary>How often a brain re-decides WHAT to do. Movement still updates every frame.</summary>
        public const float DecideHz = 5f;
        /// <summary>
        /// How often a brain re-senses. Deliberately NOT per frame: a perception pass runs a
        /// line-of-sight test per candidate, and Collision.LineBlocked walks the world's collider
        /// list — with five brains that was thousands of segment tests every frame, all match, which
        /// is exactly the steady cost [perf] warns about. 10 Hz keeps reactions sharp (100 ms is well
        /// inside human reaction time) at a sixth of the work, and the small lag reads as the bot
        /// noticing you rather than knowing you.
        /// </summary>
        public const float PerceiveHz = 10f;
        /// <summary>How often the belief field diffuses. Cheap, but not per-frame cheap ([perf]).</summary>
        public const float BeliefHz = 4f;
        /// <summary>Score handicap in favour of whatever the bot is already doing.</summary>
        public const float CommitBonus = 0.12f;
        /// <summary>Minimum seconds an action holds before anything may replace it.</summary>
        public const float MinDwell = 0.9f;
        /// <summary>Radius treated as "swept" around a bot each belief tick.</summary>
        public const float SweepRadius = 26f;
    }

    public enum BotSkill { Easy = 0, Normal = 1, Hard = 2 }

    /// <summary>
    /// Difficulty as INFORMATION QUALITY, not as cheating or reflexes.
    ///
    /// The usual two dials for a stealth-game monster are both bad. Making it slower is transparent —
    /// the player out-walks it and the dread evaporates. Making it see further is worse, because the
    /// giveaway is not that it finds you, it is that it finds you in a way nothing could have: it
    /// turns toward you through a hillside and the illusion never recovers.
    ///
    /// Scaling belief instead keeps every behaviour identical at every tier. A hard Yeti trusts its
    /// cues more and its picture of you decays more slowly, so it commits to a good guess. An easy one
    /// half-trusts what it heard and its belief blurs outward fast, so it searches the right general
    /// area and keeps missing. Same stalking, same ambushes, same everything — it is just wronger. The
    /// player cannot tell the tiers apart by watching it move, only by how often it is right.
    ///
    /// Costs no behaviour code at all, which is the other reason to do it this way.
    /// </summary>
    public static class BotDifficulty
    {
        public static BotSkill Level = BotSkill.Normal;

        /// <summary>How much a cue (roar, print, call-out) moves the belief field.</summary>
        public static float RumourTrust =>
            Level == BotSkill.Easy ? 0.55f : Level == BotSkill.Hard ? 1.35f : 1f;

        /// <summary>How fast belief blurs outward. Higher = loses the thread sooner.</summary>
        public static float SpreadMul =>
            Level == BotSkill.Easy ? 1.9f : Level == BotSkill.Hard ? 0.65f : 1f;

        public static string Label =>
            Level == BotSkill.Easy ? "EASY   (half-trusts cues, loses the thread fast)"
          : Level == BotSkill.Hard ? "HARD   (trusts cues, holds a picture of you)"
          :                          "NORMAL";
    }

    /// <summary>One scored candidate. A struct — this list is rebuilt several times a second.</summary>
    public struct ScoredAction
    {
        public string Name;
        public float Score;
    }

    /// <summary>
    /// Scores candidates, applies commitment, and remembers the choice. One per brain.
    /// </summary>
    public sealed class UtilityChooser
    {
        private readonly List<ScoredAction> _scores = new List<ScoredAction>(12);
        private float _decideAt;
        private float _chosenAt;

        public string Chosen { get; private set; } = "—";

        /// <summary>Last frame's scores, highest first — read by the F3 overlay. Not a copy.</summary>
        public IReadOnlyList<ScoredAction> Scores => _scores;

        /// <summary>True when enough time has passed to re-run the scoring pass.</summary>
        public bool ShouldDecide => Time.time >= _decideAt;

        public void Begin()
        {
            _scores.Clear();
            _decideAt = Time.time + 1f / BotBrainTuning.DecideHz;
        }

        /// <summary>Offer a candidate. Scores at or below zero are dropped — an action that scores
        /// nothing is not "unlikely", it is inapplicable, and keeping it only clutters the overlay.</summary>
        public void Consider(string name, float score)
        {
            if (score <= 0f) return;
            _scores.Add(new ScoredAction { Name = name, Score = score });
        }

        /// <summary>
        /// Resolve the scoring pass into a choice. Applies the commitment handicap and the dwell
        /// floor, then sorts (descending) so the overlay can print the top few.
        /// </summary>
        public string Resolve()
        {
            if (_scores.Count == 0) { Chosen = "IDLE"; return Chosen; }

            // Insertion sort: the list is ~9 entries, so this beats List.Sort's comparer allocation
            // and delegate call — the sort runs several times a second per bot, all match.
            for (int i = 1; i < _scores.Count; i++)
            {
                ScoredAction key = _scores[i];
                int j = i - 1;
                while (j >= 0 && _scores[j].Score < key.Score) { _scores[j + 1] = _scores[j]; j--; }
                _scores[j + 1] = key;
            }

            string top = _scores[0].Name;
            if (top == Chosen) return Chosen;

            // Is the incumbent even on offer any more?
            //
            // This has to be asked BEFORE the commitment rules, because Consider() drops anything
            // scoring at or below zero — an action that has become INAPPLICABLE is not a low score,
            // it is absent. Looking it up and defaulting to 0f (which is what this did) meant a
            // challenger had to clear 0 + CommitBonus to unseat something that was no longer being
            // offered at all, so any candidate below 0.12 lost to a dead action and the bot sat in it.
            // Commitment is for arguing between things it COULD do; it must never defend a rung that
            // has fallen away.
            float incumbent = 0f;
            bool stillOffered = false;
            for (int i = 0; i < _scores.Count; i++)
                if (_scores[i].Name == Chosen) { incumbent = _scores[i].Score; stillOffered = true; break; }

            if (stillOffered)
            {
                // Still inside the dwell floor: refuse to switch at all.
                if (Time.time - _chosenAt < BotBrainTuning.MinDwell) return Chosen;
                // Outside the floor: the challenger must beat the incumbent by the commitment margin.
                if (_scores[0].Score < incumbent + BotBrainTuning.CommitBonus) return Chosen;
            }

            Bank();              // credit the outgoing action before it stops being current
            Chosen = top;
            _chosenAt = Time.time;
            return Chosen;
        }

        /// <summary>Force a choice — used by hard interrupts (frozen, downed, dragged) that are not
        /// decisions at all and must not be argued with by the commitment rules.</summary>
        public void Force(string name)
        {
            if (Chosen == name) return;
            Bank();
            Chosen = name;
            _chosenAt = Time.time;
        }

        // --- telemetry --------------------------------------------------------------
        // How a play-test turns into numbers. "The bots felt aimless" is not actionable; "they spent
        // 78% of the night in SWEEP and 2% in FILM" is — it says the search is working and the payoff
        // is not. Accumulated ON TRANSITION rather than per frame, so it is genuinely free: one
        // dictionary write each time an action changes, and nothing at all in between.

        private readonly Dictionary<string, float> _timeIn = new Dictionary<string, float>(12);

        /// <summary>Credit the time spent in the outgoing action. Call before Chosen changes.</summary>
        private void Bank()
        {
            float held = Time.time - _chosenAt;
            if (held <= 0f || Chosen == null) return;
            _timeIn.TryGetValue(Chosen, out float t);
            _timeIn[Chosen] = t + held;
        }

        /// <summary>
        /// "SWEEP 62% · INVESTIGATE 18% · FILM 9%" — the night in one line. Called once per match, so
        /// this one is allowed to allocate and sort plainly.
        /// </summary>
        public string Histogram(int top = 5)
        {
            Bank();              // include the action still running, or the last one is always missing
            _chosenAt = Time.time;

            float total = 0f;
            foreach (var kv in _timeIn) total += kv.Value;
            if (total <= 0f) return "no activity";

            var rows = new List<KeyValuePair<string, float>>(_timeIn);
            rows.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new System.Text.StringBuilder(96);
            int lim = Mathf.Min(top, rows.Count);
            for (int i = 0; i < lim; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(rows[i].Key).Append(' ')
                  .Append((rows[i].Value / total * 100f).ToString("0")).Append('%');
            }
            return sb.ToString();
        }

        /// <summary>"FILM 0.82 · FLEE 0.41 · SWEEP 0.19" for the overlay.</summary>
        public string DebugTop(int n = 3)
        {
            if (_scores.Count == 0) return Chosen;
            var sb = new System.Text.StringBuilder(64);
            int lim = Mathf.Min(n, _scores.Count);
            for (int i = 0; i < lim; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(_scores[i].Name).Append(' ').Append(_scores[i].Score.ToString("0.00"));
            }
            return sb.ToString();
        }
    }

    /// <summary>Small scoring helpers, so the weight curves read the same in both brains.</summary>
    public static class Util
    {
        /// <summary>1 at <paramref name="near"/> or closer, 0 at <paramref name="far"/> or beyond.</summary>
        public static float Closeness(float d, float near, float far)
        {
            if (far <= near) return d <= near ? 1f : 0f;
            return Mathf.Clamp01(1f - (d - near) / (far - near));
        }

        /// <summary>Ramps 0..1 across the band, the opposite way round.</summary>
        public static float Ramp(float v, float lo, float hi)
        {
            if (hi <= lo) return v >= hi ? 1f : 0f;
            return Mathf.Clamp01((v - lo) / (hi - lo));
        }

        /// <summary>Sharpen a 0..1 signal so mid values matter less than extremes.</summary>
        public static float Sharpen(float t) => t * t;
    }
}
