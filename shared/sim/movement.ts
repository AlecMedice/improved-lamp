import { PLAYER } from "./constants";
import { clamp, lerp } from "./math";
import { resolveCollision, logOverlap, lakeDepth, groundHeightAt, climbSupport } from "./collision";
import type { World } from "./index";

/** Per-player physics state the sim owns. Presentation (bob, audio, light) lives in LocalPlayer. */
export type PlayerSimState = {
  x: number;
  z: number;
  feetY: number; // true feet height (>= groundY while airborne)
  groundY: number; // terrain height under the player
  vy: number; // vertical velocity while airborne
  grounded: boolean;
  yaw: number; // authoritative look angle (client owns aim, but it drives movement)
  stamina: number;
  exhausted: boolean; // true once stamina hits 0, until it recovers past the threshold
  battery: number;
  curEye: number; // eased eye height (lerps toward standing/crouched)
  flashlightOn: boolean;
  isBigfoot: boolean;
  eyeHeight: number; // standing eye height for this role
};

/** One frame of movement intent. This is exactly what the client streams to the server. */
export type MoveInput = {
  w: boolean;
  s: boolean;
  a: boolean;
  d: boolean;
  yaw: number;
  jump: boolean;
  leap: boolean; // Bigfoot-only: stamina-gated vertical bound (client sets it only for Bigfoot)
  climb: boolean; // Bigfoot-only: scale a climbable structure (client sets it only for Bigfoot)
  vault: boolean; // searcher-only: hop over a fallen log (client sets it only for hunters)
  sprint: boolean;
  crouch: boolean;
  dt: number;
};

/** Transient per-step outputs the client uses for presentation (head-bob, footstep cadence). */
export type StepResult = { moving: boolean; sprinting: boolean };

/**
 * External multipliers applied to this step. The sim owns no escalation table — the server's
 * ESCALATION (ForestRoom.ts) is the single source of truth, replicated to clients; both sides
 * compose these from it (plus the post-incapacitation slow) and pass them in.
 */
export type StepModifiers = {
  speedMul: number; // slow factor * per-night Bigfoot speed escalation (1 = baseline)
  batteryDrainMul: number; // per-night flashlight drain escalation
  staminaDrainMul: number; // per-night sprint drain escalation
};

/**
 * Advance one player by a single input. Pure w.r.t. (state, input, world, mods);
 * mutates `st` in place (callers clone when they need history). Ported line-for-line from the
 * old LocalPlayer.update physics so client prediction and server authority agree bit-for-bit.
 */
export function stepPlayer(st: PlayerSimState, input: MoveInput, world: World, mods: StepModifiers): StepResult {
  const dt = input.dt;
  st.yaw = input.yaw;

  // Movement is relative to yaw only (looking up/down doesn't fly you around).
  const fx = -Math.sin(st.yaw);
  const fz = -Math.cos(st.yaw);
  const rx = Math.cos(st.yaw);
  const rz = -Math.sin(st.yaw);
  let wx = 0;
  let wz = 0;
  if (input.w) { wx += fx; wz += fz; }
  if (input.s) { wx -= fx; wz -= fz; }
  if (input.d) { wx += rx; wz += rz; }
  if (input.a) { wx -= rx; wz -= rz; }

  const crouching = input.crouch;
  const moving = wx * wx + wz * wz > 0;
  const sprinting = moving && input.sprint && !st.exhausted && !crouching;
  let speed = (sprinting ? PLAYER.sprintSpeed : PLAYER.walkSpeed) * (st.isBigfoot ? PLAYER.bigfootSpeedMul : 1) * mods.speedMul;
  if (crouching) speed *= PLAYER.crouchSpeedMul;

  // Terrain obstacles: fallen logs slow hunters only; lake slows everyone (less so Bigfoot).
  // A hunter can VAULT a log — a stamina-gated hop that clambers over it instead of wading (slowed).
  // While airborne over the log (a vault in progress) the slow doesn't apply.
  if (!st.isBigfoot) {
    const logOvl = logOverlap(world.fallenLogs, st.x, st.z, PLAYER.radius);
    if (logOvl > 0 && st.grounded) {
      if (input.vault && st.stamina >= PLAYER.vaultStaminaCost) {
        st.vy = PLAYER.vaultHopSpeed;
        st.grounded = false;
        st.stamina -= PLAYER.vaultStaminaCost;
      } else {
        speed *= lerp(1, PLAYER.logSlowFactor, logOvl);
      }
    }
  }
  const lakeDep = lakeDepth(st.x, st.z);
  if (lakeDep > 0) {
    speed *= lerp(1, st.isBigfoot ? PLAYER.lakeBigfootFactor : PLAYER.lakeHunterFactor, lakeDep);
  }

  if (moving) {
    // Matches the old THREE wish.normalize()+addScaledVector order exactly (divide, then * (speed*dt)).
    const len = Math.sqrt(wx * wx + wz * wz);
    const move = speed * dt;
    st.x += (wx / len) * move;
    st.z += (wz / len) * move;
  }

  // Keep inside the world, push out of trees, then sit on the terrain.
  const half = PLAYER_WORLD_HALF;
  st.x = clamp(st.x, -half, half);
  st.z = clamp(st.z, -half, half);
  // Save intended position (post-clamp) so the step check can compare it.
  const ix = st.x;
  const iz = st.z;
  // Collision is climb-aware: a Bigfoot at/above a climbable's top isn't pushed out (it walks on top).
  const resolved = resolveCollision(world.colliders, ix, iz, PLAYER.radius, st.feetY, world.getHeight);
  const wasPushed = (resolved.x - ix) ** 2 + (resolved.z - iz) ** 2 > 1e-4;
  st.x = resolved.x;
  st.z = resolved.z;
  // Ground rises to a structure's top when standing over its footprint (perched), else terrain.
  st.groundY = groundHeightAt(world.climbables, world.getHeight, st.x, st.z);

  // Auto-step: if a collider pushed us back but the terrain at the intended spot is only a
  // small rise, lift over it rather than sliding around the obstacle.
  if (wasPushed && st.grounded && moving) {
    const destGY = world.getHeight(ix, iz);
    const rise = destGY - st.groundY;
    if (rise >= 0 && rise <= PLAYER.stepHeight) {
      st.x = ix;
      st.z = iz;
      st.groundY = destGY;
    }
  }

  // Vertical: climb (Bigfoot scales a structure) takes precedence, then leap, then jump + gravity.
  // feetY is the true feet height; >= groundY while airborne or perched on a structure.
  const climb = st.isBigfoot && input.climb && !crouching
    ? climbSupport(world.climbables, world.getHeight, st.x, st.z, PLAYER.radius, PLAYER.climbReach)
    : null;
  if (climb && st.stamina > 0) {
    // Scale the surface: rise toward its top (capped), clinging to the side (XZ pinned by the pushout)
    // and draining stamina so Bigfoot can't hang forever. Push forward at the top to mount it.
    st.feetY = Math.min(climb.top, st.feetY + PLAYER.climbSpeed * dt);
    st.vy = 0;
    st.grounded = false;
    st.stamina = Math.max(0, st.stamina - PLAYER.climbStaminaDrain * dt);
  } else {
    // Leap is a taller, stamina-gated bound; it takes precedence over a normal jump for Bigfoot.
    if (st.grounded && !crouching && st.isBigfoot && input.leap && st.stamina >= PLAYER.leapStaminaCost) {
      st.vy = PLAYER.leapSpeed;
      st.grounded = false;
      st.stamina -= PLAYER.leapStaminaCost;
    } else if (st.grounded && input.jump && !crouching) {
      st.vy = PLAYER.jumpSpeed;
      st.grounded = false;
    }
    if (st.grounded) {
      // Ride terrain/structure-top as it changes; a big drop (walking off a ledge) starts a fall.
      if (st.groundY < st.feetY - PLAYER.stepHeight) {
        st.grounded = false;
        st.vy = 0;
      } else {
        st.feetY = st.groundY;
      }
    } else {
      st.vy -= PLAYER.gravity * dt;
      st.feetY += st.vy * dt;
      if (st.feetY <= st.groundY) {
        st.feetY = st.groundY;
        st.vy = 0;
        st.grounded = true;
      }
    }
  }

  // Crouch: ease the eye height toward standing or crouched.
  const targetEye = crouching ? st.eyeHeight * PLAYER.crouchFactor : st.eyeHeight;
  st.curEye += (targetEye - st.curEye) * Math.min(1, dt * PLAYER.eyeLerp);

  // Resources (drains scaled by the server-driven per-night escalation multipliers).
  // Hitting 0 stamina exhausts you: no sprinting until it recovers past a threshold. While *holding*
  // climb against a surface, suppress regen (even at 0 stamina) or the gate never bites — you'd ratchet
  // up a tick at a time as regen refills between attempts. Release the climb to recover.
  if (sprinting) st.stamina = Math.max(0, st.stamina - PLAYER.staminaDrainPerSec * mods.staminaDrainMul * dt);
  else if (!climb) st.stamina = Math.min(100, st.stamina + PLAYER.staminaRegenPerSec * dt);
  if (st.stamina <= 0) st.exhausted = true;
  else if (st.exhausted && st.stamina >= PLAYER.staminaRecover) st.exhausted = false;

  if (st.flashlightOn) {
    st.battery = Math.max(0, st.battery - PLAYER.batteryDrainPerSec * mods.batteryDrainMul * dt);
    if (st.battery <= 0) st.flashlightOn = false;
  }

  return { moving, sprinting };
}

// Movement clamps just inside the world edge (matches the old LocalPlayer `half = 398`).
const PLAYER_WORLD_HALF = 398;
