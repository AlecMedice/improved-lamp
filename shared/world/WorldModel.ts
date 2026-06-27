import { mulberry32, makeValueNoise } from "../rng";
import { WORLD } from "../config";
import { CAVES } from "./caves";

export type Collider = { x: number; z: number; r: number };

export type FallenLog = {
  cx: number; cz: number; // centre
  ax: number; az: number; // unit axis along the trunk (world XZ)
  halfLen: number;        // half-length of the trunk
  r: number;              // trunk radius
};

/** A placed conifer — the client renders one instanced tree per entry. */
export type TreeInstance = { x: number; z: number; s: number; ry: number };

/** THREE.MathUtils.smoothstep, reimplemented pure (no Three.js dependency). */
function smoothstep(x: number, min: number, max: number): number {
  if (x <= min) return 0;
  if (x >= max) return 1;
  const t = (x - min) / (max - min);
  return t * t * (3 - 2 * t);
}

/**
 * The deterministic, render-free world: terrain height + static colliders + fallen logs + lake.
 *
 * This is the single source of truth for collision/terrain. The client's `Environment` builds
 * its meshes from this model (and delegates `getHeight`/`resolveCollision`/… to it); the server
 * builds one per room to validate movement. Generation order and the seeded `rand()` stream
 * mirror the old `Environment.build*` methods exactly, so colliders line up with the visible
 * trees and both sides agree on every obstacle.
 */
export class WorldModel {
  /** Static circle colliders (trees, the RV, cave boulders, lookout tower). */
  readonly colliders: Collider[] = [];
  /** Fallen logs (capsule slow-down zones for hunters). */
  readonly fallenLogs: FallenLog[] = [];
  /** Placed trees the client renders (the collider for each is also in `colliders`). */
  readonly trees: TreeInstance[] = [];
  /** Lake ellipse SW of camp — wading slow-down. */
  readonly lake = { x: -120, z: -110, rx: 60, rz: 45 };

  private noise = makeValueNoise(WORLD.seed);

  constructor() {
    this.buildTrees();
    this.buildRVColliders(9, -4, -0.5); // matches Environment.buildRV(9, -4, -0.5)
    this.buildCaveColliders();
    this.colliders.push({ x: 220, z: -230, r: 2.4 }); // lookout tower
    this.buildFallenLogs();
  }

  /** Terrain height at world (x,z). Players/props sample this to sit on the ground. */
  getHeight(x: number, z: number): number {
    const nx = x * 0.0065;
    const nz = z * 0.0065;
    let h = 0;
    let amp = 1;
    let freq = 1;
    let norm = 0;
    for (let o = 0; o < 4; o++) {
      h += this.noise(nx * freq, nz * freq) * amp;
      norm += amp;
      amp *= 0.5;
      freq *= 2;
    }
    h = (h / norm) * WORLD.hillHeight;
    const d = Math.sqrt(x * x + z * z);
    const flat = smoothstep(d, WORLD.baseCampRadius, WORLD.baseCampRadius + 12);
    return h * flat;
  }

  /** Push an (x,z) point out of any solid collider it overlaps. Returns the resolved point. */
  resolveCollision(x: number, z: number, radius: number): { x: number; z: number } {
    let nx = x;
    let nz = z;
    for (const t of this.colliders) {
      const min = t.r + radius;
      const dx = nx - t.x;
      const dz = nz - t.z;
      const d2 = dx * dx + dz * dz;
      if (d2 < min * min && d2 > 1e-6) {
        const d = Math.sqrt(d2);
        const push = min - d;
        nx += (dx / d) * push;
        nz += (dz / d) * push;
      }
    }
    return { x: nx, z: nz };
  }

  /** True if a solid collider blocks the straight (XZ) line between two points. */
  lineBlocked(ax: number, az: number, bx: number, bz: number): boolean {
    const dx = bx - ax;
    const dz = bz - az;
    const len2 = dx * dx + dz * dz || 1e-6;
    for (const t of this.colliders) {
      let s = ((t.x - ax) * dx + (t.z - az) * dz) / len2;
      s = Math.max(0, Math.min(1, s));
      const cx = ax + dx * s;
      const cz = az + dz * s;
      const ddx = t.x - cx;
      const ddz = t.z - cz;
      if (ddx * ddx + ddz * ddz < t.r * t.r) return true;
    }
    return false;
  }

  /**
   * Capsule overlap against all fallen logs, 0 = clear, 1 = fully inside.
   * Used to apply the hunter slow-down penalty (Bigfoot ignores logs).
   */
  logOverlap(x: number, z: number, playerRadius: number): number {
    let best = 0;
    for (const log of this.fallenLogs) {
      const dx = x - log.cx;
      const dz = z - log.cz;
      const t = Math.max(-log.halfLen, Math.min(log.halfLen, dx * log.ax + dz * log.az));
      const nx = log.cx + t * log.ax;
      const nz = log.cz + t * log.az;
      const dist = Math.hypot(x - nx, z - nz);
      const threshold = log.r + playerRadius;
      if (dist < threshold) best = Math.max(best, 1 - dist / threshold);
    }
    return best;
  }

  /**
   * How deep into the lake the point is, 0 = outside, 1 = dead centre.
   * Used to scale the wading speed penalty.
   */
  lakeDepth(x: number, z: number): number {
    const nx = (x - this.lake.x) / this.lake.rx;
    const nz = (z - this.lake.z) / this.lake.rz;
    const d2 = nx * nx + nz * nz;
    return d2 < 1 ? Math.max(0, 1 - Math.sqrt(d2)) : 0;
  }

  /** True if (x,z) is within r of a cave mouth (keeps the forest/bushes clear of entrances). */
  nearCave(x: number, z: number, r: number): boolean {
    return CAVES.some((c) => (c.x - x) ** 2 + (c.z - z) ** 2 < r * r);
  }

  // --- Generation (mirrors the old Environment.build* methods) -----------------

  /**
   * Scatter conifers via the same seeded stream as the old `buildForest`. Note the
   * rotation `rand()` is still consumed even though the collider ignores it — that keeps
   * the stream in lockstep so every subsequent tree's x/z/scale is identical.
   */
  private buildTrees() {
    const rand = mulberry32(WORLD.seed ^ 0x9e3779b9);
    const n = WORLD.treeCount;
    const half = WORLD.size / 2 - 6;
    for (let i = 0; i < n; i++) {
      const x = (rand() * 2 - 1) * half;
      const z = (rand() * 2 - 1) * half;
      if (Math.sqrt(x * x + z * z) < WORLD.baseCampRadius + 4) continue; // keep clearing open
      if (this.nearCave(x, z, 7)) continue; // keep cave mouths clear
      const s = 0.7 + rand() * 0.9;
      const ry = rand() * Math.PI * 2; // mirrors q.setFromAxisAngle(up, rand()*PI*2) in buildForest
      this.trees.push({ x, z, s, ry });
      this.colliders.push({ x, z, r: 0.45 * s });
    }
  }

  /** A few circles along the RV body so players can't walk through it. */
  private buildRVColliders(x: number, z: number, ry: number) {
    const cos = Math.cos(ry);
    const sin = Math.sin(ry);
    // THREE applyAxisAngle about +Y maps local (lx,0,0) -> (lx*cos, 0, -lx*sin).
    for (const lx of [-2.2, 0, 2.2]) {
      this.colliders.push({ x: x + lx * cos, z: z - lx * sin, r: 1.6 });
    }
  }

  /** Cave boulder horseshoes — sides + back are solid; the mouth (toward centre) stays walkable. */
  private buildCaveColliders() {
    for (const cave of CAVES) {
      const dl = Math.hypot(cave.x, cave.z) || 1;
      const dx = -cave.x / dl; // toward map centre (the open side)
      const dz = -cave.z / dl;
      const px = -dz; // perpendicular (sides of the mouth)
      const pz = dx;
      this.colliders.push({ x: cave.x - dx * 3, z: cave.z - dz * 3, r: 1.8 });
      this.colliders.push({ x: cave.x + px * 3.0, z: cave.z + pz * 3.0, r: 1.5 });
      this.colliders.push({ x: cave.x - px * 3.0, z: cave.z - pz * 3.0, r: 1.5 });
    }
  }

  /** Curated fallen-log slow-down zones (same table + math as Environment.buildFallenTrees). */
  private buildFallenLogs() {
    const logs: [number, number, number, number][] = [
      [120, -160, 0.3, 11],   // south, path toward a cave
      [60, -90, 1.8, 9],      // near creek crossing
      [-60, -70, 0.7, 10],    // creek area SW of camp
      [-170, -80, -0.4, 12],  // lake shore approach
      [-200, 30, 1.1, 10],    // NW of camp
      [160, 80, -0.8, 9],     // NE forest
      [240, -160, 0.5, 13],   // SE near lookout tower
      [-100, 200, 1.4, 10],   // northern forest
      [50, 260, -0.6, 11],    // north
      [-280, 120, 0.9, 9],    // far NW
    ];
    const trunkR = 0.38;
    for (const [cx, cz, angle, len] of logs) {
      // localX (1,0,0) after rotateZ(PI/2) then rotation.y=angle → (cos(angle), 0, -sin(angle)).
      this.fallenLogs.push({ cx, cz, ax: Math.cos(angle), az: -Math.sin(angle), halfLen: len / 2, r: trunkR });
    }
  }
}
