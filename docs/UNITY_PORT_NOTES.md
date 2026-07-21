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
- ~~Fallen logs **slow** the player; the design calls for them to **block**.~~ **Fixed 2026‑07‑20.**
  `Collision.ResolveLogs` pushes a *grounded hunter* out of the log capsule in both sims; Bigfoot
  strides over untouched and a vaulter is airborne, so the trunk passes beneath. The vault trigger
  moved to a **padded reach** (`Player.VaultReach`) — with the push-out in place a hunter can never
  stand inside a log, so a prompt tested at the bare radius would be one that could never appear.
- A large dark polygonal artifact was reported near cave mouths in the web build. Caves were rebuilt
  in Unity with a deliberately dark recess sphere — check whether the two are the same thing.
- The lake was reported as not slowing players. `Collision.LakeDepth` does apply a slow in the shared
  sim, so this may already be fixed in the port — worth confirming in a play pass.

**Planned: a bigger map** (owner, 2026‑07‑20). `World.Size` is 800 m and is the one number the whole
world scales from, so raising it is cheap — but these do **not** follow automatically, and each is a
silent degradation rather than an error:

| Also raise | Why |
|---|---|
| `World.TreeCount` | It is a fixed number of *placement draws* over a larger area, so density falls as the square of the size increase. 2,500 draws over 800 m ≈ 8 m spacing; the same 2,500 over 1,600 m is 16 m — back to the meadow this pass just fixed. |
| `PathGen.MaxSteps` | Trails walk `StepLength` (26 m) per step until they leave the map. At 40 steps they exit an 800 m map comfortably; on a bigger one they stop dead in open forest. |
| `CaveGen.MinRadius` / `RadiusSpan` / `MinSpacing` | Caves sit on a fixed 150–340 m ring, so on a larger map they'd cluster in the middle and leave the rim empty. |
| `ForestGrid` (WorldBuilder) | 8×8 chunks are sized for an 800 m map (100 m cells). Keep cells near 100 m or frustum culling gets coarse. |
| `MapView.BgRes` | Baked map background resolution — fixed pixels over a larger world means a blurrier map. |

Terrain, the lake and the lookout are **fixed coordinates** near the origin and will simply sit in one
corner of a much larger map; they need moving or scaling by hand. Any change here means regenerating
`golden.json` and re-running the parity harness (see §3).

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

## 3b. The world is rebuilt at runtime now — nothing may cache a `GameWorld`

The host rolls a **per-session seed** (`GameManager.WorldSeed`) and clients rebuild the forest when it
arrives, so `WorldBuilder.World` is no longer a build-once constant. Two rules fall out, and both
failures are silent:

- **Never cache the world in a field.** `HPPlayer._world` and `GameManager._world` were
  `= WorldBuilder.EnsureWorld()` assignments in `Awake`/`OnStartClient`; both are now **properties**
  that read the static. A captured reference keeps stepping players against the *default* world's
  colliders — you collide with trees that aren't drawn and walk through ones that are.
- **Anything baked from the world must be invalidated with it.** `MapView` bakes a terrain image once
  (`_bg`); without `InvalidateBackground()` the map draws last session's ridges under this session's
  markers. Same failure class as the mirrored-map bug in §2: internally consistent, quietly wrong.

The reseed itself is just "destroy the children, run the builders again" — every mesh is parented to
the `WorldBuilder` transform, while `PostFX`/`HPAudio` are *components* on that GameObject and so
survive (re-synthesizing the audio cues would cut the wind beds).

## 3c. Adding a rejection to the tree loop is safe; adding a *draw* is not

`WorldData.BuildColliders` and `WorldBuilder.BuildForest` walk the same RNG stream in lockstep so the
rendered trunks land exactly on the invisible colliders. This pass added two rejections (lake, trail
corridor) to **both**. That is safe *only* because every `continue` sits **before** the scale and
rotation draws — rejecting a candidate consumes no extra numbers, so later candidates are unaffected.

Insert a `rand()` call, or move a rejection below the draws in one file and not the other, and the two
loops desync partway through: the first few hundred trees look right and the rest of the forest has
its colliders offset from its trunks. **Undergrowth deliberately uses its own stream**
(`seed ^ 0x5eedb115`) so clutter can be retuned freely without ever touching tree placement.

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

**Worst case seen:** the title screen's settings page grew to ten rebindable actions and pushed its
own BACK button off the bottom of a 1133×528 window — **no way out of the menu at all**. Two lessons:
a page that grows with content must **scroll**, and any escape control (BACK, CLOSE, CONFIRM) belongs
*outside* the scroll where layout can't move it. Every sub-page now also answers **Esc** as a second
way home, on the principle that a UI should never have exactly one exit.

## 7. Performance: the browser was hiding the real cost

The web build caps its device pixel ratio (`QUALITY.pixelRatioCap`), so Three.js never rendered at
native resolution. **Unity does.** On integrated graphics at 2560×1600 that alone is the difference
between choppy and smooth — fill rate scales with the *square* of resolution.

`HPQuality` is the Unity counterpart: URP `renderScale` (default 0.7, live slider in the pause menu),
MSAA off, shadow distance 50 → 35 m. If more is needed, in order: **bloom** in `PostFX` (full-screen,
multi-pass — the most expensive single effect), then the realtime point lights in `WorldBuilder`,
then `UndergrowthCount`, then `World.TreeCount`. **Do not start with the IMGUI HUD** — it is not the
bottleneck.

**The forest is chunked, and it has to stay that way.** Trees and undergrowth build into an 8×8 grid
of combined meshes (`ForestGrid`) rather than one mesh per material. A single combined mesh has a
map-sized bounding box, so Unity **can never frustum-cull any of it** — every trunk is submitted every
frame regardless of where you look. That was survivable at 700 trees and is not at 2,400. Per-cell
meshes let the camera discard everything behind it and everything past the fog, which is most of the
map; the cost is more draw calls, which is the cheap side of that trade. If you ever "simplify" this
back to one combine, the frame time will not show it in a small test scene and will show it badly on
integrated graphics at native resolution.

Separately, input latency: stepping the sim at a fixed 20 Hz and rendering an interpolation between
the last two states parks the camera a full step (50 ms) in the past. `StepPlayer` is pure and takes
`dt`, so the owner steps **once per frame with the real frame delta** (hitch-clamped). This reverts
when FishNet prediction is adopted (see §0), which owns the cadence itself.

## 7b. Play-testing tools (F3 overlay + seed pin)

Two dev affordances exist specifically so a play-test produces *data* instead of impressions:

- **`F3` — diagnostics overlay** (`HPDebug`). Frame time + worst frame in the last second, render
  scale, the world seed, tree/trail/undergrowth/light counts, match phase, player count, tick rate.
  Number keys flip the **cost levers live, in the §7 order**: `1` bloom, `2` prop lights,
  `3` undergrowth, `4` shadows. The point is that "it felt slow" doesn't distinguish four causes with
  four different fixes, and toggling beats rebuilding.
- **Seed pin** (title screen, under the dev persona strip). The forest is rolled per hosting session,
  so **a bug found in one map is otherwise unreproducible** — the map is gone when you restart. The
  overlay prints the live seed; paste it into the field to get that exact forest back. Blank/`0` =
  random. Ignored when joining, since the host owns the seed.

Note `4` writes `QualitySettings.shadowDistance`, the same knob `HPQuality` owns — re-applying
settings from the pause menu will overwrite it. Fine for a dev toggle, just don't read it as sticky.

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
