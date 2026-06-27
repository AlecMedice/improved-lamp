import { Color } from "three";

// World + movement constants now live in the shared module (single source of truth with the
// authoritative server). Re-exported here so existing `../config` imports keep working.
export { WORLD, PLAYER, CAVE } from "@shared/config";
export { CAVES } from "@shared/world/caves";

/** Colyseus server endpoint. Override with VITE_SERVER_URL at build/dev time. */
export const SERVER_URL = (import.meta.env.VITE_SERVER_URL as string) || "ws://localhost:2567";

/** One night runs 8pm -> 8am in this many seconds; Bigfoot wins by surviving TOTAL_NIGHTS. Mirrors the server. */
export const NIGHT_SECONDS = 300;
export const TOTAL_NIGHTS = 3;

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
