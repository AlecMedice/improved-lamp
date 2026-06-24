import { PLAYER, ESCALATION } from "./constants";
import { clamp, lerp } from "./math";
import { resolveCollision, logOverlap, lakeDepth } from "./collision";
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
  night: number; // current night — drives per-night escalation
};

/** One frame of movement intent. This is exactly what the client streams to the server. */
export type MoveInput = {
  w: boolean;
  s: boolean;
  a: boolean;
  d: boolean;
  yaw: number;
  jump: boolean;
  sprint: boolean;
  crouch: boolean;
  dt: number;
};

/** Transient per-step outputs the client uses for presentation (head-bob, footstep cadence). */
export type StepResult = { moving: boolean; sprinting: boolean };

/**
 * Advance one player by a single input. Pure w.r.t. (state, input, world, externalSpeedMul);
 * mutates `st` in place (callers clone when they need history). Ported line-for-line from the
 * old LocalPlayer.update physics so client prediction and server authority agree bit-for-bit.
 * `externalSpeedMul` is the slow factor (1, or PLAYER.slowFactor while slowed).
 */
export function stepPlayer(st: PlayerSimState, input: MoveInput, world: World, externalSpeedMul: number): StepResult {
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
  let speed = (sprinting ? PLAYER.sprintSpeed : PLAYER.walkSpeed) * (st.isBigfoot ? PLAYER.bigfootSpeedMul : 1) * externalSpeedMul;
  if (crouching) speed *= PLAYER.crouchSpeedMul;
  // Escalation: Bigfoot grows faster each night.
  if (st.isBigfoot) speed *= 1 + ESCALATION.bigfootSpeedPerNight * (st.night - 1);

  // Terrain obstacles: fallen logs slow hunters only; lake slows everyone (less so Bigfoot).
  if (!st.isBigfoot) {
    const logOvl = logOverlap(world.fallenLogs, st.x, st.z, PLAYER.radius);
    if (logOvl > 0) speed *= lerp(1, PLAYER.logSlowFactor, logOvl);
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
  const resolved = resolveCollision(world.colliders, ix, iz, PLAYER.radius);
  const wasPushed = (resolved.x - ix) ** 2 + (resolved.z - iz) ** 2 > 1e-4;
  st.x = resolved.x;
  st.z = resolved.z;
  st.groundY = world.getHeight(st.x, st.z);

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

  // Vertical: jump + gravity. feetY is the true feet height; >= groundY while airborne.
  if (st.grounded && input.jump && !crouching) {
    st.vy = PLAYER.jumpSpeed;
    st.grounded = false;
  }
  if (st.grounded) {
    st.feetY = st.groundY; // ride the terrain as it rises/falls
  } else {
    st.vy -= PLAYER.gravity * dt;
    st.feetY += st.vy * dt;
    if (st.feetY <= st.groundY) {
      st.feetY = st.groundY;
      st.vy = 0;
      st.grounded = true;
    }
  }

  // Crouch: ease the eye height toward standing or crouched.
  const targetEye = crouching ? st.eyeHeight * PLAYER.crouchFactor : st.eyeHeight;
  st.curEye += (targetEye - st.curEye) * Math.min(1, dt * PLAYER.eyeLerp);

  // Escalation: gear drains faster each night (battery + sprint stamina).
  const drainEsc = 1 + ESCALATION.hunterDrainPerNight * (st.night - 1);

  // Resources. Hitting 0 stamina exhausts you: no sprinting until it recovers past a threshold.
  st.stamina = sprinting
    ? Math.max(0, st.stamina - PLAYER.staminaDrainPerSec * drainEsc * dt)
    : Math.min(100, st.stamina + PLAYER.staminaRegenPerSec * dt);
  if (st.stamina <= 0) st.exhausted = true;
  else if (st.exhausted && st.stamina >= PLAYER.staminaRecover) st.exhausted = false;

  if (st.flashlightOn) {
    st.battery = Math.max(0, st.battery - PLAYER.batteryDrainPerSec * drainEsc * dt);
    if (st.battery <= 0) st.flashlightOn = false;
  }

  return { moving, sprinting };
}

// Movement clamps just inside the world edge (matches the old LocalPlayer `half = 398`).
const PLAYER_WORLD_HALF = 398;
