# Metoh migration — Bigfoot/PNW → Yeti/Himalayas

> ## ▶ DONE — read the [Build record](#build-record--what-actually-shipped) at the bottom first.
>
> **This plan has been executed** (branch `yeti_port`, 14 commits, Aug 2026). It is kept as the
> record of intent, **not** as instructions — several steps in it are now known to be wrong (the
> snowline threshold, the server-enforcement claim, the live-project path, an `IceCrack` call site,
> the Gate C command). Every one is corrected in the build record. Do not follow this literally
> any more; read what actually shipped.
>
> Still open: **nothing has been play-tested.** Verification items 4 and 5 need the editor.

**Original audience note:** this plan was written as explicit instructions for the implementing model (Opus 5). Follow it literally. Where it says "run Gate X", run it and do not proceed on red.

## Context

The game is too similar to the Steam game *BIGFOOT* (app 509980). The owner decided to re-theme the whole project to a **Yeti in the Himalayas** and rebrand it **"Metoh"** (from *metoh-kangmi*, the folklore term mistranslated as "Abominable Snowman"). Branch: `yeti_port`.

Owner-approved decisions (do NOT re-litigate):
1. **Full rename** — all code identifiers, wire strings, file names.
2. **Full rebrand to Metoh** — namespaces, directories, package names, product name, menu items.
3. **Visual re-theme in Unity ONLY.** The web client keeps its forest visuals (identifiers renamed only); web visuals are abandoned.
4. **One new mechanic ships with the migration: Deep snow & trails** (spec in Commit 5).
5. New title: **Metoh**. Room class: **MountainRoom**, room id `"mountain"`, scene `Mountain.unity`.
6. **A separate earlier branch existed for this same BIGFOOT-overlap problem**, unknown to the
   plan below when it was drafted — hence the corrections in Risks. It has since moved to its own
   standalone repo and no longer lives in this one.

## The three gates (run exactly as written)

- **Gate A (client):** `cd client && npx tsc --noEmit && npx vite build`
- **Gate B (server):** `cd server && npx tsc --noEmit && npm test`
- **Gate C (parity):** regenerate the golden fixture whenever `shared/sim` **output or key names** change, then run the harness:
  ```bash
  ./server/node_modules/.bin/tsx csharp/parity/gen-golden.ts   # rewrites golden.json
  dotnet run --project csharp/Parity                           # must end "PARITY OK"
  ```
  Note: `Parity.csproj` copies `golden.json` into `bin/` — always regen **then** run. Parity is NOT in CI today; you are the gate.
  The `ts-node` line this doc originally carried is broken on this machine (`npx -p ts-node -p typescript`
  resolves a ts-node/typescript pair that throws `Cannot read properties of undefined (reading 'fileExists')`).
  `tsx` ships in `server/node_modules` and runs the generator as-is — `gen-golden.ts` is CommonJS
  (`__dirname`) and there is no root `package.json`, so tsx treats it correctly with no flags.
  Regenerating also rewrites the file with LF endings; with `core.autocrlf` on that shows as a
  whole-file diff. Check `git diff --ignore-all-space` before assuming values moved.

Run Gates A+B after **every** commit. Run Gate C after commits 1, 2, and 5.

Before touching Unity code, read `docs/UNITY_PORT_NOTES.md` — especially [parity-lock]/[rng-lockstep] (never disturb the tree/collider RNG streams) and [feedback] (a mechanic isn't ported until its feedback is).

---

## Commit 0 — land the pending bug fixes (unrelated, already in working tree)

The three uncommitted modifications (`HPAudio.cs` audio-pool self-heal, `BigfootBot.cs` NavMeshPath-in-Awake fix, `WorldBuilder.cs` comment) are bug fixes unrelated to the re-theme. Commit them **as-is, first, under the old names** so the rename commit stays purely mechanical. Suggested message: `Fix HPAudio pool destruction on world rebuild + BigfootBot ctor crash`.

## Commit 1 — mechanical rename: bigfoot → yeti (one atomic commit)

Everything in this table lands in ONE commit so no gate ever sees a half-renamed wire contract. Nothing persists across versions and all peers rebuild together, so breaking wire renames are safe done atomically.

| Old | New | Where / notes |
|---|---|---|
| role string `"bigfoot"` | `"yeti"` | ~30 comparison sites: `server/src/rooms/ForestRoom.ts`; `client/src/core/{Game,Network,Lobby}.ts`; `client/src/entities/{LocalPlayer,RemotePlayer}.ts`; `client/src/ui/{HUD,MapView,Briefing}.ts`; `client/src/config.ts`; `client/src/main.ts`; `<option value="bigfoot">` in `client/index.html` |
| winner string `"bigfoot"` | `"yeti"` | `GameState.winner` comment, `ForestRoom.ts` win logic, client end screen |
| schema field `bigfootSpeedMul` | `yetiSpeedMul` | **Replicated Colyseus schema field = wire change.** Rename in `server/src/rooms/schema/GameState.ts` + writer in `ForestRoom.ts` + every client reader (`Network.ts` `onEscalation`, `LocalPlayer` plumbing) in the same commit. Client state is typed `any` — **tsc will NOT catch stragglers; grep is the safety net.** |
| `PLAYER.bigfootSpeedMul` | `PLAYER.yetiSpeedMul` | `shared/sim/constants.ts` + mirror `Player.BigfootSpeedMul → YetiSpeedMul` in `csharp/HollowPines.Sim/Constants.cs` |
| `PLAYER.lakeBigfootFactor` | `PLAYER.lakeYetiFactor` | same file pair + `movement.ts` / `Movement.cs` |
| `isBigfoot` (sim state field) | `isYeti` | `shared/sim/movement.ts`, `collision.ts`, `client/src/entities/LocalPlayer.ts`; C# `PlayerSimState.IsBigfoot → IsYeti` (`Types.cs`, `Movement.cs`); Unity `HPPlayer.IsBigfoot → IsYeti` (~59 uses) |
| `HPPlayer.RoleBigfoot` | `RoleYeti` | **byte value 1 unchanged — rename the const name only** |
| `GameManager.WinnerBigfoot` | `WinnerYeti` | **byte value 2 unchanged** |
| `HPPlayer.WantsBigfoot` (SyncVar) | `WantsYeti` | FishNet keys SyncVars by codegen order, not name — safe because all peers rebuild together |
| `BigfootBot` / `BigfootBot.cs` | `YetiBot` / `YetiBot.cs` | `git mv`; also `SpawnBigfootBot → SpawnYetiBot` and references in `GameManager.cs` |
| `bigfoots`, `bigfootSid`, locals | `yetis`, `yetiSid`, `yeti` | `ForestRoom.ts`, `GameManager.cs` |
| `bigfootTrajectory` golden key | `yetiTrajectory` | `csharp/parity/gen-golden.ts` + `Program.cs` (`BigfootTrajectory()` method, `bigfoot[i].*` labels) → **regen golden.json** (values identical, keys change) |
| misc | | `getBigfootPosition`, `computeBigfootInView`, `_bigfootVision`, `SetBigfootVision`, `bigfootLeft`, `hitBigfoot`, `BigfootTrajectory`, `BigfootPickHint`, `BigfootLine`, `DrawBigfoot`, `_wantsBigfoot`, test names in `server/test/sim.movement.test.ts`, the `IsSpecialtyId("bigfoot")` probe string in `Program.cs` |
| comments naming Bigfoot in **code** files | Yeti | sweep with grep; leave `docs/` for Commit 6 |

Do **NOT** rename the `HP*` class prefix (HPPlayer, HPAudio, HPHud…) — it's a generic prefix tied to prefab names/FishNet hashes; renaming buys nothing.

Verify: `grep -ri bigfoot --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=docs .` → 0 hits (package-lock excluded/regenerated). Gates A, B, C (regen golden).

## Commit 2 — rebrand to Metoh (namespaces, dirs, room, scene, packaging)

| Old | New |
|---|---|
| namespaces `HollowPines.{Sim,Game,EditorTools,Parity}` | `Metoh.{Sim,Game,EditorTools,Parity}` — includes every `using HollowPines.Sim` in Unity scripts and `Parity/Program.cs` |
| dir `csharp/HollowPines.Sim/` | `csharp/Metoh.Sim/` (`git mv`; fix the compile-include glob in `csharp/Parity/Parity.csproj` and the file map in `csharp/README.md`) |
| dir `unity/Assets/HollowPines/` | `unity/Assets/Metoh/` (`git mv`) |
| `GameSceneSetup.ScenePath` `"Assets/Scenes/Forest.unity"` | `"Assets/Scenes/Mountain.unity"` |
| `PrefabDir` `"Assets/HollowPines/Prefabs"` | `"Assets/Metoh/Prefabs"` |
| MenuItems `"Hollow Pines/…"` | `"Metoh/Set Up Game Scene (Mountain)"`, `"Metoh/Build Windows (Game)"` |
| `PlayerSettings.productName` `"Hollow Pines"` | `"Metoh"`; `BuildPath` → `Build/Windows/Metoh.exe` |
| shader name `"HollowPines/NightSky"` | `"Metoh/NightSky"` — first line of `NightSky.shader` AND the `Shader.Find` in `WorldBuilder.BuildSky` + its warning string |
| class/file `ForestRoom` / `ForestRoom.ts` | `MountainRoom` / `MountainRoom.ts` (`git mv`) |
| room id `define("forest", …)` | `define("mountain", MountainRoom)` in `server/src/index.ts` + `joinOrCreate("forest")` in `client/src/core/Network.ts` and `Lobby.ts` |
| package names `hollow-pines-client` / `hollow-pines-server` | `metoh-client` / `metoh-server` — then **run `npm install` in both `client/` and `server/` to regenerate lockfiles** (the name appears twice per lockfile; don't hand-edit) |
| `.cursor/rules/hollow-pines.mdc` | `git mv` → `.cursor/rules/metoh.mdc`; update its globs (they reference the old paths) and prose |
| `client/index.html` `<title>` | `Metoh`; `"FOREST MAP"` → `"MOUNTAIN MAP"` (full copy pass is Commit 6) |
| `TitleMenu.cs` `"HOLLOW PINES"` + hints | `"METOH"` (Bigfoot text in hints already fixed in Commit 1) |
| `unity/README.md`, banner comments, `GameSceneSetup` log strings, `HPAudio.cs` header | quick brand pass now; full rewrite in Commit 6 |

Gates A, B, C (namespace-only — **no golden regen needed**, values and keys unchanged).

## Commit 3 — Unity visual re-theme (`unity/Assets/Metoh/Scripts/Game/WorldBuilder.cs`)

**Hard rule (UNITY_PORT_NOTES [rng-lockstep]): never touch tree/collider RNG streams or any shared-sim value.** Only colors, mesh choices, and material selection — plus the undergrowth pass, which owns its private stream (`seed ^ 0x5eedb115`) and may be freely retuned.

Palette constants (lines ~50–59) — suggested values, tune by eye:

| Constant | Old | New |
|---|---|---|
| `GroundCol` | `0x2e4023` | `0xc9d6e2` moonlit snowpack |
| `TrunkCol` | `0x4a3828` | `0x3b3129` |
| `CrownDark` | `0x3a6028` | `0x2c4437` blue-green conifer |
| `CrownLight` | `0x4a7835` | `0x8fa3ad` snow-laden bough |
| `RockCol` | `0x585860` | `0x6b7078` granite |
| `LogCol` | `0x5a4030` | `0x4a3c30` |
| `LakeCol` | `0x2a5a6a` | `0x9fc4d8` ice sheet (inner emissive `0x0a2a3a` → `0x1c4258`) |
| `FernCol` | `0x35521f` | repurpose as `DriftCol 0xdde7ee` |
| `BushCol` | `0x2b4a26` | repurpose as `ScreeCol 0x565c66` |
| `TrailCol` | `0x51452f` | `0xaebac4` packed snow — must stay visually distinct from `GroundCol`; it becomes a gameplay surface in Commit 5 |

Builder changes:
- **`BuildForest`** — snowline: crown material currently alternates by `treeIndex % 2` (NOT an RNG draw, so safe to change). New rule: `World.GetHeight(x,z)` above ~60% of `HillHeight` → all `CrownLight` (snow-caked) and skip the third cone so high trees read stunted; below → mix as today. Placement/scale/rotation draws untouched.
- **`BuildUndergrowth`** — keep the same draw count and order (`x, z, kind, s, rot`): replace fern/bush meshes with **snow drifts** (squashed sphere, `DriftCol`), **scree rocks** (`ScreeCol`), and for a narrow `kind` band (~3%) a **prayer-flag pole** (thin cylinder + 4–5 tiny colored cubes: `0xc0392b/0xf1c40f/0x2980b9/0x27ae60/0xecf0f1`) — a navigation landmark in a white palette. Bias by `GetHeight`: high → scree, valley → drifts (pure `if`, no extra draws).
- **`BuildLake`** — frozen tarn: keep the terrain-conforming sheet and the `LakeDepth` slow (now read as slick ice/breaking crust). Brighter emissive ice material; optional 4–6 pressure-ridge boxes from a private RNG stream (render-only).
- **`BuildRv` → `BuildBasecamp`** — expedition basecamp on the **same seeded transform** (`WorldData.Rv` — keep the sim field name; the collider box is parity-locked, so the hut body must fill roughly the RV's 6.6×2.5×2.3 footprint): plank hut (`0x8a7a62`) + canvas A-frame tents (`0xc7563c` expedition orange) + crates. **Keep the warm lit window + porch lamp** (the safe-place beacon). `DuffelPosition()` derivation unchanged.
- **`BuildDuffel`** — recolor to expedition kit.
- **`BuildCaves`** — ice crevasses by recolor: rock → `0x8fb6c9`, dark rock → `0x4a6a80`, void → `0x06121c`, glow `0x4a6ab0` → `0x7fc0e8`. Display names `"Cave"` → `"Crevasse"`; sim `Caves` API untouched.
- **`BuildTower`** — stays; grey-weathered timber `0x5a5148`, cooler lamp.
- **`BuildCamp`** — campfire stays (expedition fire ring).
- **`BuildLighting` / `SkyKeys` (~line 1016)** — colder, slightly brighter (snow bounces moonlight): shift keys blue (dusk `0x3a3550→0x354060`, deep night `0x0a0e1c→0x0a1220`), Ambient +~20%, FogDensity −~10%, Stars up slightly at dusk/dawn. `NightSky.shader` needs **no edit** — driven from these keys.
- **`ClueMarker.cs`** — footprint → blue-shadowed snow print; branch clue → cracked-ice slab. `TrailMark` orange flag stays (pops on snow).
- **`MapView.cs`** — baked ground/lake/trail colors must match the new palette.

Gates A/B unaffected (Unity-only C#); smoke-compile per UNITY_PORT_NOTES [workflow] if the scratch csproj rig exists.

## Commit 4 — audio re-theme (`unity/Assets/Metoh/Scripts/Game/HPAudio.cs`)

- **`BranchSnap` → `IceCrack`** (`"ice_crack"`): keep the click transient, add a short descending ring (2 detuned sine partials ~800→300 Hz over ~0.25 s under the noise burst). Update both call sites (`GameManager` clue drop, `HPPlayer`).
- **`CreekBed` → tarn bed**: keep the positional source anchored at the lake (its guide-you-there function survives). Loop = low wind-gust noise + sparse **ice groans** (slow 40–70 Hz sine sweeps, 2–3 harmonics, 2–4 per loop at randomized offsets, long attack/decay). Rename `_creek → _tarn`, GameObject `"CreekBed" → "TarnBed"`.
- **Footsteps → snow crunch**: shorter bodies (soft 0.16→0.12 s, heavy 0.24→0.20 s), higher noise cutoff, amplitude-modulate the noise at ~40 Hz; heavy (Yeti) keeps the low thud.
- Optional tie-in with Commit 5: `inDeepSnow` flag into `PlayFootstep` for a duller off-trail crunch (pure presentation).
- Web `client/src/core/AudioEngine.ts` stays forest-themed (decision 3).

## Commit 5 — signature mechanic: Deep snow & trails

**Design:** snow records movement both ways — searchers track the Yeti's existing clue trail, and
**searchers now leave snow prints only the Yeti can see** (feeds its senses overlay) — while the
deep drifts that actually *impede* you collect in the valley floors: **searchers wade them, the
Yeti does not**. Zones are **derived, not replicated**, from `generatePaths`/`pathDepth`,
`lakeDepth` and `getHeight`, all already seed-deterministic.

**Owner decision — the print zone and the slow zone are SEPARATE rules.** The original spec
below applied `deepSnowFactor` everywhere off-trail. Measured against the real world
(400×400 sample grid, `WORLD.seed`), the camp clearing is 0.16% of the map, the tarn 1.32% and
the trail corridors 1.96% — so "everywhere off-trail" is **96.6%** of the map. That is not a
routing choice, it is a flat searcher nerf: it would move the Yeti's speed edge from 1.22× to
1.56× before per-night escalation. So:

- **Prints** drop everywhere off-trail (96.6% coverage) — the Yeti's tracking signal stays dense.
- **The slow** applies only in low-lying drift basins, ~⅓ of the map, which a searcher can read
  and route around. Terrain runs −10.2 … +7.8 on this seed, so the threshold is a height cut.

### 5a. Shared sim (edit the TS/C# pair identically — parity-locked)

`shared/sim/constants.ts` + `csharp/Metoh.Sim/Constants.cs`:
```ts
deepSnowFactor: 0.78,  // searcher speed multiplier at full drift depth (Yeti unaffected)
driftHeight: -2.0,     // at/above this terrain height the wind scours the crust bare — no slow
driftDepth: 1.5,       // metres below driftHeight at which the drift reaches full depth
trailPacked: 0.35,     // fraction of a corridor's half-width that feathers; the core is fully packed
```

Two exported predicates (mirror as `Movement.DeepSnowDepth` / `Movement.LeavesSnowPrints` in C#):

```ts
/** 0 = wind-scoured crust .. 1 = full knee-deep drift. Trails pack it flat; camp and tarn are exempt. */
export function deepSnowDepth(world: World, x: number, z: number): number;
/** Does a searcher standing here press a track into unbroken snow? True everywhere off-trail. */
export function leavesSnowPrints(world: World, x: number, z: number): boolean;
```

`deepSnowDepth` = basin depth × (1 − packed), where basin depth ramps over `driftDepth` below
`driftHeight`, and `packed` ramps to 1 across the outer `trailPacked` of the corridor. Feathering
the corridor rather than the plan's `lerp(deepSnowFactor, 1, pathDepth)` matters: `pathDepth`
decays to 0 at the corridor *edge*, so that form gave full speed only on the exact centreline and
made a trail feel slow everywhere but its middle. `leavesSnowPrints` is the same exclusions
without the height cut, so the slow zone is a strict subset of the print zone.

In `stepPlayer`, immediately after the lake-slow block, before displacement:
```ts
if (!st.isYeti && st.grounded && lakeDep === 0) {
  const drift = deepSnowDepth(world, st.x, st.z);
  if (drift > 0) speed *= lerp(1, PLAYER.deepSnowFactor, drift);
}
```
(Cost is trivial: `pathDepth` = 4 paths × ≤41 points per step at 20 Hz × 6 players.)

**Parity:** regen `golden.json` (the hunter trajectory starts at the origin, which is inside the camp clearing, so it may not move — check rather than assume), add a vitest case in `server/test/sim.movement.test.ts` ("a searcher in a drift basin covers less ground than one on scoured crust; the Yeti covers the same in both") and mirror it as `DeepSnowTests(seed)` in `csharp/Parity/Program.cs`. Gate C.

**How the slow applies — corrected.** The plan claimed the web build enforces it "on both sides
(client prediction + `MountainRoom.applyMove` re-validation)". It does not. `applyMove` validates a
move — world-bounds clamp, speed-gate token bucket, collision pushout, terrain feet-clamp — but it
never re-runs `stepPlayer`, so the drift slow is applied entirely by the **client's** prediction and
the server only bounds the maximum. A hacked web client can ignore deep snow exactly like a hacked
Unity client; the speed gate still caps it at sprint speed + margin, so the exploit is "not slowed",
never "faster than legitimate". That is the same trust level as all movement in the Unity build
today, and the same as `lakeHunterFactor`, which has always worked this way.

Consequence for verification: the slow **cannot** be asserted from a socket-level smoke test, since
driving the socket by hand measures the harness rather than the sim. It is covered by vitest and the
C# parity harness instead. The smoke test covers what the server genuinely owns: where prints drop
and how they are capped. Note the client-auth caveat in UNITY_PORT_NOTES [open-items] for both builds, not
just Unity.

### 5b. Searcher snow prints — reuse the Clue pipeline

**Server/web (`server/src/rooms/MountainRoom.ts`, `GameState.ts`, client):**
- Tuning consts: `SNOWPRINT_STRIDE = 2.4` (m), `SNOWPRINT_LIFETIME = 35` (s, NOT escalated), `MAX_SNOWPRINTS = 120`.
- New clue ctype `"snowprint"` — reuses the existing `Clue` schema class, no schema change.
- In `update()`: `dropSnowPrints(searchers)` — per active searcher keep a last-print position (like the Yeti's clue stride tracker); when horizontal distance ≥ stride AND `leavesSnowPrints(world, x, z)`, add a snowprint. **Cap/expire snowprints in their own accounting so they can never evict the Yeti's clue trail** (the hunters' win-condition resource; `MAX_CLUES` untouched).
- Visibility: replicated to all (Colyseus state is shared) but **render-filtered** — `client/src/world/ClueField.ts` and `MapView.ts` draw `"snowprint"` only when the local role is `"yeti"`. This is the one web-client change this mechanic ships with.

**Unity (`GameManager.cs`, `ClueMarker.cs`, `MapView.cs`):**
- `ClueMarker`: `public const byte TypeSnowPrint = 3`. Visual: two small flattened dark-blue ellipse discs (boot pair), no glow.
- `GameManager` host tick: `DropSnowPrints()` — same rule via `Movement.LeavesSnowPrints(WorldBuilder.World, x, z)` reading each searcher's replicated transform (no new RPC). Spawn `_cluePrefab` with `TypeSnowPrint` into a **separate `_snowPrints` list** with its own 35 s lifetime and FIFO cap 120 — being outside `_clues` automatically excludes prints from collect/cast/hair/duffel logic.
- Visibility: `ClueMarker.OnStartClient` disables the print's renderers unless the local player `IsYeti`; the senses overlay (`V`) adds prints to its glow pass (same emissive-swap treatment as the scent trail). `MapView` draws print dots for the Yeti only.
- `YetiBot` steering toward fresh prints = optional follow-up, not in scope.

**Feedback (required per UNITY_PORT_NOTES [feedback]):** searcher HUD shows a "DEEP SNOW" pill while `DeepSnowDepth > 0.35` — a threshold, not `> 0`, so the pill doesn't flicker across a feathered basin edge (Unity `HPHud.cs` required; web `HUD.ts` optional); off-trail footsteps duller (Commit 4 hook); one briefing-card sentence per role. Note the pill tracks the *slow*, which is the thing the searcher can act on; prints are deliberately not surfaced to them.

Gates A, B (new vitest case), C (regen golden + mirrored test).

## Commit 6 — docs + copy rewrite

- `docs/STORY.md` — Himalayan rewrite: a high valley near a glacial tarn; metoh-kangmi folklore; keep the five characters/specialties, re-ground them as an expedition.
- `docs/GAME_DESIGN.md` — Yeti terminology; add the Deep snow & trails section (zone rule, `deepSnowFactor`, print stride/lifetime/visibility); lake wading → ice/slush.
- `docs/CHARACTER_FUNC_DEV.md` — terminology pass (plaster casts → snow casts).
- `docs/ROADMAP.md` — touch-ups; note parity-regen events.
- `docs/BIGFOOT_DEPTH.md` — **delete** (its premise is the BIGFOOT-overlap decision this migration resolves); port any still-relevant depth ideas into GAME_DESIGN.md first.
- `docs/UNITY_PORT_NOTES.md` — sync paths in [workflow] (doc is stale on the live project path), three-trees list (`unity/Assets/Metoh/{Scripts,Shaders}`, `csharp/Metoh.Sim`), `Metoh.exe` cmdline, menu names, add the client-auth deep-snow caveat to [open-items].
- `README.md`, `CLAUDE.md` — full rewrite (orientation, message contract with `yetiSpeedMul` + `"snowprint"`, room `"mountain"`, run instructions).
- `client/index.html` + `ui/Briefing.ts` — copy strings.
- `docs/July19Work.md` — was left as-is at the time (explicit historical record, and the one allowed
  grep hit). **Deleted 2026-08-05** at the owner's call; it is in git history if it is ever wanted.

## Commit 7 (optional) — add a `parity` CI job to `.github/workflows/ci.yml` (`actions/setup-dotnet@v4`, .NET 8, `dotnet run --project csharp/Parity`). First confirm `csharp/Parity/bin|obj` are untracked.

---

## Risks / gotchas (read before starting)

1. **Owner must re-sync the live Unity project after Commit 2 — into a NEW folder.** The repo has
   no `.meta`/`Library`; the existing playable copy was a SEPARATE tree belonging to an unrelated
   earlier build (not the Bigfoot build this doc assumed, and with no `BigfootBot.cs` at all). The
   plan's original instruction ("delete the old `Assets/HollowPines/` and copy the new trees over")
   would therefore have overwritten that unrelated build in place. Instead: **set up a fresh
   `C:\Users\amedi\Metoh_port`** and leave the old tree untouched. Steps: create the new Unity
   project, copy the three trees in, run **Metoh → Set Up Game Scene (Mountain)** (scene/prefabs/SceneIds/
   AssetPathHashes all regenerate together — that's why the PrefabDir change is safe), rebuild
   `Metoh.exe`. Old builds can never talk to new ones. **Give the owner click-by-click steps when
   reaching that point** (they're new to Unity).
2. **`yetiSpeedMul` is a breaking Colyseus wire change** — client+server deploy together (fine, nothing persists), but client state is `any`-typed: grep, not tsc, catches missed readers.
3. **FishNet SyncVar renames** (`WantsYeti`) are safe only across a synchronized rebuild — never mix old/new builds.
4. **Golden regen discipline:** regen in Commits 1 and 5 only, then immediately `dotnet run`.
5. **Never disturb tree/collider RNG lockstep** ([rng-lockstep]) — Commit 3 changes must be material/mesh-level or in the undergrowth's private stream; the height-based crown rule uses no RNG draw, which is why it's legal.
6. **Snowprints must live in their own list/cap** on both server implementations so they can't evict the Yeti trail.
7. Trail coupling raises the stakes on any future "bigger map" work: paths stopping short would strand players in permanent deep snow.
8. No case-only path renames on Windows (none are planned; keep it that way).

## Verification checklist

1. Gates A+B green after every commit; Gate C ends `PARITY OK` after Commits 1, 2, 5.
2. `grep -ri "bigfoot\|hollowpines\|hollow.pines" --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj --exclude-dir=dist --exclude-dir=.git .` → zero hits outside this file (and, at the time, `docs/July19Work.md`, since deleted). `dist/` and `.git` must be excluded too: `client/dist/` exists locally as an untracked build artifact and currently holds stale pre-rebrand bundles, and `.git` holds old commit messages — all three would otherwise show as false hits.
3. Web smoke (throwaway `client/_smoke.mjs`, per CLAUDE.md pattern): yeti + searcher join; roar/grab/film; winner `"yeti"`; off-trail searcher measurably slower; snowprint clues appear in state.
4. Unity solo (owner play-test): snow world with legible packed trails, prayer flags/basecamp/crevasses, "DEEP SNOW" pill + slow off-trail, ice-crack + tarn-groan audio, title "METOH", `Metoh.exe`.
5. Unity two-instance: snow prints appear behind the searcher, visible only in the Yeti instance (+ glow under `V`); Yeti unaffected by deep snow; grab/dazzle/film/revive intact.
6. New vitest deep-snow case + `DeepSnowTests` in `Program.cs` both green.

---

# Build record — what actually shipped

*Written after execution. Everything above is the PLAN as amended before work started; this section
is the outcome, including the places the plan turned out to be wrong. Branch `yeti_port`, 14 commits.
**Nothing below has been play-tested** — every gate here is a compiler or a test, never an eye.*

```
43fe8a7 Realism pass: give every surface a material instead of a colour
d94237f Add the CPU searcher shell, and turn PLAY AS YETI on
9c12be7 Give the CPU Yeti tracking, reactions, and a reason to hold its roar
1525af4 Finish the copy pass: the title cards still said Hollow Pines
11df097 Add a parity CI job so the C# sim can't drift unnoticed
60e7175 Rewrite the docs for Metoh, and retire the Bigfoot depth plan
3d64ffa Snow prints: searchers leave tracks only the Yeti can read
a68043c Deep snow: drifts slow searchers, the Yeti crosses them
d8cfc9c Re-theme the audio: ice, not wood and water
38df898 Re-theme the world: a Himalayan snowfield, not a forest
b9bdee6 Rebrand to Metoh: namespaces, directories, room, scene, packaging
893d375 Rename the creature: Bigfoot becomes Yeti
238e801 Add the Metoh migration plan, with the corrections it needed
25978cd Fix HPAudio pool destruction on world rebuild + BigfootBot ctor crash
```

Commit 5 was split (5a sim + parity, 5b the feature) so the parity-locked change bisects on its own.
Commit 7 was taken, not skipped.

## Where the plan was wrong

Recorded because these are failure *shapes* worth recognising again, not just fixes.

1. **The snowline threshold would have done nothing.** The plan said "above ~60% of `HillHeight`".
   `HillHeight` is the noise **amplitude** (14), not a reachable height — the shipping seed's terrain
   spans about −10.2 to +7.8, so that cut sits above the highest ground on the map and would have
   snow-caked exactly zero trees. Replaced with a measured constant (3.0 ≈ the 85th percentile on a
   400×400 sample grid). *Shape: a tuning value derived from a generator's parameter rather than from
   its output.*
2. **The golden fixture could not see deep snow.** Regenerating after the sim change produced a
   **byte-identical file**, which reads as "nothing changed" and actually meant the fixture had no
   probe capable of observing the new behaviour: the hunter trajectory starts at the origin, inside
   the camp clearing, and 40 sprint steps carry it ~17 m, so it never leaves the exempt zone. Added
   `deepSnowProbes` (full basin, feathered edge, scoured ridge, trail, tarn, camp) and
   `driftTrajectory`. *Shape: a green test that never executed the code.* The CI job in Commit 7 now
   regenerates and diffs the fixture for exactly this reason.
3. **The slow is not server-enforced, on either build.** The plan claimed the web build validated it
   "on both sides (client prediction + `applyMove` re-validation)". `applyMove` validates a move —
   bounds, speed-gate token bucket, collision pushout, feet clamp — but never re-runs `stepPlayer`,
   so the drift slow is applied entirely client-side, exactly as in Unity. A hacked client can be
   "not slowed", never "faster than legitimate". Same trust level `lakeHunterFactor` has always had.
4. **The live Unity project was not what the plan assumed.** The plan described the existing live
   project folder as the Bigfoot build and told the owner to delete `Assets/HollowPines/` inside
   it. That tree was actually an unrelated earlier build. Re-sync now targets a **fresh**
   `C:\Users\amedi\Metoh_port`, leaving the old project folder intact.
5. **`IceCrack` had one call site, not the two listed** — `ClueMarker.OnStartClient`, where the clue
   drop actually makes its noise.
6. **The Gate C `ts-node` invocation does not run on this machine.** `tsx` from `server/node_modules`
   runs the generator unmodified.

## Beyond the plan

Four commits of work the migration did not cover.

**Copy pass finished (1525af4).** The web title card was still `<h1>HOLLOW&nbsp;PINES</h1>` — the
rebrand's sed missed it because of the `&nbsp;` entity, so the first screen any web player sees was
the last one carrying the old name. Also: Unity taglines off "the pines", player-facing CAVE →
CREVASSE throughout (the sim's `Caves` API and the keybind names deliberately untouched), duffel "by
the RV" → "at basecamp", both win screens off "THE FOREST", map `LAKE` → `TARN`, and the definite
articles the mechanical rename deferred.

**Yeti AI (9c12be7).** It could already sense you honestly, but it could not *find* you honestly and
did not react to anything you did. It now follows **snow prints** rather than walking at the nearest
searcher's true position — the same information the fiction grants it, which also makes deep snow cut
both ways: stay on packed trails or in camp and it has nothing to follow. The omniscient prowl
survives only as a last-resort floor behind the F3 `[P]` toggle, and is worth play-testing **off**.
Added DAZZLED (break off and leave the beam, since roar/grab are locked anyway), DRAG (haul a victim
away from the duffel before dropping them), carrier-and-bogged target preference, and roar discipline
(hold it unless it catches two, or the lone target is inside ~14 m). Crevasse fast-travel was a silent
no-op for bots — `TargetTeleport` is a TargetRpc to an owning client — so `TryCaveTravel` now branches
to a server-side path.

**CPU searchers (d94237f).** A deliberate **shell**: perception, the priority ladder
(FLEE → REVIVE → BANK → FILM → COLLECT → INVESTIGATE → EXPLORE), navigation and every server
hand-off are real and wired; the judgement inside several rungs is shallow and marked TODO.
`PLAY AS YETI` is live. Full state, and the honest list of what is missing, in
`UNITY_PORT_NOTES.md` [searcher-bots].

**Realism pass (43fe8a7).** Owner-directed change of art direction; supersedes the low-poly framing
in `GAME_DESIGN.md` [workflow] for the Unity target. The cause of the "just polygons" look was **materials,
not mesh density** — every surface was `URP/Lit`, flat colour, smoothness 0.05. Fixed with
procedurally generated normal maps (`ProcTex.cs`, tileable noise, still no asset files), per-class
PBR response, bounce-weighted Trilight ambient, soft shadows and split toning. The blocker was that
no generated mesh carried **UVs or tangents**, without which a normal map cannot bind at all. Full
write-up in `UNITY_PORT_NOTES.md` [materials] — including the one win impossible from this repo: **SSAO is a
URP Renderer Feature living on a `.asset` the repo does not track.** Owner step, and the largest
remaining gain, because AO is what grounds an object instead of leaving it hovering over the snow.

## After the record — two more art passes (2026-08-05)

Both landed after the build record above was written, and neither is part of the migration. Recorded
here because this file is where someone looks to find out what state the build is actually in.

**Legibility pass (0ef25a8).** Owner report after the realism pass shipped: *"everything looks way too
much the same"* and *"it looks like a PS1 game"*. The second had almost nothing to do with the art —
**two settings were cancelling the entire realism pass.** `RenderScale` shipped at 0.7, so the scene
rendered at 70% and was upscaled, dissolving the fine normal grain [materials] is built on; and
`HighDetail = renderScale > 0.7f` was tested against a shipping default of *exactly* 0.7, so it was
false on every clean install and nobody had ever seen the expensive tier. *Shape: a boundary condition
set to the same value as a default is a switch that is always off, and it fails silently as an art
problem rather than as a bug.* Then the fix for "everything looks the same", which was a **value-range**
problem and not a hue one: `Shaders/Snowpack.shader` blends snow against wind-stripped rock by slope,
the deep-snow basin is finally visible, the value ramp widened, a seeded ridgeline renders in the
skybox, crevasses got identity colours, and light levels rose ~40%. Also `MeshUtil.Conifer` and `Rock`,
because [materials] was right about surfaces and **wrong about silhouettes**. SSAO stopped being a README step
nobody performed and became `Metoh → Configure Render Pipeline`. Full write-up in [legibility].

**Graphics pass (this commit).** Owner report: *"the yeti is literally 2 ovals on top of one
another"* — an understatement. It was **one** primitive capsule plus two spheres for eyes, searchers
were the same capsule, and there was **no character animation anywhere in the project**. Every body
slid across the snow in a fixed pose.

- **Jointed procedural bodies** (`Avatar.cs` over new `MeshUtil.Lathe`/`Limb`/`Blob`) — a hierarchy of
  separate meshes moved by transform. No rig, no skinning, no `.fbx`, because an asset file would break
  "clone it and it runs". The Yeti is the owner-chosen **hunched bruiser**: shoulder yoke ~1.7x hip
  width, head ahead of the spine rather than on top of it, arms hanging below the knee.
- **Full procedural animation** — walk cycle, counter-swinging arms, torso lean into turns and into
  speed, head leading the turn, and poses for roar / carry / film / frozen / incapacitated. Driven
  **entirely from already-replicated state**; it adds no SyncVar and no RPC, so it cannot desync.
- **Weather** (`Weather.cs`) — falling snow, ground spindrift, breath vapour on remotes, motes in the
  torch beam. The project had **zero** particle systems. It follows `Camera.main` and bootstraps before
  connection, so the **title cinematic gets the same weather the match does**.
- **Title card** (`TitleActors.cs`) — the menu backdrop is the longest look anyone gets at the bodies,
  and it was an empty valley. Searchers now idle around the fire in the camp shot and the Yeti crosses
  the corridor in the trail shot. They touch no networking and are destroyed on connect.
- **Torch** — a visible additive beam (`Shaders/TorchBeam.shader`), riding the **hand** so a remote's
  beam swings with their arm, warmer colour, and owner-only soft shadows on the high tier.
- **Props** — cave mouths (a scaled sphere is a convex shape doing the job of a hole), their brows,
  hanging icicles, real ridge tents instead of four-sided pyramids, the duffel, footprint pads and
  broken-ice slabs, and the dropped proof pile.

*Shape worth keeping from this one:* the realism pass added a stretched fur normal map and applied it
to a capsule. **A material cannot fix an outline** — and where [legibility] learned that on trees, the same
mistake was sitting untouched on the creature the whole game is named after.

## Pre-existing bugs found along the way

Each was found by touching something adjacent, and each had been quietly wrong for a while.

- `SetTimeOfDay` re-asserted `LightShadows.Hard` **every frame**, so shadow quality could never be
  configured from anywhere.
- The world **leaked its entire material set on every reseed** (`new Material` allocates a native
  object Unity does not collect). Survivable at a couple of dozen; not at ~200 after per-chunk
  tinting. Now swept on rebuild.
- `ServerBotDrive` skipped writing `Battery`, reasoning that "the bot never lights a torch" — true
  while the only bot was the Yeti. CPU searchers do, and a stale battery makes them invisible to
  Sam's spare-battery scan, which skips anyone at ≥ 99%. *When a second kind of something appears,
  re-read the assumptions the first one baked in.*
- Snow prints stay in `ClueMarker.All` on a searcher's client (only the renderer is hidden), so
  unfiltered, a searcher's own tracks would satisfy the evidence-in-sight test and permanently unlock
  their clue-trail map layer. `ClueMarker.IsYetiTrail` now gates every consumer of that list.
- `ClueMarker` registered in `All` on the **client** callbacks, so a server-side reader worked only by
  the accident of a listen host also being a client. Moved to the network callbacks.
- URP drives every main-texture UV from `_BaseMap_ST` and every detail UV from `_DetailAlbedoMap_ST`;
  tiling set on `_BumpMap` or `_DetailNormalMap` is silently ignored.

## Verification status

| Gate | State |
|---|---|
| A — client `tsc` + `vite build` | green |
| B — server `tsc` + vitest | green, **39 tests** (was 35) |
| C — `PARITY OK` | green, with 26 new deep-snow cross-checks; now in CI with a regen-and-diff step |
| Unity smoke-compile ([workflow]) | green, 0 warnings 0 errors |
| Web smoke (throwaway, deleted) | green — prints appear, coexist with the Yeti trail, never drop in camp, track the searcher not the Yeti, stop when standing still |
| Brand sweep | zero hits outside this file (`docs/July19Work.md`, the other permitted hit, has since been deleted) |
| **Owner play-test** | **not done — nothing here has been seen or heard** |

Checklist items 4 and 5 (Unity solo, Unity two-instance) remain **open**: they need the editor and a
person. The deep-snow smoke test also proved *why* the slow itself cannot be asserted at socket
level — hand-driving a socket measures the harness, not the sim — so that stays covered by vitest and
the parity harness instead.

## 2026-08-06 — merging `main`, and finding out none of this had ever run

Two things happened this day, and the second one reframes the first.

**1. `main` and `yeti_port` had built the same feature twice.** While this branch wrote `Avatar.cs`,
a cloud-agent PR (#9, `claude/graphics-realism-title-screen-w5fc2v`) landed `CharacterMesh.cs` +
`CharacterRig.cs` on `main` — the same procedural-body feature, independently, down to the same
`ObjectId * 31` variant hash and the same distance-driven gait rule. ~1,300 lines of parallel work.

Resolved by merge commit `e160a30`, **keeping `Avatar.cs`** (richer pose set — roar, carry, filming,
frozen/incap, yaw-rate head lead — and `TitleActors`/`TorchBeam` are built on its anchors, while
nothing depended on the rig) and deleting the other implementation **whole**. Three of the losing
branch's findings outlived its code and were kept: the parka recolour (broken on *both* branches, in
different ways), the leaked reparented flashlight in `DisposeVisuals`, and the JOIN timeout stranded
below an early return. Full detail in `UNITY_PORT_NOTES.md` [bodies]. Lost and not yet replaced:
`SetStatusTint`, so frozen/incap/dazzled bodies currently read from pose alone.

`main` was then fast-forwarded to the merge; both branches and both remotes sit on `e160a30`.

**2. None of it had ever been compiled by Unity.** The live project had not been synced since
**2026-08-01** or opened since **08-02**. Everything from the bodies pass onward — `Avatar.cs`,
`Weather.cs`, `TorchBeam.cs`, `TitleActors.cs`, the startup fix — existed only in the repo.

The owner's two reports that drove this day's work (*"the yeti is literally 2 ovals"*, *"the title
screen hangs on an image"*) were **accurate**: that is exactly what Aug-1 code does. Capsule bodies
are precisely what the build had, because `Avatar.cs` was not in it. The mistaken belief was on the
repo side — that committing and pushing put code in front of the player. It does not, and the
distance between "committed" and "running" had grown to five days without anything surfacing it.

Synced, then rebuilt headlessly (`-batchmode -executeMethod GameSceneSetup.SetUpScene`): **0
`error CS`**, scene wired. That first real compile immediately found something no amount of reading
would have:

> **`Snowpack.shader` — the terrain shader — had never parsed.** `[Header(Break-up)]`: ShaderLab
> reads the text inside `[Header(...)]` as a bare token, so the **hyphen** is a parse error that
> fails the *entire* shader. And `WorldBuilder` handles it *gracefully* — `Shader.Find` misses, it
> logs a warning, falls back to flat snow — so the slope rock-blend, the deep-snow basin tint and
> the wind-scour had simply never rendered, presenting as "the art didn't land" rather than as a
> bug. One character. Third time on this project that material work silently didn't render; written
> up under [legibility] with the rule: **if a `Shader.Find` fallback fires, treat it as an error.**

The durable lesson is in `CLAUDE.md` and [workflow]: **editing this repo does not change what the
owner plays**, and a play-test report must be dated against
`Library/ScriptAssemblies/Assembly-CSharp.dll` before it is debugged.

## If you are picking this up next

**State:** `main` == `yeti_port` == `origin/*` == `e160a30`. Working tree carries the uncommitted
Snowpack fix + these docs. The live project at `C:\Users\amedi\Metoh_port` **is** synced and
compiles clean as of 2026-08-06 — but has still never been in Play mode.

In rough order of value:

1. **Run it — and this time it is genuinely runnable.** Everything visual and audible is still
   unseen: all character animation, all weather, the torch shaft, the title actors, and the snowpack
   material that has never once rendered. The animation pass is mostly *motion*, which a compiler
   cannot check at all — limb rotation signs were derived in a comment rather than observed, so an
   arm swinging backwards is a sign flip, not a redesign.
2. **Read the `[boot]` line in the Console** and settle the title-screen hang. The NavMesh bake is
   out of `Awake`, but the geometry build (~2,500 trees + ~5,200 undergrowth meshes) is still
   synchronous before the first frame. If `geometry` dominates, spread it across frames — the
   cinematic only needs the *sim* world (`GetHeight`, `Paths`), not the meshes, and collision is
   analytic, so deferring the geometry is safe.
3. **Give `Avatar` a colour API** to restore the status tint lost with `CharacterRig` — frozen,
   incapacitated and dazzled currently have no colour signal at all.
4. **Add SSAO** in the live project ([materials]) — biggest remaining visual gain, two minutes of clicking.
5. **`SearcherBot`'s EXPLORE**, which is random roam, and is why a bot team reads as five people
   wandering rather than as a search party.
6. **Balance the deep-snow constants** — `deepSnowFactor`, `driftHeight`, `driftDepth`, print
   stride/lifetime are all first-guess and none has met a player.
