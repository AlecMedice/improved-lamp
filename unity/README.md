# Hollow Pines — Unity R1 relay slice

The **first** step of the Unity/Steam migration (see [`docs/UNITY_MIGRATION.md`](../docs/UNITY_MIGRATION.md)).
Its only job is to **de-risk the netcode**: prove that a Unity build can run host-authoritative,
with a friend joining over **Steam Datagram Relay** — the R.E.P.O. topology — *before* any real game
logic is ported. If a cube moves on both screens over the relay, the hard/unknown part is solved.

> **These scripts do not compile in this repo.** They reference `FishNet`, `Steamworks`, and
> `FishySteamworks`, which only exist inside a configured Unity project. That's expected — this
> folder is drop-in source + setup, not a standalone build.

## What's here

```
unity/
  steam_appid.txt                              # "480" (Valve's Spacewar test app — relay works without your own appid)
  Assets/HollowPines/Scripts/
    Net/SteamLobby.cs                          # create/join Steam lobby → start FishNet host/client over the relay
    Net/NetworkHud.cs                          # throwaway on-screen Host / Invite / Disconnect buttons
    Player/PlayerCube.cs                       # host-authoritative "cube that moves" (ServerRpc input → server move)
    Player/CubeSpawner.cs                      # spawns one cube per connected client
```

## Stack (and one refinement from the plan)

| Piece | Package |
| --- | --- |
| Engine | **Unity 6 LTS** (or 2022.3 LTS) |
| Netcode | **FishNet** (Fish-Networking) |
| Steam transport | **FishySteamworks** |
| Steam API | **Steamworks.NET** |

Refinement vs. the first sketch: FishySteamworks is built on **Steamworks.NET**, so the scaffold uses
Steamworks.NET (which ships `SteamManager`) rather than Facepunch.Steamworks — one Steam binding, no
duplicate `SteamAPI.Init()`. If you later prefer Facepunch, you'd swap the transport too.

## One-time setup

1. **Install Unity 6 LTS** via Unity Hub. Add **Windows/Mac Standalone Build Support**. Create a new
   **3D (Built-in or URP)** project, then copy this `unity/Assets/HollowPines` folder into its `Assets/`
   and put `steam_appid.txt` in the **project root** (next to `Assets/`).
2. **Import the packages** (all free):
   - **FishNet** — Asset Store, or [github.com/FirstGearGames/FishNet](https://github.com/FirstGearGames/FishNet) (`.unitypackage`).
   - **Steamworks.NET** — [github.com/rlabrecque/Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) (import the release `.unitypackage`; it includes `SteamManager.cs`).
   - **FishySteamworks** — [github.com/FirstGearGames/FishySteamworks](https://github.com/FirstGearGames/FishySteamworks).
3. **Steam client** must be installed and **logged in** on every machine you test on (the relay routes
   through the running Steam client). App **480** lets you test without a Steamworks partner account.

## Scene wiring (~10 minutes, once)

1. **NetworkManager object.** Create an empty GameObject `NetworkManager`; add FishNet's
   **`NetworkManager`** component. Add a **`FishySteamworks`** component to the same object (or a child)
   and, on the NetworkManager's **TransportManager**, set **Transport = that FishySteamworks**.
2. **Steam + HUD.** On the same object, add **`SteamManager`** (from Steamworks.NET), **`SteamLobby`**,
   and **`NetworkHud`**.
3. **Cube prefab.** Create a Cube (`GameObject → 3D Object → Cube`). Add **`NetworkObject`**,
   **`NetworkTransform`** (this replicates position), and **`PlayerCube`**. Drag it into `Assets/` to
   make a prefab, then delete it from the scene.
4. **Register the prefab.** Add it to FishNet's **Spawnable Prefabs** list (the `DefaultPrefabObjects`
   asset — FishNet can auto-populate it).
5. **Spawner.** On a scene object add **`CubeSpawner`**; assign the cube **prefab** to its
   `Player Prefab` field.
6. Add a ground plane so the cubes have something to sit on. Save the scene.

## Build & test the relay (two machines is the real test)

**Fastest local test (one machine, two instances) — install [ParrelSync](https://github.com/VeriorPus/ParrelSync):**
1. `ParrelSync → Clones Manager → Create new clone`, then open the clone.
2. In the **original** editor press Play → **Host (Steam lobby)**. A cube spawns.
3. In the **clone** editor press Play → it should receive `GameLobbyJoinRequested` when you accept an
   invite; or click **Host** first is not needed — use the invite flow below. A second cube spawns.
4. Drive each cube with **WASD**. You should see both cubes move on both screens.

> Note: two clients under the *same* Steam account on one machine is awkward for the overlay invite.
> The clean test is **two machines / two Steam accounts** (or a friend): Host on A, then on B accept
> A's invite (or A's friends-list → **Join Game**). This is also the only way to actually exercise NAT
> traversal + relay.

**Shippable build:** `File → Build Settings → Build` → run the `.exe`. Keep `steam_appid.txt` beside it.

### Expected result (R1 done)

- Host sees its own cube; each joiner spawns another cube.
- All cubes move on all screens, driven by their owners, with position coming from the **host**.
- No port-forwarding, no IP entry — only Steam. That confirms the relay topology works.

## Troubleshooting

- **"Steam not initialized"** — Steam client not running/logged in, or `steam_appid.txt` (containing
  `480`) not beside the executable / at the project root during Play mode.
- **Joiner never connects** — confirm the **TransportManager's Transport is FishySteamworks** (not the
  default Tugboat/UDP), and that `SetClientAddress` is receiving the host's SteamID64 (log it in
  `SteamLobby.OnLobbyEntered`).
- **Cubes don't move on remote screens** — the prefab is missing a **`NetworkTransform`**, or it isn't
  in the **Spawnable Prefabs** list.

## Next

R1 proves the pipe. Next is **R2/R3** (freeze `shared/sim`, port it to C# with the vitest suite as the
parity oracle) and **R4** (swap this placeholder movement for the ported deterministic sim under
FishNet prediction). Full sequence in [`docs/UNITY_MIGRATION.md`](../docs/UNITY_MIGRATION.md).
