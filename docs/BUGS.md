# Hollow Pines — Known Bugs

Working list of known issues. Newest first. Remove an entry once it's verified fixed.

---

## OPEN — Bigfoot: large dark polygonal blob in view near caves

**Severity:** medium (visual / immersion; not a blocker)
**Role affected:** Bigfoot only
**First reported:** 2026-06-20

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
