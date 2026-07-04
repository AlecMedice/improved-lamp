# Hollow Pines — local multi‑tab test plan

How to exercise the whole 1‑vs‑5 game **on one machine**, using several browser tabs/windows as the
six players. No second computer needed. This is a manual smoke/QA pass, not an automated suite — for
scripted server assertions see the headless‑smoke pattern in `CLAUDE.md`.

---

## 1. One‑time setup

```bash
cd server && npm install
cd client && npm install
```

## 2. Start the two processes (two terminals, leave both running)

```bash
# Terminal A — authoritative server (ws://localhost:2567)
cd server
NIGHT_SECONDS=60 npm run dev        # 60s nights make a full 3‑night match ~3 min; omit for real 600s

# Terminal B — game client (http://localhost:5173)
cd client && npm run dev
```

Open **http://localhost:5173** (the Vite URL). `ws://localhost:2567` is the backend socket, **not** a web
page — don't open it in a browser. Confirm the server is healthy with `curl http://localhost:2567/health`.

> The client also runs standalone if the server is down (offline solo) — useful for world/art/UI checks,
> but clues, pings, nights, roar/grab, revive, dazzle and remote players only exist with the server up.

## 3. Stand up six players in tabs

Each browser tab = one player. Roles are assigned by the server when the host starts, but you can force a
tab's role with the **`?devRole=`** query param so you always get a known 1‑Bigfoot / 5‑searcher split:

| Tab | URL | Becomes |
|-----|-----|---------|
| 1 (host) | `http://localhost:5173/?devRole=searcher` | Searcher · **host** (first to join) |
| 2 | `http://localhost:5173/?devRole=bigfoot` | Bigfoot |
| 3–6 | `http://localhost:5173/?devRole=searcher` | Searchers |

Add **`&perf`** to any tab for the fps / draw‑call / tree / dpr overlay (e.g. `/?devRole=bigfoot&perf`).

In **every** tab: type a name, click **“Multiplayer lobby”**. They gather in the host's lobby. In tab 1
(the 👑 host) press **Start** — the server assigns roles (honouring the `devRole` overrides) and night 1
begins. You don't need all six to test; a Bigfoot + 1–2 searchers covers most flows. The host can also
Start solo to walk the world.

### Multi‑tab realities (important)
- **Pointer lock is single‑tab.** Only the focused tab captures the mouse; click into a tab to drive it,
  then Alt‑Tab / click another to act as that player. You play one avatar at a time and let the rest
  stand — fine for verifying interactions (roar freezes the idle searcher, grab drags them, etc.).
- **Two side‑by‑side windows beat tabs** for cause‑and‑effect checks (drive Bigfoot in the left window,
  watch a searcher get frozen in the right).
- **localStorage is shared across same‑origin tabs** (settings + keybinds). To test a clean first‑run, or
  different keybinds per player, use a **separate browser profile or an incognito window** per tab.
- If a tab shows “server offline”, the server terminal isn't running or `curl …/health` fails.

---

## 4. Controls reference (default binds — all rebindable in Settings)

| | Searcher | Bigfoot |
|--|----------|---------|
| Move / look | WASD + mouse | WASD + mouse |
| Sprint | **L‑Shift** | **L‑Shift = charge** (burst dash, cooldown) |
| Crouch | **L‑Ctrl** | — |
| Jump / context | **Space** (jump · **vault** a log) | **Space** (**leap** · **climb** a structure) |
| Flashlight | **F** | — |
| Film / Roar | **Hold Right‑Mouse** = film | **Right‑Mouse** = roar |
| Grab | — | **Left‑Mouse** = grab / drop |
| Revive | **Hold E** near a downed ally | — |
| Senses overlay | — | **V** |
| Map | **M** (Bigfoot: click a cave to fast‑travel) | **M** |
| Ping | **Q** (or click the map) | — |
| Pause / settings | **Esc** or the gear | same |

---

## 5. Feature checklists

Tick these off; each line is one observable behaviour.

### Movement & world (either role, offline‑OK)
- [ ] WASD moves, mouse looks, sprint is faster, crouch lowers + slows you.
- [ ] You can't walk through tree trunks, the RV, the tower, or cave boulders (pushed out, no phasing).
- [ ] Terrain height is followed (you walk up/down slopes, never sink or float).
- [ ] Fallen logs **slow searchers**; **Space vaults** a log (stamina cost) and negates the slow.
- [ ] Wading into the lake slows you; the creek is audible as you approach.

### Networking (server up, ≥2 tabs)
- [ ] A second player appears as an avatar and moves smoothly (interpolated, no teleport jitter).
- [ ] Avatars animate — legs/arms swing while walking, settle to an idle when standing.
- [ ] Bigfoot reads as a bulkier, hunched creature with glowing eye‑shine; searchers read as people.
- [ ] Kill the server mid‑match → the other player freezes; restart within ~20 s → they reconnect.

### Bigfoot offense (Bigfoot tab vs an idle searcher tab)
- [ ] **Right‑Mouse roar** freezes searchers within ~25 m (cyan status icon); roar audio carries farther.
- [ ] **Left‑Mouse grab** on a frozen searcher incapacitates them, drags them, and **zeroes the team's
      captured footage**; they recover after ~60 s, slowed for a bit.
- [ ] **Space leap** clears a log/rise; **L‑Shift charge** gives a short forward burst (then cooldown).
- [ ] **Space vs the tower / RV / cave boulders** climbs the side and stands on top; step off to drop.
- [ ] **V** toggles the senses overlay — searchers + your recent scent trail glow through the trees.

### Searcher counterplay
- [ ] **Hold E** next to a downed teammate fills a revive bar (~4 s) and restores them before incap ends.
- [ ] Keeping a **flashlight trained on Bigfoot** (~1.2 s, range + cone + line‑of‑sight) **dazzles** it —
      its roar/grab lock briefly; breaking LOS (a tree between you) never dazzles.

### Objectives & loop
- [ ] **Hold Right‑Mouse** with Bigfoot centered/in range/in LOS fills the film bar; 3 solid videos → hunters win.
- [ ] Footage is **pooled** across the team and across nights (and erased by a grab).
- [ ] Nights advance 8pm→8am with a fade between; escalation makes Bigfoot faster / drains quicker each night.
- [ ] Bigfoot surviving all 3 nights → Bigfoot wins; either outcome shows the results screen; host can rematch.

### Map, caves, pings
- [ ] **M** shows compass, grid, camp/tower/lake landmarks, your heading triangle, and the caves.
- [ ] Hunters also see teammates, pings, and the recent clue trail **only while in contact** (Bigfoot heard
      nearby or fresh evidence in sight); Bigfoot's legend hides Team/Ping/Tracks.
- [ ] **Q** (or map‑click) drops a stakeout ping visible to the whole hunter team.
- [ ] Bigfoot standing in a cave mouth → open **M**, click another cave → fade + emerge there (then cooldown).

### UI / polish (Phase 5)
- [ ] Night‑1 start shows the role‑tailored **dusk briefing**; any key begins.
- [ ] **Esc / gear** opens Settings; brightness (gamma) visibly brightens the scene, master volume + mouse
      sensitivity apply live and persist across reloads.
- [ ] Rebinding a control in Settings takes effect and survives a reload; **Reset** restores defaults.

### Performance (`&perf`)
- [ ] Overlay shows fps, draws, tris, tree count, dpr. Walking changes the **tree** count (LOD/cull) with no
      visible pop at the fog line. `window.__perf()` returns the same numbers from the console.

---

## 6. Handy dev shortcuts
- `NIGHT_SECONDS=20 npm run dev` (server) — very short nights to reach night‑2/3 escalation + win/loss fast.
- `?devRole=bigfoot|searcher` — deterministic role per tab. First `devRole=bigfoot` request wins.
- `?perf` — perf overlay + `window.__perf()`.
- `window.__previewAvatars()` (any `?perf`/tab, in the console) — drops a hunter + Bigfoot in front of the
  camera for a close‑up art/proportion check without staging a whole match.
- Separate browser profiles / incognito windows — isolate localStorage (clean first‑run, per‑player binds).
