# Metoh

> An asymmetric 1‑vs‑5 multiplayer hunt set in a dusk‑to‑dawn Himalayan valley.
> Five investigators search for evidence of the Yeti. One player **is** the Yeti.
>
> Off the packed trails the snow lies deep: it slows the searchers in the low ground, never the
> Yeti — and it keeps a record of everyone who crosses it that only the Yeti can read.

**Working title:** *Metoh* &nbsp;•&nbsp; **Repo:** `improved-lamp` (the flashlight is your lifeline — an *improved lamp*, if you will).

Stylized **low‑poly, smooth‑shaded** visuals. Volumetric flashlight cones, drifting fog,
and a sky that bleeds from dusk into a black, witching‑hour night.

---

## What's in this repo

```
improved-lamp/
├── README.md                ← you are here
├── CLAUDE.md                ← fast orientation + conventions (start here to work on it)
├── docs/
│   ├── GAME_DESIGN.md       ← full game design document (GDD), source of truth for rules
│   ├── STORY.md             ← the short story + the five searchers
│   ├── CHARACTER_FUNC_DEV.md ← specialties, the evidence/casting system, the duffel
│   ├── ROADMAP.md           ← phased development plan & milestones
│   ├── UNITY_PORT_NOTES.md  ← Unity traps and conventions (read before touching unity/)
│   ├── Metoh_migration.md   ← the Hollow Pines → Metoh re-theme plan and its rationale
│   └── July19Work.md        ← the Unity port's build log (historical record, not current)
├── client/              ← Three.js + Vite + TypeScript (the game you run in a browser)
├── server/              ← Colyseus + TypeScript (authoritative multiplayer room)
├── shared/sim/          ← the deterministic world + movement sim, imported by both
├── csharp/              ← Metoh.Sim (the C# port of shared/sim) + its parity harness
└── unity/               ← the Unity + FishNet desktop build (where new gameplay lands)
```

## Quick start

The client runs **standalone** (single‑player walk‑around) even without the server,
so you can see the world immediately. Add the server for multiplayer.

**1. Client**
```bash
cd client
npm install
npm run dev        # open the printed http://localhost:5173
```
Click the canvas to lock the mouse. `WASD` move, mouse look, `F` flashlight, **hold right‑mouse to film Yeti**, `M` map, `Q` ping.

**The hunt (3 nights, 8pm→8am each):** Searchers start at an expedition basecamp; **the Yeti starts in a crevasse**. It leaves a fading trail of **footprints and broken crust** — follow it. Off the packed trails the snow keeps a record of you too: in the low ground it **slows you and not the Yeti**, and anywhere off-trail your boots leave **prints only the Yeti can see**. The trails are fast and leave nothing — at the cost of sightlines that make you easy to film. **Searchers win** by capturing **3 solid videos** of Yeti (light it up, hold it in frame ~3s; footage is pooled across the team and across nights). **Yeti wins** by **surviving all 3 nights**. Yeti fights back: **right-click to ROAR** (freezes nearby searchers ~30s), then **left-click to GRAB** a frozen searcher — dragging them and **erasing the team's footage** (they recover after a minute, briefly slowed). Press **`M`** for a top-down **map** (position, camp, caves; hunters also see teammates, stakeout pings, and the recent trail when in contact). Hunters press **`Q`** (or click the map) to drop a shared **stakeout ping**. Yeti opens the **map in a cave mouth and clicks a destination cave** to fast-travel. Open one tab as Yeti and another as a searcher to see it in action.

**2. Server (optional, for multiplayer)**
```bash
cd server
npm install
npm run dev        # Colyseus on ws://localhost:2567
```
With the server running, open multiple browser tabs — each becomes a player in the same match.

> Status: this is a **scaffold / vertical-slice starting point**, not a finished game.
> See [`docs/ROADMAP.md`](docs/ROADMAP.md) for what's built and what's next.

## Tech stack

| Layer        | Choice                                | Why |
|--------------|---------------------------------------|-----|
| Rendering    | [Three.js](https://threejs.org)       | Mature WebGL, great for stylized low‑poly + custom lighting/fog |
| Build/dev    | [Vite](https://vitejs.dev) + TypeScript | Instant HMR, typed gameplay code |
| Networking   | [Colyseus](https://colyseus.io)       | Authoritative rooms + state sync built for small‑session multiplayer (perfect for 1v5) |
| Language     | TypeScript everywhere                 | One language, shared types client↔server |

See [`docs/GAME_DESIGN.md`](docs/GAME_DESIGN.md) for the full design.
