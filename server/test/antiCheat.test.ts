import { describe, it, expect } from "vitest";
import { refillAllowance, gateStep, staminaCeiling, filmVisible } from "../src/rooms/antiCheat";
import type { Collider } from "../../shared/sim";

// These guard the Track A integrity fixes: the client is not trusted for the win condition (filming),
// its resource claims, or its position. Each function is the exact logic ForestRoom runs per move.

describe("filmVisible (A1 — server-authoritative filming)", () => {
  // Hunter at origin looking down -z (yaw 0 → forward = (0,-1)); Bigfoot 10 m in front.
  const HRY = 0;
  const bf = { x: 0, z: -10 };
  const range = 38;
  const aimCos = Math.cos(0.6);

  it("accepts a clear, in-range, in-frame shot", () => {
    expect(filmVisible([], 0, 0, HRY, bf.x, bf.z, range, aimCos)).toBe(true);
  });

  it("rejects filming through a collider (the wall-hack the old client-trusted check allowed)", () => {
    const wall: Collider[] = [{ x: 0, z: -5, r: 1 }]; // squarely between hunter and Bigfoot
    expect(filmVisible(wall, 0, 0, HRY, bf.x, bf.z, range, aimCos)).toBe(false);
  });

  it("rejects filming while facing away", () => {
    expect(filmVisible([], 0, 0, Math.PI, bf.x, bf.z, range, aimCos)).toBe(false);
  });

  it("rejects filming beyond range", () => {
    expect(filmVisible([], 0, 0, HRY, 0, -50, range, aimCos)).toBe(false);
  });
});

describe("speed gate (A3 — token bucket, not per-message slack)", () => {
  it("refills at max speed and caps at the burst", () => {
    expect(refillAllowance(0, 8.5, 0.05, 3)).toBeCloseTo(0.425);
    expect(refillAllowance(2.9, 8.5, 1, 3)).toBe(3); // capped
  });

  it("passes a step within budget through unchanged", () => {
    const g = gateStep(0, 0, 1, 0, 3);
    expect(g.x).toBeCloseTo(1);
    expect(g.spent).toBeCloseTo(1);
  });

  it("clamps an over-budget teleport to the budget distance along the requested direction", () => {
    const g = gateStep(0, 0, 200, 0, 3); // a 200 m teleport, 3 m budget
    expect(Math.hypot(g.x, g.z)).toBeCloseTo(3);
    expect(g.spent).toBeCloseTo(3);
  });

  it("gains no free distance from move-spam with no elapsed time", () => {
    // Spam 100 tiny 'moves' that each request a big jump, but with dt=0 the bucket never refills.
    let allowance = 3; // start full
    let x = 0;
    let traveled = 0;
    for (let i = 0; i < 100; i++) {
      allowance = refillAllowance(allowance, 8.5, 0, 3); // dt=0 → no refill
      const g = gateStep(x, 0, x + 50, 0, allowance);
      traveled += g.spent;
      allowance -= g.spent;
      x = g.x;
    }
    expect(traveled).toBeLessThanOrEqual(3 + 1e-6); // total travel bounded by one burst, not 100×
  });
});

describe("staminaCeiling (A2 — resource envelope)", () => {
  it("allows regen up to the sim rate plus slack", () => {
    expect(staminaCeiling(50, 12, 0.05, 2)).toBeCloseTo(52.6);
  });

  it("rejects an implausible jump (client reports 100 after a fraction of a second)", () => {
    const ceil = staminaCeiling(20, 12, 0.05, 2);
    expect(Math.min(100, ceil)).toBeLessThan(100); // a client claiming full stamina is clamped down
  });
});
