/** Deterministic helpers so every client generates the *same* forest from a seed. */

/** Fast seeded PRNG. Returns a function producing floats in [0,1). */
export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Smooth, seeded 2D value noise in ~[-1,1]. Cheap; good enough for gentle terrain. */
export function makeValueNoise(seed: number): (x: number, y: number) => number {
  const rand = mulberry32(seed);
  const size = 256;
  const perm = new Uint8Array(size * 2);
  const grad = new Float32Array(size);
  for (let i = 0; i < size; i++) {
    perm[i] = i;
    grad[i] = rand() * 2 - 1;
  }
  for (let i = size - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    const tmp = perm[i];
    perm[i] = perm[j];
    perm[j] = tmp;
  }
  for (let i = 0; i < size; i++) perm[size + i] = perm[i];

  const fade = (t: number) => t * t * (3 - 2 * t);
  const valueAt = (ix: number, iy: number) => grad[perm[(perm[ix & 255] + (iy & 255)) & 255]];

  return (x: number, y: number) => {
    const x0 = Math.floor(x);
    const y0 = Math.floor(y);
    const fx = fade(x - x0);
    const fy = fade(y - y0);
    const v00 = valueAt(x0, y0);
    const v10 = valueAt(x0 + 1, y0);
    const v01 = valueAt(x0, y0 + 1);
    const v11 = valueAt(x0 + 1, y0 + 1);
    const a = v00 + fx * (v10 - v00);
    const b = v01 + fx * (v11 - v01);
    return a + fy * (b - a);
  };
}
