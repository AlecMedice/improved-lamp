# Hollow Pines — C# sim port (migration phase R3)

A faithful C# port of the deterministic simulation in [`shared/sim/`](../shared/sim) — the **game
logic that survives the Unity move** (movement physics, collision, terrain, world generation, RNG,
cave fast-travel, character specialties). Translated so it produces the *same* results as the
TypeScript sim; the host and clients will run identical physics under FishNet
(see [`docs/UNITY_MIGRATION.md`](../docs/UNITY_MIGRATION.md)).

```
csharp/
  HollowPines.Sim/     engine-agnostic C# sim (drop straight into a Unity Assets/ folder)
  Parity/              console harness — golden cross-check + mirrored vitest invariants
  parity/golden.json   golden values dumped from the REAL TypeScript sim (the source of truth)
  parity/gen-golden.ts the regenerator for golden.json
```

## Design constraints (why it ports cleanly)

- **No `UnityEngine` dependency.** `HollowPines.Sim` references only `System.*`, so it compiles both
  in a plain .NET project (for the parity test) and inside Unity. Keep it that way.
- **`double` everywhere**, because the TS sim uses `number` (IEEE-754 double). `float` would diverge
  on the first collision push-out. Use `float` only at the Unity transform boundary.
- **Bit-exact RNG.** `Mulberry32` uses `uint` arithmetic (wraps mod 2^32, matching JS `| 0` / `>>>` /
  `Math.imul`). The value-noise gradient table is `float[]` on purpose — the TS code stores it in a
  `Float32Array`, and that truncation is observable.
- **JS `Math.round` semantics.** Cave coordinates use `SimMath.JsRound` (round half toward +infinity).

## File map (TS → C#)

| TypeScript (`shared/sim/`) | C# (`HollowPines.Sim/`) |
| --- | --- |
| `math.ts` | `SimMath.cs` |
| `rng.ts` | `Rng.cs` |
| `constants.ts` | `Constants.cs` (`World`, `Player`, `CaveRules`, `Clue`, `CaveGen`, `Sim`) |
| `terrain.ts` | `Terrain.cs` |
| `caves.ts` (`generateCaves`, `nearestCaveIndex`, `caveEmergePoint`) | `Caves.cs` |
| `world.ts` | `WorldData.cs` + structs in `Types.cs` |
| `collision.ts` | `Collision.cs` |
| `movement.ts` (`stepPlayer`, `StepModifiers.staminaMax`) | `Movement.cs` |
| `specialties.ts` (deal + getters + identity) | `Specialties.cs` |
| `index.ts` (`World`, `makeWorld`) | `GameWorld.cs` (renamed to avoid the `World` constants class) |

## Verify determinism

```bash
dotnet run --project csharp/Parity
```

Two layers:
1. **Golden cross-check** — reconstructs the TS golden scenarios in C# and asserts against
   `parity/golden.json` (dumped from the real TS sim): RNG stream, value noise, terrain, caves +
   emerge points + `nearestCaveIndex`, colliders, world summary, hunter/Bigfoot trajectories, and the
   specialty identity/getters/deals.
2. **Mirrored property tests** — the behavioural invariants the maintained vitest suite pins
   (`server/test/sim.determinism.test.ts`, `sim.movement.test.ts`, `caves.test.ts`,
   `specialties.test.ts`), re-expressed in C#: world determinism, stamina exhaustion gate, battery
   drain, the Endurance stamina ceiling, Bigfoot leap arc, NaN-safety at the edge, cave spacing, and
   the specialty deal.

Integer / pure-arithmetic paths are checked **exactly**; `sin`/`cos`/`sqrt` paths use a 1e-9 epsilon
(JS vs .NET last-ULP — harmless, the host is authoritative). Expected tail:
`PARITY OK — C# sim matches the TypeScript golden fixture and the vitest invariants.`

### Regenerating the golden fixture

If you change `shared/sim`, re-dump from the TS source (requires `ts-node`), then re-run the harness:

```bash
# from the repo root
ts-node --skip-project \
  --compiler-options '{"module":"commonjs","moduleResolution":"node","esModuleInterop":true,"strict":false}' \
  csharp/parity/gen-golden.ts        # writes csharp/parity/golden.json
dotnet run --project csharp/Parity
```

Cross-check the TS side itself with the maintained suite: `cd server && npm install && npm test`.
