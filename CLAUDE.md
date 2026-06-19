# Hollow Pines — agent orientation

Asymmetric 1‑vs‑5 multiplayer horror game. Five **searchers** hunt a Pacific‑NW forest
for proof of **Bigfoot** (played by the 6th player). Browser game: **Three.js client +
Colyseus server, TypeScript everywhere**. Stylized low‑poly, smooth‑shaded, dusk‑to‑dawn.

Read `docs/` for the full picture: `GAME_DESIGN.md` (GDD, the source of truth for
rules), `ROADMAP.md` (what's done / next), `PROMPT.md`, `STORY.md`. This file is the
fast orientation + conventions.

## Run it
Two parts, two terminals. **`ws://localhost:2567` is the backend, not a web page — open the Vite URL.**
```bash
cd client && npm install && npm run dev   # game at http://localhost:5173  ← open THIS
cd server && npm install && npm run dev    # Colyseus at ws://localhost:2567 (leave running)
```
The client runs **standalone** (offline solo) if the server is down — you just won't see
other players, clues, pings, nights, or abilities. To test multiplayer, open one tab as
Bigfoot and another as a searcher (`server/src/rooms` is authoritative).

## Verify your work (do this before claiming done)
```bash
cd client && npx tsc --noEmit && npx vite build     # client typechecks + bundles
cd server && npx tsc --noEmit                        # server typechecks
```
For server/gameplay logic, a **headless smoke test** is the fast way to prove behavior:
write a throwaway `client/_smoke.mjs` using `colyseus.js` (already installed in `client/`),
connect 1–2 clients, drive messages, assert on `room.state`, then delete it. Example
pattern used during development: join a bigfoot + searcher, send `move`/`roar`/`grab`,
read `room.state.players` / `videosCaptured`, `process.exit(ok?0:1)`. Run the server in
the background first and wait with `curl --retry --retry-connrefused http://localhost:2567/health`.
Don't commit smoke files or `client/dist/`.

## Architecture
- **Per‑client Three.js scene.** Each browser renders its own scene, so per‑role visuals
  (e.g. Bigfoot's brighter exposure/night‑vision) are just local and don't leak.
- **Server is authoritative** for match state: night clock, clues, pings, roar/grab/
  incapacitation, footage tally, win/loss. The simulation runs at 20 Hz in
  `ForestRoom.update()`.
- **Movement is client‑sent + server‑clamped** (v1). Clients stream `move` ~15 Hz; the
  server validates and ignores moves from non‑`active` players. Remotes interpolate.
  (Upgrade path: server‑authoritative movement + reconciliation — see ROADMAP Phase 2.)
- **Deterministic world.** Terrain + tree placement come from `WORLD.seed` via
  `client/src/util/rng.ts`, so every client builds the *same* forest. `CAVES` coordinates
  are **duplicated in `client/src/config.ts` and `server/src/rooms/ForestRoom.ts` — keep
  them in sync.**
- **`y` is feet height** in the network/schema (terrain height), so avatars sit on the
  ground for everyone; the local camera adds eye height.

## Client↔server message contract
Client → server (`Network.send*`):
- `move` `{x,y,z,ry, flashlightOn, battery, stamina, recording, inView}` — `y` = feet.
- `ping` `{x,z}` — hunters only (stakeout marker).
- `roar` — Bigfoot: AoE freeze.
- `grab` — Bigfoot: grab nearest frozen hunter / drop the dragged one.

Server state (`GameState`, replicated):
- `players: Map<sid, Player>` — `{role, name, x,y,z,ry, flashlightOn, battery, stamina,
  status, slowed, filming, filmProgress}`. `status ∈ "active" | "frozen" | "incapacitated"`.
- `clues: Clue[]` `{id, ctype("footprint"|"branch"), x, z, ry}` — Bigfoot's trail.
- `pings: Ping[]` `{id, x, z}` — hunter stakeout markers (1 per hunter).
- `phase, timeOfDay (0..1 of the night), nightNumber, totalNights`,
  `videosRequired, videosCaptured (team total), winner ("" | "hunters" | "bigfoot")`.

## Rules (current)
- **3 nights**, each 8pm→8am (daylight skipped, fade between nights).
- **Hunters win:** capture **3 solid videos** (hold RMB with Bigfoot in frame/range/LOS
  ~3s; pooled across the team and across nights).
- **Bigfoot wins:** **survive all 3 nights**.
- **Bigfoot offense:** RMB **roar** freezes hunters within ~25m for 30s → LMB **grab** a
  frozen hunter → incapacitate 60s (fade out, drag them, **erase the team's footage**) →
  they recover, 25% slower for 30s. Not permanent elimination.
- **Map (`M`):** both roles see self/camp/caves; hunters also see teammates, pings, and the
  *recent* clue trail **only while in contact** (Bigfoot heard nearby or recent evidence in
  sight). Bigfoot in a cave mouth clicks a cave on the map to fast‑travel.

## Where things live
Client (`client/src/`):
- `core/Game.ts` — main loop; wires input/world/net/HUD; win/end; map/cave/roar/grab.
- `core/Network.ts` — Colyseus wrapper; `on*` callbacks + `send*` + getters. State typed `any`.
- `core/Input.ts` — keyboard/mouse, pointer lock; `onPress` (keys), `onMousePress` (buttons).
- `entities/LocalPlayer.ts` — FP controller, stamina/exhaustion, `externalSpeedMul` (slow), `teleportTo`.
- `entities/RemotePlayer.ts` — avatars, Bigfoot eye‑shine, rec light, frozen/incap status icon.
- `world/Environment.ts` — terrain/forest/sky/lights/RV/caves; `getHeight`, `resolveCollision`,
  `lineBlocked`, `setTimeOfDay` (day‑night lerp), `colliders[]`.
- `world/ClueField.ts` — footprint/branch meshes; `getRecentDots`, `hasRecentClueWithin`.
- `world/PingField.ts` — in‑world ping beacons.
- `ui/HUD.ts` — DOM HUD updates. `ui/MapView.ts` — map overlay (canvas + cave buttons).
- `config.ts` — all client tunables. `index.html` — all HUD/overlay DOM + CSS. `main.ts` — bootstrap.

Server (`server/src/`):
- `rooms/ForestRoom.ts` — the authoritative room (messages, 20 Hz update, all systems + tunables at top).
- `rooms/schema/GameState.ts` — `Player` / `Clue` / `Ping` / `GameState` schema.
- `index.ts` — Colyseus + Express `/health`.

## Tuning
- **Server constants** (top of `ForestRoom.ts`): `NIGHT_SECONDS` (300), `TOTAL_NIGHTS` (3),
  `ROAR_RADIUS/ROAR_COOLDOWN/FREEZE_SECONDS`, `GRAB_RADIUS/INCAP_SECONDS/SLOW_SECONDS`,
  `FILM_RANGE/FILM_SECONDS`, `CLUE_LIFETIME/STRIDE/BRANCH_CHANCE`, `PING_LIFETIME`, `CAVES`.
- **Client constants** (`config.ts`): `PLAYER` (speeds, `staminaRecover`, `slowFactor`),
  `FILM`, `ABILITY.roarCooldown`, `MAP` (clueWindow/hearRange/evidenceSight), `CAVES`, `CAVE`.

## Conventions / gotchas
- TypeScript strict; small focused modules; match the surrounding comment density + naming.
- Aesthetic: **low‑poly with smooth vertex normals** (no blocky/voxel); fog + ACES tone mapping.
- Colyseus **0.15** schema uses **legacy decorators** — server `tsconfig` has
  `experimentalDecorators: true`, `useDefineForClassFields: false`. Don't "modernize" these.
- Light intensities are physically‑based‑ish and **tuned by eye** — expect to nudge.
- **Incapacitation erases the whole team's footage** (`state.videosCaptured = 0` in the
  `grab` handler) — intentional but swingy; easy to soften to a partial penalty.
- `.gitignore` covers `node_modules/` and `dist/`; lockfiles are committed.

## Not done yet (see ROADMAP)
Bigfoot charge/leap‑climb + full senses overlay; teammate revives; audio (roar/footsteps/
heartbeat); per‑night escalation; post‑processing (bloom/vignette); lobby/ready‑up;
server‑authoritative movement; deploy. Lock the vertical slice before piling on Phase 5+.
