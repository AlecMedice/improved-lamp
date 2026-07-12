using System;
using System.Collections.Generic;

namespace HollowPines.Sim
{
    /// <summary>
    /// Searcher character specialties — pure data, shared by client + host. Ported from
    /// shared/sim/specialties.ts. Enabling layer: identity (ids + names), the tunables, the random
    /// deal, and typed getters. Ids are plain strings (as in TS) so the getters accept "", unknown
    /// ids, and "bigfoot" and fall back to the baseline.
    /// </summary>
    public static class Specialties
    {
        /// <summary>All five ids, in a stable order (the shuffle in <see cref="DealSpecialties"/> randomises the deal).</summary>
        public static readonly string[] SpecialtyIds = { "analysis", "photo", "tracking", "sound", "endurance" };

        public static bool IsSpecialtyId(string x)
        {
            if (x == null) return false;
            foreach (var id in SpecialtyIds) if (id == x) return true;
            return false;
        }

        /// <summary>Display name per specialty (HUD, nametag, dusk briefing).</summary>
        public static readonly Dictionary<string, string> CharacterName = new Dictionary<string, string>
        {
            { "analysis", "Dr. Mara Okonkwo" },
            { "photo", "Eli Vance" },
            { "tracking", "Wren Castellano" },
            { "sound", "Theo Park" },
            { "endurance", "Sam Reyes" },
        };

        // Flat numeric tunables per specialty (nested objects like flash/mark are read directly in TS
        // and aren't needed by these getters, so they're intentionally omitted here). Mirrors SPECIALTIES.
        private static readonly Dictionary<string, Dictionary<string, double>> Table =
            new Dictionary<string, Dictionary<string, double>>
        {
            { "analysis", new Dictionary<string, double>() },
            { "photo", new Dictionary<string, double> { { "filmRangeMul", 1.25 } } },
            { "tracking", new Dictionary<string, double>
                { { "clueWindowMul", 1.5 }, { "evidenceSightMul", 2.0 }, { "footstepVolumeMul", 0.5 } } },
            { "sound", new Dictionary<string, double>
                { { "hearRangeMul", 1.8 }, { "roarDirPersistSec", 10 }, { "filmProgressMul", 1.15 } } },
            { "endurance", new Dictionary<string, double>
                { { "reviveSecondsMul", 0.6 }, { "staminaMax", 150 }, { "staminaDrainMul", 0.85 } } },
        };

        private static double SpecNum(string id, string key, double dflt)
        {
            if (id != null && Table.TryGetValue(id, out var fields) && fields.TryGetValue(key, out var v))
                return v;
            return dflt;
        }

        public static double ReviveMul(string id) => SpecNum(id, "reviveSecondsMul", 1);       // Sam: 0.6
        public static double StaminaMax(string id) => SpecNum(id, "staminaMax", 100);            // Sam: 150
        public static double StaminaDrainMul(string id) => SpecNum(id, "staminaDrainMul", 1);    // Sam: 0.85
        public static double FilmProgressMul(string id) => SpecNum(id, "filmProgressMul", 1);    // Theo: 1.15
        public static double FilmRangeMul(string id) => SpecNum(id, "filmRangeMul", 1);          // Eli: 1.25
        public static double ClueWindowMul(string id) => SpecNum(id, "clueWindowMul", 1);        // Wren: 1.5
        public static double EvidenceSightMul(string id) => SpecNum(id, "evidenceSightMul", 1);  // Wren: 2.0
        public static double HearRangeMul(string id) => SpecNum(id, "hearRangeMul", 1);          // Theo: 1.8
        public static double RoarDirPersistSec(string id) => SpecNum(id, "roarDirPersistSec", 0); // Theo: 10
        public static double FootstepVolumeMul(string id) => SpecNum(id, "footstepVolumeMul", 1); // Wren: 0.5

        /// <summary>
        /// Deal a specialty to each searcher. Distinct where the pool allows (&lt;=5 searchers => all
        /// distinct); a forced id is honoured and removed from the random pool so it can't collide.
        /// Pure given <paramref name="rand"/> (inject a seeded rng in tests to reproduce a deal).
        /// Ported to consume <paramref name="rand"/> in the exact same order as the TS shuffle.
        /// </summary>
        public static Dictionary<string, string> DealSpecialties(
            IList<string> searcherSids, IDictionary<string, string> forced, Func<double> rand)
        {
            var pool = new List<string>(SpecialtyIds);
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = (int)System.Math.Floor(rand() * (i + 1));
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var outp = new Dictionary<string, string>();
            // Honour forced picks first, consuming them from the pool so random deals don't duplicate them.
            foreach (var sid in searcherSids)
            {
                string f = null;
                if (forced != null) forced.TryGetValue(sid, out f);
                if (!string.IsNullOrEmpty(f))
                {
                    outp[sid] = f;
                    int k = pool.IndexOf(f);
                    if (k >= 0) pool.RemoveAt(k);
                }
            }
            // Deal the rest from the shuffled pool (wraps to a random id only if we exceed five searchers).
            foreach (var sid in searcherSids)
            {
                if (outp.ContainsKey(sid)) continue;
                if (pool.Count > 0)
                {
                    outp[sid] = pool[0];
                    pool.RemoveAt(0);
                }
                else
                {
                    outp[sid] = SpecialtyIds[(int)System.Math.Floor(rand() * SpecialtyIds.Length)];
                }
            }
            return outp;
        }
    }
}
