import type { Collider, FallenLog } from "./world";
import { LAKE } from "./world";

export type Vec2 = { x: number; z: number };

/** Push an (x,z) point out of any solid circle collider it overlaps. Returns the resolved point. */
export function resolveCollision(colliders: Collider[], x: number, z: number, radius: number): Vec2 {
  let nx = x;
  let nz = z;
  for (const t of colliders) {
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
export function lineBlocked(colliders: Collider[], a: Vec2, b: Vec2): boolean {
  const dx = b.x - a.x;
  const dz = b.z - a.z;
  const len2 = dx * dx + dz * dz || 1e-6;
  for (const t of colliders) {
    let s = ((t.x - a.x) * dx + (t.z - a.z) * dz) / len2;
    s = Math.max(0, Math.min(1, s));
    const cx = a.x + dx * s;
    const cz = a.z + dz * s;
    const ddx = t.x - cx;
    const ddz = t.z - cz;
    if (ddx * ddx + ddz * ddz < t.r * t.r) return true;
  }
  return false;
}

/** Capsule overlap against all fallen logs, 0 = clear, 1 = fully inside (hunter slow). */
export function logOverlap(logs: FallenLog[], x: number, z: number, playerRadius: number): number {
  let best = 0;
  for (const log of logs) {
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

/** How deep into the lake the point is, 0 = outside, 1 = dead centre (wading slow). */
export function lakeDepth(x: number, z: number): number {
  const nx = (x - LAKE.x) / LAKE.rx;
  const nz = (z - LAKE.z) / LAKE.rz;
  const d2 = nx * nx + nz * nz;
  return d2 < 1 ? Math.max(0, 1 - Math.sqrt(d2)) : 0;
}
