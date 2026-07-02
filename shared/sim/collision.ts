import type { Collider, FallenLog } from "./world";
import { LAKE } from "./world";
import type { HeightFn } from "./terrain";

export type Vec2 = { x: number; z: number };

/** A player standing/hanging within this vertical slop of a climbable's top counts as "on top". */
const CLIMB_TOP_EPS = 0.06;

/**
 * Push an (x,z) point out of any solid circle collider it overlaps. Returns the resolved point.
 * Climbable structures are solid from the side but walkable on top: if `feetY`/`getHeight` are
 * supplied and the point is at/above a climbable's top, that collider is skipped so the player can
 * stand on it. Called without feetY it treats every collider as a plain full-height solid.
 */
export function resolveCollision(
  colliders: Collider[], x: number, z: number, radius: number,
  feetY = -Infinity, getHeight?: HeightFn
): Vec2 {
  let nx = x;
  let nz = z;
  for (const t of colliders) {
    if (t.climbH !== undefined && getHeight && feetY >= getHeight(t.x, t.z) + t.climbH - CLIMB_TOP_EPS) continue;
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

/**
 * Ground height at (x,z): the terrain, raised to a climbable's top when standing over its footprint
 * (so a player who has climbed onto a structure walks on its top surface, not the ground far below).
 */
export function groundHeightAt(climbables: Collider[], getHeight: HeightFn, x: number, z: number): number {
  let g = getHeight(x, z);
  for (const t of climbables) {
    const dx = x - t.x;
    const dz = z - t.z;
    if (dx * dx + dz * dz <= t.r * t.r) g = Math.max(g, getHeight(t.x, t.z) + (t.climbH ?? 0));
  }
  return g;
}

/** The top of a climbable structure Bigfoot can scale here (clinging to the side, or over the top), or null. */
export function climbSupport(
  climbables: Collider[], getHeight: HeightFn, x: number, z: number, radius: number, reach: number
): { top: number; over: boolean } | null {
  let best: { top: number; over: boolean } | null = null;
  for (const t of climbables) {
    const d = Math.hypot(x - t.x, z - t.z);
    let over: boolean;
    if (d <= t.r) over = true; // over the footprint (on/above the top)
    else if (d <= t.r + radius + reach) over = false; // within reach of the side
    else continue;
    const top = getHeight(t.x, t.z) + (t.climbH ?? 0);
    if (!best || top > best.top) best = { top, over };
  }
  return best;
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
