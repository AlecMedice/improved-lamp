import { Color } from "three";

/** Colyseus server endpoint. Override with VITE_SERVER_URL at build/dev time. */
export const SERVER_URL = (import.meta.env.VITE_SERVER_URL as string) || "ws://localhost:2567";

/** One night runs 8pm -> 8am in this many seconds; Bigfoot wins by surviving TOTAL_NIGHTS. Mirrors the server. */
export const NIGHT_SECONDS = 300;
export const TOTAL_NIGHTS = 3;

export const WORLD = {
  size: 800, // full extent; world spans -400..400 on x/z
  segments: 160, // terrain mesh resolution (~same triangle density as original)
  treeCount: 700,
  hillHeight: 14,
  seed: 1337, // shared so every client builds an identical forest
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

/** Bigfoot offense (client-side cooldown/feedback; the server is authoritative). */
export const ABILITY = {
  roarCooldown: 25, // seconds between roars (UI gate, mirrors server)
};

/**
 * Per-night escalation now lives on the server (the `ESCALATION` table in `ForestRoom.ts`),
 * which replicates the multipliers to clients — single source of truth. See GameState.
 */

/** Map readout tuning for the clue trail (the "in-contact" gating). */
export const MAP = {
  clueWindow: 15, // only tracks from the last N seconds show on the map
  hearRange: 35, // Bigfoot within this distance counts as "heard nearby"
  evidenceSight: 18, // a clue within this distance counts as "sees recent evidence"
};

/** Camera / filming mechanic — the hunters' way to win. */
export const FILM = {
  range: 35, // max distance you can record Bigfoot from
  halfFovDeg: 18, // how centred Bigfoot must be in frame
  aimHeight: 1.4, // aim point up Bigfoot's body
};

export const NET = { sendHz: 15 };

/**
 * Cave entrances — Bigfoot's lairs and the nodes of its fast-travel network.
 * Kept in sync with the server's copy in ForestRoom.ts (used for the spawn point).
 */
/**
 * Cave positions — regenerated randomly each page load so every session has a
 * different map. Five caves spread across the outer ring of the world (150-340 m
 * from centre) with at least 120 m between each pair.
 */
function _generateCaves(): ReadonlyArray<{ x: number; z: number }> {
  const out: { x: number; z: number }[] = [];
  for (let attempt = 0; out.length < 5 && attempt < 400; attempt++) {
    const angle = Math.random() * Math.PI * 2;
    const r = 150 + Math.random() * 190;
    const x = Math.round(Math.cos(angle) * r);
    const z = Math.round(Math.sin(angle) * r);
    if (out.every((c) => Math.hypot(c.x - x, c.z - z) >= 120)) out.push({ x, z });
  }
  return out;
}
export const CAVES: ReadonlyArray<{ x: number; z: number }> = _generateCaves();

export const CAVE = {
  triggerRadius: 6, // how close Bigfoot must be to a mouth to use it
  travelCooldown: 2.0, // seconds between cave jumps
};

/** Initial dusk palette. Environment.setTimeOfDay() lerps toward night and dawn. */
export const DUSK = {
  skyTop: new Color("#2a2740"),
  skyBottom: new Color("#c87b53"),
  fog: new Color("#5a4a55"),
  ambientSky: new Color("#3a3a55"),
  ambientGround: new Color("#2a2018"),
  sun: new Color("#ff9d5c"),
  ground: new Color("#3f5340"),
  trunk: new Color("#4a3a2c"),
  foliage: new Color("#33503a"),
};
