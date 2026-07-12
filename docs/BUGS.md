# Hollow Pines — Known Bugs

Working list of known issues. Newest first. Remove an entry once it's verified fixed.

---

## OPEN — Water (lake) doesn't slow players down

**Severity:** medium (mechanic not working as designed)
**Role affected:** all
**First reported:** 2026-07-11 (user playtest)

### Symptom
Wading into the lake plays the water audio correctly, but there's **no movement slowdown** — you
move through water at full speed. The lake is supposed to slow everyone (searchers heavily,
Bigfoot less), per the design.

### Where to look (not yet investigated)
- Slow factors exist in `shared/sim/constants.ts` (`PLAYER.lakeHunterFactor` 0.28,
  `lakeBigfootFactor` 0.72) and the sim applies them in `shared/sim/movement.ts` via `lakeDepth(x,z)`
  (`stepPlayer` multiplies `speed` by the lake factor when `lakeDepth > 0`).
- `lakeDepth` uses `LAKE` (`shared/sim/world.ts`). Likely suspects: the `LAKE` x/z/radius used by the
  sim doesn't match where the lake mesh is *rendered* (client `Environment`), so `lakeDepth` returns 0
  at the visible water; or the audio trigger uses a different distance check than the sim. Confirm the
  sim's `LAKE` footprint lines up with the rendered lake before adjusting factors.

---

## OPEN — Fallen logs should block, not slow

**Severity:** medium (design change — current behaviour is wrong)
**Role affected:** searchers (logs only slow hunters today)
**First reported:** 2026-07-11 (user playtest)

### Symptom / desired behaviour
Fallen logs currently **slow** a hunter who walks into them (`PLAYER.logSlowFactor` 0.35, applied in
`shared/sim/movement.ts` via `logOverlap`). Desired: a log should be **impassable** — you must
**jump / vault over it** to cross. No wade-through-slow at all.

### Where to look (not yet investigated)
- `shared/sim/movement.ts`: the `logOverlap` branch currently does `speed *= lerp(1, logSlowFactor, …)`
  when grounded and not vaulting. The fix is to treat the log as a **solid collider** (push the player
  out, like trees) instead of slowing them, while keeping the existing **vault** (`Space`) path so a
  stamina-gated hop clears it. Vault already sets `vy` and bypasses the slow — that airborne-crossing
  logic should be preserved; only the grounded "slow instead of block" needs to become "block".
- Colliders live in `shared/sim/world.ts` (logs are `FALLEN_LOGS`, currently a capsule test, not part
  of `colliders`). Making them solid means either adding capsule collision to the pushout or converting
  the slow branch into a movement block. Note: this is shared-sim (client prediction + server authority
  both run it), so it needs the determinism to stay intact — add/adjust a sim test.

---

## OPEN — Bigfoot: large dark polygonal blob in view near caves

**Severity:** medium (visual / immersion; not a blocker)
**Role affected:** Bigfoot only
**First reported:** 2026-06-20
**Retested:** 2026-07-02 — still reproduces, confirmed by the user after Phase 3A landed
(Bigfoot's night vision rework: blanket exposure buff replaced with a short-range vision
cone, and the always-on hemisphere light dimmed 0.5 → 0.12; see `docs/PHASE_3_PLAN.md`).
That rework touches hypothesis #3 below (raising the hemisphere-light floor) — the floor
is now *dimmer* than when this bug was filed, so if anything boulder silhouettes should
crush to black more easily now, not less. Worth re-running the bisect (hypothesis #1)
under the new lighting before assuming the old hypotheses still rank the same.

### Symptom
When playing as Bigfoot (notably right after spawn at a cave, and when looking up
or panning around), a large, near-black faceted/polygonal shape dominates the view —
it looks like a "black hole in the sky." It grows and shrinks as Bigfoot turns,
which is the tell-tale sign of a **world-space object at a fixed position** seen under
perspective (largest when centred in the FOV, smaller toward the screen edge), *not*
a screen-space or sky-shader artifact.

### Repro
1. Run client (offline solo is fine — `caveMouthSpawn()` runs for Bigfoot regardless of server).
2. Choose the Bigfoot role.
3. Look up / pan around at the spawn point.

### What we already tried (still present after all of these)
- Removed the cave **mouth sphere** entirely (was `SphereGeometry(2.2)` @ `0x07080b`,
  ~black, 0.5 m off the cave centre). — `buildCaves()` in `client/src/world/Environment.ts`.
- **Shrank the boulders**: back 3.4 → 2.0 m, sides 2.7 → 1.7 m (+ matching collider radii).
- **Spawn offset** moved Bigfoot 8 m toward map centre, outside the boulder horseshoe
  (server `ForestRoom.ts` onJoin, client `caveMouthSpawn()`, and cave fast-travel
  `teleportTo()` in `Game.ts`).
- **Initial yaw** set so Bigfoot faces *away* from the cave on spawn and on
  fast-travel emergence (`atan2(cave.x, cave.z)`).

### Open hypotheses / next steps (cheapest first)
1. **Confirm the source by bisecting.** Temporarily comment out `this.buildCaves()` in
   the `Environment` constructor and reload. If the blob disappears, it's still a
   boulder silhouette (grey `0x6a6a73` boulders read as near-black against the dusk sky
   at night with Bigfoot's dim hemisphere light). If it's still there, the cause is
   elsewhere — suspect a nearby **tree crown** (dark foliage `0x33503a`) or the
   **lake mesh**; bisect those next.
2. If it IS the boulders: they may be taller/closer than expected because boulder `y`
   uses `by + r*0.35` and the icosahedra are scaled in Y (`sy` 1.1–1.2). A 2 m boulder
   shouldn't fill the upper view from 8 m away, so double-check the spawn offset is
   actually applied (e.g. verify Bigfoot isn't landing at the cave centre — confirm the
   reload picked up the new build; rule out a stale Vite cache).
3. Consider giving boulders a small **emissive** or raising Bigfoot's hemisphere-light
   floor so silhouettes don't crush to pure black against the sky.
