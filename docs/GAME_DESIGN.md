# Hollow Pines — Game Design Document

**Genre:** Asymmetric (1v5) multiplayer first‑person survival‑horror
**Platform:** Web (desktop browser, keyboard + mouse)
**Session:** 6 players, one ~10–15 minute match, dusk → dawn
**Visual style:** Stylized low‑poly, smooth‑shaded, atmospheric

---

## 1. Design pillars

1. **Asymmetric tension.** Five fragile cooperators vs. one strong predator. Both sides have agency and outs.
2. **The dark is the enemy.** Light = safety *and* a beacon. Flashlight management is the core risk/reward verb.
3. **Stylized, never blocky.** Smooth low‑poly forms, fog, soft light. Mood over realism.
4. **Easy to learn.** Controls legible in under a minute; the HUD teaches by doing.

---

## 2. Roles

### 2.1 Searchers (5) — *the expedition*
- **Goal:** Collect evidence → transmit it from base camp's radio, **or** survive until dawn.
- **Fragile:** A direct Bigfoot grab **downs** them (not kills). Downed players crawl slowly and bleed a "found" beacon; a teammate can **revive** them with `E`. Two downs = incapacitated (out for the match, can still spectate/ping).
- **Cooperative tools:** Each searcher has one specialty (see `STORY.md`), but all share the same base verbs: move, sprint, jump, flashlight, interact, ping.
- **Resources:** Stamina (sprint), flashlight battery (depletes; spares exist in the world / from the medic).

### 2.2 Bigfoot (1) — *the resident*
- **Goal:** Stop the expedition before dawn — incapacitate the team and/or destroy evidence so it can't be transmitted.
- **Strengths:** ~1.3× searcher speed, can **charge**, **leap**, and **climb** trees/rocks; **roar** (area‑of‑effect scare that blurs vision + drains stamina + can extinguish a flashlight briefly).
- **Senses:** Sees active flashlight cones and recent footprints/sound pings at range (the "instincts" overlay). Can **smell** a trail to the nearest searcher on cooldown.
- **Counterplay:** Loud (footfalls audible), briefly **stunned by the photographer's flash** and by sustained focused flashlight, and must commit/cool‑down on big abilities.

---

## 3. Core loop (one match)

```
DUSK (briefing)  →  SEARCH  →  ESCALATION (night phases)  →  TRANSMIT / SURVIVE  →  RESOLUTION
```

1. **Dusk briefing (~30s):** Searchers spawn at base camp, choose a flashlight + tool. Bigfoot picks a den. Tutorial hints surface.
2. **Search:** 5–7 **evidence nodes** spawn across the map (footprint cast, fur tuft, photo target, audio cue, nest/bones, claw‑marked tree, scat). Searchers need **any N** (default 3) to win by transmission.
3. **Escalation:** The match clock drives **night phases** (below). Each phase darkens the world and buffs Bigfoot.
4. **Transmit / survive:** Bring collected evidence to the **base‑camp radio** and hold `E` to transmit (takes time, makes noise — a climactic risk moment). Alternatively, survive with ≥1 searcher standing until **dawn**.
5. **Resolution:** See win conditions (§5).

---

## 4. Time of day & night phases

A single match is a compressed night. `timeOfDay` runs 0→1 over the match length.

| Phase        | Clock      | Sky / fog                  | Effect |
|--------------|-----------|----------------------------|--------|
| **Dusk**     | 0.00–0.15 | Amber/violet, light fog    | Briefing + grace period; Bigfoot can't grab yet |
| **Nightfall**| 0.15–0.45 | Deep blue, fog thickens     | Full hunt begins |
| **Midnight** | 0.45–0.75 | Near‑black, cold moonlight  | Bigfoot +speed; flashlights drain faster |
| **Witching** | 0.75–0.95 | Black, heavy fog            | Bigfoot's senses overlay always on; max pressure |
| **Dawn**     | 0.95–1.00 | Pale teal sunrise           | Survivors win if any remain; Bigfoot weakens |

---

## 5. Win / loss conditions

**Searchers win if:**
- They **transmit ≥ N evidence** (default 3) from base camp, **or**
- **≥ 1 searcher** is still standing at **dawn**.

**Bigfoot wins if:**
- **All 5 searchers** are incapacitated before dawn, **or**
- The searchers **fail to transmit** enough evidence and Bigfoot has destroyed/blocked the rest.

**Tunable knobs:** N evidence required, number of nodes, match length, revive count, Bigfoot speed/cooldowns, battery drain rate.

---

## 6. Controls

> Designed to be discoverable. The HUD shows the relevant prompt contextually (e.g. "Hold E to collect").

### Searcher
| Input | Action |
|-------|--------|
| `W A S D` | Move |
| Mouse | Look |
| `Shift` | Sprint (drains stamina) |
| `Space` | Jump / vault low obstacles |
| `F` | Toggle flashlight |
| `E` | Interact — collect evidence / revive teammate / transmit |
| `Q` | Ping / shout (places a marker, alerts team) |
| `Tab` | Objectives & evidence count |
| `Esc` | Release mouse / menu |

### Bigfoot
| Input | Action |
|-------|--------|
| `W A S D` | Move |
| Mouse | Look |
| `Shift` | Charge (burst of speed) |
| `Space` | Leap / climb |
| `LMB` | Swipe / grab (downs a searcher) |
| `RMB` | Roar (area scare) |
| `E` | Smell nearest trail / destroy evidence node |

---

## 7. Systems

### 7.1 Flashlight (the centerpiece)
- A `SpotLight` parented to the searcher's camera; warm cone + soft falloff + slight flicker.
- **Battery** drains while on; off = stealth but near‑blind. Spare batteries spawn in the world and the **Medic** carries extras.
- **Tells Bigfoot where you are:** an active cone is visible to Bigfoot's senses overlay at range. Light discipline is a real decision.
- **Defensive use:** sustained focus on Bigfoot's face builds a small stun meter (the photographer's flash fills it instantly).

### 7.2 Evidence
- Nodes are interactable objects (`E` to collect/cast, takes a short channel). Some require a specific tool to be *full value* (e.g. audio for Theo, photo for Eli) but anyone can grab partials.
- Carried evidence must be **transmitted at base camp**. Bigfoot can **destroy uncollected nodes** and **scatter** dropped evidence from a downed searcher.

### 7.3 Stamina, downs & revives
- Sprinting drains stamina; walking/idle regenerates it. Roars drain it.
- Grab → **downed** (crawl, beacon). Teammate `E` revive (channel). Second down → **incapacitated**.

### 7.4 Bigfoot senses
- Toggleable/auto "instincts" overlay highlighting recent footprints, active flashlights, and ping/shout sources within radius. Stronger in later phases.

### 7.5 Audio (design intent)
- Directional footsteps, distant roars, the creek, wind, flashlight click, heartbeat that rises with proximity. Audio is a primary information channel for both sides.

---

## 8. Art & rendering direction

- **Geometry:** low‑poly meshes with **smooth vertex normals** (`computeVertexNormals`, `flatShading: false`) → rounded, readable, *not* voxel/blocky. Trees = tapered trunks + stacked smooth conifer cones; terrain = noise‑displaced plane with smoothed normals.
- **Materials:** `MeshStandardMaterial`, low‑saturation palette, subtle emissive on lights/eyes.
- **Atmosphere:** `FogExp2` distance fog tuned per phase; `ACESFilmicToneMapping`; post‑processing pass for **bloom** (flashlights), **vignette**, and light **film grain**.
- **Sky:** gradient skydome / hemisphere light driven by `timeOfDay` from dusk → night → dawn.
- **Performance:** instanced trees/ferns, LODs, baked where possible, shadow only from key lights + the local flashlight.

---

## 9. Networking architecture (Colyseus)

- **Room:** `ForestRoom` (capacity 6). Holds `GameState` (see `server/src/rooms/schema/`):
  - `players: Map<sessionId, Player>` — `{ role, x, y, z, ry, flashlightOn, battery, stamina, status }`
  - `evidence: ArrayLike<EvidenceNode>` — `{ id, type, x, z, collectedBy, transmitted }`
  - `phase`, `timeOfDay`, `evidenceTransmitted`, `winner`
- **Authority:** Server owns match phase, time, evidence, and validates actions. v1 movement is client‑sent + server‑relayed (lightweight, with sanity clamps); the **upgrade path** is server‑authoritative movement with reconciliation.
- **Tick:** server simulation/broadcast at ~15–20 Hz; clients **interpolate** remote players between snapshots.
- **Role assignment:** first joiner can volunteer for Bigfoot; otherwise random among connected players at match start.
- **Lobby → match → results** room states.

---

## 10. UI / HUD

- **Minimal in‑world HUD:** flashlight battery, stamina, evidence collected/required, current phase clock, contextual interact prompt.
- **Objectives panel** (`Tab`): evidence list, teammate status, transmission progress.
- **Bigfoot HUD:** ability cooldowns, senses toggle, searchers‑downed counter.
- **Diegetic where possible** (battery on the flashlight model, evidence in a field journal).

---

## 11. Accessibility & onboarding

- Remappable keys; toggle vs. hold options (sprint, flashlight); adjustable brightness/gamma (the game is *dark* by design — give a calibration screen).
- Colorblind‑safe markers; subtitle/closed‑caption cues for important sounds (roar, nearby footsteps).
- 60‑second interactive tutorial during the dusk briefing.

---

## 12. Scope guardrails

**v1 (vertical slice):** one map, core loop, 2 abilities per side, evidence + transmit, dusk→dawn lighting, 6‑player Colyseus room.
**Not in v1:** accounts/persistence, ranked matchmaking, voice chat, mobile, cosmetics, multiple maps.

See [`ROADMAP.md`](ROADMAP.md) for the phased build order and how it maps to this scaffold.
