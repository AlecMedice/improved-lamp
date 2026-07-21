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
export * from "./paths";
export * from "./world";
export * from "./collision";
export * from "./movement";
export * from "./specialties";

import { generateCaves, type Cave } from "./caves";
import { generatePaths, type ForestPath } from "./paths";
import { makeTerrain, type HeightFn } from "./terrain";
import { buildColliders, FALLEN_LOGS, type Collider, type FallenLog } from "./world";

/** Everything the movement sim needs to run, built once from a seed. */
export type World = {
  seed: number;
  caves: readonly Cave[];
  paths: readonly ForestPath[]; // logging trails out of camp (tree-free corridors)
  getHeight: HeightFn;
  colliders: Collider[];
  climbables: Collider[]; // the subset of colliders with a `climbH` (structures Bigfoot can scale/perch on)
  fallenLogs: FallenLog[];
};

/** Build the full deterministic world for a seed (terrain + caves + trails + colliders + logs). */
export function makeWorld(seed: number): World {
  const caves = generateCaves(seed);
  const paths = generatePaths(seed);
  const colliders = buildColliders(seed, caves, paths);
  return {
    seed,
    caves,
    paths,
    getHeight: makeTerrain(seed),
    colliders,
    climbables: colliders.filter((c) => c.climbH !== undefined),
    fallenLogs: FALLEN_LOGS,
  };
}
