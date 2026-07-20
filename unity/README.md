# Hollow Pines — Unity build

The Unity/FishNet port of the game. Traps, conventions and remaining work live in
[`docs/UNITY_PORT_NOTES.md`](../docs/UNITY_PORT_NOTES.md) — **read that before changing anything here.**

> These scripts compile **inside a configured Unity project**, not in this repo. See "Workflow" below.

## What's here

```
unity/
  Assets/HollowPines/Scripts/
    Game/          # the game: world, player, host match loop, HUD, map, audio, post-FX
    Editor/        # GameSceneSetup.cs — one-click scene + prefab wiring, and the Windows build
    Net/           # SteamLobby.cs + NetworkHud.cs — dormant behind #if HP_STEAM (Steam is deferred)
  fishnet-patches/ # required patch to make FishNet 4.7.2 compile on Unity 6.5
  steam_appid.txt  # Steam only: "480" (Valve's Spacewar test app)
```

`Assets/HollowPines/Sim` is **not** in this folder — it's copied from
[`csharp/HollowPines.Sim`](../csharp) during the sync step. That copy is the parity-tested shared
simulation and stays canonical in the repo.

## Setup (once)

1. **Unity 6000.5.4f1** (Unity 6.5) via Unity Hub, with **Windows Build Support**. URP project.
2. **FishNet 4.7.2** — Asset Store ("Fish-Networking") or a release from
   [github.com/FirstGearGames/FishNet](https://github.com/FirstGearGames/FishNet).
3. **Apply [`fishnet-patches/`](fishnet-patches/)** — FishNet 4.7.2 does *not* compile on Unity 6.5
   without it (see Troubleshooting).
4. Copy `unity/Assets/HollowPines` and `csharp/HollowPines.Sim` (as `Assets/HollowPines/Sim`) into the
   project's `Assets/`.

## Running it

**Hollow Pines → Set Up Game Scene (Forest)** builds `Assets/Scenes/Forest.unity` from scratch: camera,
NetworkManager + Tugboat, the game systems, and every spawnable prefab, with all references wired.
Then press **Play → START NEW GAME → START MATCH**.

**Re-run that menu item whenever the scene needs a new component or spawnable prefab.** The scene has
no hand-made content, so rebuilding costs nothing — and the setup script does two things Unity's
editor callbacks would otherwise miss on a scripted build (SceneIds and prefab AssetPathHashes; see
`UNITY_PORT_NOTES.md` §1, which is the single most expensive trap in this port).

**Hollow Pines → Build Windows (Game)** produces `Build/Windows/HollowPines.exe`. For two players, run
the exe alongside the editor and **JOIN GAME → 127.0.0.1**.

There's a **DEV — force persona** strip on the title screen: several systems are gated behind one
character (casting is Mara's, the flash Eli's, the spare battery Sam's), so this avoids rerolling
matches to test them.

## Workflow

Edit in **this repo**, then sync into the Unity project:

```powershell
robocopy "<repo>\unity\Assets\HollowPines" "C:\Users\amedi\HollowPines\Assets\HollowPines" /E
```

Robocopy exit codes below 8 mean success. Verify without opening Unity by smoke-compiling against
`Library/ScriptAssemblies` — see `UNITY_PORT_NOTES.md` §8; it has caught real errors repeatedly.

## Architecture (current)

`Scripts/Game/` renders the deterministic world from the shared sim (terrain, forest, RV, caves,
tower, logs, lake, day-night sky), runs first-person movement through
`HollowPines.Sim.Movement.StepPlayer`, and runs the whole authoritative match loop ported from
`ForestRoom.ts` — night clock + escalation, clue trail, roar → freeze → grab → incap, revive,
flashlight dazzle, server-checked filming, the evidence/casting loop and the duffel, and win/loss —
as FishNet SyncVars and RPCs.

- **Movement is interim owner-simulated** (client-authoritative NetworkTransform). Fine for
  friends-play; adopting FishNet's Replicate/Reconcile prediction is the main outstanding netcode
  task (`UNITY_PORT_NOTES.md` §0). **Outcomes are already host-authoritative** — status, filming,
  dazzle, grabs and proof are all decided by the host.
- **Not yet done:** reconnection grace, lobby/session semantics, rigged + animated models and the
  real art/UI pass. Steam relay is deliberately deferred until shipping is on the table.

## Troubleshooting

- **FishNet won't compile on Unity 6000.5+** (`CS0619` on `SceneHandle`/`GetInstanceID`, plus a red
  "Failed to find entry-points … Unity.FishNet.Codegen") — apply [`fishnet-patches/`](fishnet-patches/).
  The codegen error is fallout and clears once `FishNet.Runtime` compiles.
- **START NEW GAME throws a NullReferenceException** — almost certainly a scene/prefab identity
  problem upstream, not the menu. Read `<project>/Logs/Editor.log` (**not** the one in AppData) and
  look for an error *earlier* than the reported one; then re-run **Set Up Game Scene (Forest)**.
  `UNITY_PORT_NOTES.md` §1 has the full explanation.
- **Something spawns but never appears** — the prefab isn't in FishNet's Default Prefab Objects.
  Re-run the scene setup, or **Tools → Fish-Networking → Refresh Default Prefabs**.
- **Input dead in Play mode** — the URP template ships new-Input-System-only. The setup script sets
  Active Input Handling to "Both"; that needs **one editor restart** to take effect.
- **Choppy framerate** — the browser build capped its resolution and Unity doesn't. Lower
  **resolution scale** in Settings or the pause menu; see `UNITY_PORT_NOTES.md` §7 for what to cut next.
