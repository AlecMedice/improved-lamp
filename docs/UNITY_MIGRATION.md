# Hollow Pines — Unity migration plan

The web build (Three.js client + Colyseus server, TypeScript) proved out the design. The target now
is a **downloadable Steam game where one player hosts and friends join through a relay** — the R.E.P.O.
/ Lethal Company shape: the host is authoritative, Steam Datagram Relay does NAT punch-through, and
there's no dedicated server anyone runs. This doc is the plan and the running record.

Legend: ✅ done · 🟡 in progress · ⬜ not started

---

## Why migrate (and why now)

1. **The distribution model doesn't fit the current netcode.** Colyseus is a *dedicated-server*
   framework; "players host their own" there means installing Node, running a process, and
   port-forwarding — the exact friction the relay model removes. A browser tab also can't be a
   Steam-relay host. Reaching the R.E.P.O. topology means replacing the networking layer **regardless**
   of engine, so do it where the relay is free and native: Unity + Steamworks.
2. **We're near the natural cut line.** The remaining web-roadmap work is art/animation + deploy, and
   Unity *is* that pipeline — building it in Three.js would be throwaway.

## Sequencing: de-risk the netcode first, port the sim last

The original sketch put the C# sim port first. **Reordered, and here's the reason:** `main` moved ~17
commits in a single day, including changes to `shared/sim` itself (specialties, cave helpers, a new
`staminaMax` modifier). A sim port written against that went stale within 24 hours. Lesson: **the game
logic is still a moving target — porting it first is building on sand.**

So the first step is the part that is (a) the genuine unknown and (b) completely independent of the
churning game logic: **the relay netcode**. The sim port is low-risk mechanical translation, fully
verifiable by tests, and should happen once against a *frozen* target.

| Phase | What | Status |
| --- | --- | --- |
| **R1 — Netcode vertical slice** | Unity + FishNet + FishySteamworks: a **cube that moves**, host + a friend joining over Steam Datagram Relay. No real game logic. | 🟡 scaffolded → [`unity/`](../unity) |
| **R2 — Freeze the sim surface** | In TS: finish wiring the specialty system (currently an "enabling layer," not live), then declare `shared/sim` + the rules frozen. | ⬜ |
| **R3 — Port `shared/sim` → C#** | Translate the sim incl. `specialties.ts` + the new cave/stamina helpers. **Parity-gated by mirroring `server/test/*.ts`** (the maintained truth) + a golden cross-check. | ✅ → [`csharp/`](../csharp), parity PASS |
| **R4 — Host authority** | Drop the ported sim into R1; reimplement `ForestRoom`'s systems (night clock, roar/grab/incap, escalation, specialties deal, anti-cheat) as a FishNet host loop; movement under FishNet prediction. | ⬜ |
| **R5 — Presentation** | Rebuild renderer/audio/HUD/map natively; rigged/animated models (absorbs old Phase 6). | ⬜ |
| **R6 — Ship** | Steamworks app, depots, build upload, store page. | ⬜ |

**The one strategic call that's yours:** R2's freeze. Either (a) pause TS feature work and freeze now,
or (b) keep iterating in TS and only freeze/port when the design is truly done ("port-last"). For a
small team, **port-last** is the lower-tax path — keep iterating where it's cheap, don't maintain two
implementations. **R1 needs no freeze**, so it can start immediately either way.

## Locked stack

| Layer | Choice | Notes |
| --- | --- | --- |
| Engine | **Unity 6 LTS** (or 2022.3 LTS) | — |
| Netcode | **FishNet** | Host-authoritative listen-server; free; modern prediction (Replicate/Reconcile) |
| Steam transport | **FishySteamworks** | Wraps Steam Datagram Relay → free NAT traversal + relay fallback |
| Steam API | **Steamworks.NET** | FishySteamworks is built on it — one Steam binding, no duplicate init. (Ships `SteamManager`.) |
| Lobby / invites | **Steam Lobbies** | Friends "Join Game" + invite overlay, no IP sharing |
| Distribution | **Steam** | Steamworks partner account ($100 one-time per app) at ship time; test with appid **480** (Spacewar) until then |

## Topology: what changes from the web build

The web build split authority (Colyseus server) from a thin predicting client. The Steam build keeps
the split but **collapses the server into the host client**:

- **Host player = the authority.** `ForestRoom.update()`'s 20 Hz loop becomes a system that runs on the
  host only, driven by FishNet server callbacks. No separate Node process.
- **Clients predict + reconcile** as today, streaming inputs and easing toward host corrections — which
  is exactly why the shared deterministic sim matters.
- **`shared/sim` is the piece that survives.** Everything else is rebuilt or dropped:

| Piece | Fate |
| --- | --- |
| `shared/sim/` (movement, collision, terrain, world gen, RNG, specialties) | **Ported to C#** (R3) — runs on host + clients |
| `ForestRoom` authority (clock, roar/grab/incap, footage, escalation, specialties deal, `antiCheat.ts`) | **Reimplemented as a FishNet host loop** (R4) |
| `GameState` schema (`Player`/`Clue`/`Ping`) | **Reimplemented as FishNet SyncTypes / networked objects** (R4) |
| Colyseus (WebSocket dedicated server) | **Dropped** — replaced by the FishySteamworks relay |
| Three.js client (`Environment`, `RemotePlayer`, `AudioEngine`, `HUD`, `MapView`, post-FX) | **Rebuilt in Unity** (R5) |
| GDD rules + shared tunables (`shared/sim/constants.ts`) | **Reused as spec** — mirrored into C# constants |

## Determinism notes (from the R3 port)

These are the invariants the shipped C# port holds (verified by `csharp/Parity`):

- **Doubles, not floats.** The TS sim is IEEE-754 `double` throughout; the port must use `double` or it
  diverges on the first collision push-out. Convert to `float` only at the Unity transform boundary.
- **Bit-exact RNG.** `mulberry32` needs `uint` arithmetic (wraps mod 2^32, matching JS `| 0` / `>>>` /
  `Math.imul`). The value-noise gradient table must be `float[]` — the TS side stores it in a
  `Float32Array`, and that single-precision truncation is observable.
- **JS `Math.round`, not banker's rounding.** Cave coordinates round half toward +infinity.
- **`sin`/`cos`/`sqrt`/`hypot` can differ in the last ULP** between JS and .NET (and across CPUs). Not a
  problem: the **host is authoritative**, so tiny client drift is corrected by the same reconciliation
  the web build already uses. Strict lockstep (not needed for this design) would require fixed-point.

## How to compile & test

**R1 — Unity relay slice** (on your machine — needs the Unity editor): full setup + scene wiring +
host/join test in [`unity/README.md`](../unity/README.md). Summary: install Unity 6 LTS + FishNet +
Steamworks.NET + FishySteamworks, wire the NetworkManager/transport/cube, keep `steam_appid.txt` (480)
by the build, run Steam logged in, then Host on one instance and join from another (ParrelSync for one
machine; two machines/accounts for a real relay test).

**R3 — C# sim library** (headless, no Unity needed — done, verified):
```bash
dotnet run --project csharp/Parity           # golden cross-check + mirrored vitest invariants → "PARITY OK"
```
(In a fresh container: `apt-get update && apt-get install -y dotnet-sdk-8.0` first.)

**Cross-check against the TS source of truth (already in the repo):**
```bash
cd server && npm install && npm test         # vitest determinism/movement/caves suite the C# port must match
```

## Open decisions

- **Freeze strategy (R2):** freeze-now vs. port-last — see above; recommend port-last for a small team.
- **Host migration:** R.E.P.O. drops the session if the host leaves. Recommend matching that for v1
  (host migration is a large amount of extra complexity).
- **Facepunch.Steamworks vs. Steamworks.NET:** using Steamworks.NET to pair cleanly with
  FishySteamworks; revisit only if a needed Steam API is missing.
