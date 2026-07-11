import { describe, it, expect } from "vitest";
import { makeWorld, stepPlayer, PLAYER, WORLD } from "../../shared/sim";
import type { PlayerSimState, MoveInput, StepModifiers } from "../../shared/sim";

const world = makeWorld(WORLD.seed);
const MODS: StepModifiers = { speedMul: 1, batteryDrainMul: 1, staminaDrainMul: 1 };

function makeState(over: Partial<PlayerSimState> = {}): PlayerSimState {
  const groundY = world.getHeight(0, 0);
  return {
    x: 0, z: 0, feetY: groundY, groundY, vy: 0, grounded: true, yaw: 0,
    stamina: 100, exhausted: false, battery: 100, curEye: PLAYER.eyeHeight,
    flashlightOn: false, isBigfoot: false, eyeHeight: PLAYER.eyeHeight, ...over,
  };
}

function makeInput(over: Partial<MoveInput> = {}): MoveInput {
  return {
    w: false, s: false, a: false, d: false, yaw: 0, jump: false, leap: false,
    climb: false, vault: false, sprint: false, crouch: false, dt: 0.05, ...over,
  };
}

describe("stamina drain + exhaustion gate", () => {
  it("sprinting drains stamina to exhaustion, then blocks sprinting until recovery", () => {
    const st = makeState();
    for (let i = 0; i < 200 && !st.exhausted; i++) stepPlayer(st, makeInput({ w: true, sprint: true }), world, MODS);
    expect(st.exhausted).toBe(true);
    expect(st.stamina).toBe(0);

    // Now stop and recover; sprinting stays gated until stamina passes staminaRecover.
    let regainedBelowThreshold = true;
    for (let i = 0; i < 200 && st.exhausted; i++) {
      stepPlayer(st, makeInput(), world, MODS);
      if (st.exhausted && st.stamina >= PLAYER.staminaRecover) regainedBelowThreshold = false;
    }
    expect(st.exhausted).toBe(false);
    expect(regainedBelowThreshold).toBe(true); // exhausted only cleared at/after the recovery threshold
    expect(st.stamina).toBeGreaterThanOrEqual(PLAYER.staminaRecover);
  });
});

describe("battery drain", () => {
  it("drains only while the flashlight is on, and cuts out at empty", () => {
    const st = makeState({ flashlightOn: true });
    stepPlayer(st, makeInput({ dt: 1 }), world, MODS);
    expect(st.battery).toBeCloseTo(100 - PLAYER.batteryDrainPerSec, 5);

    for (let i = 0; i < 200 && st.flashlightOn; i++) stepPlayer(st, makeInput({ dt: 1 }), world, MODS);
    expect(st.battery).toBe(0);
    expect(st.flashlightOn).toBe(false);
  });
});

describe("Bigfoot leap", () => {
  it("launches at leapSpeed, spends stamina, and peaks near the analytic apex", () => {
    const st = makeState({ isBigfoot: true });
    const groundY = st.groundY;

    stepPlayer(st, makeInput({ leap: true }), world, MODS);
    expect(st.grounded).toBe(false);
    // Leap spends leapStaminaCost, then the same tick's regen adds a little back (not sprinting/climbing).
    expect(st.stamina).toBeCloseTo(100 - PLAYER.leapStaminaCost + PLAYER.staminaRegenPerSec * 0.05, 5);
    expect(st.vy).toBeCloseTo(PLAYER.leapSpeed - PLAYER.gravity * 0.05, 5);

    // Fly the arc out (no horizontal input keeps groundY fixed) and track the apex.
    let apex = st.feetY;
    for (let i = 0; i < 200 && !st.grounded; i++) {
      stepPlayer(st, makeInput(), world, MODS);
      apex = Math.max(apex, st.feetY);
    }
    const analytic = (PLAYER.leapSpeed * PLAYER.leapSpeed) / (2 * PLAYER.gravity); // v^2 / 2g ≈ 2.82 m
    expect(apex - groundY).toBeGreaterThan(analytic - 0.3);
    expect(apex - groundY).toBeLessThan(analytic + 0.4);
    expect(st.grounded).toBe(true); // came back down
  });

  it("won't leap without enough stamina", () => {
    const st = makeState({ isBigfoot: true, stamina: PLAYER.leapStaminaCost - 1 });
    stepPlayer(st, makeInput({ leap: true }), world, MODS);
    expect(st.grounded).toBe(true); // stayed on the ground
  });
});

describe("purity / robustness", () => {
  it("never produces NaN when hammered at the world edge", () => {
    const st = makeState({ x: 399, z: -399 });
    for (let i = 0; i < 50; i++) stepPlayer(st, makeInput({ w: true, sprint: true, yaw: i }), world, MODS);
    expect(Number.isFinite(st.x) && Number.isFinite(st.z) && Number.isFinite(st.feetY)).toBe(true);
    expect(Math.abs(st.x)).toBeLessThanOrEqual(400);
    expect(Math.abs(st.z)).toBeLessThanOrEqual(400);
  });
});
