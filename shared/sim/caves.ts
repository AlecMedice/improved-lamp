import { mulberry32 } from "./rng";
import { CAVE, CAVE_GEN } from "./constants";

export type Cave = { x: number; z: number };

/**
 * Cave entrances — Bigfoot's lairs and the nodes of its fast-travel network.
 * Seed-derived so the client and the server generate the IDENTICAL set (previously
 * each used Math.random() independently, so every client + the server disagreed).
 * Five caves spread across the outer ring (150..340 m from centre), ≥120 m apart.
 */
export function generateCaves(seed: number): Cave[] {
  const rand = mulberry32(seed ^ CAVE_GEN.seedXor);
  const out: Cave[] = [];
  for (let attempt = 0; out.length < CAVE_GEN.count && attempt < CAVE_GEN.maxAttempts; attempt++) {
    const angle = rand() * Math.PI * 2;
    const r = CAVE_GEN.minRadius + rand() * CAVE_GEN.radiusSpan;
    const x = Math.round(Math.cos(angle) * r);
    const z = Math.round(Math.sin(angle) * r);
    if (out.every((c) => Math.hypot(c.x - x, c.z - z) >= CAVE_GEN.minSpacing)) out.push({ x, z });
  }
  return out;
}

/** Index of a cave whose mouth (x,z) is within `CAVE.triggerRadius`, or -1. One place for client + server. */
export function nearestCaveIndex(caves: readonly Cave[], x: number, z: number): number {
  const r2 = CAVE.triggerRadius * CAVE.triggerRadius;
  for (let i = 0; i < caves.length; i++) {
    const dx = caves[i].x - x;
    const dz = caves[i].z - z;
    if (dx * dx + dz * dz <= r2) return i;
  }
  return -1;
}

/**
 * Where a traveller emerges from a cave mouth: `CAVE.emergeOffset` metres toward map centre
 * (outside the boulder horseshoe), facing back into the forest. Shared so the server's authoritative
 * `caveTravel` and the client's fade-in animation land on the exact same spot + heading.
 */
export function caveEmergePoint(cave: Cave): { x: number; z: number; yaw: number } {
  const dl = Math.hypot(cave.x, cave.z) || 1;
  return {
    x: cave.x - (cave.x / dl) * CAVE.emergeOffset,
    z: cave.z - (cave.z / dl) * CAVE.emergeOffset,
    yaw: Math.atan2(cave.x, cave.z),
  };
}
