# Metoh — Game Design Document

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
- **Goal:** Capture **3 solid videos of Yeti** (team total — every searcher's footage counts toward the same tally) before Yeti survives all three nights.
- **Filming:** Hold the camera on Yeti, in frame and in range, to build a clip; ~3s in frame = one solid video. You must actually *see* Yeti — usually by lighting it with your flashlight or catching its eye-shine.
- **Roared → frozen:** Yeti's roar **freezes** every nearby searcher in fear for 30s — you can still look around, but you can't move or film.
- **Grabbed → incapacitated:** if Yeti reaches a frozen searcher and grabs them, they're **incapacitated for 60s** (screen fades to black, Yeti can drag them anywhere) and the **team's collected footage is erased**. After that they **recover**, but move **25% slower for 30s**. No permanent elimination.
  > **Superseded in the Unity build (2026-07-20).** Proof is now *carried* and only banked at the camp duffel, so a grab **spills what that one searcher was carrying** as a recoverable pile on the ground — it never touches the team's banked total, and it destroys nothing. See `CHARACTER_FUNC_DEV.md` §8. The rule above is the **web build's** behaviour and is retained because the web build is still the behavioural spec for everything else.
- **Cooperative tools:** Each searcher has one specialty (see `STORY.md`), but all share the same base verbs: move, sprint, flashlight, film, ping.
- **Resources:** Stamina (sprint — hitting empty leaves you winded until it recovers), flashlight battery.

### 2.2 Yeti (1) — *the resident*
- **Goal:** **Survive 3 nights** without the expedition getting their 3 videos.
- **Start:** Yeti begins at one of several **cave** lairs out in the forest — never at the searchers' camp.
- **Strengths:** ~1.2× searcher speed and better night vision.
- **Roar (right-click):** an AoE fear blast (~25m) that **freezes** nearby searchers for 30s. ~25s cooldown.
- **Grab (left-click):** grab a **frozen** searcher to incapacitate them for 60s — **drag them anywhere** and **erase the team's footage** *(Unity build: spills their carried proof as a recoverable pile instead — see §2.1)*. Left-click again to drop them.
- **Cave network (fast travel):** the caves form a tunnel network. In a cave mouth, open the **map (`M`) and click a destination cave** to emerge there — flank the team or escape a stakeout. (~2s cooldown.)
- **The trail problem:** Yeti **leaves a trail** — footprints and broken branches — that hunters follow. Moving more = a longer, fresher trail; standing still hides you.
- **Senses:** sees who is currently **filming** (their recording light) and which searchers are **frozen** (a grab target) vs **incapacitated**.

---

## 3. Core loop (3 nights)

```
NIGHT (8pm→8am)  →  TRACK  →  FILM  →  ROAR/GRAB pressure  →  (fade) next night  →  RESOLUTION
```

1. **Night begins (8pm):** searchers at base camp, Yeti at a cave. Daylight is skipped — each night runs **8pm → 8am**, then a fade to the next.
2. **Track:** Yeti leaves **footprints and broken branches**. Searchers read the fading trail (and their map, when in contact) to close in.
3. **Film:** get Yeti in frame (hold right‑mouse), in range, lit up — ~3s of clean footage = one solid video. The team needs **3** (`videosRequired`), pooled across all searchers and **carried across nights**.
4. **Pressure:** Yeti **roars** to freeze searchers, then **grabs** a frozen one to incapacitate + drag them and **wipe the team's footage**.
5. **Resolution:** 3 videos → searchers win; Yeti survives all 3 nights → Yeti wins. See §5.

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

**Searchers win if:** the team captures **≥ 3 solid videos** of Yeti (`videosRequired`, pooled across all searchers and nights).

**Yeti wins if:** it **survives all `totalNights` (3) nights** without the team reaching the footage target.

**Tunable knobs** (server `MountainRoom`): `videosRequired`, `FILM_SECONDS`/`FILM_RANGE` (filming), `ROAR_RADIUS`/`ROAR_COOLDOWN`/`FREEZE_SECONDS` (roar), `GRAB_RADIUS`/`INCAP_SECONDS`/`SLOW_SECONDS` (grab), `NIGHT_SECONDS`/`TOTAL_NIGHTS`, `CLUE_LIFETIME`/`STRIDE`/`BRANCH_CHANCE` (the trail), Yeti speed.

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
| Right Mouse (hold) | Raise camera & **film** Yeti (build a video clip) |
| `M` | Toggle the **map** (your position, base camp, caves, teammates, the clue trail) |
| `E` | Interact — revive teammate *(planned, Phase 3)* |
| `Q` | Drop a **stakeout ping** for the team (or click the map to place one) |
| `Tab` | Objectives & footage count |
| `Esc` | Release mouse / menu |

### Yeti
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
- **Tells Yeti where you are:** an active cone is visible to Yeti's senses overlay at range. Light discipline is a real decision.
- **Defensive use:** sustained focus on Yeti's face builds a small stun meter (the photographer's flash fills it instantly).

### 7.2 Filming Yeti (how hunters win) — *implemented*
- Hold **right‑mouse** to raise the camcorder. A clip builds only while Yeti is **in frame** (centred within `FILM.halfFovDeg`), **in range** (`FILM_RANGE`), and **not hidden behind a trunk**. ~`FILM_SECONDS` of clean footage = **one solid video**; lose the shot and the clip drains.
- **Authoritative:** the client reports "Yeti in frame", the server confirms range and tallies `videosCaptured`; the team needs `videosRequired` (3).
- You usually must **light Yeti** (flashlight) or catch its **eye‑shine** to film it — which gives your own position away. Yeti sees a red **recording light** on anyone filming it.

### 7.3 Clue trail — the hint framework — *implemented*
- As Yeti walks, the **server** drops `Clue` entities every `STRIDE` metres: mostly **footprints** (oriented along its heading), occasionally **broken branches**. They're shared state, so the whole team follows the same trail.
- In the **world** each clue **fades and expires** after `CLUE_LIFETIME`. On the **map** the readout is tighter: only tracks from the last `MAP.clueWindow` seconds show, and only while the hunter is **in contact** — Yeti within `MAP.hearRange` ("heard nearby") **or** a recent clue within `MAP.evidenceSight` ("sees evidence"). Walk away and the map trail clears.
- Extensible: add `fur`, `claw‑marked tree`, `scat`, or `nest` as new `ctype`s; a Tracker specialty could highlight them.

### 7.4 Roar → grab → incapacitate (Yeti's offense) — *implemented*
- **Roar** (`RMB`, `ROAR_COOLDOWN`): every active searcher within `ROAR_RADIUS` is **frozen** for `FREEZE_SECONDS` — they can look but not move or film.
- **Grab** (`LMB`): grabs the nearest **frozen** searcher within `GRAB_RADIUS` → **incapacitated** for `INCAP_SECONDS` (their screen fades to black, Yeti **drags** them by walking), and the **team's `videosCaptured` is wiped to 0**. Left-click again drops them (they stay incapacitated where left).
- **Recovery:** after `INCAP_SECONDS` the searcher recovers to active but is **slowed** (`PLAYER.slowFactor`) for `SLOW_SECONDS`. Not eliminated — Yeti wins only by surviving the nights.

### 7.5 Stamina & exhaustion — *implemented*
- Sprinting drains **stamina**; walking/idle regenerates it. **Hitting 0 exhausts you**: sprint is locked out until stamina recovers past `PLAYER.staminaRecover` (no more sprint‑stutter at empty).

### 7.6 Audio (design intent)
- Directional footsteps, distant roars, the tarn's ice groans, wind, flashlight click, heartbeat that rises with proximity. Audio is a primary information channel for both sides.

### 7.7 Deep snow & trails — *implemented*

The signature mechanic of the Himalayan setting, and the one place the information flows **both**
ways. Two separate rules, deliberately not the same zone:

- **The slow.** Snow lies everywhere, but only the low ground *holds* it — ridges are scoured back
  to crust by the wind, valley floors pile deep enough to wade. Below `PLAYER.driftHeight` a
  searcher's speed falls toward `PLAYER.deepSnowFactor` (0.78 at full depth, ramping in over
  `PLAYER.driftDepth`). **The Yeti is unaffected** — it is built for this. Roughly a third of the
  map, which is the point: it's a routing decision a searcher can read and avoid, not a blanket tax.
  Applying it everywhere off-trail would cover ~96% of the map and simply make searchers slower.
- **The prints.** Anywhere off-trail — the *wider* rule, including the scoured high ground where
  wading costs nothing — a moving searcher presses a `"snowprint"` clue every `SNOWPRINT_STRIDE`
  (2.4 m), living `SNOWPRINT_LIFETIME` (35 s, **not** escalated). These are replicated to everyone
  but **render-filtered to the Yeti alone**, and they feed its senses overlay and its map. The
  searchers track the Yeti's clue trail; the Yeti tracks theirs.

The **packed trail network** is now a gameplay surface, not decoration: corridors are neither deep
nor print-recording, so a trail is fast and leaves nothing — bought with the long sightlines that
make you easy to film. The **camp clearing** is trampled flat and the **tarn** is ice; both are
exempt from both rules.

Zones are **derived, not replicated** — every client, the Colyseus server and the Unity host compute
them from the same seeded world (`deepSnowDepth` / `leavesSnowPrints` in `shared/sim/movement.ts`,
mirrored in `csharp/Metoh.Sim/Movement.cs`).

> **Trust note.** The slow is applied by the *client's* prediction on both builds. Neither
> `MountainRoom.applyMove` nor the Unity host re-runs `stepPlayer` — the server validates a move
> (bounds, speed-gate token bucket, collision, feet clamp) rather than recomputing it. A hacked
> client can therefore be "not slowed", never "faster than legitimate". Same trust level as
> `lakeHunterFactor`, which has always worked this way.

### 7.8 Deferred depth ideas

Carried over from the retired `BIGFOOT_DEPTH.md`, whose premise — stay with Bigfoot and win on
depth — was settled the other way by the Metoh migration. The depth ideas outlived the premise:

- **Theo's audio recording as a third evidence type.** Hold record while the Yeti is within his
  hear range for ~5 s; no line-of-sight required, which gives it a genuinely different risk profile
  from film (needs LOS) and casting (needs a print and 6 s stationary).
- **Proof dropped, not destroyed, on a grab.** Carried proof currently dies with the carrier. Better:
  it drops as a recoverable pile at the grab point, auto-despawning after ~60 s. That creates a real
  decision on both sides — the Yeti chooses between dragging the body off and guarding the spill,
  the team between rescuing the teammate and recovering the proof. *(Shipped since: `ProofPile.cs`.)*
- **Night 2 and 3 briefings.** Night 1 has a dusk card; the later nights have none. Briefings that
  report what has actually happened — proof banked, who has been taken, which modifiers are live —
  would make the three-night arc read as a story rather than a timer resetting.

---

## 8. Art & rendering direction

> **Two palettes on purpose (Metoh re-theme, Aug 2026).** The **Unity build** is the Himalayan one:
> moonlit snowpack, snow-caked conifers above the snowline, an expedition basecamp where the RV was,
> ice crevasses for caves, and a frozen tarn for the lake — which now reads as slick ice and breaking
> crust rather than water, though the sim still applies the same `LakeDepth` slow underneath. The
> **web build deliberately keeps its forest visuals**; only identifiers were renamed there. Anywhere
> below that says "forest", "ferns" or "lake" describes the web build and the pre-re-theme geometry
> the Unity build inherited — the *structure* is unchanged, the dressing isn't.

- **Geometry:** low‑poly meshes with **smooth vertex normals** (`computeVertexNormals`, `flatShading: false`) → rounded, readable, *not* voxel/blocky. Trees = tapered trunks + stacked smooth conifer cones; terrain = noise‑displaced plane with smoothed normals.
- **Materials:** `MeshStandardMaterial`, low‑saturation palette, subtle emissive on lights/eyes.
- **Atmosphere:** `FogExp2` distance fog tuned per phase; `ACESFilmicToneMapping`; post‑processing pass for **bloom** (flashlights), **vignette**, and light **film grain**.
- **Sky:** gradient skydome / hemisphere light driven by `timeOfDay` from dusk → night → dawn.
  > **Unity build (2026‑07‑20):** a procedural skybox shader (`Shaders/NightSky.shader`) —
  > horizon‑to‑zenith gradient (brightest low, darkest overhead, which is the way a real night sky
  > runs), a seeded twinkling star field with a milky‑way band that fades out toward dawn, and an
  > actual **moon** with phase, limb darkening, maria and a halo. The directional "moon" light is
  > aimed down the same vector the disc is drawn at, so the shadows agree with where the moon is.
  > It has to be a skybox rather than geometry: `RenderSettings.fog` would erase a world‑space moon
  > at any believable distance. Before this the sky was a flat solid camera‑clear colour and there
  > was no moon at all — only a light named after one.
  >
  > **The moon wanes across the three nights, and is a difficulty dial.** Moonlight is the only thing
  > that lets searchers cross the forest without burning flashlight battery, so dimming it raises the
  > cost of moving — on top of the battery‑drain escalation already in the `ESCALATION` table.
  >
  > | Night | Phase | Track | Peak elevation | Light range |
  > |---|---|---|---|---|
  > | 1 | full | E → SSW | 68° | 0.22–0.41 |
  > | 2 | gibbous | ESE → SW | 60° | 0.19–0.34 |
  > | 3 | half | SE → WSW | 52° | 0.12–0.23 |
  >
  > **No night ever goes moonless** — the moon is always in the sky, it just rides lower and dimmer.
  > Escalation comes from phase, altitude and brightness rather than from the moon leaving, because
  > taking it away entirely stacked a blackout on top of the battery escalation.
  >
  > It always tracks **east → west** through the southern sky, for every player. Every night moves at
  > the same angular rate and only the *starting point* on the arc differs (`MoonNight.ArcStart`), so
  > a later night simply begins further along — no night's sky appears to run faster than another's,
  > and `ArcStart + MoonArcRate` is held below 1 so the arc never completes inside a night.
  >
  > ⚠️ **East is world −X, not +X.** `MapView` mirrors its x axis to match the sim's handedness, and
  > its compass labels put W at +X and E at −X; north is −Z. Assuming otherwise puts the moonrise in
  > the west. A bright full moon also washes out the fainter stars, so night 3 trades moonlight for a
  > visibly better sky.
- **Performance:** instanced trees/ferns, LODs, baked where possible, shadow only from key lights + the local flashlight.
- **Landmarks (navigation):** the base‑camp clearing (campfire + lit **RV**) anchors the searchers; **cave entrances** (rounded boulder horseshoes with a dark mouth and a faint inner glow) mark Yeti's lairs and fast‑travel nodes. Distinct silhouettes help players orient in the dark.
- **Fire‑lookout tower — *implemented (Unity)*:** searchers **climb the ladder** on its camp‑facing side (press jump alongside it; W/S up and down, jump to hop off) to reach the platform. Up top, hold the **binoculars** key to **glass the forest under night vision** — a zoomed, image‑intensified (green, brightened) view that reveals the treeline you can't otherwise see, so the tower is a real scouting position. Yeti scales the tower with its own climb; the ladder and binoculars are searcher‑only. Mechanically the tower was already a climbable collider, so a searcher stands on the platform via the shared sim's existing logic — only the ascent (the ladder) and the optics are new, both client‑side, no sim change.
- **Logging trails — *implemented (Unity)*:** four seed‑derived trails meander out of the camp clearing. They are **real terrain, not decoration**: no trees grow in the corridor, so a trail is a genuinely open lane. Taking one is a **speed‑for‑exposure trade** — fast going and easy navigation, bought with long sightlines that make you simple to spot and simple to film.
- **Undergrowth:** ferns, bushes and mossy rocks fill the forest floor. Deliberately **render‑only and knee‑to‑waist height** — clutter never blocks a searcher, and it is too low to hide a standing player that the line‑of‑sight check believes is visible. Anything tall enough to break that promise has to be a real collider in the shared sim instead.

---

## 9. Networking architecture (Colyseus)

- **Room:** `MountainRoom` (capacity 6). Holds `GameState` (see `server/src/rooms/schema/`):
  - `players: Map<sessionId, Player>` — `{ role, name, x, y, z, ry, flashlightOn, battery, stamina, status, filming, filmProgress }` (`y` is feet height)
  - `clues: Clue[]` — `{ id, ctype, x, z, ry }` — the footprint/branch trail Yeti leaves
  - `phase`, `timeOfDay`, `videosRequired`, `videosCaptured`, `winner`
- **Authority:** Server owns match phase/time, the clue trail, filming tallies, catching, and win/loss. v1 movement is client‑sent + server‑clamped; clients send `recording`/`inView` intent and the server confirms range before crediting footage. **Upgrade path:** server‑authoritative movement with reconciliation.
- **Tick:** server simulation/broadcast at ~15–20 Hz; clients **interpolate** remote players between snapshots.
- **Role assignment:** first joiner can volunteer for Yeti; otherwise random among connected players at match start.
- **Lobby → match → results** room states.

---

## 10. UI / HUD

- **Minimal in‑world HUD:** flashlight battery, stamina, footage captured/required, current phase clock, contextual prompt, filming viewfinder + clip bar.
- **Map (`M`) — *implemented*:** top‑down overlay for both roles showing the player's position + heading, base camp, and caves. Hunters also see teammates, **stakeout pings**, and the **recent clue trail — but only while in contact** (Yeti heard nearby, or recent footprints in sight). For Yeti in a cave mouth, caves become **clickable fast‑travel destinations** (with a fade‑to‑black transition). Opening the map frees the cursor and pauses local movement.
  > **Unity build divergence (2026‑07‑20): caves start hidden from searchers.** The map used to hand
  > the team all five lairs at spawn, which deleted the exploration half of the game — you could stake
  > out Yeti's front doors on night 1 without having seen the forest. A mouth now appears only once
  > a searcher physically walks within ~22 m of it. Discovery is **per cave** (finding one says nothing
  > about the others) and **team‑wide** (all five searchers get it, so scouting is worth calling out),
  > and it **resets at the start of each match**. Yeti always sees its own network. The map footer
  > counts them off — `caves found 2/5` — so a blank map reads as "not found yet" rather than "this
  > map doesn't show caves".
- **Stakeout pings (`Q` / map click) — *implemented*:** hunters drop a shared marker (one active per hunter; ~35s lifetime) to coordinate. Pings show on every hunter's map and as an in‑world beacon; they're hidden from Yeti.
- **Yeti HUD:** *(planned)* ability cooldowns, senses toggle, searchers‑caught counter.
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
