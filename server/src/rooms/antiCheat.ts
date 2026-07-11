/**
 * Pure server-authority helpers — the anti-cheat math the client can't be trusted to do.
 * Extracted from ForestRoom so the validation is unit-testable in isolation (no Colyseus lifecycle,
 * no network): given plain numbers/colliders in, a decision out. ForestRoom composes these with its
 * per-session state (token buckets, timestamps). Keep them dependency-free apart from shared/sim.
 */
import { lineBlocked, type Collider, type Vec2 } from "../../../shared/sim";

/**
 * Refill a movement "token bucket" of travel distance (metres) at `maxSpeed` for `dtSec`, capped at
 * `burst`. Charging over *time* (not a flat slack per message) is what stops move-spam from earning
 * free distance: many tiny moves refill only what real time allows, so total travel stays speed-bounded.
 */
export function refillAllowance(prev: number, maxSpeed: number, dtSec: number, burst: number): number {
  return Math.min(burst, prev + maxSpeed * dtSec);
}

/**
 * Clamp a requested step to the available distance `budget`. Returns the (possibly pulled-back) point
 * along the requested direction and the metres actually spent (the caller subtracts that from the bucket).
 */
export function gateStep(
  fromX: number, fromZ: number, toX: number, toZ: number, budget: number
): { x: number; z: number; spent: number } {
  const dx = toX - fromX;
  const dz = toZ - fromZ;
  const dist = Math.hypot(dx, dz);
  if (dist > budget && dist > 1e-6) {
    const k = budget / dist;
    return { x: fromX + dx * k, z: fromZ + dz * k, spent: budget };
  }
  return { x: toX, z: toZ, spent: dist };
}

/**
 * The most stamina a client may legitimately report after `dtSec`: the previous value plus the sim's
 * regen rate over that interval, plus a little slack for jitter. Drain is unbounded (sprint/climb burn
 * it fast, escalation faster); only *gaining* stamina implausibly is a cheat, so we cap the upper bound.
 */
export function staminaCeiling(prev: number, regenPerSec: number, dtSec: number, slack: number): number {
  return prev + regenPerSec * dtSec + slack;
}

/**
 * Server-authoritative filming visibility, independent of the client's `inView`: Bigfoot must be within
 * `range`, roughly in front of the hunter (aim cone from the replicated yaw, `dot >= aimCos`), and not
 * hidden behind a collider (line-of-sight). Mirrors the dazzle beam check — the pattern the room already
 * trusts. This is the hunter *win condition*, so it must not be forgeable.
 */
export function filmVisible(
  colliders: Collider[], hx: number, hz: number, hry: number, bx: number, bz: number,
  range: number, aimCos: number
): boolean {
  const dx = bx - hx;
  const dz = bz - hz;
  const dist = Math.hypot(dx, dz);
  if (dist > range || dist < 1e-3) return false;
  // Forward from the hunter's yaw (matches the sim's -sin/-cos convention).
  const dot = (dx / dist) * -Math.sin(hry) + (dz / dist) * -Math.cos(hry);
  if (dot < aimCos) return false;
  const a: Vec2 = { x: hx, z: hz };
  const b: Vec2 = { x: bx, z: bz };
  return !lineBlocked(colliders, a, b);
}
