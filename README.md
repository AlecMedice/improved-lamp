# Hollow Pines

> An asymmetric 1‑vs‑5 multiplayer hunt set in a dusk‑to‑dawn Pacific Northwest forest.
> Five investigators search for evidence of Bigfoot. One player **is** Bigfoot.

**Working title:** *Hollow Pines* &nbsp;•&nbsp; **Repo:** `improved-lamp` (the flashlight is your lifeline — an *improved lamp*, if you will).

Stylized **low‑poly, smooth‑shaded** visuals. Volumetric flashlight cones, drifting fog,
and a sky that bleeds from dusk into a black, witching‑hour night.

---

## What's in this repo

```
improved-lamp/
├── README.md            ← you are here
├── docs/
│   ├── PROMPT.md        ← the rewritten / improved game prompt (a real spec)
│   ├── GAME_DESIGN.md   ← full game design document (GDD)
│   ├── STORY.md         ← the short story + the five searchers
│   └── ROADMAP.md       ← phased development plan & milestones
├── client/              ← Three.js + Vite + TypeScript (the game you run in a browser)
└── server/              ← Colyseus + TypeScript (authoritative multiplayer room)
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
Click the canvas to lock the mouse. `WASD` move, mouse look, `F` flashlight, **hold right‑mouse to film Bigfoot**, `M` map, `Q` ping.

**The hunt:** Searchers start at a campfire-and-RV base camp; **Bigfoot starts in a cave** out in the forest. Bigfoot leaves a trail of **footprints and broken branches** that fades over time — follow it. Searchers win by capturing **3 solid videos** of Bigfoot (light it up, hold it in frame for ~3s); Bigfoot wins by **catching** the whole team first. Press **`M`** for a top-down **map** (your position, base camp, caves; hunters also see teammates, the clue trail, and stakeout pings). Hunters press **`Q`** (or click the map) to drop a shared **stakeout ping** — visible to the whole team on the map and as an in-world beacon — to coordinate. Bigfoot stands in a **cave mouth, opens the map, and clicks a destination cave** to fast-travel there (with a fade transition) and flank the team. Open one tab as Bigfoot and another as a searcher to see it in action.

**2. Server (optional, for multiplayer)**
```bash
cd server
npm install
npm run dev        # Colyseus on ws://localhost:2567
```
With the server running, open multiple browser tabs — each becomes a player in the same forest.

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
