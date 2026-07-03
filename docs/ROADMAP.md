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

## Phase 1 — Feel & environment ✅
- ✅ Tune movement: head‑bob (walk/sprint), footsteps, crouch (`Ctrl`‑hold: lower eye height + slower, stealthier steps)
- ✅ Collision (terrain height sampling + tree trunk colliders)
- ✅ Jump (`Space`, gravity + ballistic feetY, networked) + auto‑step/vault over small rises + stamina + sprint
- ✅ Day‑night cycle driving sky/fog/ambient over the match (`timeOfDay`)
- ✅ Landmarks: base camp (campfire + **RV**), **cave entrances**, **creek**, **trailhead sign**, **lookout tower**, **lake**, **fallen logs** (asymmetric: slow hunters, not Bigfoot), bushes + shore rocks, terrain vertex coloring
- ✅ Audio (`core/AudioEngine.ts`): gusting wind + proximity‑driven creek beds, **positional** footsteps (own + remote, true 3D), flashlight click — see Phase 5 for the full cue set

**Goal:** the forest *feels* like a place you don't want to be alone in. **← done.**

---

## Phase 2 — Multiplayer solidified 🟢
- ✅ **Server‑authoritative movement** — the shared deterministic world (`shared/sim/`) gives the server terrain + collision; the `move` handler validates every update (world‑bounds clamp, **max‑speed gate** vs teleport/speedhack, **collision pushout** vs phasing, terrain feet‑clamp). Cave fast‑travel is a validated `caveTravel` command, not a client self‑teleport.
- ✅ **Client reconciliation** — local player predicts as before, then **eases toward the server's corrected position** (`LocalPlayer.correctTo`); large desyncs snap. *(Resources stay client‑owned; full input‑replay prediction is the deferred Phase 2.3 stretch — see plan.)*
- ✅ **Client‑side interpolation** — remotes render on a small **snapshot buffer** (interpolate between bracketing snapshots, render ~100 ms behind) for smooth constant‑velocity motion; teleports snap.
- ✅ **Shared deterministic world + movement sim** — `shared/sim/` (rng, constants, terrain, colliders, **seeded `CAVES`**, and the pure `stepPlayer` movement physics) imported by both client + server; the old `Math.random()` cave duplication is gone (client + server now agree).
- ✅ **Lobby → match → results room lifecycle** (`matchPhase` lobby/playing/results; clock only runs in‑match)
- ✅ **Role assignment** — host presses Start; server picks **one random Bigfoot** (solo player roams as searcher)
- ✅ **Disconnect/reconnect handling** — 20s reconnection grace (`allowReconnection`), `connected` flag, host reassignment; host migration N/A (server‑owned)
- ✅ Host **rematch** (results → Return to lobby) + preserved offline‑solo entry path

**Goal:** 6 players reliably share one forest with smooth movement. **← done** (server has final say on position; full input‑replay prediction deferred as an optional polish pass).

---

## Phase 3 — Asymmetry & abilities ✅
- 🟡 Two playable roles with distinct cameras/speed/models *(distinct speed, height, night‑vision, avatars + Bigfoot eye‑shine done; rigged models pending — Phase 6)*
- ✅ Bigfoot **roar** (AoE freeze) → **grab** frozen hunter → **incapacitate + drag + erase footage**; recover slowed
- ✅ Bigfoot mobility kit — **leap** (`Space`, stamina‑gated bound), **charge** (`Shift`, forward burst dash on a cooldown), **surface‑climb** (`Space` vs a structure: scale the tower/RV/boulders and stand on top)
- ✅ Bigfoot **senses overlay** (`V`) — predator vision revealing hunters (and its own scent trail) through the forest
- ✅ Bigfoot leaves a trackable trail (footprints + broken branches) — *the clue framework*
- ✅ Bigfoot **cave fast-travel** network (spawn at a cave; pick the destination from the map)
- ✅ **Map** (`M`) for both roles — position/heading, camp, caves; hunters also see teammates + recent clue trail
- ✅ **Stakeout pings** (`Q`/map-click) — shared hunter markers on the map + world beacons
- ✅ Fade-to-black transition on Bigfoot cave fast-travel + between nights
- ✅ Frozen/incapacitated states synced; remote status icons (grab targets)
- ✅ Searcher **counterplay** — **revive** a downed teammate (`E`), **dazzle** Bigfoot with a sustained flashlight (locks its roar/grab), and **vault** a fallen log (`Space`)

**Goal:** "be the monster vs. survive the monster" is real and fun. **← both sides now have offense *and* defense.**

---

## Phase 4 — Game loop & objectives 🟡
- ✅ **Filming win condition** — capture 3 solid videos of Bigfoot (client detects in‑frame, server tallies; pooled across nights)
- ✅ Clue trail spawn/expire/fade ("the trail goes cold"); map trail gated by contact + recency
- ✅ **3-night structure** — each night 8pm→8am, daylight skipped, fade between nights
- ✅ Win/loss resolution + results screen (3 videos → hunters; survive 3 nights → Bigfoot)
- ✅ HUD: battery, stamina (with exhaustion), footage, night + clock, viewfinder, status banner, roar cooldown
- ✅ **Escalation buffs per night** — server `ESCALATION` table (faster Bigfoot, faster battery/stamina drain, shorter roar cooldown, longer freeze, faster‑cooling trail; replicated to clients) + role‑specific tutorial hints

**Goal:** a full match has a beginning, middle, and a winner. **← core of the vertical slice is in.**

---

## Phase 5 — Audio, UI & polish 🟡
- ✅ Spatial audio (footsteps, roars, heartbeat proximity) — true 3D positional (THREE.AudioListener);
  diegetic roars (server broadcast), branch‑snaps, remote footsteps; procedural synthesis with
  optional sample overrides (hybrid; see `client/public/audio/`)
- ✅ Post‑processing pass — `EffectComposer` (bloom on flashlight/eye‑shine/campfire/rec lights + shader
  vignette + subtle moving film grain); vignette tightens toward midnight (per‑phase). Tunables in `config.POST`
- ⬜ Menus, settings, key rebinding, gamma calibration
- 🟡 Tutorial *(role‑specific start‑of‑match hints done; dusk‑briefing version pending)*

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
One map · 6 players online · both roles playable · flashlight + clue trail ·
film 3 videos to win / catch the team to win · dusk→dawn lighting · win/loss + results.
