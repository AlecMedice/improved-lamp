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

1. ✅ **Enabling layer:** `Player.specialty` + `characterName` schema, the shared `SPECIALTIES` table,
   random deal in `startMatch`, client HUD/name label, **and the debug persona switch (§2.5)** — identity
   + force/switch persona, no gameplay modifiers.
2. ✅ **Sam + Wren + Theo:** Sam — revive ×0.6, stamina cap 150 (sim + envelope) dealt full, sprint drain
   ×0.85; Wren — clue window ×1.5 / evidence sight ×2, quiet footsteps, **team-visible trail mark** (new
   `marks` list + `mark` RPC + key `G` + map render); Theo — film progress ×1.15, hear range ×1.8,
   **roar-direction HUD** (lingers 10s). *(Deviations: the stamina drain-mul applies to sprint only —
   leap/climb benefit indirectly via the deeper pool; Sam's `batteryGift` is deferred to step 4.)*
3. **Eli's flash** (new RPC) — model on `dazzle`/`roar`; add its server-validation test. *(next)*
4. **Mara** (interim coordinator) and **Sam's battery hand-off** — ride alongside the evidence-variety and
   battery-pickup roadmap items.

Verify per `CLAUDE.md` after each step (`client tsc + vite build`, `server tsc + npm test`, two-tab feel
pass); add a server test for any new validation.

## 5b. Briefing-card copy: numbers vs. plain language *(open UX task)*

The Unity dusk-briefing cards (`HPHud.CardFor`) currently **derive every figure from the live
constants** — Wren's clue window from `MapView.ClueWindow × clueWindowMul`, Sam's revive from
`GameManager.ReviveSeconds × reviveMul`, and so on. That was deliberate: a hand-typed card silently
starts lying the moment anyone retunes a specialty, and these values *will* be retuned.

**But raw numbers are not player-facing copy.** "×1.5 clue window" or even "tracks stay readable
for 22.5 s" is spec language; a player reads *"you can follow a trail long after it has gone cold
for everyone else."* The owner's call (2026-07-19): **cards should read as plain language about
what you can DO, not as a stat block.**

Wanted, next pass on the cards:
- Lead each perk with the *capability*, in the player's words. Numbers become supporting detail, or
  disappear entirely where the comparison ("farther than anyone", "twice as fast") carries it.
- Keep the derived values in code as the source of truth so copy can't drift — e.g. pick the phrase
  from a threshold on the live value, rather than printing the value raw.
- The Bigfoot card is already close to this voice; the searcher cards are the ones to bring up.
- Same applies to the in-game prompts and the `[H]` controls card.

## 6. Decisions

- ✅ **Mara** — ~~identity-only for now~~ **SUPERSEDED 2026-07-19 (owner call): the non-film evidence
  system was built, and Mara's analysis specialty shipped with it.** See §8.
- ✅ **Stamina ceiling** — per-player clamp; everyone 100, Sam 150.
- ✅ **Assignment** — **pure random each match** (distinct deal at match start, per the story). No lobby
  pick/lock; the `?devSpecialty` debug switch covers forcing a persona for testing.
- ✅ **Power level** — **Standard (§3 / §7)** for the first build; playtest will retune from there.

All spec decisions are now settled — the doc is ready to build from.

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

---

## 8. Non-film evidence + the last two abilities *(built 2026-07-19, Unity)*

Owner call: build the deferred abilities for real rather than shipping cards that describe nothing.
All three landed together, because Mara's specialty only exists if physical evidence does.
**Implemented in the Unity build only** — the TypeScript build does not have this system.

### Casting tracks (new second win path)

**Corrected 2026-07-19 after owner review.** The first pass had Bigfoot *shedding plaster casts*,
which is nonsense — a cast is something a **person makes from a track**. The model now is:

- Some footprints land in ground soft and deep enough to be worth working. The server flags them
  (`ClueMarker.Castable`, `CastableChance` 16% of dropped prints); they render bigger and darker,
  ringed with displaced earth and a pale glint so they read as workable from a distance.
- **Only `MaxCastablePrints` (4) are live at once** — a newer workable print *overrides* the oldest,
  and they also go cold on the normal clue lifetime. "Available for a time," not forever.
- **Only Mara can cast one** (`CasterSpecialty = "analysis"`). Holding the interact key runs a
  `CastSeconds` (6 s) stationary channel; progress lives on the PRINT, so an interrupted cast bleeds
  off rather than resetting. Completing it is +1 proof and consumes the print.
- Bigfoot **ruins a workable print by treading on it** (`CastStompRadius`) — its own trail is its
  liability, and it now has a reason to walk back down it.

**Why Mara and not Wren** (the owner asked; worth recording): Wren *finds* prints — her doubled
evidence sight and longer clue window are exactly the "which of these is worth anything" skill — but
casting is lab work with a kit, which is Mara's. The pairing is deliberate and hands the two
trail-focused personas complementary halves of one loop: **Wren leads the team to the print, Mara
works it.** A non-Mara searcher standing on a castable print is told what it is and why they can't
act on it, rather than getting no prompt at all.

**Win condition is PROOF:** `VideosCaptured + EvidenceCollected >= VideosRequired` (still 3). The
HUD breaks it out as "1 film · 2 casts".

| | Filming | Casting |
|---|---|---|
| Speed | fast (3 s in frame) | slow (6 s stationary channel) |
| Risk | must close on Bigfoot | never requires seeing it |
| Who | anyone | **Mara only** |
| Counterplay | Bigfoot hunts the filmer | Bigfoot treads out its own deep tracks |

Both paths then share the same second half: **carry it home to the duffel** (below).

### The duffel — carry, then store *(owner design, 2026-07-19; supersedes both earlier rules)*

Asking what "bagging" meant surfaced that instant banking was the weakest part of the design. The
owner's replacement is an **extraction loop**, and it's a real improvement:

- Every piece of proof a searcher gathers — a finished video, a cast, **and any evidence type added
  later** — goes into that player's **carried inventory** (`HPPlayer.CarriedFilm` / `CarriedCasts`;
  `CarriedTotal` sums whatever exists). It counts for **nothing** while carried.
- Proof is banked only by walking it to the **evidence duffel beside the RV** and holding the
  interact key for ~1.2 s (`GameManager.TryDeposit`, `WorldBuilder.DuffelPosition()`). Stored proof
  is **permanent**.
- **Bigfoot cannot touch the duffel.** There is no RPC, no radius check, nothing — the bag is not
  interactable by Bigfoot in any way, by construction.
- **A grab destroys only what that searcher was carrying.** Stored proof is untouched.

This replaces BOTH earlier rules — the original "a grab wipes all team footage" and the interim
"a grab wipes everything including casts". The punishment is now proportional to how greedy one
player chose to be, which is a decision they made, instead of a team-wide reset nobody could
influence. It also finally gives Bigfoot a *positional* strategy — camp the walk home, when their
hands are full — without letting it attack the safe zone itself. `TryDeposit` is deliberately
type-agnostic: a new evidence kind needs a carried counter and no change to the deposit path.

**Win condition:** *stored* proof (`VideosCaptured + EvidenceCollected`) `>= VideosRequired`,
**raised 3 → 6** (owner call) now that there are two ways to gather proof and more evidence types
planned — 3 was reachable far too quickly. The HUD top bar says **STORED n/6**, and carried proof
gets its own pulsing "CARRYING … — UNSAVED" banner, because it's the most decision-relevant number a
searcher has. *Never hardcode the target in copy — read `VideosRequired` (`HPHud.NeededProof()`).*

**The duffel is readable, not just a drop-off.** Standing at it opens a manifest panel
(`HPHud.DrawDuffelManifest`): tapes and casts broken out separately, `SECURED n/6`, how many pieces
are still missing, and what's still unsaved in your own pack. A write-only container gave the team
no way to check their own case without doing the arithmetic off the top bar.

**Still open:** carry capacity is unlimited (risk scales naturally — more carried, more to lose, so
a cap may be unnecessary); a grabbed searcher's proof is *destroyed* rather than **dropped as a
recoverable pile**, which would be the more interesting version and is the obvious next iteration.

### Eli's camera flash (`HPAction.Flash`, default G)

Per §3: range 22 m, ~29° cone, LOS-checked, **3 s dazzle** (reuses the torch-dazzle state, so it
locks roar + grab), **1 charge per night**. The cost is the ability: firing sets `RevealedFor` on
Eli, and Bigfoot sees him blazing through the trees for **5 s** — drawn by `HPHud.DrawRevealed`,
deliberately **independent of the senses overlay**, because the reveal is something the flash did to
Eli, not something Bigfoot chose to switch on. Everyone near the flash gets a white screen bloom.

### Sam's spare battery (hold interact near a teammate)

+50 battery, 1 charge per night (the doc said `charges: 1`; per-night chosen to match Eli and
because "carries spare batteries" reads plural — flag if you disagree).
**Gotcha worth remembering:** this is the ONLY place a battery goes *up*, and `ServerVitals` enforces
battery-only-decreases. The server therefore also sends `TargetGrantBattery` so the receiver raises
its **local sim** value — without it the client's next vitals push would immediately undo the gift.

### Hold-key disambiguation

Revive, collect, and battery hand-off share one key. `HPPlayer.HoldActionTarget()` resolves exactly
one — priority **downed teammate → evidence → battery** (life-critical first) — and returns the
prompt string, which `HPHud.DrawPrompts` displays verbatim. **One resolver for both the input and
the prompt**, so the HUD can never advertise an action the key won't perform.

### Not done
- Hair samples / scat as additional evidence types. Only castable prints ship. Hair caught on a
  branch is the one kind Bigfoot genuinely *would* shed, so it's the natural next addition — and it
  could sensibly be collectable by **anyone**, unlike casting.
- Carrying evidence physically rather than banking it instantly (see "bagging" above).
- No server-side unit tests — the TS `server/test/` suite doesn't cover the Unity build. If this
  system ever ports back to TS, `filmVisible`-style validation tests should come with it.
- Balance is untested: `CastableChance` (0.16), `MaxCastablePrints` (4), `CastSeconds` (6) and
  `CastStompRadius` (2.2) are first guesses. The specific risk to watch: gating the whole second win
  path behind ONE persona makes it dead weight whenever Mara is downed, absent, or simply not in the
  deal (fewer than 5 searchers). If that bites, the fallback is letting anyone cast at, say, double
  the time — keeping Mara's edge without making the path conditional on her.
