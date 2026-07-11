# Hollow Pines — Full Analysis & Improvement Plan

> **How to read this document:** It is the brief for the next implementation pass.
> Part 1 assesses where the game is today (mechanics, story, code). Part 2 is the
> prioritized improvement plan with concrete implementation guidance. Paths/lines
> current as of `main` @ 890dbc0 (2026-07-11). Read `CLAUDE.md` first — it is accurate
> and the fastest orientation; `docs/GAME_DESIGN.md` is the rules source of truth.

Project state: roadmap Phases 0–5 complete, Phase 6 (art/perf) mostly complete
(hand-authored rigged models pending), Phase 7 (deploy) not started. The vertical slice
is real and playable: 3-night loop, filming win condition, roar→grab→incap, Bigfoot
mobility kit (leap/charge/climb/senses), searcher counterplay (revive/dazzle/vault),
per-night escalation, lobby lifecycle, settings/rebinding, dusk briefing, post-FX,
procedural audio.

---

# Part 1 — Assessment

## 1.1 Core game mechanics

**Genuinely good:**
- The loop is complete and coherent: track (clue trail) → film (light reveals you) →
  pressure (roar/grab erases footage) → escalate across 3 nights. Both sides have
  offense *and* defense.
- The flashlight is a real risk/reward verb, exactly as the GDD pillar demands: you
  must light Bigfoot to film it (revealing yourself), and sustained light dazzles it.
- The contact-gated map trail (clues visible only while Bigfoot is heard nearby or
  fresh evidence is in sight) is an elegant information-asymmetry mechanic.
- The per-night `ESCALATION` table (speed 1.0→1.22, battery drain 1.0→1.55, roar
  cooldown 25s→17.5s, clue lifetime ×0.65 by night 3) gives the match a difficulty arc
  with a single server-side source of truth.

**Design gaps (ranked by impact):**

1. **The five searchers are mechanically identical.** `STORY.md` defines five
   specialties (Mara/Analysis, Eli/Photo, June/Tracking, Theo/Sound, Sam/Endurance);
   the GDD references them; none exist in code. Largest promise-vs-delivery gap and
   the biggest replayability lever.
2. **Footage wipe-to-zero is brutally swingy.** One grab at 2/3 videos erases all team
   progress (`ForestRoom.ts:231`, `state.videosCaptured = 0`). CLAUDE.md itself flags
   it ("intentional but swingy; easy to soften"). A night-3 grab can undo ~25 minutes
   of play; there is no attrition cost to Bigfoot, so repeated grabs can reset hunters
   indefinitely (Bigfoot only needs to run the clock).
3. **Stalemate/stall risk.** Bigfoot's win is passive (survive), and cave fast-travel
   lets it disengage on contact; hunters have no tool to force engagement and no
   secondary objectives. Landmarks (lake, tower, creek) are navigation-only.
4. **Promised systems missing:** GDD §7.1 promises battery pickups in the world and
   Medic-carried spares — not implemented (and battery pressure is the core hunter
   constraint, especially with night-3 ×1.55 drain). GDD §7.1 also promises the
   photographer's flash stun.
5. **Long dead-time for victims.** Frozen = 30s of look-only; incapacitated = 60s of
   black screen. A targeted player can spend 90s doing nothing, with no
   micro-interaction (struggle/mash) and no spectate.
6. **Evidence variety scoped out and never returned.** The original spec (`PROMPT.md`)
   had evidence nodes (footprint cast, fur, audio, nest) + transmit-at-camp; v1
   collapsed to filming-only. GDD §7.3 explicitly designed the clue system to be
   extensible (`fur`, `claw-marked tree`, `scat`, `nest` as new `ctype`s).

## 1.2 Story arc

- `STORY.md` is strong flavor writing: a legend (11 hikers missing over 40 years),
  five characters with voice and motive, a "resident, not villain" Bigfoot, and a
  closing hook ("Dawn is at 6:14 a.m. Someone is not going to see it.") the game never
  pays off.
- **Almost none of it reaches the player.** The only in-game narrative delivery is the
  night-1 dusk briefing (a controls tutorial). Searchers are anonymous name-tags; no
  environmental storytelling ties into the legend; the results screen resolves nothing
  fictionally; nights have no story beats layered on the escalation.
- **Docs contradict the shipped game:** STORY.md specialties reference the old
  evidence-node design ("a recording counts as a full piece of evidence"); GDD §6
  still marks revive "planned, Phase 3" (it shipped); GDD §7.1 promises unimplemented
  battery pickups/flash; PROMPT.md describes superseded win conditions.

## 1.3 Code

**Overall:** ~5.6k lines of strict TypeScript; small focused modules; zero
TODO/FIXME/`@ts-ignore`; unusually good comments; performance-conscious client
(instancing, LOD, light-budget cull). The architecture — pure deterministic sim in
`shared/sim/` imported by both sides, server-authoritative movement with prediction +
reconciliation — is genuinely solid for a browser game. Problems are concentrated:

### Authority / cheat surface (ship-blocking before any public deploy)

- **`inView` is client-trusted — the hunter win condition is forgeable.** The client
  alone computes "Bigfoot centered in frame + not occluded"
  (`computeBigfootInView`, `client/src/core/Game.ts:592-602`) and ships the boolean in
  the move payload; the server's `updateFilming` (`ForestRoom.ts:751-767`) re-checks
  **only 2D range** (38m). A modified client can send `recording:true, inView:true`
  and bank videos through walls. The server already validates the *harder* equivalent
  for dazzle (`updateDazzle`, `ForestRoom.ts:561-587`: range + `ry` aim-cone +
  `lineBlocked` LOS) — the pattern to copy is in the same file.
- **`battery`/`stamina` are client-owned** — server clamps to [0,100] and accepts
  (`ForestRoom.ts:132-133`). Infinite sprint/flashlight/leap/charge is a payload edit
  away; escalation drain multipliers are applied client-side (honor system); dazzle
  keys off `flashlightOn` with no battery>0 check.
- **Speed-gate per-message exploit:** `applyMove` grants a flat `SPEED_GATE_BASE = 2m`
  of slack *per message* (not per unit time), so spamming small `move`s yields free
  speed. Gate margin is also generous (1.6× sprint).
- **`devRole` backdoor ships in the room:** any client passing `options.devRole` on
  join deterministically claims Bigfoot (`ForestRoom.ts:294-295`, applied at 251-263).
  Needs an env-flag gate before deploy.
- Validated properly (good, and proof the team can do it): position
  (bounds/speed-gate/collision/terrain), dazzle (full server recompute), revive
  (intent re-validated for radius/status), roar/grab/caveTravel (server cooldowns +
  checks).

### Server (`server/src/rooms/ForestRoom.ts`, 788 lines)

- **God class, but well-organized** (banner-comment sections, helpers at bottom). The
  real hazard: **23 instance fields** (22 sid-keyed `Map`s + world) manually torn down
  in both `removePlayer` (:318-346) and `resetMatchState` (:435-462) — adding any new
  per-player map means remembering 2–3 cleanup sites. A single
  `Map<sid, PlayerRuntime>` struct would collapse this.
- **The server never runs the shared sim.** `applyMove` (:375-414) is a parallel,
  simplified re-implementation (clamp + speed gate + pushout + terrain seat) rather
  than a `stepPlayer` replay. Divergence risk is real: `Y_ABOVE_TOL = 3.75` is
  hand-derived from Bigfoot's leap apex and will silently drift if
  `leapSpeed`/`gravity` change in `shared/sim/constants.ts`.
- **Inconsistent sources of truth:** night-win check reads the `TOTAL_NIGHTS` constant
  (:482) while video-win reads `state.videosRequired` (:631); `nearestCaveIndex`
  duplicated verbatim server (:424-432) vs client (`Game.ts:605-614`); cave emerge
  offset `8m` appears 3+ times across both sides.
- `server/src/index.ts` is minimal and fine; unused `express.json()` middleware; no
  CORS/rate limiting (deploy concern, Phase 7).

### Client (`client/src/`, ~4.4k LOC incl. index.html)

- **`core/Game.ts` is the god object** (735 lines; `frame()` is ~180 lines mixing
  role branching, filming, revive, cave travel, abilities, audio, HUD). Natural
  extractions: `FilmController`, `ReviveController`, `BigfootAbilities`, post-FX setup.
- **Untyped network boundary:** `Network.ts`/`Lobby.ts` read `room.state` as `any`
  (~30 occurrences, all at this boundary); a server schema rename fails only at
  runtime. The `?? 1` fallbacks in `onEscalation` are the symptom.
- **Duplication / mirror-drift:** `byId` DOM helper copied in 4 files; cave-mouth
  spawn transform computed 3× in Game.ts; world→map coordinate math duplicated
  (`Environment.generateMapCanvas` vs `MapView.toMap`); `ClueField`'s `CLUE_FADE=50`
  "roughly matches" the server's `CLUE_LIFETIME` (drift); tower coords duplicated in
  MapView; client config mirrors `NIGHT_SECONDS`/`roarCooldown`.
- **Loose magic numbers:** reconciliation tuning (Game.ts:45-47), interpolation
  (RemotePlayer.ts:11-12), forest LOD (Environment.ts:30-32), flashlight intensities,
  eye heights — all outside `config.ts`.
- **Minor per-frame waste:** HUD DOM writes every tick with no dirty-check;
  `getBigfootPosition()` clones a Vector3 up to 2×/frame; `distanceToCreek` loops 141
  points/frame; `ClueField.update` re-traverses fully-faded clues.
- **`index.html` monolith:** 324 lines (~163 of global CSS + all HUD DOM); 4 modules
  depend on its IDs with runtime throw on mismatch.

### Testing

**Zero automated tests in the entire repo** (no runner, no test scripts, no
`*.test.ts`). Verification is dual `tsc --noEmit` + `vite build` + throwaway,
never-committed `_smoke.mjs` scripts, plus the manual `docs/TEST_PLAN.md`. This is the
most glaring gap because the whole architecture rests on **bit-identical determinism**
between client and server (`shared/sim/` even preserves discarded RNG draws for
sequence parity, `world.ts:71`) — exactly the property cheap unit tests would protect.
CI exists (GitHub Actions: client build + server typecheck) and can host a test job.

### Known bugs

- `docs/BUGS.md`, OPEN: Bigfoot sees a large near-black polygonal blob near caves
  (likely boulder silhouettes crushing to black under the dimmed hemisphere light).
  The bug entry already contains a written bisect procedure — run it.

---

# Part 2 — Improvement Plan (prioritized)

Six tracks, ordered by recommended execution. Every gameplay-affecting change follows
the repo's existing patterns: server tunables at the top of `ForestRoom.ts`, shared
movement tunables in `shared/sim/constants.ts`, client render tunables in
`client/src/config.ts`, held actions ride the move stream, abilities are RPCs with
`*ReadyAt` cooldown maps, replicated escalation-style values flow through
`Network.onEscalation` → `Game.applyEscalation`.

## Track A — Integrity: finish server authority (do first)

**A1. Server-authoritative filming (critical).** In `updateFilming`
(`ForestRoom.ts:751-767`), replace trust in `flag.inView` with a server recompute:
range (exists) + aim-cone from the hunter's replicated `ry` + `lineBlocked(colliders,
hunter, bigfoot)` — literally the `updateDazzle` pattern (:561-587) with film-specific
range/half-angle constants (`FILM_RANGE 38`, add `FILM_AIM_COS`). Keep the client's
`inView` only as a cheap early-out/HUD hint. Add `FILM_*` constants next to the
existing block (:22-24).

**A2. Server-side resource envelope (battery/stamina).** Two options; recommend the
cheaper first step: keep client simulation but make the server enforce a *rate
envelope* — per accepted move, clamp the reported delta against the max possible
regen for the elapsed time (drain can be arbitrary, regen cannot exceed
`staminaRecover`-derived rates; battery can only decrease while `flashlightOn` and
never regens). Constants already live in `shared/sim/constants.ts` (`PLAYER`), so
the server can import the same numbers. Also: reject `flashlightOn:true` when server
battery ≤ 0. (Full server `stepPlayer` replay is the deferred Phase 2.3 stretch —
don't do it in this pass; the envelope closes the exploit at ~5% of the cost.)

**A3. Fix the speed-gate per-message slack.** In `applyMove` (:375-392), convert
`SPEED_GATE_BASE` from per-message to a time-budgeted allowance (track per-sid
accumulated allowance: `allowance += maxSpeed*dt` capped at a small burst, subtract
displacement). Also consider tightening `SPEED_GATE_MARGIN` 1.6 → ~1.25 once charge
windows are the sanctioned burst path.

**A4. Gate the `devRole` backdoor.** Honor `options.devRole` only when
`process.env.ALLOW_DEV_ROLE === "1"` (or `NODE_ENV !== "production"`). Update
`docs/TEST_PLAN.md` to set it.

**A5. Single sources of truth.** Read night/video totals uniformly from schema
(`state.totalNights`, `state.videosRequired`); derive `Y_ABOVE_TOL` from
`PLAYER.leapSpeed**2/(2*PLAYER.gravity) + margin`; move the cave-emerge transform
(offset-8m + `atan2` yaw) and `nearestCaveIndex` into `shared/sim/caves.ts` and import
on both sides; replicate the effective clue lifetime (escalated) or export
`CLUE_LIFETIME` via shared so `ClueField.CLUE_FADE` can't drift.

## Track B — Tests + CI (do with/right after A; A's changes need regression cover)

**B1. Add vitest** at the repo root (or per-package, matching the split
`client`/`server` npm layout; root workspace is cleaner). Zero-DOM targets first:
- `shared/sim` determinism: same seed ⇒ identical terrain samples, collider list,
  caves (snapshot/hash); `stepPlayer` sequence tests (sprint drains, exhaustion gate,
  vault, leap arc apex, lake/log slow), pure-function purity (no NaN at world edge).
- Server validation: unit-test `applyMove` math if extracted, plus **promote the
  smoke-test pattern to a committed integration test** using `colyseus.js` against a
  bootstrapped room (`@colyseus/testing` exists for 0.15): join bigfoot + 2 searchers
  with `devRole`, assert roar freezes / grab wipes-or-steals footage / revive restores
  / dazzle blocks roar / **filming denied without LOS (the A1 regression test)** /
  speed-gate rejects a teleport and move-spam gains no distance (A3).

**B2. CI:** add the test job to the existing GitHub Actions workflow (client build +
server typecheck already run).

## Track C — Design: balance + missing systems (the biggest player-facing wins)

**C1. Soften the footage wipe (small change, huge swing reduction).** Replace
`videosCaptured = 0` (`grab` handler, :231) with: grab **steals one** banked video as
a physical *tape* dropped where the victim is released; any hunter can reclaim it
(hold-E, reusing the revive held-action pattern); if Bigfoot carries it into a cave
mouth, it's destroyed for good. Tunables: `GRAB_STEALS = 1`, `TAPE_RECLAIM_SECONDS`.
Schema: a small `tapes` ArraySchema (x, z) mirroring `pings`. This converts the
all-or-nothing reset into a positional objective both sides fight over — it also gives
Bigfoot a *proactive* goal (deliver the tape), addressing the stall incentive (§1.1.3).

**C2. Battery pickups (already promised by GDD §7.1).** Spawn `SPARE_BATTERIES` (~6)
at seed-derived points biased toward landmarks (lake/tower/creek/trailhead) —
generation belongs in `shared/sim/world.ts` beside `LOG_TABLE` so both sides agree;
hold-E to collect (+50 battery). Server-validated once A2 lands. This gives hunters a
reason to traverse the map (fixing landmark deadness) and makes night-3 battery
escalation survivable *by playing well* rather than by rationing misery.

**C3. Victim agency (quality-of-life for the losing side).**
- Frozen: mashing movement keys shaves the 30s freeze (e.g. each press −0.15s, floor
  10s) — client sends a `struggle` count via the move stream or a tiny RPC;
  server-capped rate.
- Incapacitated: replace the 60s pure-black screen with a desaturated spectate of the
  nearest active teammate (client-only render change; no netcode).

**C4. Searcher specialties (the flagship feature — see Track E for the fiction).**
Implement as data, not five code paths: a `SPECIALTIES` table (server-side where it
touches gameplay, replicated like escalation) keyed by a lobby pick, e.g.:
- **Sam (Medic):** `REVIVE_SECONDS ×0.6`, +25 max stamina, spawns holding 1 spare
  battery (giveable = drop as a pickup).
- **June (Tracker):** clue contact-window ×1.5 and clues glow at 2× `evidenceSight`
  range on her map; quieter footsteps (client audio flag).
- **Theo (Audio):** `MAP.hearRange` ×1.8 (earlier "in contact"); his roar-direction
  indicator persists 10s.
- **Eli (Photographer):** `FILM_RANGE` +25% for him; one flash per night (RPC like
  roar): instant dazzle at short range, big noise ping to Bigfoot.
- **Mara (Lead):** sees clue *freshness* (age tint) and the trail's direction arrows;
  +1 concurrent ping.
All numbers are first-guess tunables in one table. Server enforces the per-role
modifiers it owns (revive, film range, flash); client applies presentation ones.
Lobby: role picker with name/blurb from `STORY.md` (first-come, duplicates allowed
fallback). This single feature converts the anonymous team into the story's cast.

**C5. Anti-stall pressure valve (design experiment — keep cheap and tunable).** Cave
fast-travel cooldown escalates per use within a night (2s → +8s each use, resets at
dawn), so caves stay an escape tool but not an infinite disengage loop. One constant
+ one per-night counter; revisit after playtests.

## Track D — Code health (opportunistic; fold into A/C work where files overlap)

**D1. Decompose `Game.ts`:** extract `FilmController`, `ReviveController`,
`BigfootAbilities` (roar/grab/charge/senses/cave-travel), and post-FX construction
into `client/src/core/` modules; `frame()` orchestrates. Do it *before* C4 lands (C4
touches the same code).
**D2. Typed state contract:** define plain interfaces for the replicated shape
(`shared/net/state.ts` — types only, keeps `shared/` pure) and use them in
`Network`/`Lobby`; kill the ~30 boundary `any`s.
**D3. Server `PlayerRuntime`:** collapse the 22 sid-keyed Maps into one
`Map<sid, PlayerRuntime>`; teardown becomes one `delete`. Do before C4 adds more state.
**D4. Consolidate duplication:** shared `byId` DOM helper; world→map transform in one
place; move loose magic numbers (reconcile/interp/LOD constants, flashlight
intensities, eye heights) into `config.ts`.
**D5. Micro-perf:** dirty-check HUD writes; cache Bigfoot position per frame; spatial
shortcut for `distanceToCreek`; skip fully-faded clues in `ClueField.update`.

## Track E — Story integration (content pass, mostly client-side)

**E1. Cast + lobby identity:** role picker (C4) presents the five searchers with
portrait-less name cards + one-line bios from `STORY.md`; in-world nametags use
character names; the dusk briefing becomes character-voiced (Mara night 1, June night
2, Theo night 3 — escalation beats: "it's faster tonight. It knows.").
**E2. Environmental storytelling:** ~8 seed-placed journal fragments (the 11 missing
hikers) as hold-E collectibles at landmarks — pure flavor, client-rendered, a `Clue`
-like schema list so the team shares finds; finding all could be a cosmetic epilogue
line, *not* a win-condition change.
**E3. Pay off the ending:** results screen gets outcome-specific epilogue text (hunters
win: the footage airs / Bigfoot win: "the forest keeps its secret" + the 6:14 a.m.
dawn line); dawn of night 3 shifts the sky to the STORY.md-promised 6:14 sunrise.
**E4. Docs truth pass:** update GDD (revive shipped; battery pickups/flash → now real
via C2/C4; footage-steal replaces wipe), STORY.md (specialties match C4 mechanics),
PROMPT.md (mark as historical), CLAUDE.md (new messages/state), ROADMAP (new phase).

## Track F — Bugfix

**F1. BUGS.md black-blob:** run the written bisect (comment out `buildCaves()` →
boulders vs tree-crown vs lake), then fix accordingly (likely: slight emissive floor
on boulders or a minimum hemisphere-light term for Bigfoot). Close the entry.

## Suggested sequencing

1. **A1–A5 + B1–B2** (integrity + tests) — one PR-sized unit; the tests pin A's fixes.
2. **C1 (footage steal) + C2 (batteries) + C3 (victim agency)** — balance pass;
   playtest via `docs/TEST_PLAN.md` with `NIGHT_SECONDS=60`.
3. **D1–D3 refactors**, then **C4 + E1 (specialties + cast)** — the flagship.
4. **E2–E4 story/content + C5 + F1** — polish pass.

Each numbered item is independently shippable; verify per CLAUDE.md after each:
`cd client && npx tsc --noEmit && npx vite build`, `cd server && npx tsc --noEmit`,
`npx vitest run` (new), plus a headless smoke for any server behavior change and a
two-tab manual pass for feel changes.
