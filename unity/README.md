# Hollow Pines — Unity R1 slice

The Unity/Steam migration (traps + remaining work: [`docs/UNITY_PORT_NOTES.md`](../docs/UNITY_PORT_NOTES.md)):
prove a Unity build can run host-authoritative multiplayer. Two paths:

- **Path A — Local (do this first):** Unity + FishNet only. Host + join a **moving cube** on one
  machine (or a LAN), build to a `.exe` you launch from your desktop. Proves the whole pipeline with
  the least setup.
- **Path B — Steam relay (later):** add Steamworks.NET + FishySteamworks for the R.E.P.O.-style
  Steam Datagram Relay topology. The Steam scripts are already here but **dormant** behind a
  `HP_STEAM` compile flag, so Path A builds cleanly without them.

> These scripts compile **inside a configured Unity project**, not in this repo.

## What's here

```
unity/
  Assets/HollowPines/Scripts/
    Net/LocalNetworkHud.cs   # Path A: no-Steam Host/Client buttons (Tugboat/UDP)
    Player/PlayerCube.cs     # host-authoritative "cube that moves"
    Player/CubeSpawner.cs    # spawns one cube per connected client
    Net/SteamLobby.cs        # Path B: Steam lobby + relay wiring   (dormant: #if HP_STEAM)
    Net/NetworkHud.cs        # Path B: Steam host/invite HUD          (dormant: #if HP_STEAM)
    Editor/R1Setup.cs        # one-click scene wiring + Windows build (menu: "Hollow Pines")
  steam_appid.txt            # Path B only: "480" (Valve's Spacewar test app)
```

---

# Path A — local build (start here)

## Install (two things)

1. **Unity 6 LTS** via Unity Hub, with the **Windows Build Support** module. Create a new **3D (URP or
   Built-in)** project.
2. **FishNet** — free. Asset Store ("Fish-Networking") or a release `.unitypackage` from
   [github.com/FirstGearGames/FishNet](https://github.com/FirstGearGames/FishNet). Import it.

Then copy this repo's `unity/Assets/HollowPines` folder into your project's `Assets/`. (You do **not**
need `steam_appid.txt` or any Steam package for Path A — the Steam scripts stay dormant.)

## Scene wiring (~10 min, once — or one click)

**Automated:** with this folder copied in, use the menu **Hollow Pines → Set Up R1 Scene (Path A)** —
it does every step below, plus two Unity‑6‑template fixes: sets **Active Input Handling to “Both”**
(the URP template defaults to new‑Input‑System‑only, which kills legacy `Input` and standalone IMGUI;
restart the editor once after) and unticks NetworkTransform’s default **Client Authoritative** (which
would stomp the ServerRpc host movement). **Hollow Pines → Build Windows (Path A)** then produces
`Build/Windows/HollowPines.exe`. Manual steps, if you prefer:

1. **NetworkManager object.** Create an empty GameObject named `NetworkManager`. Add FishNet's
   **`NetworkManager`** component, then add a **`Tugboat`** component to the same object (that's
   FishNet's default UDP transport; the NetworkManager uses it automatically).
2. **HUD.** On the same object add **`LocalNetworkHud`**.
3. **Cube prefab.** `GameObject → 3D Object → Cube`. Add **`NetworkObject`**, **`NetworkTransform`**
   (this replicates position — **untick "Client Authoritative"**, the server drives movement), and **`PlayerCube`**. Drag it into a `Prefabs/` folder in `Assets/` to
   make a prefab, then delete the one in the scene.
4. **Register the prefab** with FishNet's **Default Prefab Objects** (FishNet usually auto-adds any
   prefab that has a `NetworkObject`; if the cube doesn't spawn later, find the `DefaultPrefabObjects`
   asset and click **Refresh/Populate**).
5. **Spawner.** On a scene object add **`CubeSpawner`** and drag the cube **prefab** into its
   `Player Prefab` field.
6. Add a **ground plane** (`GameObject → 3D Object → Plane`) so cubes have something to stand on.
   **Save the scene** (e.g. `Assets/Scenes/Main.unity`).

## Test in the editor

Press **Play**, click **Host (server + client)** — a cube spawns. Drive it with **WASD**. To see two
players, use **ParrelSync** ([github](https://github.com/VeriorPus/ParrelSync)) to open a second clone
and click **Client -> 127.0.0.1**, or build the `.exe` (below) and run it as one peer while the editor
is the other.

## Build the desktop `.exe`

1. `File → Build Settings`. Add your saved scene with **Add Open Scenes**.
2. Platform **Windows**, then **Build** and pick an output folder (e.g. your Desktop).
3. Run the `.exe`. **Host** in one instance, **Client -> 127.0.0.1** in another (or the editor). Two
   cubes, moving on both — that's Path A done: a launchable Unity multiplayer build. 🎉

---

# Path B — Steam relay (when Path A works)

Adds the real R.E.P.O. topology (host-authoritative over Steam Datagram Relay, friends join via a
Steam lobby — no port-forwarding).

1. Install **Steamworks.NET** ([github](https://github.com/rlabrecque/Steamworks.NET), includes
   `SteamManager`) and **FishySteamworks** ([github](https://github.com/FirstGearGames/FishySteamworks)).
2. **Player Settings → Player → Scripting Define Symbols** → add **`HP_STEAM`**. This activates
   `SteamLobby.cs` and `NetworkHud.cs`.
3. Put **`steam_appid.txt`** (containing `480`) in the **project root** (next to `Assets/`) and beside
   the built `.exe`. Steam client must be running + logged in.
4. On the `NetworkManager`, add a **`FishySteamworks`** component and set it as the
   **TransportManager's Transport** (replacing Tugboat). Add **`SteamManager`** + **`SteamLobby`**, and
   swap `LocalNetworkHud` for **`NetworkHud`**.
5. **Host** on one machine; a friend joins from the Steam overlay (friends list → you → **Join Game**,
   or an invite). Real relay/NAT testing wants **two machines / two Steam accounts**.

## Troubleshooting

- **FishNet won't compile on Unity 6000.5+ (CS0619 `SceneHandle`/`GetInstanceID`, plus a red
  "Failed to find entry-points … Unity.FishNet.Codegen" error)** — FishNet ≤ 4.7.2 predates
  Unity 6.5's API removals; apply [`fishnet-patches/`](fishnet-patches/) (the codegen error is
  fallout and clears once FishNet.Runtime compiles).
- **Cube doesn't appear on the other side** — the prefab is missing **`NetworkTransform`**, or it isn't
  in FishNet's **Default Prefab Objects** (open that asset → Refresh).
- **Nothing happens on Host** — confirm a **transport** component (Tugboat for A, FishySteamworks for B)
  is on the `NetworkManager`.
- **Path B: "Steam not initialized"** — Steam client not running/logged in, `HP_STEAM` not defined, or
  `steam_appid.txt` missing beside the executable / at the project root.

---

# Game scene — the ported Hollow Pines slice (post-R1)

`Scripts/Game/` ports the actual game on top of the R1 stack: the deterministic world rendered
from the shared sim (terrain/forest/RV/caves/tower/logs/lake, day-night sky), first-person
movement through `HollowPines.Sim.Movement.StepPlayer` (sprint/stamina, jump, crouch, Bigfoot
leap/climb/charge, searcher vault, log/lake slows), and the full authoritative match loop ported
from `ForestRoom.ts` (night clock + escalation, clue trail, roar→freeze→grab→incap, revive,
flashlight dazzle, server-checked filming, win/loss) as FishNet SyncVars/RPCs.

Setup: the sync step also copies [`csharp/HollowPines.Sim`](../csharp) into `Assets/HollowPines/Sim`
(that folder stays canonical + parity-tested in the repo). Then **Hollow Pines → Set Up Game Scene
(Forest)** wires `Assets/Scenes/Forest.unity` + the `HPPlayer`/`ClueMarker` prefabs, and
**Hollow Pines → Build Windows (Game)** produces the `.exe`. In Play: **Host** → **START MATCH**.

Interim architecture notes (v1 of this scene):
- **Movement is owner-simulated** (client-auth NetworkTransform) — fine for friends-play; the
  host-authoritative Replicate/Reconcile version is still to be adopted (`docs/UNITY_PORT_NOTES.md` §0).
  Outcomes (status/filming/dazzle/footage) are already host-authoritative.
- Not yet ported: reconnection grace, rigged/animated models + the art pass (Phase 6).
  (Since ported: Wren's mark, Theo's roar-direction HUD, senses overlay, post-FX/night-vision,
  nametags, head-bob, REC bead, status screen tints, title menu (start/join/settings/quit) +
  persisted settings, dusk briefing, Esc pause overlay, status countdowns, Bigfoot fog trade-off,
  contextual vault/climb/revive prompts, key rebinding, **the map overlay** — contact-gated clue
  trail, teammates, pings, marks, Bigfoot cave fast-travel — **stakeout pings**, and **the
  procedural audio engine** — all 16 cues synthesized, wind/creek beds, proximity heartbeat,
  positional roar/branch-snaps/footsteps.)

## Next

Path B (Steam relay) turns this into the R.E.P.O. topology, then **N3** swaps the interim movement
for the ported sim under FishNet prediction. Full sequence in
[`docs/UNITY_PORT_NOTES.md`](../docs/UNITY_PORT_NOTES.md).
