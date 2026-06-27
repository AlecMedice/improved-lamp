import { mulberry32 } from "../rng";
import { WORLD } from "../config";

export type Cave = { x: number; z: number };

/**
 * Five cave entrances on the outer ring of the world (150–340 m from centre), at least
 * 120 m apart. **Seeded** (was `Math.random()` independently on client + server, which
 * disagreed every run) so client and server share the exact same cave geometry — required
 * for server-side collision near cave mouths and for validating cave fast-travel.
 */
export function generateCaves(seed: number): Cave[] {
  const rand = mulberry32(seed);
  const out: Cave[] = [];
  for (let attempt = 0; out.length < 5 && attempt < 400; attempt++) {
    const angle = rand() * Math.PI * 2;
    const r = 150 + rand() * 190;
    const x = Math.round(Math.cos(angle) * r);
    const z = Math.round(Math.sin(angle) * r);
    if (out.every((c) => Math.hypot(c.x - x, c.z - z) >= 120)) out.push({ x, z });
  }
  return out;
}

/** The shared cave set for this world (derived from the world seed). */
export const CAVES: ReadonlyArray<Cave> = generateCaves(WORLD.seed ^ 0x000ca7e5);
