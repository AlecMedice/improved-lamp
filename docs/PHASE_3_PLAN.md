# Phase 3 — Asymmetry & Abilities (branch `july_26`)

> Implementation plan, mirrored into the repo so it travels with the branch (cross-machine handoff).
> Progress: **Phase 3 complete.** Increments A (Bigfoot vision), B (leap), C (revives), D (dazzle), E (log
> vault), plus the follow-up Bigfoot kit — **charge, surface-climb, senses overlay** (see the end of this
> doc). Each landed as its own commit, typechecked + smoke-tested. Notes on B/E/climb record how the final
> implementation refined the original sketch against the actual sim.

## Context
The vertical slice (Phases 0–2, most of 3–4) is shipped and `main` is a clean single-branch baseline.
Phase 3 ("be the monster vs. survive the monster") is the biggest remaining *gameplay* gap: today
Bigfoot only has roar→grab, hunters have no active defense, and Bigfoot's night advantage was a blunt
always-on brightness buff. This branch fills that out on both sides.

**Decisions (from the user):**
- **Scope:** Full Phase 3 breadth across both roles — *except* rigged models (that's art / Phase 6, deferred).
- **Bigfoot movement ability:** **Leap only** (no charge, no leap-climb).
- **Bigfoot night vision:** not a pure buff. Mirror the searcher flashlight idea — Bigfoot gets a
  **limited-distance sight, weaker/shorter than the searchers' flashlights**. The tradeoff is *range*:
  Bigfoot sees a near bubble and loses the far scene to darkness.

**Deliverables:** (A) Bigfoot limited-range vision, (B) Bigfoot leap, (C) searcher revives,
(D) searcher flashlight "dazzle" counter, (E) searcher jump/vault. Sequenced as independent,
testable increments so each can be verified (and reviewed) on its own.

## Key facts confirmed during exploration
- **Bigfoot vision (before Increment A):** `renderer.toneMappingExposure = isBigfoot ? 1.7 : 1.15` and an
  always-on `HemisphereLight(0x34405e,0x10131c,0.5)` — both in `client/src/core/Game.ts`. Per-client, so it
  never leaks to hunters. *(Increment A replaced this — see below.)*
- **Searcher flashlight** (the thing to stay under): `SpotLight(0xfff2d6, 0, 60, 0.5, 0.4, 1.5)`, intensity 140
  when on, in `client/src/entities/LocalPlayer.ts`.
- **Ability pattern:** client `input.onMousePress/onPress` → `Network.send*` → server `onMessage` with a
  `*ReadyAt` cooldown map → mutate state / `broadcast`. Roar is the template (`ForestRoom.ts` roar/grab
  handlers; cooldowns in the `roarReadyAt`/`frozenUntil`/`incapUntil`/`grabbedBy` maps).
- **Escalation replication:** server writes `bigfootSpeedMul/…/roarCooldownSec` to `GameState` each tick →
  `Network.onEscalation` → `Game.applyEscalation`. Any new replicated ability value follows this path.
- **Movement sim** `stepPlayer(st, input, world, mods)` in `shared/sim/movement.ts` is pure and shared.
  `MoveInput` has no ability flags yet; jump/gravity live at the vertical-physics block; auto-step
  (uses `PLAYER.stepHeight`) is the climb/vault hook.
- **Speed gate:** server `applyMove` clamps per-move distance to `maxSpeedFor(p)*dt + SPEED_GATE_BASE`
  and clamps `feetY` to `[groundY-Y_BELOW_TOL, groundY+Y_ABOVE_TOL]` (`Y_ABOVE_TOL=3.0`). The server does
  **not** run `stepPlayer`; it validates the streamed result. → **Leap needs `Y_ABOVE_TOL` raised to cover
  its apex; it needs no new network field.** A horizontal burst (charge) *would* trip the gate — avoided by
  dropping charge.
- **Reusable helpers:** `lineBlocked(colliders,a,b)` (LOS) in `shared/sim/collision.ts`, surfaced as
  `env.lineBlocked`; `computeBigfootInView()` (range + cone + LOS) in `Game.ts` is the exact pattern for
  flashlight-aim detection; `HUD.setPrompt/setAbility/setStatusBanner` + the film-progress bar for held-action UI.

---

## Increment A — Bigfoot limited-range vision *(client-only; no netcode)* — ✅ SHIPPED
Turned the blunt brightness buff into a short, dim sight cone that dies with distance.

- **`client/src/config.ts`:** added `BIGFOOT_VISION = { range: 22, intensity: 55, angle: 0.95, penumbra: 0.7,
  exposure: 1.2 }` (range < the flashlight's 60 m; dimmer; wide soft cone).
- **`client/src/entities/LocalPlayer.ts`:** in the Bigfoot branch, mount a camera-child `SpotLight` from
  `BIGFOOT_VISION` (rides the look direction, always on — the limit is range, not a battery).
- **`client/src/core/Game.ts`:** exposure now `BIGFOOT_VISION.exposure` (was 1.7); global hemisphere light
  dimmed 0.5→0.12 so the far scene goes dark beyond the cone.
- **Verified:** `tsc` + `vite build` pass; headless preview as Bigfoot confirms the far treeline goes dark and a
  near-field glow remains, no runtime errors. **Open follow-up:** cone reach/brightness is a by-eye tune best
  judged with interactive mouse-look (headless preview has no pointer lock) — all knobs live in `BIGFOOT_VISION`.

## Increment B — Bigfoot leap — ✅ SHIPPED
A stamina-gated vertical bound (Space, for Bigfoot) for traversal + gap-closing.
_As built: `leapSpeed 9.5` / `leapStaminaCost 30`; `Y_ABOVE_TOL` raised 3.0→3.75; the sim's vertical
block takes leap over a normal jump for Bigfoot. Role split lives in `buildInput` (the sim stays general)._

- **`shared/sim/constants.ts`:** `PLAYER.leapSpeed` (~9.5, vs `jumpSpeed` 5.2) and `PLAYER.leapStaminaCost` (~30).
- **`shared/sim/movement.ts`:** add `leap: boolean` to `MoveInput`; in the vertical block, if
  `st.isBigfoot && input.leap && st.grounded && !crouch && st.stamina >= leapStaminaCost`, set
  `vy = leapSpeed`, `grounded = false`, subtract the stamina cost. Deterministic → client/server agree.
- **`client/src/entities/LocalPlayer.ts`:** in `buildInput`, `leap = this.isBigfoot && Space`; normal
  `jump = !this.isBigfoot && Space` so Bigfoot's Space is leap, hunters' Space stays a normal jump.
- **`server/src/rooms/ForestRoom.ts`:** raise `Y_ABOVE_TOL` to cover the leap apex
  (`leapSpeed²/(2·gravity)` + margin) — no new message/field.
- **`client/src/core/Game.ts`:** extend the Bigfoot ability readout ("Leap ready" vs stamina-gated hint).

**Verify:** headless — as Bigfoot, send the leap arc; assert server accepts the raised feetY (no snap-back) and
stamina drains. In-client: leap clears a fallen log / small rise cleanly.

## Increment C — Searcher revives — ✅ SHIPPED
Free a downed teammate before the 60 s incap expires (interrupts Bigfoot's drag + footage pressure).
_As built: `REVIVE_RADIUS 3.5` / `REVIVE_SECONDS 4`; `updateRevives()` + `reviveIntent`/`reviveProgress`
maps; replicated `Player.beingRevived` drives the remote icon pulse; `revive_channel`/`revive_success` cues._

- **`server/src/rooms/ForestRoom.ts`:** `REVIVE_RADIUS` (~3.5) and `REVIVE_SECONDS` (~4). Track
  `reviveProgress: Map<targetSid, seconds>`. Follow the **filming pattern** (held action via the move stream,
  not a new RPC): extend the move payload with `reviving:boolean, reviveTarget:string`. When an *active* hunter
  revives a valid `incapacitated` target within radius, accumulate `dt`; on completion clear
  `incapUntil`+`grabbedBy`, set `status="active"` + post-incap `slowUntil`. Decay when not actively revived.
- **`client/src/core/Network.ts`:** add `reviving`/`reviveTarget` to `MovePayload` (default false/"").
- **`client/src/core/Game.ts`:** for hunters, `findNearbyIncapTeammate(REVIVE_RADIUS)`; show
  `HUD.setPrompt("Hold E to revive")`; while `KeyE` held near a valid target, set the flags + drive a local
  progress bar (server authoritative for completion). Add a `revive_channel`/`revive_success` cue.
- **`client/src/entities/RemotePlayer.ts`:** (polish) pulse the red incap icon while a revive is in progress.
- **`client/src/ui/HUD.ts` / `client/index.html`:** reuse the film-progress bar styling for a revive bar; allow
  `setPrompt` for hunters (today it's Bigfoot-gated in the update loop).

**Verify:** headless — bigfoot roars+grabs a 2nd hunter; a 3rd holds the revive flags in range; assert the target
returns to `active` (slowed) before `INCAP_SECONDS` and `grabbedBy` clears.

## Increment D — Searcher flashlight "dazzle" — ✅ SHIPPED
Sustained flashlight-on-Bigfoot deters it: shrinks its (now short) vision briefly and blocks roar/grab.
_As built: `DAZZLE_RANGE 40`, ~22° cone, `DAZZLE_FILL_SECONDS 1.2`, `DAZZLE_SECONDS 3`; `updateDazzle()`
reuses shared `lineBlocked` for LOS; replicated `Player.dazzled`; roar/grab handlers early-return while dazzled._

- **`server/src/rooms/ForestRoom.ts`:** per tick, for each active hunter with `flashlightOn`, test aim at the
  Bigfoot: `withinRange` (≤ `DAZZLE_RANGE` < 60), a cone check from the hunter's `ry`, and
  `lineBlocked(this.world.colliders, hunter, bf)` for LOS (import `lineBlocked` alongside `resolveCollision`).
  Accumulate a `dazzleFill` (decay when not aimed); at threshold set `bigfootDazzledUntil` (~3 s). Guard the
  **roar** and **grab** handlers with a dazzle check (mirrors the `roarReadyAt` early-return).
- **`server/src/rooms/schema/GameState.ts`:** add `@type("boolean") bigfootDazzled` (or `dazzledUntilSec`),
  replicated.
- **`client/src/core/Game.ts`:** Bigfoot: on `bigfootDazzled`, briefly cut/whiten the vision light + `HUD`
  "Dazzled!" and locally suppress ability input. Hunter: reuse `computeBigfootInView()` to show a local
  "dazzling…" meter while on-target (server authoritative for the effect).
- **Balance note:** dazzle is a *deterrent*, not a stun-lock — blocks new roars/grabs and shrinks sight for a few
  seconds; does not free already-grabbed hunters (that's revive).

**Verify:** headless — hunter with `flashlightOn` + aim + LOS for the dwell → assert `bigfootDazzled` flips and a
roar sent in the window is rejected. Negative: broken LOS (behind a tree collider) never dazzles.

## Increment E — Searcher jump/vault — ✅ SHIPPED
Let hunters clamber over fallen logs / small rises instead of only being slowed by them.
_As built: refined to a **log vault** — in this sim terrain rises never block movement (you walk up any
slope), so the fallen-log slow is the hunter's real asymmetric obstacle. Vault is a stamina-gated hop
(`vaultHopSpeed 4.6` / `vaultStaminaCost 12`) in the log-slow block that negates the slow; the planned
`vaultStepHeight` auto-step raise was dropped as a no-op against the actual terrain model._

- **`shared/sim/constants.ts`:** `PLAYER.vaultStepHeight` (> `stepHeight` 0.75, ~1.3) and `PLAYER.vaultStaminaCost`.
- **`shared/sim/movement.ts`:** add `vault: boolean` to `MoveInput`; in the **auto-step** block, when a searcher
  presses vault with stamina, use `vaultStepHeight` (+ a small `vy` hop) instead of `stepHeight`, costing stamina.
  **Constraint:** vault applies to terrain rises + fallen logs only, *not* solid colliders — the server's
  `applyMove` pushes out of colliders and would snap-back otherwise.
- **`client/src/entities/LocalPlayer.ts`:** `vault = !this.isBigfoot && Space` (contextual — engages when a small
  rise/log blocks forward motion; otherwise Space is a normal jump).

**Verify:** in-client, a hunter crossing a fallen log with Space clears it faster than walking (slowed), at a
stamina cost; confirm no reconciliation snap-back near trees (colliders excluded).

---

## Cross-cutting
- **Determinism:** every `stepPlayer`/`MoveInput` change is shared-module and must stay pure (the dual
  `tsc --noEmit` guards `shared/` purity). New tunables go in `shared/sim/constants.ts` (movement) or
  `client/src/config.ts` (render/vision), never duplicated.
- **Escalation:** leap/dazzle can later fold into the server `ESCALATION` table (e.g. shorter dazzle on night 3).
  Out of scope for the first pass; keep values as plain constants.
- **Docs:** update `docs/ROADMAP.md` Phase 3 (flip the ⬜ leap/senses + searcher stun/revive/vault items) and the
  `CLAUDE.md` message-contract + rules sections (new move-payload fields, Bigfoot vision, leap/revive/dazzle).

## Verification (end-to-end, per CLAUDE.md)
1. `cd client && npx tsc --noEmit && npx vite build` and `cd server && npx tsc --noEmit` (also guards shared purity).
2. Headless smoke (`client/_smoke.mjs`, throwaway, `NIGHT_SECONDS=6`): join bigfoot + 2 searchers via `devRole`,
   `startMatch`, then drive and assert each increment: leap feetY accepted; revive restores a grabbed hunter;
   dazzle flips + rejects a roar (and fails without LOS). Delete the file after.
3. Manual two-tab pass: Bigfoot's limited vision vs a hunter's longer flashlight; leap traversal; a hunter
   reviving a downed teammate; dazzling Bigfoot to deny a roar; vaulting a log.

## Follow-up increments — ✅ SHIPPED (the deferred Bigfoot kit landed after A–E)
- **Charge** (`Shift`): a discrete server-tracked speed-gate window (mirrors the roar cooldown) so a
  forward burst dash isn't clamped; client-predicted via `chargeMul` in `StepModifiers`. `CHARGE_*` in
  `ForestRoom.ts` / `CHARGE` in `config.ts`.
- **Surface-climb** (`Space` vs a structure): colliders gained an optional `climbH` (tower/RV/boulders);
  collision is climb-aware (solid from the side, walkable on top) via `groundHeightAt`/`climbSupport`; a
  `climb` `MoveInput` flag scales the surface (stamina-gated, regen suspended) with a ledge-fall on step-off.
  Server `applyMove` mirrors it for Bigfoot only (hunters stay 2D-solid — no feet-y spoof into structures).
- **Senses overlay** (`V`): a Bigfoot-only, depthTest-off silhouette on hunter avatars + scent halos on
  recent clues, composited in the existing single render pass (no EffectComposer). `SENSES` in `config.ts`.

## Out of scope (still deferred)
Rigged/animated models (art, Phase 6); post-processing (bloom/vignette/film-grain shader pass);
escalation-table integration of the new abilities.

---
_Note: nights were lengthened 300s→600s (5→10 min) as a separate tuning change on this branch; the server's
`NIGHT_SECONDS` env override still works for quick test matches._
