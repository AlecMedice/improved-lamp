import { describe, it, expect } from "vitest";
import { dealSpecialties, SPECIALTY_IDS, CHARACTER_NAME, isSpecialtyId, type SpecialtyId } from "../../shared/sim";

// The enabling layer: personas are dealt distinct and random, with the ?devSpecialty debug force honoured.
const rng = () => Math.random();

describe("dealSpecialties", () => {
  it("deals a distinct specialty to each of five searchers", () => {
    const sids = ["a", "b", "c", "d", "e"];
    const deal = dealSpecialties(sids, {}, rng);
    const dealt = sids.map((s) => deal[s]);
    expect(dealt.every(isSpecialtyId)).toBe(true);
    expect(new Set(dealt).size).toBe(5); // all distinct
  });

  it("handles fewer than five searchers (still distinct)", () => {
    const sids = ["a", "b", "c"];
    const deal = dealSpecialties(sids, {}, rng);
    expect(new Set(sids.map((s) => deal[s])).size).toBe(3);
  });

  it("honours a forced pick and never duplicates it onto a random searcher", () => {
    const sids = ["a", "b", "c", "d", "e"];
    const forced: Record<string, SpecialtyId | undefined> = { c: "photo" };
    // Run repeatedly since the rest are random — the invariant must hold every time.
    for (let i = 0; i < 50; i++) {
      const deal = dealSpecialties(sids, forced, rng);
      expect(deal.c).toBe("photo");
      const others = sids.filter((s) => s !== "c").map((s) => deal[s]);
      expect(others).not.toContain("photo"); // forced id consumed from the pool
      expect(new Set(sids.map((s) => deal[s])).size).toBe(5);
    }
  });

  it("is deterministic given a seeded rng", () => {
    const seeded = () => 0.42; // constant rng → identical deal
    const sids = ["a", "b", "c", "d", "e"];
    expect(dealSpecialties(sids, {}, seeded)).toEqual(dealSpecialties(sids, {}, seeded));
  });
});

describe("specialty identity", () => {
  it("names every id", () => {
    for (const id of SPECIALTY_IDS) expect(CHARACTER_NAME[id]).toBeTruthy();
  });

  it("validates ids", () => {
    expect(isSpecialtyId("tracking")).toBe(true);
    expect(isSpecialtyId("bigfoot")).toBe(false);
    expect(isSpecialtyId(undefined)).toBe(false);
  });
});
