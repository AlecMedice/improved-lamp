namespace HollowPines.Sim
{
    /// <summary>
    /// Canonical sim constants, shared by client and server so movement/collision is computed
    /// from one source of truth (no drift -> no reconciliation rubber-banding). Ported verbatim
    /// from shared/sim/constants.ts. Per-night escalation is NOT here: the host owns the
    /// ESCALATION table and passes multipliers into StepPlayer via <see cref="StepModifiers"/>.
    /// </summary>
    public static class World
    {
        public const double Size = 800;      // full extent; world spans -400..400 on x/z
        public const int Segments = 160;     // terrain mesh resolution (render-only, kept whole)
        // Placement draws (not the surviving trunk count): the camp clearing, the lake and the trail
        // corridors all reject candidates, so roughly 2,300 of these actually stand. That lands near
        // 8 m between trunks — a forest you can lose someone in, with lanes you can still film down.
        public const int TreeCount = 2500;
        public const double HillHeight = 14;
        public const uint Seed = 1337;       // the DEFAULT forest; a match replaces it with a per-session seed
        public const double BaseCampRadius = 16;

        /// <summary>Half the world extent; movement clamps just inside this.</summary>
        public const double Half = Size / 2; // 400
    }

    /// <summary>Fixed simulation step (s). Matches the host's 20 Hz tick so one input = one tick.</summary>
    public static class Sim
    {
        public const double Dt = 1.0 / 20.0;
    }

    public static class Player
    {
        public const double EyeHeight = 1.7;         // searcher; Bigfoot is taller
        public const double Radius = 0.4;            // collision radius
        public const double WalkSpeed = 5;
        public const double SprintSpeed = 8.5;
        public const double BigfootSpeedMul = 1.22;  // Bigfoot is faster
        public const double MouseSensitivity = 0.0022;
        public const double BatteryDrainPerSec = 1.4; // while flashlight is on
        public const double StaminaDrainPerSec = 18;  // while sprinting
        public const double StaminaRegenPerSec = 12;
        public const double StaminaRecover = 35;      // once exhausted (0), must regen to this before sprinting again
        public const double SlowFactor = 0.75;        // movement multiplier while slowed (after incapacitation)
        public const double BobFreqWalk = 1.8;
        public const double BobFreqSprint = 2.6;
        public const double BobAmpWalk = 0.05;
        public const double BobAmpSprint = 0.09;
        public const double StepIntervalWalk = 0.52;
        public const double StepIntervalSprint = 0.32;
        public const double StepHeight = 0.75;        // max terrain rise auto-stepped over (m)
        public const double VaultReach = 0.75;        // how far from a log's surface a vault can start
                                                      // (logs are solid, so you can never stand inside one)
        public const double VaultHopSpeed = 4.6;      // upward velocity when a hunter vaults a log
        public const double VaultStaminaCost = 12;    // stamina spent to vault a log (the only way through)
        public const double ClimbSpeed = 3.6;         // Bigfoot's vertical climb rate (m/s)
        public const double ClimbStaminaDrain = 22;   // stamina/sec while climbing/clinging
        public const double ClimbReach = 0.7;         // how far past a structure's edge Bigfoot can grab on (m)
        public const double LakeHunterFactor = 0.28;  // hunter speed multiplier while wading
        public const double LakeBigfootFactor = 0.72; // Bigfoot speed multiplier while wading
        public const double JumpSpeed = 5.2;          // initial upward velocity on jump (m/s)
        public const double LeapSpeed = 9.5;          // Bigfoot's leap: initial upward velocity (m/s)
        public const double LeapStaminaCost = 30;     // stamina spent per leap
        public const double Gravity = 16;             // downward acceleration while airborne (m/s^2)
        public const double CrouchFactor = 0.55;      // eye-height multiplier while crouched
        public const double CrouchSpeedMul = 0.5;     // movement-speed multiplier while crouched
        public const double EyeLerp = 12;             // how fast eye height eases between standing/crouched
    }

    /// <summary>Cave fast-travel rules — shared because the host validates `caveTravel` with them.</summary>
    public static class CaveRules
    {
        public const double TriggerRadius = 6;   // how close Bigfoot must be to a mouth to use it
        public const double TravelCooldown = 2.0;
        public const double EmergeOffset = 8;    // metres toward map centre a traveller emerges (outside the horseshoe)
    }

    /// <summary>
    /// Seconds before a dropped clue (footprint/branch) goes cold and disappears. Host-owned truth;
    /// the client fades meshes on the same window so the visible trail can't drift from the replicated one.
    /// </summary>
    public static class Clue
    {
        public const double Lifetime = 50;
    }

    /// <summary>Cave-network generation tuning (seed-derived, identical on client + host).</summary>
    public static class CaveGen
    {
        public const uint SeedXor = 0xca7e5eed; // mixes World.Seed so caves don't correlate with trees
        public const int Count = 5;
        public const double MinRadius = 150;     // caves sit on the outer ring, 150..340 m from centre
        public const double RadiusSpan = 190;
        public const double MinSpacing = 120;    // at least this far apart
        public const int MaxAttempts = 400;
    }

    /// <summary>Forest-trail generation (seed-derived, identical on client + host). See <see cref="Paths"/>.</summary>
    public static class PathGen
    {
        public const uint SeedXor = 0x7a11b1a2; // mixes World.Seed so trails don't correlate with trees or caves
        public const int Count = 4;             // trailheads leaving the camp clearing
        public const double StepLength = 26;    // metres per polyline segment
        public const int MaxSteps = 40;         // hard cap; a trail normally exits the map well before this
        public const double Jitter = 0.42;      // max heading change per step (rad) — the meander
        public const double MinHalfWidth = 2.6; // corridor half-width: wide enough for two abreast
        public const double HalfWidthSpan = 1.8; // ...plus up to this, so trails differ in how open they feel
        public const double TreeMargin = 1.2;   // extra clearance trees keep off the corridor edge
    }
}
