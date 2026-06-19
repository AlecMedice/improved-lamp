# Hollow Pines — Development Roadmap

A phased plan from the current scaffold to a playable game. Each phase ends in something you
can actually run and feel. Ship the **vertical slice** (Phases 0–4) first; everything after is
depth and polish.

Legend: ✅ done in this scaffold · 🟡 partially stubbed · ⬜ not started

---

## Phase 0 — Foundations *(this scaffold)*
- ✅ Repo structure, docs (prompt, GDD, story, roadmap), README
- ✅ Client: Vite + TypeScript + Three.js, runnable standalone
- ✅ Dusk lighting, fog, gradient sky, post‑processing hook
- ✅ Low‑poly smooth‑shaded terrain + instanced conifers
- ✅ First‑person controller (WASD + mouse look + pointer lock)
- ✅ Working flashlight (spotlight + toggle + battery drain)
- ✅ Server: Colyseus + `ForestRoom` + `GameState` schema
- ✅ Client↔server connect + remote‑player sync (graceful offline fallback)

**Run it:** see [`README.md`](../README.md). Walk the forest at dusk with a flashlight; open a second tab to see another player.

---

## Phase 1 — Feel & environment 🟡
- 🟡 Tune movement (accel, friction, head‑bob, footsteps)
- ⬜ Collision (terrain height sampling + tree/rock colliders)
- ⬜ Jump / vault, stamina + sprint
- 🟡 Day‑night cycle driving sky/fog/ambient over the match (`timeOfDay`)
- ⬜ Creek, base camp props, trailhead landmark
- ⬜ Audio bed (wind, creek, footsteps, flashlight click)

**Goal:** the forest *feels* like a place you don't want to be alone in.

---

## Phase 2 — Multiplayer solidified ⬜
- 🟡 Authoritative room state; movement validation/clamps
- ⬜ Client‑side interpolation + basic reconciliation
- ⬜ Lobby → match → results room lifecycle
- ⬜ Role assignment (volunteer/random Bigfoot)
- ⬜ Disconnect/reconnect handling, host migration N/A (server‑owned)

**Goal:** 6 players reliably share one forest with smooth movement.

---

## Phase 3 — Asymmetry & abilities ⬜
- ⬜ Two playable roles with distinct cameras/speed/models
- ⬜ Bigfoot: charge, leap/climb, grab (down), roar (AoE scare), senses overlay
- ⬜ Searcher: sprint, jump/vault, ping, flashlight stun meter
- ⬜ Downed/revive/incapacitated states synced

**Goal:** "be the monster vs. survive the monster" is real and fun.

---

## Phase 4 — Game loop & objectives ⬜
- ⬜ Evidence nodes (spawn, collect channel, tool bonuses)
- ⬜ Base‑camp radio transmission (timed, noisy, climactic)
- ⬜ Night phases & escalation buffs
- ⬜ Win/loss resolution + results screen
- ⬜ HUD: battery, stamina, evidence, phase clock, objectives (`Tab`)

**Goal:** a full match has a beginning, middle, and a winner. **← vertical slice complete.**

---

## Phase 5 — Audio, UI & polish ⬜
- ⬜ Spatial audio (footsteps, roars, heartbeat proximity)
- ⬜ Post‑processing pass (bloom, vignette, film grain) tuned per phase
- ⬜ Menus, settings, key rebinding, gamma calibration
- ⬜ Tutorial during dusk briefing

## Phase 6 — Art pass & performance ⬜
- ⬜ Authored low‑poly models + rigs/animations (searchers, Bigfoot)
- ⬜ Instancing, LODs, shadow budget, mobile‑GPU friendliness
- ⬜ Map art pass (lighting beats, landmarks for navigation)

## Phase 7 — Live & deploy ⬜
- ⬜ Deploy server (Colyseus on a host) + static client (CDN)
- ⬜ Matchmaking/quick‑play, simple stats
- ⬜ Accessibility audit, playtest balancing pass
- ⬜ (Stretch) accounts, cosmetics, additional maps

---

## Risk register
| Risk | Mitigation |
|------|------------|
| Netcode for fast first‑person feels laggy | Interpolate remotes; keep authority light in v1; move to reconciliation in P2 |
| "Too dark to play" | Gamma calibration + flashlight always available + readable silhouettes/fog |
| Asymmetric balance (1 vs 5) | Tunable knobs in GDD §5; playtest early and often |
| Browser perf with many trees/lights | Instancing, LODs, cap dynamic lights, bake where possible |
| Scope creep | Lock the vertical slice (P0–P4) before any P5+ work |

## Definition of "vertical slice" (the first real milestone)
One map · 6 players online · both roles playable · flashlight + 2 abilities/side ·
evidence + transmit + survive‑to‑dawn · dusk→dawn lighting · win/loss + results.
