import { describe, it, expect } from "vitest";
import { generateCaves, nearestCaveIndex, caveEmergePoint, CAVE, CAVE_GEN, WORLD } from "../../shared/sim";

// The cave helpers are shared so the client's fast-travel animation and the server's authoritative
// `caveTravel` land on the same spot; these pin that contract (A5 — single source of truth).

describe("cave layout", () => {
  const caves = generateCaves(WORLD.seed);

  it("generates the configured count, well-spaced on the outer ring", () => {
    expect(caves.length).toBe(CAVE_GEN.count);
    for (let i = 0; i < caves.length; i++) {
      const r = Math.hypot(caves[i].x, caves[i].z);
      expect(r).toBeGreaterThanOrEqual(CAVE_GEN.minRadius - 1);
      expect(r).toBeLessThanOrEqual(CAVE_GEN.minRadius + CAVE_GEN.radiusSpan + 1);
      for (let j = i + 1; j < caves.length; j++) {
        expect(Math.hypot(caves[i].x - caves[j].x, caves[i].z - caves[j].z)).toBeGreaterThanOrEqual(CAVE_GEN.minSpacing);
      }
    }
  });
});

describe("nearestCaveIndex", () => {
  const caves = generateCaves(WORLD.seed);

  it("finds the cave you're standing in the mouth of", () => {
    expect(nearestCaveIndex(caves, caves[2].x, caves[2].z)).toBe(2);
  });

  it("returns -1 out in the open", () => {
    expect(nearestCaveIndex(caves, 0, 0)).toBe(-1);
  });

  it("respects the trigger radius edge", () => {
    const c = caves[0];
    expect(nearestCaveIndex(caves, c.x + CAVE.triggerRadius - 0.1, c.z)).toBe(0);
    expect(nearestCaveIndex(caves, c.x + CAVE.triggerRadius + 0.5, c.z)).toBe(-1);
  });
});

describe("caveEmergePoint", () => {
  it("emerges emergeOffset metres toward centre, facing back into the forest", () => {
    const c = generateCaves(WORLD.seed)[0];
    const e = caveEmergePoint(c);
    expect(Math.hypot(e.x, e.z)).toBeCloseTo(Math.hypot(c.x, c.z) - CAVE.emergeOffset, 5);
    expect(e.yaw).toBeCloseTo(Math.atan2(c.x, c.z), 10);
  });
});
