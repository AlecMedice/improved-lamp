namespace HollowPines.Sim
{
    /// <summary>
    /// Dependency-free math helpers shared by client and server sim. Formulas match the
    /// TypeScript sim (and three.js MathUtils) exactly so the port stays numerically identical
    /// to the original Environment/LocalPlayer code (no visual or reconciliation drift).
    ///
    /// Everything is <see cref="double"/> because the TypeScript sim uses `number` (IEEE-754
    /// double) throughout — porting to float would diverge on the very first collision push-out.
    /// </summary>
    public static class SimMath
    {
        /// <summary>three.js MathUtils.clamp</summary>
        public static double Clamp(double value, double min, double max)
        {
            return System.Math.Max(min, System.Math.Min(max, value));
        }

        /// <summary>three.js MathUtils.lerp — note the (1-t)*x + t*y form, kept for float parity.</summary>
        public static double Lerp(double x, double y, double t)
        {
            return (1 - t) * x + t * y;
        }

        /// <summary>three.js MathUtils.smoothstep</summary>
        public static double Smoothstep(double x, double min, double max)
        {
            if (x <= min) return 0;
            if (x >= max) return 1;
            x = (x - min) / (max - min);
            return x * x * (3 - 2 * x);
        }

        /// <summary>
        /// JavaScript's <c>Math.round</c> (round half toward +infinity), NOT .NET's banker's
        /// rounding. Used for cave coordinates so they land on the same integers as the TS sim.
        /// </summary>
        public static double JsRound(double x)
        {
            return System.Math.Floor(x + 0.5);
        }

        /// <summary>
        /// Naive hypot matching the sim's distance math. (JS <c>Math.hypot</c> uses a scaled
        /// algorithm; for our coordinate ranges the naive form agrees, and the host is
        /// authoritative regardless — see UNITY_MIGRATION.md "Determinism".)
        /// </summary>
        public static double Hypot(double x, double y)
        {
            return System.Math.Sqrt(x * x + y * y);
        }
    }
}
