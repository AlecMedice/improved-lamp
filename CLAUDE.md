# Hollow Pines — agent orientation

Asymmetric 1‑vs‑5 multiplayer horror game. Five **searchers** hunt a Pacific‑NW forest
for proof of **Bigfoot** (played by the 6th player). Browser game: **Three.js client +
Colyseus server, TypeScript everywhere**. Stylized low‑poly, smooth‑shaded, dusk‑to‑dawn.

Read `docs/` for the full picture — every file there is current, nothing is a stale plan:
- `GAME_DESIGN.md` — the GDD, source of truth for rules · `STORY.md` — world + the five characters
- `CHARACTER_FUNC_DEV.md` — searcher specialties, the evidence/casting system, the duffel
- `ROADMAP.md` — phases · `July19Work.md` — the Unity port's build log (historical record)
- `UNITY_PORT_NOTES.md` — Unity traps, conventions and remaining work;
  **read before touching the Unity build**

This file is the fast orientation + conventions.

**Note:** the game is mid-migration to a **Unity + FishNet desktop build** (`unity/`, `csharp/`).
The web build below is still the behavioural spec, but new gameplay work lands in Unity.

## Working style (owner preference)
On collaborative design/spec sessions (story, mechanics, planning), **pause at decision
points and ask with selectable options** (the AskUserQuestion tool — the owner prefers
picking an answer over free‑form), then continue once it's answered. Don't batch several
open questions into prose and barrel ahead — surface one decision, let it be answered,
proceed. (Straightforward implementation work doesn't need this — just build.)

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
cd server && npx tsc --noEmit && npm test            # server typechecks + vitest (sim + anti-cheat)
```
The **vitest** suite (`server/test/`) covers the two things unit tests protect best: `shared/sim`
determinism (same seed ⇒ identical world + `stepPlayer` sequences) and the pure server-authority
helpers in `server/src/rooms/antiCheat.ts` (filming LOS/aim, speed-gate token bucket, resource
envelope). It imports only pure modules — no Colyseus Room/schema — so there are no decorators to
compile. Add a case here whenever you touch validation or the sim. For end-to-end *stateful* flows
(roar → grab → revive), a **headless smoke test** is still the fast way to prove behavior:
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
- **Movement is server‑authoritative** (Phase 2). The client predicts locally (shared‑sim
  `stepPlayer` in `LocalPlayer`) and streams `move` ~15 Hz; the server **re‑validates** each move
  against the shared world (`ForestRoom.applyMove`: world‑bounds clamp, speed‑gate **token bucket**,
  collision pushout, terrain feet‑clamp) and ignores moves from non‑`active` players. The client
  **reconciles** by easing toward the server's corrected position (`LocalPlayer.correctTo`);
  large desyncs snap. Remotes interpolate on a snapshot buffer. Cave fast‑travel is a validated
  `caveTravel` command. (Full input‑replay prediction is the deferred Phase 2.3 stretch — the pure
  `stepPlayer` sim is the foundation for it.)
- **The server doesn't trust client‑reported outcomes.** Pure validation helpers in
  `server/src/rooms/antiCheat.ts` back this: **filming** (the hunter win condition) is recomputed
  server‑side — range + aim cone from the replicated yaw + line‑of‑sight — so `inView` from the client
  is only a HUD hint, not the grant (`updateFilming`/`canFilm`, mirrors the dazzle beam check). The
  **speed gate** is a per‑second token bucket, not flat per‑message slack, so move‑spam earns no free
  distance. **Battery/stamina** run through a resource envelope: battery can only decrease (no pickups
  yet) and a dead battery forces the light off; stamina can't regen faster than the sim's rate.
- **Deterministic world + movement sim live in `shared/sim/`**, imported by both the client and
  the server (relative imports, no alias). Terrain height, tree/RV/cave/tower colliders, fallen
  logs, and **seeded `CAVES`** are all derived from `WORLD.seed` there, so every client and the
  server agree on the world with no duplicated coordinates. The client's `Environment` is a
  renderer that builds meshes from the shared world and delegates `getHeight`/`resolveCollision`/…
  to it; `LocalPlayer` only does presentation (camera, bob, audio, flashlight visuals).
  **Keep `shared/` pure** — no Three.js, no DOM, no decorators (the dual `tsc --noEmit` gate
  catches leaks). (Old gotcha, now fixed: `CAVES` were `Math.random()`‑duplicated and disagreed
  every run.)
- **`y` is feet height** in the network/schema (terrain height), so avatars sit on the
  ground for everyone; the local camera adds eye height.

## Client↔server message contract
Client → server (`Network.send*`):
- `move` `{x,y,z,ry, flashlightOn, battery, stamina, recording, inView, reviving, reviveTarget}` — `y` = feet.
  `reviving`/`reviveTarget` are the held-action teammate revive (like `recording` — no separate RPC).
  `inView`/`battery`/`stamina` are **hints the server re‑validates or bounds** (see anti‑cheat above),
  not trusted values; `x,z,y` are corrected by `applyMove`; only `ry` (camera aim) is taken as sent.
- `ping` `{x,z}` — hunters only (stakeout marker).
- `roar` — Bigfoot: AoE freeze *(rejected while dazzled)*. `Space` = leap (stamina-gated bound).
- `grab` — Bigfoot: grab nearest frozen hunter / drop the dragged one *(rejected while dazzled)*.
- `charge` — Bigfoot (`Shift`): opens a server-tracked speed-gate window (cooldown) so a forward burst
  dash isn't clamped as a speedhack. No move-payload field; the burst is client-predicted via `chargeMul`.
- `caveTravel` `{index}` — Bigfoot: validated cave fast‑travel (must stand in a mouth; cooldown).
- `startMatch` / `returnToLobby` — host only (lobby lifecycle).
- *(no RPC)* **surface-climb** (`Space` vs a structure) rides the existing `move` feet-y; **senses overlay**
  (`V`) is a client-only Bigfoot render toggle. Neither adds a message.

Server → client (broadcast, not state):
- `roar` `{x, z, by}` — fired on every roar so all clients can play it as **positional audio**
  from Bigfoot's real position (carries beyond the freeze radius). Clients skip their own (`by`).

Server state (`GameState`, replicated):
- `players: Map<sid, Player>` — `{role, name, x,y,z,ry, flashlightOn, battery, stamina,
  status, slowed, filming, filmProgress, connected, beingRevived, dazzled}`.
  `status ∈ "active" | "frozen" | "incapacitated"`; `beingRevived` = a teammate is reviving this
  downed hunter; `dazzled` (Bigfoot only) = a searcher's flashlight is blinding it.
- `clues: Clue[]` `{id, ctype("footprint"|"branch"), x, z, ry}` — Bigfoot's trail.
- `pings: Ping[]` `{id, x, z}` — hunter stakeout markers (1 per hunter).
- `matchPhase ("lobby"|"playing"|"results"), hostId` — lifecycle; the clock only runs while playing.
- `phase, timeOfDay (0..1 of the night), nightNumber, totalNights`,
  `videosRequired, videosCaptured (team total), winner ("" | "hunters" | "bigfoot")`.
- **Per‑night escalation** (server sets each tick from the `ESCALATION` table):
  `bigfootSpeedMul, batteryDrainMul, staminaDrainMul, roarCooldownSec`. The client applies these
  (movement/drain are client‑side in v1); freeze duration + clue lifetime are escalated
  server‑side only. Single source of truth — don't mirror the table on the client.

## Rules (current)
- **3 nights**, each 8pm→8am (daylight skipped, fade between nights).
- **Hunters win:** capture **3 solid videos** (hold RMB with Bigfoot in frame/range/LOS
  ~3s; pooled across the team and across nights).
- **Bigfoot wins:** **survive all 3 nights**.
- **Bigfoot offense:** RMB **roar** freezes hunters within ~25m for 30s → LMB **grab** a
  frozen hunter → incapacitate 60s (fade out, drag them, **erase the team's footage**) →
  they recover, 25% slower for 30s. Not permanent elimination. `Space` = **leap** (stamina-gated bound).
- **Bigfoot mobility/senses:** `Shift` = **charge** (a short forward burst dash on a cooldown, past the
  normal speed gate); `Space` against the **tower / RV / cave boulders** = **surface-climb** (scale the
  side, stamina-gated, and stand on top; step off to drop); `V` = **senses overlay** (predator vision —
  hunters and Bigfoot's own recent scent trail glow through the forest).
- **Searcher counterplay:** hold `E` near a downed teammate to **revive** them (~4s) before the incap
  expires; keep a **flashlight** trained on Bigfoot (~1.2s, range+cone+LOS) to **dazzle** it — its
  roar/grab lock and its sight cone cuts for ~3s (a deterrent, doesn't free a grabbed hunter); `Space`
  to **vault** a fallen log (stamina-gated hop that negates the log slow).
- **Map (`M`):** both roles see self/camp/caves; hunters also see teammates, pings, and the
  *recent* clue trail **only while in contact** (Bigfoot heard nearby or recent evidence in
  sight). Bigfoot in a cave mouth clicks a cave on the map to fast‑travel.

## Where things live
Client (`client/src/`):
- `core/Game.ts` — main loop; wires input/world/net/HUD/audio; win/end; map/cave/roar/grab.
- `core/Network.ts` — Colyseus wrapper; `on*` callbacks (incl. `onRoar`/`onEscalation`) + `send*` + getters. State typed `any`.
- `core/AudioEngine.ts` — all sound. **Hybrid**: cues are synthesized procedurally (no asset files),
  but any cue can be overridden by a recording in `client/public/audio/` listed in `audio/manifest.json`.
  `THREE.AudioListener` rides the camera; `playOnce`/`playAt` (positional) + wind/creek/heartbeat beds.
  Autoplay‑gated: `resume()` on first gesture (and from `Game.start()`).
- `core/Lobby.ts` — pre‑match waiting room; hands a joined `Room` to `Game`.
- `core/Input.ts` — keyboard/mouse, pointer lock; actions resolve through `Keybinds` (`isActionDown`/
  `onAction`/`captureNext`), raw `onPress`/`isDown` for fixed keys (Esc). `core/Keybinds.ts` — rebindable
  action→code map (localStorage). `ui/Briefing.ts` — the night‑1 dusk briefing (controls from `Keybinds`).
- `entities/LocalPlayer.ts` — presentation for the local player (camera, look, head‑bob, footsteps,
  flashlight visuals) around the shared `stepPlayer` sim; `externalSpeedMul` (slow), per‑night
  `nightSpeedMul`/`batteryDrainMul`/`staminaDrainMul` (composed into `StepModifiers`), `teleportTo`.
- `entities/RemotePlayer.ts` — avatars, Bigfoot eye‑shine, rec light, frozen/incap status icon,
  positional footsteps (from interpolation deltas).
- `world/Environment.ts` — terrain/forest/sky/lights/RV/caves; `getHeight`, `resolveCollision`,
  `lineBlocked`, `setTimeOfDay` (day‑night lerp), `colliders[]`.
- `world/ClueField.ts` — footprint/branch meshes; `getRecentDots`, `hasRecentClueWithin`.
- `world/PingField.ts` — in‑world ping beacons.
- `ui/HUD.ts` — DOM HUD updates. `ui/MapView.ts` — map overlay (canvas + cave buttons).
- `core/Settings.ts` + `ui/SettingsMenu.ts` — persisted player settings (brightness/gamma, volume,
  sensitivity) + the gear/`Esc` pause overlay; `Game.applySettings` live-applies (exposure/audio/look).
- **Post‑processing** lives in `core/Game.ts` (`EffectComposer`: bloom + vignette/grain `ShaderPass`);
  tunables in `config.POST`. Screen‑space flashlight beam + lens‑dirt are DOM overlays in `index.html`.
- `config.ts` — all client tunables. `index.html` — all HUD/overlay DOM + CSS. `main.ts` — bootstrap.

Server (`server/src/`):
- `rooms/ForestRoom.ts` — the authoritative room (messages, 20 Hz update, all systems + tunables at top).
- `rooms/schema/GameState.ts` — `Player` / `Clue` / `Ping` / `GameState` schema.
- `index.ts` — Colyseus + Express `/health`.

Shared (`shared/sim/`) — dependency‑free deterministic sim, imported by both sides:
- `movement.ts` — `stepPlayer` (the movement/resource physics) + `PlayerSimState`/`MoveInput`/`StepModifiers`.
- `terrain.ts` / `world.ts` / `caves.ts` / `collision.ts` / `rng.ts` — seed‑derived terrain, colliders,
  fallen logs, cave layout; `makeWorld(seed)` in `index.ts` builds the whole `World`.
- `constants.ts` — `WORLD` / `PLAYER` movement tunables (client `config.ts` re‑exports these).

## Tuning
- **Server constants** (top of `ForestRoom.ts`): `NIGHT_SECONDS` (600, overridable via the
  `NIGHT_SECONDS` env var for quick test matches), `TOTAL_NIGHTS` (3),
  `ROAR_RADIUS/ROAR_COOLDOWN/FREEZE_SECONDS`, `GRAB_RADIUS/INCAP_SECONDS/SLOW_SECONDS`,
  `FILM_RANGE/FILM_SECONDS`, `CLUE_LIFETIME/STRIDE/BRANCH_CHANCE`, `PING_LIFETIME`, `CAVES`,
  and the **`ESCALATION`** per‑night table (`speed/battery/stamina/roarCd/freeze/clueLife`
  multipliers, indexed by `nightNumber-1`).
- **Shared constants** (`shared/sim/constants.ts`): `WORLD` (seed/size), `PLAYER` (speeds, drains,
  jump/crouch, `staminaRecover`, `slowFactor`) — one copy for client + server.
- **Client constants** (`config.ts`): `FILM`, `ABILITY.roarCooldown`, `MAP`
  (clueWindow/hearRange/evidenceSight), `CAVE` (trigger/cooldown; `CAVES` layout is seed‑derived
  in shared). (Per‑night escalation is server‑owned and replicated — no client table.)
- **Audio**: cue synthesis + per‑cue volumes in `core/AudioEngine.ts`; trigger volumes/ranges at
  the call sites in `Game.ts` (and `RemotePlayer.ts` for footsteps).

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
Rigged/animated models + art/perf pass (Phase 6); deploy (Phase 7); full input‑replay movement
prediction (Phase 2.3 stretch — server authority + correction already shipped). (Done: audio —
procedural + diegetic; per‑night escalation; lobby/lifecycle + reconnection; **server‑authoritative
movement + reconciliation + shared deterministic world**; **Phase 3 asymmetry complete — Bigfoot
leap/charge/surface‑climb + limited‑range vision + senses overlay, searcher revive/dazzle/vault**;
**Phase 5 complete — post‑processing, settings menu (brightness/gamma, volume, sensitivity),
key rebinding, dusk‑briefing tutorial**.)
