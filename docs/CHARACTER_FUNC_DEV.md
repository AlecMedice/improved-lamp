# Character Specialties — Functional Dev Plan

Turning the five searchers in `docs/STORY.md` from flavor into mechanics. This doc audits **what
exists today**, specs **what each specialty should do**, and lays out **how to build it** following the
repo's existing patterns (server-owned tunables replicated to clients, held actions on the move stream,
abilities as RPCs with cooldown maps).

Status legend: ✅ shipped · 🟡 partial substrate exists · ❌ not built.

---

## 1. Where things stand today

There is **no character/specialty system**. A player's `role` is only `"searcher"` or `"bigfoot"`
(`server/src/rooms/ForestRoom.ts`, `schema/GameState.ts`). Every searcher is mechanically identical;
the five names/portraits/specialties exist only in the story doc.

What *is* built is the **shared core kit** every searcher already has — the substrate several specialties
build on:

| Core ability | Where | Notes |
|---|---|---|
| Film Bigfoot (win condition) | `ForestRoom.updateFilming` / `canFilm`; `FILM_RANGE`, `FILM_AIM_COS`; client `config.FILM` | Server-authoritative (range + aim cone + LOS). |
| Revive a downed teammate | `ForestRoom.updateRevives`; `REVIVE_RADIUS/SECONDS/DECAY` | Held action on the move stream. |
| Dazzle Bigfoot with a light | `ForestRoom.updateDazzle`; `DAZZLE_*` | Sustained beam, range + cone + LOS. |
| Vault a fallen log | `shared/sim/movement.ts` (`vault`); `PLAYER.vaultHopSpeed/vaultStaminaCost` | Negates the log slow. |
| Drop a stakeout ping | `ForestRoom` `ping` handler; `PING_LIFETIME` | One per hunter. |
| Map clue-contact trail | client `MAP.hearRange/evidenceSight/clueWindow`; `ClueField` | Trail visible only "in contact". |
| Battery / stamina | `shared/sim/constants.ts` `PLAYER.*`; server resource envelope | **No battery pickups exist yet.** |

So each specialty below is either a **per-player scalar** over something that already exists (cheap) or a
**new mechanic** (more work). That split drives the phasing in §4.

---

## 2. Randomized assignment (the enabling feature)

**Goal:** when the host starts a match, each Searcher is randomly assigned one of the five characters
(distinct where possible), replicated so every client shows the right name/specialty.

Implementation:
- **Schema:** add `@type("string") specialty = ""` (and optionally `characterName`) to `Player`
  (`schema/GameState.ts`). `""` = unassigned / Bigfoot.
- **Assignment:** in the `startMatch` handler (`ForestRoom.ts`), after roles are picked, shuffle the five
  specialty ids and deal one to each searcher. With ≤5 searchers all are distinct; if we ever allow more,
  fall back to duplicates. Bigfoot gets `""`.
- **Single source of truth:** a `SPECIALTIES` table (server-side, replicated like the `ESCALATION`
  table) holds every tunable, keyed by specialty id — one place to balance, no client mirror.
- **Client:** read `player.specialty` to label the HUD / avatar / dusk briefing, and to apply the
  presentation-only modifiers (see below). The lobby can later show the assigned character card.

This mirrors how per-night escalation already flows (server owns the table → replicates values →
client applies its share).

---

## 3. The five specialties

Each entry: the fiction → the mechanic → what it costs to build. All numbers are first-guess tunables.

### 🩹 Sam — Endurance ✅-adjacent (cheapest; all substrate exists)
- **Faster revive:** scale `REVIVE_SECONDS` per-reviver (`×0.6`). Server-owned (revive completes on the
  server).
- **More stamina:** `+25` max stamina, or slower drain — apply in the shared sim via a per-player
  `StepModifiers`-style scalar (client predicts, server envelope already bounds regen).
- **Share batteries:** depends on the **battery-pickup** system (not yet built — see roadmap Track C2).
  Interim: Sam spawns with a giveable spare (hold-E hand-off), deferred until pickups land.
- **Build cost:** low. Revive + stamina are scalars over existing systems.

### 🥾 Wren — Tracking 🟡 (scalars over the existing clue/map system)
- **Prints glow / longer contact:** widen her clue-contact window and evidence-sight range
  (`MAP.clueWindow ×1.5`, `evidenceSight ×2`) so the trail shows for her more readily and longer.
- **Mark the trail:** let her promote a clue to a team-visible marker (reuse the `ping` path with a
  distinct type, or a small `marks` array like `pings`).
- **Moves quietly:** client audio flag — quieter footstep cue on her `RemotePlayer`.
- **Build cost:** low–medium. Contact scalars are client-side; "mark" is a small new replicated list.

### 🎙️ Theo — Sound 🟡 (scalars + one client cue)
- **Early warning:** larger `MAP.hearRange ×1.8` (Bigfoot registers as "nearby" from farther off); a
  persistent roar-direction indicator (~10s) on his HUD.
- **"Recording counts as evidence":** in today's film-only model, express this as a **filming bonus**
  rather than a separate evidence type — e.g. Theo banks film progress slightly faster, or a short
  audio-only capture at close range contributes partial progress. (Keeps the single win condition; avoids
  reviving the retired evidence-node design. Revisit if/when other evidence types return.)
- **Build cost:** low–medium. Mostly client presentation + one filming scalar (server-owned).

### 📷 Eli — The Photo 🟡 substrate / ❌ flash
- **Longer capture range:** per-player `FILM_RANGE` bonus (`+25%`). Server-owned (it's the win check).
- **Flash (stun + reveal):** **new RPC**, modeled on `roar`/`dazzle` — one charge per night, short range,
  instant dazzle on Bigfoot + a loud "here I am" ping to it. Server validates range + cone + LOS + charge
  count (mirror `updateDazzle`), broadcasts a positional flash cue.
- **Build cost:** medium. Range is a scalar; the flash is a new server-validated ability + cooldown map +
  client VFX/SFX.

### 🔬 Mara — Analysis ❌ (loosest fit to current mechanics)
- **Casts evidence faster / confirms the real thing:** there is **no evidence-casting or false-positive
  mechanic today** (clues are always genuine Bigfoot tracks; the only evidence is film). Options:
  1. **Defer** until non-film evidence exists (cleanest — pairs with the future casts/samples system).
  2. **Reinterpret now** as a clue-reading edge: Mara sees clue *freshness/direction* (age tint + heading
     arrows) and gets `+1` concurrent ping, making her the team's coordinator.
- **Recommendation:** ship option 2 as her interim specialty so she isn't a no-op, and fold in the
  "confirm real finds" fiction when the evidence-variety system arrives.
- **Build cost:** medium (new client clue-read rendering) or deferred.

---

## 4. Suggested build order

1. **Enabling layer:** `Player.specialty` schema field + `SPECIALTIES` server table + random assignment
   in `startMatch` + client HUD label. Ship with **no** gameplay modifiers first, just identity — proves
   the pipeline end to end.
2. **Sam + Wren + Theo** (the scalar-only specialties): revive/stamina, clue-contact/mark, hear-range +
   filming bonus. All reuse existing systems; no new RPCs.
3. **Eli's flash** (new RPC) — the first genuinely new mechanic; model it on `dazzle`/`roar`.
4. **Mara** (interim clue-read) and **Sam's battery sharing** — these ride on other roadmap items
   (evidence variety, battery pickups); do them alongside those.

Verify per `CLAUDE.md` after each step: `client tsc + vite build`, `server tsc + npm test`, plus a
two-tab manual pass for feel. Add server-side unit tests for any new validation (e.g. the flash),
alongside the existing anti-cheat/sim suite in `server/test/`.

## 5. Open questions

- **Duplicates:** with fewer than five searchers, which specialties get dropped — random, or a fixed
  priority? With more than five (not currently possible; `maxClients = 6`), allow duplicates?
- **Lobby choice vs. pure random:** story says random. Do we ever want a pick/lock lobby instead, or
  random-only?
- **Balance:** every number here is a first guess. Specialties should be *edges*, not power spikes — the
  game's thesis is team cohesion, not carry characters.
