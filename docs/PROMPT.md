# The Improved Prompt

This is your original idea, rewritten into a precise, buildable specification. You can
hand this to a developer, a team, or an AI agent and get something coherent back.

---

## Original prompt (yours)

> I want to create a multiplayer game. The basic gameplay should be one player vs 5,
> where the individual player plays as Bigfoot and the other 5 players are searching a
> forest in the Pacific Northwest USA. Don't make anything super blocky — I want poly‑like
> structures and smooth surfaces. The game should exist at dusk and then players search
> throughout the night using flashlights. Include a short story about the 5 players
> searching for evidence of Bigfoot. Controls should be easy to understand.

---

## Improved prompt (the spec)

**Build *Hollow Pines*, a browser‑based asymmetric multiplayer horror‑survival game.**

### High concept
A 1‑vs‑5 online match. Five players are an amateur cryptid‑research expedition combing a
Pacific Northwest forest for proof that Bigfoot is real. The sixth player **is** Bigfoot,
hunting them through the dark. One match spans a single night: it begins at **dusk** and
ends at **dawn**. The searchers' only reliable light is their **flashlights**.

### Pillars (every decision serves these)
1. **Asymmetric tension** — five fragile, cooperative humans vs. one powerful, lonely predator. Neither side should feel hopeless.
2. **The dark is the real enemy** — light is safety *and* a beacon. Managing your flashlight is the core risk/reward.
3. **Stylized, not realistic** — smooth low‑poly forms, soft gradient skies, painterly fog. Mood over fidelity. **Never blocky/voxel.**
4. **Pick‑up‑and‑play** — a new player understands the controls in under a minute.

### Players & roles
- **Searchers (5):** first‑person. Fragile. Win by **collecting evidence** and **transmitting it from base camp** (or surviving to dawn). Cooperative — can revive downed teammates.
- **Bigfoot (1):** first‑person. Fast, strong, can climb and leap, senses light/sound/scent. Wins by **stopping the searchers** — destroying evidence and incapacitating the team before dawn.

### Setting & atmosphere
- **Location:** the fictional **Hollow Pines National Forest**, Pacific Northwest USA — old‑growth conifers, fern gullies, a creek, a ranger trailhead / base camp.
- **Time:** continuous **dusk → night → witching hour → dawn**. The sky and fog shift through the match; darkness deepens and Bigfoot grows bolder over time.
- **Tone:** eerie, hushed, occasionally heart‑pounding. PG‑13 dread, not gore — searchers are *dragged off / incapacitated*, not killed on screen.

### Visual direction
- **Low‑poly geometry with smooth (vertex‑averaged) normals** — rounded, readable silhouettes; no hard cubes.
- **Lighting:** a single warm dusk key light fading to cold moonlight; per‑player **volumetric flashlight cones**; emissive accents (lanterns, eyes in the dark).
- **Atmosphere:** exponential distance fog, gentle bloom on lights, vignette + subtle film grain.
- **Palette:** dusk ambers/violets → deep blues/blacks → pale dawn teal.

### Controls (must be easy)
**Searcher:** `WASD` move • mouse look • `Shift` sprint (stamina) • `Space` jump/vault • `F` toggle flashlight • `E` interact (collect / revive / transmit) • `Q` ping/shout • `Tab` objectives.
**Bigfoot:** `WASD` move • mouse look • `Shift` charge • `Space` leap/climb • `LMB` swipe/grab • `RMB` roar (area scare) • `E` smell trail / destroy evidence.

### Core loop (one match)
1. **Dusk briefing** at base camp — searchers grab a flashlight + their tool; Bigfoot picks a starting den.
2. **Search** — find evidence nodes (footprint cast, fur tuft, photo, audio, nest/bones) scattered in the forest.
3. **Pressure** — flashlights drain and attract Bigfoot; the night darkens in timed phases.
4. **Transmit / escape** — return evidence to base camp's radio and broadcast it, or survive to dawn.
5. **Resolution** — searchers win on enough evidence transmitted or surviving to dawn; Bigfoot wins by incapacitating the team or destroying the evidence first.

### Networking
- Authoritative **Colyseus** room ("ForestRoom") holding shared state: players, roles, positions, flashlight/battery, evidence, time‑of‑day, match phase.
- ~15–20 Hz state sync with client‑side interpolation; server validates movement and actions.
- Lobby for 6 players; auto‑assign or volunteer for the Bigfoot slot.

### Deliverables
A runnable web client (Three.js) + Colyseus server, a short in‑fiction story (see `STORY.md`),
and a phased plan (see `ROADMAP.md`). Stylized low‑poly art, easy controls, dusk‑to‑dawn match.

### Out of scope (v1)
Realistic graphics, gore, voice chat, ranked matchmaking, mobile controls, persistence/accounts.

---

### Why these choices
- **Asymmetric 1v5** is a proven, replayable format (think *Dead by Daylight* / *Hunt: Showdown*) and reads instantly: "be the monster, or survive the monster."
- **Flashlight‑as‑core‑mechanic** turns your "search with flashlights at night" line into the actual risk/reward verb of the game, not just set dressing.
- **Dusk‑to‑dawn as the match timer** gives a natural, atmospheric clock instead of an abstract countdown bar.
- **Browser + Three.js + Colyseus** means anyone can join from a link — the lowest‑friction path to "multiplayer with friends."
