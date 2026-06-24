/**
 * Shared, dependency-free movement + world simulation for Hollow Pines. Imported by BOTH
 * the Three.js client (prediction) and the Colyseus server (authority). Same V8 + same
 * source → bit-identical results, so client prediction reconciles cleanly. No three.js here.
 */
export * from "./math";
export * from "./rng";
export * from "./constants";
export * from "./terrain";
export * from "./caves";
export * from "./world";
export * from "./collision";
export * from "./movement";

import { generateCaves, type Cave } from "./caves";
import { makeTerrain, type HeightFn } from "./terrain";
import { buildColliders, FALLEN_LOGS, type Collider, type FallenLog } from "./world";

/** Everything the movement sim needs to run, built once from a seed. */
export type World = {
  seed: number;
  caves: readonly Cave[];
  getHeight: HeightFn;
  colliders: Collider[];
  fallenLogs: FallenLog[];
};

/** Build the full deterministic world for a seed (terrain + caves + colliders + logs). */
export function makeWorld(seed: number): World {
  const caves = generateCaves(seed);
  return {
    seed,
    caves,
    getHeight: makeTerrain(seed),
    colliders: buildColliders(seed, caves),
    fallenLogs: FALLEN_LOGS,
  };
}
