// Who this searcher is, expressed as scoring weights.
//
// UNITY_NOTES [searcher-bots] lists "specialties are dealt but never played" as an open gap: every
// bot got a character with real numbers and then played identically, because the ladder had no place
// to put a preference. Utility scoring does — a weight per action — so this is where the five
// characters finally differ.
//
// THE WEIGHTS ARE DERIVED FROM THE STAT TABLE, NOT INVENTED. Wherever Specialties already exposes a
// multiplier, the weight is a function of it: Eli films from 1.25x range so Eli weights FILM up, Sam
// revives in 0.6x the time so Sam weights REVIVE up. That coupling is the point. If someone retunes
// FilmRangeMul in shared/sim, Eli's behaviour follows automatically, and there is no second copy of
// "who is good at what" to fall out of step with the first — the exact duplication CLAUDE.md warns
// about for the escalation table. Hand-picked numbers appear only where no stat exists to derive
// from (boldness, banking discipline), and those are commented individually.
//
// Reads only Metoh.Sim.Specialties, which is parity-locked and shared with the web build, so nothing
// here can drift from the character sheet in CHARACTER_FUNC_DEV.md.
using Metoh.Sim;

namespace Metoh.Game
{
    /// <summary>Per-character scoring weights. A struct: built once per bot, copied freely.</summary>
    public struct BotProfile
    {
        // Multipliers applied to each action's base score. 1 = "plays this like anyone else".
        public float Film;
        public float Collect;
        public float Revive;
        public float Bank;
        public float Investigate;
        public float Sweep;
        public float Flee;
        public float Recover;

        /// <summary>Abilities this character actually has. The server refuses the rest, and a bot that
        /// asks for one it cannot use parks itself in a channel that never completes.</summary>
        public bool CanFlash;   // Eli
        public bool CanMark;    // Wren
        public bool CanCast;    // Mara

        /// <summary>
        /// How close this searcher will let the Yeti get before fear outranks work, in metres.
        /// Hand-picked: there is no "courage" stat to derive from. Wren is bolder because her
        /// footsteps are half volume (FootstepVolumeMul 0.5) so she genuinely is harder to notice;
        /// Eli is bolder because the flash is a real panic button; Mara is the most cautious because
        /// casting pins her in place for a channel.
        /// </summary>
        public float PanicRange;

        /// <summary>How much proof this one hoards before heading for the duffel. Hand-picked, and
        /// deliberately varied: a team that all bank at 2 travels as a convoy.</summary>
        public int BankAt;

        public static BotProfile For(string specialtyId)
        {
            // Neutral baseline — also what an un-dealt or unknown specialty gets, so a missing deal
            // degrades to "a competent generic searcher" rather than to a bot with zeroed weights
            // that refuses to do anything.
            var p = new BotProfile
            {
                Film = 1f, Collect = 1f, Revive = 1f, Bank = 1f,
                Investigate = 1f, Sweep = 1f, Flee = 1f, Recover = 1f,
                PanicRange = 14f, BankAt = 2,
            };

            if (!Specialties.IsSpecialtyId(specialtyId)) return p;

            // --- derived from the real table ---------------------------------------
            // Filming ability scales with both reach and speed of the capture.
            p.Film = (float)(Specialties.FilmRangeMul(specialtyId) * Specialties.FilmProgressMul(specialtyId));
            // Reading the ground: a longer clue memory and better evidence sight both mean this
            // character gets more out of walking a trail than anyone else does.
            p.Investigate = (float)((Specialties.ClueWindowMul(specialtyId) + Specialties.EvidenceSightMul(specialtyId)) * 0.5);
            p.Collect = (float)Specialties.EvidenceSightMul(specialtyId);
            // A faster revive is a cheaper rescue, so it should win the argument more often. Inverted:
            // the stat is a duration multiplier, so lower is better.
            p.Revive = (float)(1.0 / System.Math.Max(0.35, Specialties.ReviveMul(specialtyId)));
            // Stamina headroom is what lets someone range far from camp and still get home.
            p.Sweep = (float)(Specialties.StaminaMax(specialtyId) / 100.0);
            // Hearing feeds lead-following, so sharp ears bias toward chasing sounds down.
            p.Investigate *= (float)(0.75 + 0.25 * Specialties.HearRangeMul(specialtyId));

            // --- hand-picked, per character ----------------------------------------
            switch (specialtyId)
            {
                case "photo":      // Eli Vance
                    p.CanFlash = true;
                    p.PanicRange = 11f;  // the flash makes standing your ground a real option
                    p.BankAt = 2;
                    break;
                case "tracking":   // Wren Castellano
                    p.CanMark = true;
                    p.PanicRange = 12f;  // half-volume footsteps: genuinely harder to hear
                    p.BankAt = 3;        // ranges furthest, so banks in bigger trips
                    break;
                case "analysis":   // Dr. Mara Okonkwo
                    p.CanCast = true;
                    p.PanicRange = 17f;  // casting is a channel; being caught in one is the worst case
                    p.BankAt = 2;
                    break;
                case "endurance":  // Sam Reyes
                    p.PanicRange = 15f;
                    p.BankAt = 3;        // the pack mule
                    p.Recover = 1.4f;    // best placed to go back for a spilled bag
                    break;
                case "sound":      // Theo Park
                    p.PanicRange = 13f;
                    p.BankAt = 2;
                    break;
            }
            return p;
        }
    }
}
