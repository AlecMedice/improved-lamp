/**
 * Dumps golden values from the REAL TypeScript sim (shared/sim) so the C# port can be asserted
 * bit-for-bit against it. The TS sim is the source of truth; regenerate this whenever shared/sim
 * changes, then re-run the C# parity harness.
 *
 * Run from the repo root (requires ts-node on PATH):
 *   ts-node --skip-project \
 *     --compiler-options '{"module":"commonjs","moduleResolution":"node","esModuleInterop":true,"strict":false}' \
 *     csharp/parity/gen-golden.ts
 *
 * Then verify the port:
 *   dotnet run --project csharp/Parity
 */
import * as fs from "fs";
import * as path from "path";
import {
  mulberry32,
  makeValueNoise,
  makeTerrain,
  generateCaves,
  nearestCaveIndex,
  caveEmergePoint,
  buildColliders,
  makeWorld,
  stepPlayer,
  dealSpecialties,
  SPECIALTY_IDS,
  CHARACTER_NAME,
  reviveMul,
  staminaMax,
  staminaDrainMul,
  filmProgressMul,
  filmRangeMul,
  clueWindowMul,
  evidenceSightMul,
  hearRangeMul,
  roarDirPersistSec,
  footstepVolumeMul,
  CAVE,
  PlayerSimState,
  MoveInput,
  StepModifiers,
} from "../../shared/sim/index";

const SEED = 1337;

// 1) RNG stream
const rand = mulberry32(SEED);
const rngStream: number[] = [];
for (let i = 0; i < 8; i++) rngStream.push(rand());

// 2) value-noise samples
const noise = makeValueNoise(SEED);
const noiseSamples = [
  [0, 0], [1.5, -2.25], [10.123, 4.567], [-3.14, 2.71], [100.5, -100.5],
].map(([x, y]) => ({ x, y, v: noise(x, y) }));

// 3) terrain samples
const height = makeTerrain(SEED);
const terrainSamples = [
  [0, 0], [50, 50], [-120, -110], [220, -230], [399, -399], [17, 3],
].map(([x, z]) => ({ x, z, h: height(x, z) }));

// 4) caves
const caves = generateCaves(SEED);

// 4b) cave helpers — emerge points for every cave + a few nearestCaveIndex probes.
const caveEmerge = caves.map((c) => caveEmergePoint(c));
const nearestProbes = [
  { x: caves[2].x, z: caves[2].z, i: nearestCaveIndex(caves, caves[2].x, caves[2].z) },
  { x: 0, z: 0, i: nearestCaveIndex(caves, 0, 0) },
  { x: caves[0].x + CAVE.triggerRadius - 0.1, z: caves[0].z, i: nearestCaveIndex(caves, caves[0].x + CAVE.triggerRadius - 0.1, caves[0].z) },
  { x: caves[0].x + CAVE.triggerRadius + 0.5, z: caves[0].z, i: nearestCaveIndex(caves, caves[0].x + CAVE.triggerRadius + 0.5, caves[0].z) },
];

// 5) colliders
const colliders = buildColliders(SEED, caves);
const colliderSummary = {
  count: colliders.length,
  first3: colliders.slice(0, 3),
  last: colliders[colliders.length - 1],
};

// 6) world
const world = makeWorld(SEED);
const worldSummary = {
  colliderCount: world.colliders.length,
  climbableCount: world.climbables.length,
  fallenLogCount: world.fallenLogs.length,
  firstLog: world.fallenLogs[0],
};

// 7) stepPlayer trajectories — a hunter sprinting forward, then a Bigfoot leaping and falling.
const mods: StepModifiers = { speedMul: 1, batteryDrainMul: 1, staminaDrainMul: 1 };

const hg = world.getHeight(0, 0);
const hunter: PlayerSimState = {
  x: 0, z: 0, feetY: hg, groundY: hg, vy: 0, grounded: true,
  yaw: 0, stamina: 100, exhausted: false, battery: 100, curEye: 1.7,
  flashlightOn: true, isBigfoot: false, eyeHeight: 1.7,
};
const inputWalk: MoveInput = {
  w: true, s: false, a: false, d: false, yaw: 0.6,
  jump: false, leap: false, climb: false, vault: false,
  sprint: true, crouch: false, dt: 1 / 20,
};
const hunterTrajectory: any[] = [];
for (let i = 0; i < 40; i++) {
  stepPlayer(hunter, inputWalk, world, mods);
  if (i % 10 === 9) {
    hunterTrajectory.push({
      i, x: hunter.x, z: hunter.z, feetY: hunter.feetY,
      stamina: hunter.stamina, battery: hunter.battery, exhausted: hunter.exhausted,
    });
  }
}

const bg = world.getHeight(30, 30);
const bf: PlayerSimState = {
  x: 30, z: 30, feetY: bg, groundY: bg, vy: 0, grounded: true,
  yaw: 1.0, stamina: 100, exhausted: false, battery: 100, curEye: 2.4,
  flashlightOn: false, isBigfoot: true, eyeHeight: 2.4,
};
const inputLeap: MoveInput = {
  w: true, s: false, a: false, d: false, yaw: 1.0,
  jump: false, leap: true, climb: false, vault: false,
  sprint: false, crouch: false, dt: 1 / 20,
};
const bigfootTrajectory: any[] = [];
for (let i = 0; i < 30; i++) {
  stepPlayer(bf, i === 0 ? inputLeap : { ...inputLeap, leap: false }, world, mods);
  if (i % 6 === 5) {
    bigfootTrajectory.push({
      i, x: bf.x, z: bf.z, feetY: bf.feetY, vy: bf.vy, grounded: bf.grounded, stamina: bf.stamina,
    });
  }
}

// 8) specialties — identity, getter table, and two DETERMINISTIC deals (seeded rng) the C# port reproduces.
const specialtyIds = [...SPECIALTY_IDS];
const characterNames = specialtyIds.map((id) => CHARACTER_NAME[id]);
const getterProbe = ["endurance", "sound", "tracking", "photo", "analysis", "", "bigfoot"].map((id) => ({
  id,
  reviveMul: reviveMul(id), staminaMax: staminaMax(id), staminaDrainMul: staminaDrainMul(id),
  filmProgressMul: filmProgressMul(id), filmRangeMul: filmRangeMul(id), clueWindowMul: clueWindowMul(id),
  evidenceSightMul: evidenceSightMul(id), hearRangeMul: hearRangeMul(id),
  roarDirPersistSec: roarDirPersistSec(id), footstepVolumeMul: footstepVolumeMul(id),
}));
const dealSids = ["a", "b", "c", "d", "e"];
const dealPlain = dealSpecialties(dealSids, {}, mulberry32(999));
const dealForced = dealSpecialties(dealSids, { c: "photo" }, mulberry32(999));

const golden = {
  seed: SEED, rngStream, noiseSamples, terrainSamples, caves, caveEmerge, nearestProbes,
  colliderSummary, worldSummary, hunterTrajectory, bigfootTrajectory,
  specialties: { specialtyIds, characterNames, getterProbe, dealSids, dealPlain, dealForced },
};

const outPath = path.resolve(__dirname, "golden.json");
fs.writeFileSync(outPath, JSON.stringify(golden, null, 2) + "\n");
console.log("wrote", outPath);
