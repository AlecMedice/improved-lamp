/**
 * Canonical sim constants, shared by client and server so movement/collision is
 * computed from one source of truth (no drift → no reconciliation rubber-banding).
 * The client's config.ts re-exports WORLD/PLAYER from here and adds its own
 * render-only values (colors, MAP, FILM, etc.). Per-night escalation is NOT here:
 * the server owns the ESCALATION table (ForestRoom.ts) and replicates multipliers,
 * which callers pass into stepPlayer via StepModifiers.
 */

export const WORLD = {
  size: 800, // full extent; world spans -400..400 on x/z
  segments: 160, // terrain mesh resolution (render-only, kept here to keep WORLD whole)
  treeCount: 700,
  hillHeight: 14,
  seed: 1337, // shared so every client + the server build an identical forest
  baseCampRadius: 16,
};

/** Half the world extent; movement clamps just inside this. */
export const WORLD_HALF = WORLD.size / 2; // 400

/** Fixed simulation step (s). Matches the server's 20 Hz tick so one input = one tick. */
export const SIM_DT = 1 / 20;

export const PLAYER = {
  eyeHeight: 1.7, // searcher; Bigfoot is taller (see LocalPlayer)
  radius: 0.4, // collision radius
  walkSpeed: 5,
  sprintSpeed: 8.5,
  bigfootSpeedMul: 1.22, // Bigfoot is faster
  mouseSensitivity: 0.0022,
  batteryDrainPerSec: 1.4, // while flashlight is on
  staminaDrainPerSec: 18, // while sprinting
  staminaRegenPerSec: 12,
  staminaRecover: 35, // once exhausted (0), must regen to this before sprinting again
  slowFactor: 0.75, // movement multiplier while slowed (after incapacitation)
  bobFreqWalk: 1.8, // head-bob cycles per second while walking
  bobFreqSprint: 2.6, // head-bob cycles per second while sprinting
  bobAmpWalk: 0.05, // vertical bob amplitude in metres while walking
  bobAmpSprint: 0.09, // vertical bob amplitude in metres while sprinting
  stepIntervalWalk: 0.52, // seconds between footstep sounds while walking
  stepIntervalSprint: 0.32, // seconds between footstep sounds while sprinting
  stepHeight: 0.75, // max terrain rise auto-stepped over when a collider blocks (m)
  logSlowFactor: 0.35, // hunter speed multiplier when clambering over a fallen log
  vaultHopSpeed: 4.6, // upward velocity when a hunter vaults a fallen log (a clamber, < jumpSpeed)
  vaultStaminaCost: 12, // stamina spent to vault a log (negates its slow instead of wading over it)
  climbSpeed: 3.6, // Bigfoot's vertical climb rate up a climbable structure (m/s)
  climbStaminaDrain: 22, // stamina/sec while climbing/clinging (regen is suspended while climbing)
  climbReach: 0.7, // how far past a structure's edge Bigfoot can grab on to start a climb (m)
  lakeHunterFactor: 0.28, // hunter speed multiplier while wading (heavy kit)
  lakeBigfootFactor: 0.72, // Bigfoot speed multiplier while wading (strong, but water impedes)
  jumpSpeed: 5.2, // initial upward velocity on jump (m/s)
  leapSpeed: 9.5, // Bigfoot's leap: initial upward velocity (m/s), well above a hunter's jump
  leapStaminaCost: 30, // stamina spent per leap (gates spamming)
  gravity: 16, // downward acceleration while airborne (m/s^2)
  crouchFactor: 0.55, // eye-height multiplier while crouched
  crouchSpeedMul: 0.5, // movement-speed multiplier while crouched
  eyeLerp: 12, // how fast eye height eases between standing/crouched
};

/** Cave fast-travel rules — shared because the server validates `caveTravel` with them. */
export const CAVE = {
  triggerRadius: 6, // how close Bigfoot must be to a mouth to use it
  travelCooldown: 2.0, // seconds between cave jumps
  emergeOffset: 8, // metres toward map centre a traveller emerges (outside the boulder horseshoe)
};

/**
 * Seconds before a dropped clue (footprint/branch) goes cold and disappears. Server-owned truth:
 * the server ages clues by this (scaled by per-night escalation) and the client fades its meshes on
 * the same window, so the visible trail can't drift from the replicated one.
 */
export const CLUE_LIFETIME = 50;

/** Cave-network generation tuning (seed-derived, identical on client + server). */
export const CAVE_GEN = {
  seedXor: 0xca7e_5eed, // mixes WORLD.seed so caves don't correlate with tree placement
  count: 5,
  minRadius: 150, // caves sit on the outer ring, 150..340 m from centre
  radiusSpan: 190,
  minSpacing: 120, // at least this far apart
  maxAttempts: 400,
};
