# Hollow Pines — Bigfoot Depth Path

## Why this branch exists

Hollow Pines overlaps with the Steam game BIGFOOT (app 509980), which also has a player-controlled
Bigfoot PvP mode (1 Bigfoot vs up to 4 hunters). The decision on this branch is to **stay with
Bigfoot and win on depth**, not swap the creature. The Steam game is a survival/hunting co-op that
bolted on a player-Bigfoot mode. Hollow Pines was designed as asymmetric PvP from the ground up with:

- A film-not-kill win condition (evidence, not combat)
- 5 named characters with distinct asymmetric mechanical specialties
- A duffel extraction loop with carry risk
- A 3-night escalating arc with per-night modifiers
- Server-authoritative movement + anti-cheat depth

None of those exist in the Steam game. This branch deepens all of them.

---

## Game overview

**Asymmetric 1-vs-5 multiplayer horror.** 5 searchers hunt a Pacific NW forest for evidence of
Bigfoot, played by the 6th player. Browser prototype (Three.js + Colyseus, TypeScript) is the
behavioral spec; active development is in **Unity + FishNet** (`unity/`, `csharp/`).

Read `CLAUDE.md` at the repo root for the full architecture, message contract, and conventions.
Read `docs/CHARACTER_FUNC_DEV.md` for the authoritative spec on all 5 character specialties.
Read `docs/GAME_DESIGN.md` for the full GDD.

**Win conditions:**
- Searchers: bank 6 pieces of proof at the evidence duffel before 3 nights end
- Bigfoot: survive all 3 nights

**Evidence types (Unity build):**
- Film footage: any searcher holds RMB with Bigfoot in frame/range/LOS for ~3s
- Plaster cast: Mara only, 6s stationary channel on a castable footprint
- Audio recording (Theo): planned, not yet shipped

**The duffel:** proof counts only when banked at the RV duffel. A grab destroys only what that
searcher was carrying — stored proof is permanent. Bigfoot cannot touch the duffel.

**3-night escalation:** each night gets faster, darker, meaner. Server-owned `ESCALATION` table
drives `bigfootSpeedMul`, `batteryDrainMul`, `staminaDrainMul`, `roarCooldownSec`.

---

## The 5 searchers

Each match, players are randomly assigned one of these roles. All share a core kit (film, revive,
dazzle, vault, ping). Specialties make each one better at a specific system.

| Character | Specialty | Core mechanic |
|---|---|---|
| 🔬 Dr. Mara Okonkwo | Analysis | Casts plaster from deep footprints (+1 proof, 6s channel, Mara only). Wren spots the prints; Mara works them. |
| 📷 Eli Vance | Photo | Film range ×1.25. Camera flash: 22m, stuns Bigfoot 3s, reveals Eli's position 5s. 1 charge/night. |
| 🥾 Wren Castellano | Tracking | Evidence sight ×2, clue window ×1.5, silent footsteps. Can mark Bigfoot's trail for the team (G key). |
| 🎙️ Theo Park | Sound | Hear range ×1.8 (map shows Bigfoot nearby from much farther). Film speed ×1.15. Roar-direction HUD lingers 10s. |
| 🩹 Sam Reyes | Endurance | Revive time ×0.6 (4s → 2.4s). Stamina max 150 (vs. 100). Battery gift: +50 battery to a teammate, 1 charge/night. |

---

## Current Unity build state (as of 2026-07-23)

**Shipped:**
- All 5 character specialties with full mechanics
- Duffel extraction loop (carry/bank risk)
- Castable prints + Mara casting (second win path)
- Dev persona picker (force any character for testing without re-rolling)
- Crouch stealth (crouching Bigfoot leaves no trail; crouching searcher is silent)
- Clue decay (clues visibly go cold over their lifetime)
- Bigfoot: leap, charge, surface-climb, senses overlay, cave fast-travel
- Searcher: dazzle, revive, vault
- 3-night escalation with per-night modifiers

**Not yet shipped:**
- Theo's audio recording as a third evidence type
- Briefing card copy (cards show raw stat numbers; should be plain-language capability descriptions)
- Night 2 and 3 briefing screens (night 1 dusk briefing exists; later nights have none)
- Proof-dropped-on-grab (currently destroyed; dropping as recoverable pile is the better design)
- Balance pass on all specialty numbers (first-guess values, unplaytested)
- Hair samples / scat as additional non-film evidence (Mara's cast is the only non-film path today)

---

## Priority work on this branch

### 1. Theo's audio recording evidence
Theo's parabolic mic should produce a third evidence type alongside film and cast.
- Theo holds record while Bigfoot is within his `hearRange` (~63m with specialty)
- Recording completes after ~5s sustained proximity (no LOS required — audio only)
- Goes into carried inventory; banked at duffel like film/cast
- Counts toward the 6-proof total
- This is the only evidence type that requires NO visual contact — meaningfully different risk profile

### 2. Briefing card copy pass
Cards currently print raw multipliers and meters. Each card should describe what the player can DO,
not stat values. Derive plain-language phrasing from the live constant rather than printing the value.
- Don't print: "×1.8 hear range (63m)"
- Do print: "You hear Bigfoot moving from twice as far as anyone else."
Keep the derived values in code as the source of truth — pick phrases from thresholds, not raw numbers.
Full brief in `docs/CHARACTER_FUNC_DEV.md §5b`.

### 3. Proof-dropped-on-grab
Currently a grab destroys the carried proof instantly. The better design: carried proof drops as a
recoverable pile at the grab location that the team can retrieve.
- Adds a decision for Bigfoot: drag the searcher away or guard the dropped proof?
- Adds a decision for the team: rescue the downed teammate or recover the proof first?
- Implement as a world object spawned at grab position, auto-despawn after ~60s
- A grabbed searcher's proof is NOT destroyed if a teammate retrieves the pile before it despawns

### 4. Hair sample evidence
A second non-film path any searcher can use (not Mara-only like casting).
- Server flags some branch-break clues as `Hairy` (random chance, ~20%)
- Any searcher: hold interact ~2s to collect; goes to carry inventory, bank at duffel for +1 proof
- Weaker than Mara's cast (anyone can do it, lower risk, lower yield feels right)
- Natural addition: hair on branches is the one thing Bigfoot would physically shed

### 5. Night 2 and 3 escalating briefings
Night 1 has a dusk briefing. Nights 2 and 3 should have briefings that reflect what's happened:
- How many proof pieces banked so far
- Who has been grabbed / incapacitated
- What night modifiers are now active ("It's faster tonight.")
This makes the 3-night structure feel like a story with stakes, not just a timer resetting.

---

## Key files (Unity build)

- `HPPlayer.cs` — player controller, carry inventory, hold-action resolver
- `GameManager.cs` — match lifecycle, night transitions, win condition, duffel deposit (`TryDeposit`)
- `HPHud.cs` — all HUD including briefing cards (`CardFor`), duffel manifest, prompts
- `ClueManager.cs` / `ClueMarker.cs` — clue spawning, castable flag, lifetime decay
- `WorldBuilder.cs` — duffel position, structure placement
- `ServerVitals.cs` — battery/stamina resource envelope

TypeScript web build (behavioral spec, not active dev target):
- `server/src/rooms/ForestRoom.ts` — authoritative room
- `shared/sim/` — deterministic sim (movement, terrain, collision)
- `client/src/` — Three.js client

---

## Conventions

- Unity + FishNet is the active build — new gameplay goes here
- TypeScript/Colyseus web build is the behavioral spec; keep it compiling but don't add features
- Low-poly smooth vertex normals (no blocky/voxel look); fog + ACES tone mapping
- Server owns anything that touches the win condition; client owns prediction + presentation
- Keep ability tunables at the top of `GameManager.cs` / `ForestRoom.ts` — no scattered magic numbers
- Don't hardcode display numbers in briefing copy — derive from live constants
- Verify with: `cd client && npx tsc --noEmit && npx vite build` and `cd server && npx tsc --noEmit && npm test`
