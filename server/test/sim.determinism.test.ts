import { describe, it, expect } from "vitest";
import { makeWorld, generateCaves, WORLD } from "../../shared/sim";

// The whole client-prediction / server-authority design rests on the sim being bit-identical on both
// sides for a given seed. These pin that: the same seed must rebuild the exact same world, and a
// different seed must actually differ (so the seed is really driving generation).

describe("world generation is deterministic", () => {
  it("rebuilds identical caves for the same seed", () => {
    expect(generateCaves(WORLD.seed)).toEqual(generateCaves(WORLD.seed));
  });

  it("rebuilds identical colliders (count + positions) for the same seed", () => {
    const a = makeWorld(WORLD.seed);
    const b = makeWorld(WORLD.seed);
    expect(b.colliders.length).toBe(a.colliders.length);
    expect(b.colliders.map((c) => [c.x, c.z, c.r])).toEqual(a.colliders.map((c) => [c.x, c.z, c.r]));
    expect(b.climbables.length).toBe(a.climbables.length);
    expect(b.fallenLogs).toEqual(a.fallenLogs);
  });

  it("samples identical terrain heights for the same seed", () => {
    const a = makeWorld(WORLD.seed).getHeight;
    const b = makeWorld(WORLD.seed).getHeight;
    for (const [x, z] of [[0, 0], [12, -34], [-120, 200], [399, -399]]) {
      expect(b(x, z)).toBe(a(x, z));
    }
  });

  it("produces a different world for a different seed", () => {
    const a = makeWorld(WORLD.seed);
    const b = makeWorld(WORLD.seed + 1);
    const same = a.colliders.length === b.colliders.length &&
      a.colliders.every((c, i) => c.x === b.colliders[i].x && c.z === b.colliders[i].z);
    expect(same).toBe(false);
  });

  it("keeps terrain finite even at the world corners (no NaN leaks)", () => {
    const h = makeWorld(WORLD.seed).getHeight;
    for (const [x, z] of [[400, 400], [-400, -400], [400, -400]]) {
      expect(Number.isFinite(h(x, z))).toBe(true);
    }
  });
});
