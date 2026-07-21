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
- **Goal:** Capture **3 solid videos of Bigfoot** (team total — every searcher's footage counts toward the same tally) before Bigfoot survives all three nights.
- **Filming:** Hold the camera on Bigfoot, in frame and in range, to build a clip; ~3s in frame = one solid video. You must actually *see* Bigfoot — usually by lighting it with your flashlight or catching its eye-shine.
- **Roared → frozen:** Bigfoot's roar **freezes** every nearby searcher in fear for 30s — you can still look around, but you can't move or film.
- **Grabbed → incapacitated:** if Bigfoot reaches a frozen searcher and grabs them, they're **incapacitated for 60s** (screen fades to black, Bigfoot can drag them anywhere) and the **team's collected footage is erased**. After that they **recover**, but move **25% slower for 30s**. No permanent elimination.
  > **Superseded in the Unity build (2026-07-20).** Proof is now *carried* and only banked at the camp duffel, so a grab **spills what that one searcher was carrying** as a recoverable pile on the ground — it never touches the team's banked total, and it destroys nothing. See `CHARACTER_FUNC_DEV.md` §8. The rule above is the **web build's** behaviour and is retained because the web build is still the behavioural spec for everything else.
- **Cooperative tools:** Each searcher has one specialty (see `STORY.md`), but all share the same base verbs: move, sprint, flashlight, film, ping.
- **Resources:** Stamina (sprint — hitting empty leaves you winded until it recovers), flashlight battery.

### 2.2 Bigfoot (1) — *the resident*
- **Goal:** **Survive 3 nights** without the expedition getting their 3 videos.
- **Start:** Bigfoot begins at one of several **cave** lairs out in the forest — never at the searchers' camp.
- **Strengths:** ~1.2× searcher speed and better night vision.
- **Roar (right-click):** an AoE fear blast (~25m) that **freezes** nearby searchers for 30s. ~25s cooldown.
- **Grab (left-click):** grab a **frozen** searcher to incapacitate them for 60s — **drag them anywhere** and **erase the team's footage** *(Unity build: spills their carried proof as a recoverable pile instead — see §2.1)*. Left-click again to drop them.
- **Cave network (fast travel):** the caves form a tunnel network. In a cave mouth, open the **map (`M`) and click a destination cave** to emerge there — flank the team or escape a stakeout. (~2s cooldown.)
- **The trail problem:** Bigfoot **leaves a trail** — footprints and broken branches — that hunters follow. Moving more = a longer, fresher trail; standing still hides you.
- **Senses:** sees who is currently **filming** (their recording light) and which searchers are **frozen** (a grab target) vs **incapacitated**.

---

## 3. Core loop (3 nights)

```
NIGHT (8pm→8am)  →  TRACK  →  FILM  →  ROAR/GRAB pressure  →  (fade) next night  →  RESOLUTION
```

1. **Night begins (8pm):** searchers at base camp, Bigfoot at a cave. Daylight is skipped — each night runs **8pm → 8am**, then a fade to the next.
2. **Track:** Bigfoot leaves **footprints and broken branches**. Searchers read the fading trail (and their map, when in contact) to close in.
3. **Film:** get Bigfoot in frame (hold right‑mouse), in range, lit up — ~3s of clean footage = one solid video. The team needs **3** (`videosRequired`), pooled across all searchers and **carried across nights**.
4. **Pressure:** Bigfoot **roars** to freeze searchers, then **grabs** a frozen one to incapacitate + drag them and **wipe the team's footage**.
5. **Resolution:** 3 videos → searchers win; Bigfoot survives all 3 nights → Bigfoot wins. See §5.

---

## 4. Nights & time

The hunt is **3 nights**, each a compressed **8pm → 8am** (`NIGHT_SECONDS`, daylight skipped). `timeOfDay` runs 0→1 within a night, then `nightNumber` advances with a fade. The sky/fog lerp dusk → deep night → dawn each night.

| Phase        | Clock (of night) | Sky / fog                  |
|--------------|------------------|----------------------------|
| **Dusk**     | 0.00–0.15        | Amber/violet, light fog    |
| **Nightfall**| 0.15–0.45        | Deep blue, fog thickens    |
| **Midnight** | 0.45–0.75        | Near‑black, cold moonlight |
| **Witching** | 0.75–0.95        | Black, heavy fog           |
| **Dawn**     | 0.95–1.00        | Pale teal — roll to next night (or end on night 3) |

---

## 5. Win / loss conditions

**Searchers win if:** the team captures **≥ 3 solid videos** of Bigfoot (`videosRequired`, pooled across all searchers and nights).

**Bigfoot wins if:** it **survives all `totalNights` (3) nights** without the team reaching the footage target.

**Tunable knobs** (server `ForestRoom`): `videosRequired`, `FILM_SECONDS`/`FILM_RANGE` (filming), `ROAR_RADIUS`/`ROAR_COOLDOWN`/`FREEZE_SECONDS` (roar), `GRAB_RADIUS`/`INCAP_SECONDS`/`SLOW_SECONDS` (grab), `NIGHT_SECONDS`/`TOTAL_NIGHTS`, `CLUE_LIFETIME`/`STRIDE`/`BRANCH_CHANCE` (the trail), Bigfoot speed.

---

## 6. Controls

> Designed to be discoverable. The HUD shows the relevant prompt contextually (e.g. "Hold E to collect").

### Searcher
| Input | Action |
|-------|--------|
| `W A S D` | Move |
| Mouse | Look |
| `Shift` | Sprint (drains stamina) |
| `Space` | Jump / **vault a fallen log** (stamina‑gated — logs are solid, so vaulting or going around are the only ways past) |
| `F` | Toggle flashlight |
| Right Mouse (hold) | Raise camera & **film** Bigfoot (build a video clip) |
| `M` | Toggle the **map** (your position, base camp, caves, teammates, the clue trail) |
| `E` | Interact — revive teammate *(planned, Phase 3)* |
| `Q` | Drop a **stakeout ping** for the team (or click the map to place one) |
| `Tab` | Objectives & footage count |
| `Esc` | Release mouse / menu |

### Bigfoot
| Input | Action |
|-------|--------|
| `W A S D` | Move |
| Mouse | Look |
| `RMB` | **Roar** — freeze nearby searchers (~25m) for 30s (~25s cooldown) |
| `LMB` | **Grab** a frozen searcher → incapacitate + drag + erase footage; click again to drop |
| `M` | Toggle the **map**; in a cave mouth, **click a cave to fast-travel** there |
| `Shift` | Sprint (drains stamina) |
| `Space` | Leap / climb *(planned)* |

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
- In the **world** each clue **fades and expires** after `CLUE_LIFETIME`. On the **map** the readout is tighter: only tracks from the last `MAP.clueWindow` seconds show, and only while the hunter is **in contact** — Bigfoot within `MAP.hearRange` ("heard nearby") **or** a recent clue within `MAP.evidenceSight` ("sees evidence"). Walk away and the map trail clears.
- Extensible: add `fur`, `claw‑marked tree`, `scat`, or `nest` as new `ctype`s; a Tracker specialty could highlight them.

### 7.4 Roar → grab → incapacitate (Bigfoot's offense) — *implemented*
- **Roar** (`RMB`, `ROAR_COOLDOWN`): every active searcher within `ROAR_RADIUS` is **frozen** for `FREEZE_SECONDS` — they can look but not move or film.
- **Grab** (`LMB`): grabs the nearest **frozen** searcher within `GRAB_RADIUS` → **incapacitated** for `INCAP_SECONDS` (their screen fades to black, Bigfoot **drags** them by walking), and the **team's `videosCaptured` is wiped to 0**. Left-click again drops them (they stay incapacitated where left).
- **Recovery:** after `INCAP_SECONDS` the searcher recovers to active but is **slowed** (`PLAYER.slowFactor`) for `SLOW_SECONDS`. Not eliminated — Bigfoot wins only by surviving the nights.

### 7.5 Stamina & exhaustion — *implemented*
- Sprinting drains **stamina**; walking/idle regenerates it. **Hitting 0 exhausts you**: sprint is locked out until stamina recovers past `PLAYER.staminaRecover` (no more sprint‑stutter at empty).

### 7.6 Audio (design intent)
- Directional footsteps, distant roars, the creek, wind, flashlight click, heartbeat that rises with proximity. Audio is a primary information channel for both sides.

---

## 8. Art & rendering direction

- **Geometry:** low‑poly meshes with **smooth vertex normals** (`computeVertexNormals`, `flatShading: false`) → rounded, readable, *not* voxel/blocky. Trees = tapered trunks + stacked smooth conifer cones; terrain = noise‑displaced plane with smoothed normals.
- **Materials:** `MeshStandardMaterial`, low‑saturation palette, subtle emissive on lights/eyes.
- **Atmosphere:** `FogExp2` distance fog tuned per phase; `ACESFilmicToneMapping`; post‑processing pass for **bloom** (flashlights), **vignette**, and light **film grain**.
- **Sky:** gradient skydome / hemisphere light driven by `timeOfDay` from dusk → night → dawn.
- **Performance:** instanced trees/ferns, LODs, baked where possible, shadow only from key lights + the local flashlight.
- **Landmarks (navigation):** the base‑camp clearing (campfire + lit **RV**) anchors the searchers; **cave entrances** (rounded boulder horseshoes with a dark mouth and a faint inner glow) mark Bigfoot's lairs and fast‑travel nodes. Distinct silhouettes help players orient in the dark.
- **Logging trails — *implemented (Unity)*:** four seed‑derived trails meander out of the camp clearing. They are **real terrain, not decoration**: no trees grow in the corridor, so a trail is a genuinely open lane. Taking one is a **speed‑for‑exposure trade** — fast going and easy navigation, bought with long sightlines that make you simple to spot and simple to film.
- **Undergrowth:** ferns, bushes and mossy rocks fill the forest floor. Deliberately **render‑only and knee‑to‑waist height** — clutter never blocks a searcher, and it is too low to hide a standing player that the line‑of‑sight check believes is visible. Anything tall enough to break that promise has to be a real collider in the shared sim instead.

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
- **Map (`M`) — *implemented*:** top‑down overlay for both roles showing the player's position + heading, base camp, and caves. Hunters also see teammates, **stakeout pings**, and the **recent clue trail — but only while in contact** (Bigfoot heard nearby, or recent footprints in sight). For Bigfoot in a cave mouth, caves become **clickable fast‑travel destinations** (with a fade‑to‑black transition). Opening the map frees the cursor and pauses local movement.
  > **Unity build divergence (2026‑07‑20): caves start hidden from searchers.** The map used to hand
  > the team all five lairs at spawn, which deleted the exploration half of the game — you could stake
  > out Bigfoot's front doors on night 1 without having seen the forest. A mouth now appears only once
  > a searcher physically walks within ~22 m of it. Discovery is **per cave** (finding one says nothing
  > about the others) and **team‑wide** (all five searchers get it, so scouting is worth calling out),
  > and it **resets at the start of each match**. Bigfoot always sees its own network. The map footer
  > counts them off — `caves found 2/5` — so a blank map reads as "not found yet" rather than "this
  > map doesn't show caves".
- **Stakeout pings (`Q` / map click) — *implemented*:** hunters drop a shared marker (one active per hunter; ~35s lifetime) to coordinate. Pings show on every hunter's map and as an in‑world beacon; they're hidden from Bigfoot.
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
