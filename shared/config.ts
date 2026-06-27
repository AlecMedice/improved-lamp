/**
 * World + movement constants shared by client and server.
 *
 * Pure data — no Three.js, no DOM. The client re-exports these from `client/src/config.ts`
 * (alongside its render-only constants); the server imports them for movement validation.
 * Single source of truth so the authoritative simulation matches the client's prediction.
 */

export const WORLD = {
  size: 800, // full extent; world spans -400..400 on x/z
  segments: 160, // terrain mesh resolution (~same triangle density as original)
  treeCount: 700,
  hillHeight: 14,
  seed: 1337, // shared so every client + the server build an identical forest
  baseCampRadius: 16,
};

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
  lakeHunterFactor: 0.28, // hunter speed multiplier while wading (heavy kit)
  lakeBigfootFactor: 0.72, // Bigfoot speed multiplier while wading (strong, but water impedes)
  jumpSpeed: 5.2, // initial upward velocity on jump (m/s)
  gravity: 16, // downward acceleration while airborne (m/s^2)
  crouchFactor: 0.55, // eye-height multiplier while crouched
  crouchSpeedMul: 0.5, // movement-speed multiplier while crouched
  eyeLerp: 12, // how fast eye height eases between standing/crouched
};

export const CAVE = {
  triggerRadius: 6, // how close Bigfoot must be to a mouth to use it
  travelCooldown: 2.0, // seconds between cave jumps
};
