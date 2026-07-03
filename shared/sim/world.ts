import { mulberry32 } from "./rng";
import { WORLD } from "./constants";
import type { Cave } from "./caves";

export type Collider = {
  x: number; z: number; r: number;
  /** If set, the structure is climbable: solid from the side, walkable on top at this many metres
   *  above its base terrain (Bigfoot can scale it and perch). Undefined = a plain, un-scalable solid. */
  climbH?: number;
};
export type FallenLog = {
  cx: number; cz: number; // centre
  ax: number; az: number; // unit axis along the trunk (world XZ)
  halfLen: number;        // half-length of the trunk
  r: number;              // trunk radius
};

/** Lake: an ellipse SW of camp. Speed penalty + shore colouring read from this. */
export const LAKE = { x: -120, z: -110, rx: 60, rz: 45 };

/** Fixed wooden fire-lookout tower location (a single circle collider). */
export const LOOKOUT = { x: 220, z: -230, r: 2.4 };

/** Research RV parked beside the campfire (3 circle colliders along the body). */
export const RV = { x: 9, z: -4, ry: -0.5 };

/**
 * Fallen-log obstacles (slow hunters, not Bigfoot). [cx, cz, angle(rad), length(m)].
 * After the trunk mesh is laid flat (rotateZ) and turned by `angle`, its long axis in
 * world XZ is (cos(angle), -sin(angle)); trunk radius is 0.38.
 */
export const LOG_TABLE: [number, number, number, number][] = [
  [120, -160, 0.3, 11],
  [60, -90, 1.8, 9],
  [-60, -70, 0.7, 10],
  [-170, -80, -0.4, 12],
  [-200, 30, 1.1, 10],
  [160, 80, -0.8, 9],
  [240, -160, 0.5, 13],
  [-100, 200, 1.4, 10],
  [50, 260, -0.6, 11],
  [-280, 120, 0.9, 9],
];

export const FALLEN_LOGS: FallenLog[] = LOG_TABLE.map(([cx, cz, angle, len]) => ({
  cx, cz, ax: Math.cos(angle), az: -Math.sin(angle), halfLen: len / 2, r: 0.38,
}));

function nearCave(caves: readonly Cave[], x: number, z: number, r: number): boolean {
  return caves.some((c) => (c.x - x) ** 2 + (c.z - z) ** 2 < r * r);
}

/**
 * Deterministically build the circle colliders (trees, RV, caves, lookout tower) for a
 * seed + cave set. Ported from Environment's build* methods; the tree loop preserves the
 * exact rand() call order (incl. the rotation draw it discards) so the collider positions
 * match the rendered tree instances byte-for-byte.
 */
export function buildColliders(seed: number, caves: readonly Cave[]): Collider[] {
  const colliders: Collider[] = [];

  // Trees — mirror buildForest()'s placement loop exactly.
  const rand = mulberry32(seed ^ 0x9e3779b9);
  const half = WORLD.size / 2 - 6;
  for (let i = 0; i < WORLD.treeCount; i++) {
    const x = (rand() * 2 - 1) * half;
    const z = (rand() * 2 - 1) * half;
    if (Math.sqrt(x * x + z * z) < WORLD.baseCampRadius + 4) continue; // keep clearing open
    if (nearCave(caves, x, z, 7)) continue; // keep cave mouths clear
    const s = 0.7 + rand() * 0.9;
    rand(); // rotation draw — discarded here, but must be consumed to keep the sequence aligned
    colliders.push({ x, z, r: 0.45 * s });
  }

  // RV — 3 circles along the body, rotated by RV.ry about Y. Its flat roof is a low perch.
  const c = Math.cos(RV.ry);
  const s = Math.sin(RV.ry);
  for (const lx of [-2.2, 0, 2.2]) {
    colliders.push({ x: RV.x + lx * c, z: RV.z + -lx * s, r: 1.6, climbH: 2.8 });
  }

  // Caves — horseshoe of boulders; side + back are solid, the mouth (toward centre) is open.
  // The boulders are climbable — Bigfoot can perch on them above its lair.
  for (const cave of caves) {
    const dl = Math.hypot(cave.x, cave.z) || 1;
    const dx = -cave.x / dl;
    const dz = -cave.z / dl;
    const px = -dz;
    const pz = dx;
    colliders.push({ x: cave.x - dx * 3, z: cave.z - dz * 3, r: 1.8, climbH: 2.4 });
    colliders.push({ x: cave.x + px * 3.0, z: cave.z + pz * 3.0, r: 1.5, climbH: 2.4 });
    colliders.push({ x: cave.x - px * 3.0, z: cave.z - pz * 3.0, r: 1.5, climbH: 2.4 });
  }

  // Lookout tower — the tallest climb; its render platform sits at ~10 m.
  colliders.push({ x: LOOKOUT.x, z: LOOKOUT.z, r: LOOKOUT.r, climbH: 9.5 });

  return colliders;
}
