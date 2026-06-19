import { Color } from "three";

/** Colyseus server endpoint. Override with VITE_SERVER_URL at build/dev time. */
export const SERVER_URL = (import.meta.env.VITE_SERVER_URL as string) || "ws://localhost:2567";

/** One night runs 8pm -> 8am in this many seconds; Bigfoot wins by surviving TOTAL_NIGHTS. Mirrors the server. */
export const NIGHT_SECONDS = 300;
export const TOTAL_NIGHTS = 3;

export const WORLD = {
  size: 400, // full extent; world spans -200..200 on x/z
  segments: 96, // terrain mesh resolution
  treeCount: 320,
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
};

/** Bigfoot offense (client-side cooldown/feedback; the server is authoritative). */
export const ABILITY = {
  roarCooldown: 25, // seconds between roars (UI gate, mirrors server)
};

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
export const CAVES: ReadonlyArray<{ x: number; z: number }> = [
  { x: 120, z: 45 },
  { x: -115, z: 95 },
  { x: 70, z: -135 },
  { x: -95, z: -80 },
  { x: 10, z: 150 },
];

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
