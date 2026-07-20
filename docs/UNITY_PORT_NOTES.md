# Unity port — traps, conventions, and hard-won lessons

Durable engineering notes distilled from the Unity/FishNet port (July 2026). Everything here cost
real debugging time at least once. The dated work orders these came from are gone; this is the part
worth keeping.

For persona/evidence design, see [`CHARACTER_FUNC_DEV.md`](CHARACTER_FUNC_DEV.md).

---

## 0. Open items inherited from the retired planning docs

`UNITY_MIGRATION.md`, `NETWORKING.md` and `BUGS.md` were retired on 2026-07-19. Everything in them
was either finished or is captured below — this is the part that was still outstanding.

**Networking still outstanding** (the decided model is host-authoritative, no host migration in v1).
**Most of this is adopting FishNet features rather than writing netcode** — worth being precise about
which is which, because it changes the size of the job:

*FishNet already provides it — the work is restructuring our code to use it:*
- **Movement under prediction.** FishNet has a full `Replicate`/`Reconcile` prediction system. We are
  deliberately NOT using it yet: movement is *interim owner-simulated* over a client-authoritative
  `NetworkTransform`, while every outcome (status, filming, dazzle, grab, proof) is already
  host-authoritative. Adopting it means moving `HPPlayer.StepSim` into a `[Replicate]` method and
  making `PlayerSimState` a reconcile struct. **When it lands, FishNet's tick loop owns the step
  cadence and the per-frame stepping in §7 reverts.**
- **Lag compensation** for the filming/dazzle cone checks — FishNet ships `ColliderRollback`; the
  work is enabling it and rolling back at the right tick.

*Genuinely ours to build — FishNet does not cover it:*
- **Session/lobby semantics** — ready-up rules, reconnection grace, rejoining with your prior role.

**Steam is explicitly deferred** (owner call, 2026-07-19): the game runs on direct-IP Tugboat and
that is fine for friends-play. Steam relay is a transport swap plus Steamworks.NET, an app ID and
friend invites — revisit when shipping is actually on the table, not before.

**Known bugs, unverified against the Unity build:**
- Fallen logs **slow** the player; the design calls for them to **block** (vault over, or go around).
- A large dark polygonal artifact was reported near cave mouths in the web build. Caves were rebuilt
  in Unity with a deliberately dark recess sphere — check whether the two are the same thing.
- The lake was reported as not slowing players. `Collision.LakeDepth` does apply a slow in the shared
  sim, so this may already be fixed in the port — worth confirming in a play pass.

---

## 1. FishNet stamps identity metadata from editor callbacks — scripted setup outruns it

**This bit twice, and both times the reported error was several steps downstream of the cause.**

FishNet assigns two pieces of identity from editor callbacks that assume a human working pace:

| What | For | Assigned by | Symptom when missing |
|---|---|---|---|
| `SceneId` | scene NetworkObjects | callback throttled to once/250 ms | `"expected to be initialized but was not"`; the object never runs `OnStartServer`, so nothing it owns ever happens |
| `AssetPathHash` | prefab NetworkObjects | prefab-import postprocessor | `DefaultPrefabObjects.Sort()` throws `"An item with the same key has already been added. Key: 0"` inside `NetworkManager.Awake()` → the NetworkManager never registers → `InstanceFinder.NetworkManager` is null → **a NullReferenceException somewhere else entirely** |

`GameSceneSetup` builds the whole scene and every prefab in one synchronous run, so it outruns both.
It therefore stamps them explicitly:

- `AssignSceneIds(scene)` — reflection onto FishNet's internal `NetworkObject.CreateSceneId`.
- `StampAssetPathHashes(prefabs)` — public API (`NetworkObject.SetAssetPathHash` +
  `Hashing.GetStableHashU64`), mirroring `DefaultPrefabObjects.SetAssetPathHashes`.

> **Rule: any new prefab the setup script creates MUST be added to the `StampAssetPathHashes` array,
> and any new scene NetworkObject must exist before `AssignSceneIds` runs.**

Manual escape hatch: **Tools → Fish-Networking → Utility → Reserialize NetworkObjects**
(tick *Reserialize Scenes* for scene objects), or **Refresh Default Prefabs**.

## 2. Three.js is right-handed; Unity is left-handed. The shared sim is Three's.

`shared/sim` (and its C# port) uses `forward = (-sin yaw, -cos yaw)`, `right = (cos yaw, -sin yaw)`.
A Unity body rotated to match that **forward** (`yaw * Rad2Deg + 180`) has the exact opposite
**right**. Forward agrees, right is mirrored — so W/S feel correct while A/D are swapped, and no yaw
remapping fixes both at once. The mirror is inherent; it must be reconciled at each boundary:

- `HPPlayer.StepSim` — A/D fed **crossed** into `MoveInput` (the single input-boundary fix).
- `HPHud.DrawRoarDirection` — negates the sim-right term, or Theo's arrow points to the wrong side.
- `MapView.ToMap` — **mirrors x** (`_half - x`); `DrawSelf` rotates by `+yaw`; `HandleClicks`
  inverts x back; and the baked background must be written with the column index flipped to match.

**The map bug this caused is the cautionary tale:** the terrain texture was baked un-mirrored while
every marker on top of it was mirrored, so the map drew the ground backwards under correctly-placed
labels. It looked internally consistent — the tell was that the lake sat an *exact* reflection from
its own label. Perfect reflection = an axis convention disagreeing between two code paths; drift =
a scaling or offset error.

Server-side aim cones (filming, dazzle) only ever dot with **forward**, so they were never affected.

## 3. The C# sim is parity-locked — fix around it, never through it

`csharp/HollowPines.Sim` is verified against the TypeScript `shared/sim`. Do not "fix" gameplay by
editing it. Ability tunables live in `GameManager` constants (precedent: Wren's mark, the flash, the
battery gift, all the casting numbers). Presentation problems get renderer-side fixes.

Worked example — **the lake**. It is 120 m × 90 m and `World.HillHeight` is 14, so a flat water
plane at the centre's height floated metres above every lower fold of ground; players 90 m away were
rendered *underneath* it and the map looked flooded. Carving a basin into the terrain was not an
option: players stand on the sim's analytic height, so a visual-only basin leaves them walking on
invisible ground above the water. The fix was a **terrain-conforming water sheet** — same coverage as
`Collision.LakeDepth`, never above the land, and consistent with the rule that you wade rather than
swim.

Related: trees stand *in* the lake because `WorldData.BuildColliders` skips only the camp clearing
and cave mouths. `BuildForest` mirrors that RNG stream exactly, so skipping them visually would
leave invisible tree colliders in the water. Fixing it properly means a lake exclusion in **both**
sims plus a re-run parity check.

## 4. A ported mechanic isn't ported until its FEEDBACK is

Five separate "bugs" this session were working mechanics with missing or misleading feedback:

- **Roar spam** — the server refused the extra roars correctly, but the client played the roar sound
  on every click regardless, so it *sounded* uncooldowned. **Any predicted feedback needs the same
  gate as the request, or server authority is invisible to the player.**
- **"Stamina doesn't drain"** — it drained exactly as designed; the HUD printed the charge label only
  *during* cooldown, so a ready ability showed nothing at all. Every ability now states itself in
  both states.
- **"Cave system is lost"** — handler, map buttons and cooldown were all intact. The port had simply
  dropped the on-screen cue telling Bigfoot a mouth was usable, making the whole network
  undiscoverable.
- **START NEW GAME did nothing** — the click handler swallowed failures. It now checks
  `StartConnection()`'s bool, try/catches, `Debug.LogException`s, and shows the reason on screen.
  **A menu button that silently does nothing is the worst possible failure mode**, and the error
  surface is what made trap #1 diagnosable at all.
- **Lobby right-click "went static"** — correct behaviour (it hands over first-person control), but a
  first-person view of someone standing still is pixel-identical to a frozen image. Fixed by
  *blending* the camera over ~0.4 s so the motion itself signals the change.

## 5. Camera ownership: world space vs local space

The camera is parented to the player for first-person and un-parented for the lobby cinematic. Two
traps came out of that, both worth re-reading before touching camera code:

- While un-parented, **local space *is* world space** — a `localPosition` write flings the camera to
  the world origin. Guard those writes on `_cam.transform.parent == transform`.
- Running the look handler inside the cinematic *and* falling through to the normal path applies the
  mouse delta **twice** (doubled sensitivity).

## 6. IMGUI panels must clamp to the window

Every panel is manual pixel math with no anchoring, so fixed-size boxes clip on small windows (a
560×400 briefing card lost its bottom on a 1133×528 Game view). **No fixed-pixel IMGUI panel without
a `Mathf.Min(..., Screen.height - margin)` clamp**; scroll anything that can still overflow, and keep
confirm buttons *outside* the scroll. This class of bug recurs with every content addition — the R5
UI pass should move to UI Toolkit/uGUI with real anchors.

Also: overlays that reserve screen space must account for each other. The map frame centres in the
space left *after* the HUD's top bar, or its title runs straight through the clock.

## 7. Performance: the browser was hiding the real cost

The web build caps its device pixel ratio (`QUALITY.pixelRatioCap`), so Three.js never rendered at
native resolution. **Unity does.** On integrated graphics at 2560×1600 that alone is the difference
between choppy and smooth — fill rate scales with the *square* of resolution.

`HPQuality` is the Unity counterpart: URP `renderScale` (default 0.7, live slider in the pause menu),
MSAA off, shadow distance 50 → 35 m. If more is needed, in order: **bloom** in `PostFX` (full-screen,
multi-pass — the most expensive single effect), then the realtime point lights in `WorldBuilder`,
then `World.TreeCount`. **Do not start with the IMGUI HUD** — it is not the bottleneck.

Separately, input latency: stepping the sim at a fixed 20 Hz and rendering an interpolation between
the last two states parks the camera a full step (50 ms) in the past. `StepPlayer` is pure and takes
`dt`, so the owner steps **once per frame with the real frame delta** (hitch-clamped). This reverts
when FishNet prediction is adopted (see §0), which owns the cadence itself.

## 8. Workflow

- **Edit in the repo** (`unity/Assets/HollowPines/`), then `robocopy /E` into
  `C:\Users\amedi\HollowPines\Assets\HollowPines`. Robocopy exit codes < 8 are success.
- **Smoke-compile outside Unity** before handing over — a scratch csproj (netstandard2.1, LangVersion
  9, `ENABLE_INPUT_SYSTEM`, plus `UNITY_EDITOR` and `UnityEditor*.dll` for a second editor pass)
  against `Library/ScriptAssemblies/*.dll` and the Unity Managed DLLs. This has caught real errors
  repeatedly.
- **The editor log lives at `<project>/Logs/Editor.log`**, *not* the one in `AppData` (which goes
  stale with multiple editor instances). When something fails at runtime, read that file — the first
  error is usually several steps upstream of the reported one.
- **Re-run "Hollow Pines → Set Up Game Scene (Forest)"** whenever the scene gains a component or a
  spawnable prefab. The scene has no hand-made content; rebuilding it costs nothing.
- `HollowPines.Sim` collides with UnityEngine on `Collider`/`Collision` — qualify them.
- FishNet 4.7.2 does not compile on Unity 6000.5 unpatched; see [`../unity/fishnet-patches/`](../unity/fishnet-patches/README.md).

## 9. Copy should read as capability, not as a stat block

Briefing cards derive every figure from the live constants so they can't drift — good — but raw
numbers are spec language, not player-facing copy. A card should say *"you can follow a trail long
after it's gone cold for everyone else"*, not *"clue window 22.5 s"*. Keep the derived values as the
source of truth and choose the phrasing from thresholds on them. **Only ever list abilities that
actually ship** — a card that teaches a control which does nothing is worse than a shorter card.
