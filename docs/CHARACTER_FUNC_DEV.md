# Character Specialties — Functional Dev Plan

Turning the five searchers in `docs/STORY.md` from flavor into mechanics. This doc audits **what
exists today**, specs **what each specialty should do**, and lays out **how to build it** following the
repo's existing patterns (server-owned tunables replicated to clients, held actions on the move stream,
abilities as RPCs with cooldown maps).

**Design principle.** All five specialties are first-class mechanics, not cosmetic perks — and **all
five ship**, not just the cheap ones. Each should be *always relevant*: it changes how that character
plays for the whole match and touches a real system (filming, the clue/map trail, revive/incap,
battery/stamina, Bigfoot deterrence). A player should feel their specialty every night. The balance
target is *distinct, defining playstyles that still win on cohesion* — impactful, not a solo carry.

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

## 2. Framework

### 2.1 Data model

- **Specialty id** — one of `"analysis" | "photo" | "tracking" | "sound" | "endurance"` (Mara, Eli,
  Wren, Theo, Sam). Bigfoot / unassigned = `""`.
- **Schema** (`schema/GameState.ts`) — add to `Player`:
  `@type("string") specialty = ""` and `@type("string") characterName = ""`. Both are set once at match
  start and never change during a match, so they cost one replication each, not per-tick.
- **The `SPECIALTIES` table lives in `shared/sim/` and is pure** (plain constants — no Three.js, no
  decorators). Because the numbers never change mid-match, we do **not** replicate them like the
  `ESCALATION` table (which we replicate precisely because it changes each night). Instead **both sides
  import the same table** and look it up by the player's replicated `specialty` id. Only the *assignment*
  travels over the wire. This keeps a single source of truth and respects `shared/` purity.

### 2.2 Where each modifier is enforced

The rule mirrors the anti-cheat work already done: **the server owns anything that can change the
outcome; the client owns presentation and prediction, bounded by the server.** Because the table is
shared, the server's resource envelope and the client's prediction read the *same* per-player numbers, so
they never fight during reconciliation.

| Modifier | Enforced by | Why |
|---|---|---|
| revive time, film range, film-progress rate, flash, extra pings, trail marks | **server** | touch the win condition / match outcome |
| stamina max & drain | **shared** (client predicts, server envelope clamps to the same per-player cap) | movement is client-predicted but server-bounded |
| clue glow, contact window, evidence sight, hear range, roar-direction HUD, quieter footsteps, name tags | **client** | presentation only — no outcome effect |

> **Decided:** the resource envelope's stamina clamp becomes **per-player max**, read from the table —
> everyone stays at `100` except Sam at `150`. (Battery stays a flat `[0,100]` for now.)

### 2.3 Assignment

In the `startMatch` handler, after Bigfoot is chosen: shuffle the five ids and deal one to each searcher
(assignment is not sim state, so ordinary `Math.random` shuffle is fine). ≤5 searchers ⇒ all distinct;
Bigfoot gets `""`. Set `characterName` from a name map. `maxClients = 6` (1 Bigfoot + 5 searchers), so
duplicates never occur in practice.

### 2.4 New message contract (only two specialties need one)

- `flash` — Eli, client→server RPC, no payload. Server validates charges-left-this-night + range + aim
  cone + LOS (mirror `updateDazzle`), applies a dazzle, marks Eli's position visible to Bigfoot for a few
  seconds, and broadcasts a positional `flash` cue. Charges reset each night.
- `mark` — Wren, client→server RPC `{x,z}` (or a held action). Promotes a nearby clue to a team-visible
  trail marker (a small `marks` ArraySchema, modeled on `pings`). Cooldown'd.

Every other specialty is a **scalar or a client render toggle** — no new messages.

### 2.5 Debug: switching personas easily (build this with the enabling layer)

Testers need to try each character without rerolling matches, so the persona switch is a **first-class
part of the enabling layer**, gated by the same `ALLOW_DEV_ROLE` flag as `?devRole` (off in production):

- **`?devSpecialty=<id>` join param** (mirrors `?devRole=`) — `tracking | photo | sound | endurance |
  analysis`. When set, the match-start deal honours it for that client instead of dealing random, so a
  tester always spawns as the persona they want. Combine with `?devRole=searcher`.
- **In-match hot-swap key** (debug only) — a keybind (e.g. `[` / `]` to cycle, or a small debug overlay)
  sends a `debugSetSpecialty {id}` message; the server (only when `ALLOW_DEV_ROLE`) reassigns the caller's
  `specialty` live and re-applies modifiers. Lets one tester feel all five back-to-back in a single match
  without rejoining. Rejected entirely in production.

Both routes reuse the same assignment path — the debug switch just overrides the random id.

### 2.6 Testing

Add to `server/test/` alongside the anti-cheat/sim suite: assignment deals distinct ids; the film-range
bonus shifts `canFilm`'s threshold; `reviveSecondsMul` changes completion time; flash validation rejects
out-of-range / no-LOS / no-charge; the envelope honours a per-player stamina cap.

---

## 3. Base constants (first pass)

Proposed as a pure shared table. Every number is annotated with the **baseline it modifies** so the
effect is legible; all are first guesses meant for your feedback and playtest tuning.

```ts
// shared/sim/specialties.ts (proposed) — pure; imported by client + server.
export type SpecialtyId = "analysis" | "photo" | "tracking" | "sound" | "endurance";

export const CHARACTER_NAME: Record<SpecialtyId, string> = {
  analysis:  "Dr. Mara Okonkwo",
  photo:     "Eli Vance",
  tracking:  "Wren Castellano",
  sound:     "Theo Park",
  endurance: "Sam Reyes",
};

export const SPECIALTIES = {
  // 🔬 Mara — IDENTITY-ONLY for now (no gameplay modifier). Her "analysis" fantasy needs non-film
  // evidence to attach to (casts/samples/false clues), which doesn't exist yet. She still gets a name,
  // portrait, and dusk-briefing line; her mechanic lands when the evidence-variety system does. See §4.
  analysis: {},

  // 📷 Eli — reach + the flash.
  photo: {
    filmRangeMul: 1.25,       // FILM_RANGE 38 -> 47.5m (server); client FILM.range 35 -> 43.75m
    flash: {
      range:         22,      // short reach (< dazzle's 40)
      aimCos:        Math.cos(0.5), // ~29° cone — an aimed shot, a touch tighter than filming
      dazzleSeconds: 3,       // reuse DAZZLE_SECONDS: locks Bigfoot's roar/grab + cuts its sight
      revealSeconds: 5,       // Bigfoot sees Eli's position marked for this long (the "here I am")
      chargesPerNight: 1,     // one flash per night, refills at each nightfall
    },
  },

  // 🥾 Wren — the trail specialist.
  tracking: {
    clueWindowMul:     1.5,   // MAP.clueWindow 15 -> 22.5s (trail stays visible longer)
    evidenceSightMul:  2.0,   // MAP.evidenceSight 18 -> 36m (spots clues from farther)
    footstepVolumeMul: 0.5,   // client audio: half-volume footsteps
    mark: { cooldownSec: 8, lifetimeSec: 50 }, // team-visible trail marker (~CLUE_LIFETIME 50)
  },

  // 🎙️ Theo — ears + a filming edge.
  sound: {
    hearRangeMul:      1.8,   // MAP.hearRange 35 -> 63m (earlier "in contact" warning)
    roarDirPersistSec: 10,    // roar-direction indicator lingers on his HUD
    filmProgressMul:   1.15,  // banks film ~15% faster (server-owned; FILM_SECONDS 3.0 effective ~2.6)
  },

  // 🩹 Sam — keeps the team standing.
  endurance: {
    reviveSecondsMul: 0.6,    // REVIVE_SECONDS 4 -> 2.4s
    staminaMax:       150,    // (baseline 100) much deeper reserve — Sam is the endurance carry
    staminaDrainMul:  0.85,   // sprint/leap/climb cost ~15% less
    batteryGift: { amount: 50, charges: 1 }, // hand a spare battery (hold-E); needs the hand-off action
  },
} as const;
```

**Systems each specialty pulls on** (so none is a niche perk):

| Character | Always-on effect | New action | Core system it reshapes |
|---|---|---|---|
| 🩹 Sam | faster revives, deeper stamina | give battery | revive/incap + stamina/battery economy |
| 🥾 Wren | longer/wider trail vision, quiet | mark trail | the clue/map trail |
| 🎙️ Theo | long-range hearing, faster film | — | early-warning + the win condition |
| 📷 Eli | longer film range | flash (stun+reveal) | the win condition + Bigfoot deterrence |
| 🔬 Mara | *identity only for now (deferred)* | — | — (lands with non-film evidence) |

---

## 4. Per-character notes & caveats

- **Sam** — cheapest; revive/stamina are scalars over shipped systems. `batteryGift` is the one piece
  that needs a new hand-off action (and pairs naturally with the roadmap's battery-pickup work).
- **Wren / Theo** — mostly client presentation + a couple of server-owned scalars (`filmProgressMul`,
  marks). No fundamentally new mechanics.
- **Eli** — the `flash` is the only genuinely new **ability RPC**; everything else is a scalar.
- **Mara** — **decided: identity-only for now.** There is no evidence-casting or false-clue mechanic
  today (every clue is a genuine Bigfoot track, film is the only evidence), so her "analysis" fantasy has
  nothing to attach to. She ships as a name/portrait/briefing identity with **no gameplay modifier**, and
  her real specialty is built together with the non-film evidence system (casts / hair samples / false
  clues). Until then she's a fully playable searcher with the shared core kit, just no specialty edge.

---

## 5. Suggested build order

1. **Enabling layer:** `Player.specialty` + `characterName` schema, the shared `SPECIALTIES` table, random
   deal in `startMatch`, client HUD/name label, **and the debug persona switch (§2.5)** — **no gameplay
   modifiers yet**, just identity + the ability to force/switch persona. Proves the pipeline end to end
   and gives testers the switch before any specialty exists to try.
2. **Sam + Wren + Theo** (scalars + one small marks list): revive/stamina, clue-contact/mark, hear-range +
   film-progress. Reuse existing systems; no new RPCs.
3. **Eli's flash** (new RPC) — model on `dazzle`/`roar`; add its server-validation test.
4. **Mara** (interim coordinator) and **Sam's battery hand-off** — ride alongside the evidence-variety and
   battery-pickup roadmap items.

Verify per `CLAUDE.md` after each step (`client tsc + vite build`, `server tsc + npm test`, two-tab feel
pass); add a server test for any new validation.

## 6. Decisions

- ✅ **Mara** — identity-only for now; her specialty ships with the non-film evidence system.
- ✅ **Stamina ceiling** — per-player clamp; everyone 100, Sam 150.
- **Assignment** — pure random each match (story's intent), or a lobby pick/lock later? *(open)*
- ✅ **Power level** — **Standard (§3 / §7)** for the first build; playtest will retune from there.

## 7. Power-level tiers

"Power level" = how much a specialty swings a match. The same mechanic reads very differently at
different numbers — a subtle edge you barely notice, or a defining advantage the team builds around. The
base constants in §3 are the **Standard** column. Representative dials:

| Dial | Subtle | **Standard (§3)** | Bold |
|---|---|---|---|
| Sam — revive time (from 4s) | 0.8 → 3.2s | **0.6 → 2.4s** | 0.45 → 1.8s |
| Sam — stamina max (from 100) | 120 | **150** | 180 |
| Eli — film range (from 38m) | ×1.15 → 44m | **×1.25 → 47.5m** | ×1.4 → 53m |
| Eli — flashes / night | 1 | **1** | 2 |
| Theo — hear range (from 35m) | ×1.5 → 52m | **×1.8 → 63m** | ×2.2 → 77m |
| Theo — film speed (from 3.0s) | ×1.1 | **×1.15** | ×1.3 |
| Wren — evidence sight (from 18m) | ×1.6 → 29m | **×2.0 → 36m** | ×2.5 → 45m |
| Wren — clue window (from 15s) | ×1.3 → 20s | **×1.5 → 22.5s** | ×2.0 → 30s |

- **Subtle** — flavourful nudges; the team plays the same, just slightly smoother. Lowest balance risk.
- **Standard** — you feel your specialty and lean into it, but a coordinated team still wins on cohesion.
- **Bold** — specialties are build-defining; the team plans around who they got. Highest swing / risk.

*(Sam's stamina is already set to 150 = Standard here per your call.)*
