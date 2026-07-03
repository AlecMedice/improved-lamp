import { Color } from "three";
import { WORLD, generateCaves } from "../../shared/sim";

// Movement/world sim constants now live in shared/sim so the client and the authoritative
// server compute identical physics. Re-exported here so existing imports are unchanged.
export { WORLD, PLAYER, CAVE } from "../../shared/sim";

/** Colyseus server endpoint. Override with VITE_SERVER_URL at build/dev time. */
export const SERVER_URL = (import.meta.env.VITE_SERVER_URL as string) || "ws://localhost:2567";

/** One night runs 8pm -> 8am in this many seconds; Bigfoot wins by surviving TOTAL_NIGHTS. Mirrors the server. */
export const NIGHT_SECONDS = 600;
export const TOTAL_NIGHTS = 3;

/** Bigfoot offense (client-side cooldown/feedback; the server is authoritative). */
export const ABILITY = {
  roarCooldown: 25, // seconds between roars (UI gate, mirrors server)
};

/** Bigfoot charge — a short forward burst (mirrors the server; the client predicts it). */
export const CHARGE = {
  speedMul: 1.9, // burst multiplier over sprint during the window
  duration: 1.2, // seconds the burst lasts
  cooldown: 6, // seconds after the burst before another charge (UI gate)
};

/** Bigfoot senses overlay ("V") — predator vision that reveals prey + scent through the forest. */
export const SENSES = {
  range: 60, // hunters within this of Bigfoot glow through trees (0 = unlimited)
  hunterColor: 0xff5a6e, // warm silhouette for prey
  hunterOpacity: 0.5,
  scentColor: 0x8fe3ff, // cool marker on Bigfoot's own recent trail
};

/** Searcher revive (client-side prompt + progress bar; the server is authoritative on completion). */
export const REVIVE = {
  radius: 3.5, // how close to a downed teammate you must stand (mirrors server REVIVE_RADIUS)
  seconds: 4, // hold time to free them (mirrors server REVIVE_SECONDS) — drives the local bar
};

/**
 * Bigfoot's night sight. It has no flashlight, so instead of the old always-on brightness buff
 * it gets a dim, short-range vision cone riding the camera — deliberately weaker and shorter than
 * a searcher's flashlight (range 60, intensity 140): Bigfoot sees a near bubble and loses the far
 * scene to darkness. `exposure` replaces the old per-role tone-mapping so distance actually goes
 * dark. Tuned by eye (project convention). See LocalPlayer (the light) + Game (exposure).
 */
export const BIGFOOT_VISION = {
  range: 22, // metres the sight reaches (< flashlight's 60)
  intensity: 55, // beam brightness (< flashlight's 140)
  angle: 0.95, // wide, soft cone (creature eyes, not a torch)
  penumbra: 0.7, // soft edge
  exposure: 1.2, // tone-mapping exposure (~ the searcher's 1.15; was 1.7 always-on)
};

/**
 * Per-night escalation lives on the server (the `ESCALATION` table in `ForestRoom.ts`),
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
 * Cave entrances — Bigfoot's lairs and the nodes of its fast-travel network. Seed-derived
 * (shared/sim) so the client, every other client, and the server agree on the layout;
 * the fast-travel rules (`CAVE`) are shared too since the server validates the jump.
 */
export const CAVES: ReadonlyArray<{ x: number; z: number }> = generateCaves(WORLD.seed);

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
