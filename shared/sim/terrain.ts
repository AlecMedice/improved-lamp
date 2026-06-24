import { makeValueNoise } from "./rng";
import { smoothstep } from "./math";
import { WORLD } from "./constants";

export type HeightFn = (x: number, z: number) => number;

/**
 * Build the terrain-height sampler for a seed. fBm value-noise (4 octaves) scaled by
 * hillHeight, flattened to ground level near the base camp. Ported verbatim from the
 * old Environment.getHeight so the collision mesh and the sim agree exactly.
 */
export function makeTerrain(seed: number): HeightFn {
  const noise = makeValueNoise(seed);
  return (x: number, z: number): number => {
    const nx = x * 0.0065;
    const nz = z * 0.0065;
    let h = 0;
    let amp = 1;
    let freq = 1;
    let norm = 0;
    for (let o = 0; o < 4; o++) {
      h += noise(nx * freq, nz * freq) * amp;
      norm += amp;
      amp *= 0.5;
      freq *= 2;
    }
    h = (h / norm) * WORLD.hillHeight;
    const d = Math.sqrt(x * x + z * z);
    const flat = smoothstep(d, WORLD.baseCampRadius, WORLD.baseCampRadius + 12);
    return h * flat;
  };
}
