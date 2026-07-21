namespace HollowPines.Sim
{
    /// <summary>Per-player physics state the sim owns. Presentation (bob, audio, light) lives elsewhere.</summary>
    public sealed class PlayerSimState
    {
        public double X;
        public double Z;
        public double FeetY;    // true feet height (>= GroundY while airborne)
        public double GroundY;  // terrain height under the player
        public double Vy;       // vertical velocity while airborne
        public bool Grounded;
        public double Yaw;      // authoritative look angle (client owns aim, but it drives movement)
        public double Stamina;
        public bool Exhausted;  // true once stamina hits 0, until it recovers past the threshold
        public double Battery;
        public double CurEye;   // eased eye height (lerps toward standing/crouched)
        public bool FlashlightOn;
        public bool IsBigfoot;
        public double EyeHeight; // standing eye height for this role
    }

    /// <summary>One frame of movement intent. This is exactly what the client streams to the host.</summary>
    public struct MoveInput
    {
        public bool W;
        public bool S;
        public bool A;
        public bool D;
        public double Yaw;
        public bool Jump;
        public bool Leap;   // Bigfoot-only: stamina-gated vertical bound
        public bool Climb;  // Bigfoot-only: scale a climbable structure
        public bool Vault;  // searcher-only: hop over a fallen log
        public bool Sprint;
        public bool Crouch;
        public double Dt;
    }

    /// <summary>Transient per-step outputs the client uses for presentation (head-bob, footstep cadence).</summary>
    public struct StepResult
    {
        public bool Moving;
        public bool Sprinting;
    }

    /// <summary>
    /// External multipliers applied to this step. The sim owns no escalation table — the host's
    /// ESCALATION table is the single source of truth, replicated to clients; both sides compose
    /// these from it (plus the post-incapacitation slow) and pass them in.
    /// </summary>
    public struct StepModifiers
    {
        public double SpeedMul;        // slow factor * per-night Bigfoot speed escalation (1 = baseline)
        public double BatteryDrainMul; // per-night flashlight drain escalation
        public double StaminaDrainMul; // per-night sprint drain escalation (× the Endurance specialty's reduction)
        public double? StaminaMax;     // per-player stamina ceiling (Sam's Endurance raises it; null => 100)
    }

    public static class Movement
    {
        // Movement clamps just inside the world edge (matches the old LocalPlayer `half = 398`).
        private const double PlayerWorldHalf = 398;

        /// <summary>
        /// Advance one player by a single input. Pure w.r.t. (state, input, world, mods); mutates
        /// <paramref name="st"/> in place (callers clone when they need history). Ported line-for-line
        /// from shared/sim/movement.ts so client prediction and host authority agree.
        /// </summary>
        public static StepResult StepPlayer(PlayerSimState st, MoveInput input, GameWorld world, StepModifiers mods)
        {
            double dt = input.Dt;
            st.Yaw = input.Yaw;

            // Movement is relative to yaw only (looking up/down doesn't fly you around).
            double fx = -System.Math.Sin(st.Yaw);
            double fz = -System.Math.Cos(st.Yaw);
            double rx = System.Math.Cos(st.Yaw);
            double rz = -System.Math.Sin(st.Yaw);
            double wx = 0;
            double wz = 0;
            if (input.W) { wx += fx; wz += fz; }
            if (input.S) { wx -= fx; wz -= fz; }
            if (input.D) { wx += rx; wz += rz; }
            if (input.A) { wx -= rx; wz -= rz; }

            bool crouching = input.Crouch;
            bool moving = wx * wx + wz * wz > 0;
            bool sprinting = moving && input.Sprint && !st.Exhausted && !crouching;
            double speed = (sprinting ? Player.SprintSpeed : Player.WalkSpeed)
                           * (st.IsBigfoot ? Player.BigfootSpeedMul : 1) * mods.SpeedMul;
            if (crouching) speed *= Player.CrouchSpeedMul;

            // Terrain obstacles: fallen logs BLOCK hunters (see the push-out below); the lake slows
            // everyone. The only way through a log on foot is a VAULT — a stamina-gated hop over it.
            // Reach is measured with a padded radius because the push-out means a grounded hunter can
            // never actually stand inside a log: the prompt has to fire from alongside it, not on top.
            if (!st.IsBigfoot && st.Grounded && input.Vault && st.Stamina >= Player.VaultStaminaCost)
            {
                if (Collision.LogOverlap(world.FallenLogs, st.X, st.Z, Player.Radius + Player.VaultReach) > 0)
                {
                    st.Vy = Player.VaultHopSpeed;
                    st.Grounded = false;
                    st.Stamina -= Player.VaultStaminaCost;
                }
            }
            double lakeDep = Collision.LakeDepth(st.X, st.Z);
            if (lakeDep > 0)
            {
                speed *= SimMath.Lerp(1, st.IsBigfoot ? Player.LakeBigfootFactor : Player.LakeHunterFactor, lakeDep);
            }

            if (moving)
            {
                // Matches the old THREE wish.normalize()+addScaledVector order exactly.
                double len = System.Math.Sqrt(wx * wx + wz * wz);
                double move = speed * dt;
                st.X += (wx / len) * move;
                st.Z += (wz / len) * move;
            }

            // Keep inside the world, push out of trees, then sit on the terrain.
            double half = PlayerWorldHalf;
            st.X = SimMath.Clamp(st.X, -half, half);
            st.Z = SimMath.Clamp(st.Z, -half, half);
            // Logs first, so the position the tree pass and the auto-step both work from is already
            // log-legal — otherwise auto-step could hand the player back a spot inside a trunk.
            if (!st.IsBigfoot && st.Grounded)
            {
                var cleared = Collision.ResolveLogs(world.FallenLogs, st.X, st.Z, Player.Radius);
                st.X = cleared.X;
                st.Z = cleared.Z;
            }
            // Save intended position (post-clamp) so the step check can compare it.
            double ix = st.X;
            double iz = st.Z;
            // Collision is climb-aware: a Bigfoot at/above a climbable's top isn't pushed out.
            var resolved = Collision.ResolveCollision(world.Colliders, ix, iz, Player.Radius, st.FeetY, world.GetHeight);
            bool wasPushed = (resolved.X - ix) * (resolved.X - ix) + (resolved.Z - iz) * (resolved.Z - iz) > 1e-4;
            st.X = resolved.X;
            st.Z = resolved.Z;
            // Ground rises to a structure's top when standing over its footprint (perched), else terrain.
            st.GroundY = Collision.GroundHeightAt(world.Climbables, world.GetHeight, st.X, st.Z);

            // Auto-step: if a collider pushed us back but the terrain at the intended spot is only a
            // small rise, lift over it rather than sliding around the obstacle.
            if (wasPushed && st.Grounded && moving)
            {
                double destGY = world.GetHeight(ix, iz);
                double rise = destGY - st.GroundY;
                if (rise >= 0 && rise <= Player.StepHeight)
                {
                    st.X = ix;
                    st.Z = iz;
                    st.GroundY = destGY;
                }
            }

            // Vertical: climb (Bigfoot scales a structure) takes precedence, then leap, then jump + gravity.
            // FeetY is the true feet height; >= GroundY while airborne or perched on a structure.
            ClimbSupportResult? climb = st.IsBigfoot && input.Climb && !crouching
                ? Collision.ClimbSupport(world.Climbables, world.GetHeight, st.X, st.Z, Player.Radius, Player.ClimbReach)
                : null;
            if (climb.HasValue && st.Stamina > 0)
            {
                // Scale the surface: rise toward its top (capped), clinging to the side (XZ pinned by
                // the push-out) and draining stamina so Bigfoot can't hang forever.
                st.FeetY = System.Math.Min(climb.Value.Top, st.FeetY + Player.ClimbSpeed * dt);
                st.Vy = 0;
                st.Grounded = false;
                st.Stamina = System.Math.Max(0, st.Stamina - Player.ClimbStaminaDrain * dt);
            }
            else
            {
                // Leap is a taller, stamina-gated bound; it takes precedence over a normal jump for Bigfoot.
                if (st.Grounded && !crouching && st.IsBigfoot && input.Leap && st.Stamina >= Player.LeapStaminaCost)
                {
                    st.Vy = Player.LeapSpeed;
                    st.Grounded = false;
                    st.Stamina -= Player.LeapStaminaCost;
                }
                else if (st.Grounded && input.Jump && !crouching)
                {
                    st.Vy = Player.JumpSpeed;
                    st.Grounded = false;
                }
                if (st.Grounded)
                {
                    // Ride terrain/structure-top as it changes; a big drop (walking off a ledge) starts a fall.
                    if (st.GroundY < st.FeetY - Player.StepHeight)
                    {
                        st.Grounded = false;
                        st.Vy = 0;
                    }
                    else
                    {
                        st.FeetY = st.GroundY;
                    }
                }
                else
                {
                    st.Vy -= Player.Gravity * dt;
                    st.FeetY += st.Vy * dt;
                    if (st.FeetY <= st.GroundY)
                    {
                        st.FeetY = st.GroundY;
                        st.Vy = 0;
                        st.Grounded = true;
                    }
                }
            }

            // Crouch: ease the eye height toward standing or crouched.
            double targetEye = crouching ? st.EyeHeight * Player.CrouchFactor : st.EyeHeight;
            st.CurEye += (targetEye - st.CurEye) * System.Math.Min(1, dt * Player.EyeLerp);

            // Resources (drains scaled by the host-driven per-night escalation multipliers).
            // Hitting 0 stamina exhausts you: no sprinting until it recovers past a threshold. While
            // *holding* climb against a surface, suppress regen (even at 0 stamina) or the gate never
            // bites. Release the climb to recover. Regen clamps to the per-player ceiling (Endurance raises it).
            double staminaMax = mods.StaminaMax ?? 100;
            if (sprinting) st.Stamina = System.Math.Max(0, st.Stamina - Player.StaminaDrainPerSec * mods.StaminaDrainMul * dt);
            else if (!climb.HasValue) st.Stamina = System.Math.Min(staminaMax, st.Stamina + Player.StaminaRegenPerSec * dt);
            if (st.Stamina <= 0) st.Exhausted = true;
            else if (st.Exhausted && st.Stamina >= Player.StaminaRecover) st.Exhausted = false;

            if (st.FlashlightOn)
            {
                st.Battery = System.Math.Max(0, st.Battery - Player.BatteryDrainPerSec * mods.BatteryDrainMul * dt);
                if (st.Battery <= 0) st.FlashlightOn = false;
            }

            return new StepResult { Moving = moving, Sprinting = sprinting };
        }
    }
}
