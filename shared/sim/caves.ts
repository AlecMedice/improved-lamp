import { mulberry32 } from "./rng";
import { CAVE_GEN } from "./constants";

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
