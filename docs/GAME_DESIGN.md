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
- **Goal:** Capture **3 solid videos of Bigfoot** (team total) — without getting caught. Surviving to dawn is a secondary escape win.
- **Filming:** Hold the camera on Bigfoot, in frame and in range, to build a clip; ~3s in frame = one solid video. You must actually *see* Bigfoot — usually by lighting it with your flashlight or catching its eye-shine.
- **Fragile:** If Bigfoot gets close enough it **catches** you — you're out for the match (spectate). When the whole team is caught, Bigfoot wins. (Future: downs + teammate revives, per ROADMAP Phase 3.)
- **Cooperative tools:** Each searcher has one specialty (see `STORY.md`), but all share the same base verbs: move, sprint, jump, flashlight, interact, ping.
- **Resources:** Stamina (sprint), flashlight battery (depletes; spares exist in the world / from the medic).

### 2.2 Bigfoot (1) — *the resident*
- **Goal:** Stop the expedition before they get 3 videos — catch the searchers before the tape gets out.
- **Start:** Bigfoot begins at one of several **cave** lairs out in the forest — never at the searchers' camp.
- **Strengths:** ~1.2× searcher speed and better night vision. (Future: **charge**, **leap**, **climb**, and **roar** — ROADMAP Phase 3.)
- **Cave network (fast travel):** The caves form a tunnel network. Standing in a cave mouth, Bigfoot opens the **map (`M`) and clicks the destination cave** to emerge there — crossing the map in an instant to flank the team or escape a stakeout. (~2s cooldown.)
- **The trail problem:** Bigfoot **leaves a trail** — footprints and broken branches — that hunters follow to find it. Moving more = a longer, fresher trail. Standing still hides you but lets the team regroup.
- **Senses:** Sees who is currently **filming** (their recording light) and active flashlight cones. (Future: full "instincts" overlay + smell, per ROADMAP.)
- **Counterplay:** Loud (footfalls audible), briefly **stunned by the photographer's flash** and by sustained focused flashlight, and must commit/cool‑down on big abilities.

---

## 3. Core loop (one match)

```
DUSK (briefing)  →  TRACK  →  FILM  →  ESCALATION (night phases)  →  RESOLUTION
```

1. **Dusk briefing:** Searchers spawn at base camp; Bigfoot starts out in the trees. During dusk Bigfoot **cannot catch** anyone (grace period).
2. **Track:** Bigfoot leaves **footprints and broken branches** as it moves. Searchers read this fading trail to close in on it.
3. **Film:** Get Bigfoot in frame (hold right‑mouse), in range, lit up — ~3s of clean footage = one solid video. The team needs **3** (`videosRequired`).
4. **Escalation:** The match clock drives **night phases** (below); it gets darker and Bigfoot faster. Get too close and Bigfoot catches you.
5. **Resolution:** 3 videos → searchers win; whole team caught → Bigfoot wins. See §5.

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
- The team captures **≥ 3 solid videos** of Bigfoot (`videosRequired`), **or**
- **≥ 1 searcher** survives (un‑caught) until **dawn** — the expedition escapes.

**Bigfoot wins if:**
- **Every searcher** has been **caught** before the team reaches 3 videos.

**Tunable knobs** (server `ForestRoom`): `videosRequired`, `FILM_SECONDS` (footage per video), `FILM_RANGE`, `CATCH_RADIUS`, `CLUE_LIFETIME`/`STRIDE`/`BRANCH_CHANCE` (the trail), match length, Bigfoot speed.

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
| Right Mouse (hold) | Raise camera & **film** Bigfoot (build a video clip) |
| `M` | Toggle the **map** (your position, base camp, caves, teammates, the clue trail) |
| `E` | Interact — revive teammate *(planned, Phase 3)* |
| `Q` | Ping / shout (places a marker, alerts team) *(planned)* |
| `Tab` | Objectives & footage count |
| `Esc` | Release mouse / menu |

### Bigfoot
| Input | Action |
|-------|--------|
| `W A S D` | Move |
| Mouse | Look |
| `Shift` | Charge (burst of speed) |
| `Space` | Leap / climb |
| `M` | Toggle the **map**; in a cave mouth, **click a cave to fast-travel** there |
| `LMB` | Swipe / grab (downs a searcher) *(planned)* |
| `RMB` | Roar (area scare) *(planned)* |

---

## 7. Systems

### 7.1 Flashlight (the centerpiece)
- A `SpotLight` parented to the searcher's camera; warm cone + soft falloff + slight flicker.
- **Battery** drains while on; off = stealth but near‑blind. Spare batteries spawn in the world and the **Medic** carries extras.
- **Tells Bigfoot where you are:** an active cone is visible to Bigfoot's senses overlay at range. Light discipline is a real decision.
- **Defensive use:** sustained focus on Bigfoot's face builds a small stun meter (the photographer's flash fills it instantly).

### 7.2 Filming Bigfoot (how hunters win) — *implemented*
- Hold **right‑mouse** to raise the camcorder. A clip builds only while Bigfoot is **in frame** (centred within `FILM.halfFovDeg`), **in range** (`FILM_RANGE`), and **not hidden behind a trunk**. ~`FILM_SECONDS` of clean footage = **one solid video**; lose the shot and the clip drains.
- **Authoritative:** the client reports "Bigfoot in frame", the server confirms range and tallies `videosCaptured`; the team needs `videosRequired` (3).
- You usually must **light Bigfoot** (flashlight) or catch its **eye‑shine** to film it — which gives your own position away. Bigfoot sees a red **recording light** on anyone filming it.

### 7.3 Clue trail — the hint framework — *implemented*
- As Bigfoot walks, the **server** drops `Clue` entities every `STRIDE` metres: mostly **footprints** (oriented along its heading), occasionally **broken branches**. They're shared state, so the whole team follows the same trail.
- Each clue **fades and expires** after `CLUE_LIFETIME`. A dense, bright trail = Bigfoot is near and recent; a sparse/faint one = it has moved on ("the trail goes cold").
- Extensible: add `fur`, `claw‑marked tree`, `scat`, or `nest` as new `ctype`s; a Tracker specialty could highlight them.

### 7.4 Catching, stamina & (future) revives
- Sprinting drains **stamina**; walking/idle regenerates it.
- **Caught:** if Bigfoot gets within `CATCH_RADIUS` of an active hunter (after the dusk grace), that hunter is **out** (spectates). Whole team caught → Bigfoot wins. *(Planned: downs + teammate revives instead of instant‑out, Phase 3.)*

### 7.5 Audio (design intent)
- Directional footsteps, distant roars, the creek, wind, flashlight click, heartbeat that rises with proximity. Audio is a primary information channel for both sides.

---

## 8. Art & rendering direction

- **Geometry:** low‑poly meshes with **smooth vertex normals** (`computeVertexNormals`, `flatShading: false`) → rounded, readable, *not* voxel/blocky. Trees = tapered trunks + stacked smooth conifer cones; terrain = noise‑displaced plane with smoothed normals.
- **Materials:** `MeshStandardMaterial`, low‑saturation palette, subtle emissive on lights/eyes.
- **Atmosphere:** `FogExp2` distance fog tuned per phase; `ACESFilmicToneMapping`; post‑processing pass for **bloom** (flashlights), **vignette**, and light **film grain**.
- **Sky:** gradient skydome / hemisphere light driven by `timeOfDay` from dusk → night → dawn.
- **Performance:** instanced trees/ferns, LODs, baked where possible, shadow only from key lights + the local flashlight.
- **Landmarks (navigation):** the base‑camp clearing (campfire + lit **RV**) anchors the searchers; **cave entrances** (rounded boulder horseshoes with a dark mouth and a faint inner glow) mark Bigfoot's lairs and fast‑travel nodes. Distinct silhouettes help players orient in the dark.

---

## 9. Networking architecture (Colyseus)

- **Room:** `ForestRoom` (capacity 6). Holds `GameState` (see `server/src/rooms/schema/`):
  - `players: Map<sessionId, Player>` — `{ role, name, x, y, z, ry, flashlightOn, battery, stamina, status, filming, filmProgress }` (`y` is feet height)
  - `clues: Clue[]` — `{ id, ctype, x, z, ry }` — the footprint/branch trail Bigfoot leaves
  - `phase`, `timeOfDay`, `videosRequired`, `videosCaptured`, `winner`
- **Authority:** Server owns match phase/time, the clue trail, filming tallies, catching, and win/loss. v1 movement is client‑sent + server‑clamped; clients send `recording`/`inView` intent and the server confirms range before crediting footage. **Upgrade path:** server‑authoritative movement with reconciliation.
- **Tick:** server simulation/broadcast at ~15–20 Hz; clients **interpolate** remote players between snapshots.
- **Role assignment:** first joiner can volunteer for Bigfoot; otherwise random among connected players at match start.
- **Lobby → match → results** room states.

---

## 10. UI / HUD

- **Minimal in‑world HUD:** flashlight battery, stamina, footage captured/required, current phase clock, contextual prompt, filming viewfinder + clip bar.
- **Map (`M`) — *implemented*:** top‑down overlay for both roles showing the player's position + heading, base camp, and caves. Hunters also see teammates and the clue trail (a live tracking map). For Bigfoot in a cave mouth, caves become **clickable fast‑travel destinations**. Opening the map frees the cursor and pauses local movement.
- **Bigfoot HUD:** *(planned)* ability cooldowns, senses toggle, searchers‑caught counter.
- **Diegetic where possible** (battery on the flashlight model, footage in a field journal).

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
