# Hollow Pines — Mothman Port

## Why this branch exists

This branch swaps Bigfoot for **Mothman** as the played creature, in response to competitive overlap
with Steam's BIGFOOT game (app 509980). Mothman was chosen over other alternatives after ruling out:

- **Skinwalker:** disguise/impersonation mechanics are defeated by voice chat — players just talk
- **Wendigo:** possession is the same problem; cold/winter theming fights the Pacific NW setting
- **Dogman:** very low cultural recognition, high lore onboarding tax
- **Original creature:** maximum IP freedom but starts from zero familiarity with every player

**Why Mothman works:**
- Evidence-as-warning narrative: searchers film it to warn the world before a disaster, not just
  to prove it exists — stronger motivation to approach danger
- No voice-chat problem: abilities are physical (dive, screech, glide), not social
- Widely known in American cryptid culture — low onboarding tax
- No cultural sensitivity issues
- Distinct silhouette: tall, winged, dark, glowing red eyes — unambiguously not Bigfoot
- Not locked to Point Pleasant, WV — sightings are reported broadly; Hollow Pines can own it

**The 5 searchers, 3-night structure, duffel extraction loop, and 6-proof win condition all survive
unchanged.** This is a creature swap + ability redesign + lore rewrite, not a game redesign.

---

## Game overview

**Asymmetric 1-vs-5 multiplayer horror.** 5 searchers hunt a Pacific NW forest for evidence of
Mothman, played by the 6th player. Browser prototype (Three.js + Colyseus, TypeScript) is the
behavioral spec; active development is in **Unity + FishNet** (`unity/`, `csharp/`).

Read `CLAUDE.md` at the repo root for the full architecture, message contract, and conventions.
Read `docs/CHARACTER_FUNC_DEV.md` for the authoritative spec on all 5 character specialties.

**Win conditions (unchanged):**
- Searchers: bank 6 pieces of proof at the evidence duffel before 3 nights end
- Mothman: survive all 3 nights

**Narrative framing shift:**
- Bigfoot path: "prove it exists"
- Mothman path: "film it before something happens" — Mothman appears before disasters; the team
  needs footage to warn people before history repeats in Hollow Pines. Nobody will believe them
  without it. Nobody believed the last witnesses either.

---

## Ability kit redesign

The core asymmetric structure is unchanged. Bigfoot's abilities map to Mothman equivalents with
adjusted feel — the two-step Roar→Grab becomes a faster Dive→Grab that rewards positional play.

| Bigfoot ability | Mothman equivalent | Key difference |
|---|---|---|
| **Roar** (AoE freeze) | **Screech** (AoE disorientation) | Doesn't stop movement; scrambles compass, map, and directional audio for ~8s. Harder to avoid, less punishing — they can run, they just can't navigate. |
| **Leap** (stamina-gated bound) | **Dive** (aerial lunge from height) | Requires being at elevation (perched or after a glide). Closes distance in a fast arc. Landing impact staggers nearby searchers. Stamina-gated. |
| **Charge** (speed burst dash) | **Glide** (sustained forward burst) | Similar speed, wider turning radius — trades maneuverability for distance and silence. |
| **Grab** (grab frozen hunter) | **Grab** (unchanged) | Same grab/drag/incapacitate loop. No freeze prerequisite — Screech disorientation creates the window. |
| **Surface-climb** (vault structures) | **Perch** (land atop structures) | Mothman can roost on tower/RV roof. Passive height advantage; enables Dive. |
| **Senses overlay** (predator vision) | **Premonition** (prophetic vision) | Highlights searchers' recent paths (where they were) rather than current glowing positions (where they are). Fits the harbinger lore. |
| **Cave fast-travel** | **Removed** | No direct replacement in Phase 1. Rooftop-to-rooftop Glide covers some of this mobility. |

**Combo redesign:**
- Bigfoot: Roar first (freeze), then Grab. Two steps, with a freeze window.
- Mothman: Dive from Perch (fast close), then Grab immediately. Faster combo, shorter reaction window.
  Compensated by: Screech can be used beforehand to disorient, and Mothman has better aerial speed.

**Searcher counterplay (unchanged):**
- Dazzle: flashlight trained on Mothman disrupts it (~1.2s) — stuns mid-Dive especially
- Revive: hold E on downed teammate (~4s)
- Vault: hop over fallen log
- Eli flash: stuns Mothman mid-Dive (most valuable use case given Dive speed)

---

## Evidence trail changes

**Keep:**
- Ground footprints (Mothman lands and walks; prints are taloned and bipedal — distinct from bear)
- Branch disturbance clues

**Add:**
- **Wing-scrape marks:** high on tree trunks where nothing else reaches. Wren spots these.
  Server spawns on Perch; visible to all searchers but Wren sees them at 2× range.
- **Dead bird clusters:** 3–5 dead birds beneath a Mothman perch site. Environmental harbinger sign.
  Any searcher can see these; Wren spots them farther.
- **Red-eye sighting:** brief directional glint on the map when Mothman is airborne. Not a
  precise position — just a heading. Replaces the roar-direction HUD ping.

**Modify:**
- Castable prints (Mara's mechanic) → **Biological sample from a wing-scrape or bird cluster.**
  Identical mechanics (6s stationary channel, +1 proof, Mara only), different prop and lore.
  Mara analyzes the physical evidence rather than casting plaster. No gameplay change.

**New evidence type:**
- **Theo's screech recording:** Theo's parabolic mic records a Mothman screech as audio evidence.
  Completes after ~5s sustained proximity during a screech event. No LOS required. Banks at duffel
  for +1 proof. Only evidence type requiring no visual contact — meaningfully different risk profile.

---

## The 5 searchers (Mothman version)

All 5 characters keep their names, personalities, and core mechanical specialties. Minor lore
updates only — the mechanics are the same systems applied to the new evidence types.

| Character | Specialty | Mothman-specific note |
|---|---|---|
| 🔬 Dr. Mara Okonkwo | Analysis | Analyzes harbinger evidence (wing-scrape sites, bird clusters) instead of plaster casting. Same 6s channel, same +1 proof, new prop. Her "is this real?" expertise applies even better to a prophetic creature. |
| 📷 Eli Vance | Photo | Unchanged. Flash stuns Mothman mid-Dive (extremely valuable). Film range bonus applies to Mothman footage. |
| 🥾 Wren Castellano | Tracking | Sees wing-scrape marks and bird clusters at 2× range. Ground prints unchanged. |
| 🎙️ Theo Park | Sound | Hears Mothman's sub-sonic wing beats before a Dive — the pre-attack audio warning. Screech recording = new evidence type. |
| 🩹 Sam Reyes | Endurance | Completely unchanged. Revive/stamina/battery gift stay. |

---

## Story / lore rewrite targets

### STORY.md
Replace the Bigfoot-centric opening brief with Mothman framing:

- The five characters aren't going to prove a myth. They're going to document a herald.
- Sightings clusters have been reported in the Hollow Pines area. The last time patterns like this
  appeared somewhere, a disaster followed within weeks. Nobody believed those witnesses.
- The goal shifts: the footage isn't for credibility alone — it's a warning. If they get it on
  record before something happens, maybe this time someone listens.
- The Mothman's perspective paragraph (currently "Not a villain. A resident."): Mothman doesn't
  care about the searchers specifically. It appears where tragedy is about to occur. Their presence
  is incidental to whatever it came to witness. It doesn't want to be documented because being
  documented changes what it is.

### GAME_DESIGN.md
Update all creature references. Win condition framing stays structurally identical.

### docs/CHARACTER_FUNC_DEV.md
Update Mara's specialty description (biological sample replaces casting).
Add Theo's screech recording as an evidence type.

---

## Build order

### Phase 1 — Creature rename + ability scaffolding
1. Find-and-replace `"bigfoot"` role string → `"mothman"` across codebase
   (TypeScript: `ForestRoom.ts`, `GameState.ts`, `Game.ts`, `Network.ts`, `HUD.ts`;
   Unity: `GameManager.cs`, `HPPlayer.cs`, `HPHud.cs`, schema)
2. Rename ability RPCs: `roar` → `screech`, `leap` → `dive`, `charge` → `glide`
   Update all message handlers, client send functions, and HUD references
3. Stub new ability parameters using Bigfoot's current values as a baseline
4. Remove cave fast-travel RPC and server handler (stub out for now)

### Phase 2 — New ability feel
5. **Screech:** disorientation effect (spinning compass overlay, scrambled directional audio,
   map blackout) instead of movement freeze. Duration ~8s. Searchers keep moving.
6. **Dive:** requires Mothman to be at elevation (Perch or airborne). Fast arc to target ground
   point. Landing impact: stamina drain + brief stumble on searchers within ~3m radius.
7. **Perch:** Mothman can land on top of tower/RV roof. Needs collision support for Mothman's
   mass. Enables Dive from above.
8. **Premonition (senses overlay):** render fading path trails where searchers walked in the last
   ~10s rather than their current glowing silhouettes.
9. **Glide:** largely same as Charge — forward burst — but with a wider minimum turning radius.

### Phase 3 — Evidence trail
10. Add wing-scrape mark clue type: server spawns on Perch, lifetime similar to footprints
11. Add dead bird cluster: spawns under Perch site, static, longer lifetime
12. Replace castable print mechanic with biological sample (Mara): same code path, new
    `ClueMarker` variant and interact prompt
13. Implement Theo screech recording: proximity + duration check during screech event, produces
    carried proof item

### Phase 4 — Story / lore
14. Rewrite `STORY.md` opening brief, Mothman section, and arrival passage
15. Update character description for Mara in `STORY.md` (harbinger evidence vs. casting)
16. Update `GAME_DESIGN.md` creature references
17. Update briefing cards and HUD text

---

## Key files

**TypeScript web build (behavioral spec):**
- `server/src/rooms/ForestRoom.ts` — all ability handlers (roar/leap/charge/grab/caveTravel)
- `server/src/rooms/schema/GameState.ts` — Player schema, role strings
- `client/src/core/Game.ts` — client ability triggers, HUD wiring
- `client/src/core/Network.ts` — send functions and message names
- `client/src/ui/HUD.ts` — role display text
- `shared/sim/constants.ts` — creature speed/ability tunables

**Unity build (active dev):**
- `GameManager.cs` — match lifecycle, ability server logic
- `HPPlayer.cs` — ability RPCs, carry inventory, hold-action resolver
- `HPHud.cs` — briefing cards, prompts, role display
- `ClueManager.cs` / `ClueMarker.cs` — clue types and spawning

**Lore docs to update:**
- `docs/STORY.md`
- `docs/GAME_DESIGN.md`
- `docs/CHARACTER_FUNC_DEV.md` (Mara + Theo sections)

---

## Conventions

- TypeScript strict + Colyseus 0.15 legacy decorators on server — don't modernize decorators
- Unity + FishNet is the active build; TypeScript build must stay compiling even if not extended
- Low-poly smooth vertex normals (no blocky/voxel look)
- Server owns win condition logic; client owns prediction + presentation
- `shared/sim/` must stay dependency-free (no Three.js, no DOM, no decorators)
- Keep ability tunables at the top of `ForestRoom.ts` / `GameManager.cs`
- Verify: `cd client && npx tsc --noEmit && npx vite build` and `cd server && npx tsc --noEmit && npm test`
