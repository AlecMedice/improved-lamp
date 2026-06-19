import { Color } from "three";

/** Colyseus server endpoint. Override with VITE_SERVER_URL at build/dev time. */
export const SERVER_URL = (import.meta.env.VITE_SERVER_URL as string) || "ws://localhost:2567";

/** Length of one match (a compressed night), in seconds. Mirrors the server. */
export const MATCH_SECONDS = 600;

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
