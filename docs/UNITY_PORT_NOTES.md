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
  *Partly done (2026-07-20):* a **disconnect handler** exists — `HPPlayer.OnStopServer` →
  `GameManager.ServerForgetPlayer`, which scrubs the leaver from every per-player dictionary (as key
  AND as value: the teammate they were reviving, the Bigfoot dragging them) and **aborts the match to
  the lobby** with an on-screen reason if it becomes unplayable (Bigfoot gone, or no searchers left).
  Still **not** done: reconnection grace and rejoining with your prior role/state — a leaver is gone
  for good, mid-match.

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

## 6b. Single-player / the CPU Bigfoot bot

A legitimate **offline mode** (title → SINGLE PLAYER → PLAY AS SEARCHER): a lone human searcher vs a
CPU Bigfoot, no internet. It's also the fastest solo test harness. Architecture, because it's a
pattern worth reusing for future bots:

**A bot is just an HPPlayer spawned with no owner.** `Spawn(nob, null)` → `base.IsOwner` is false on
every machine including the host → `OwnerUpdate()` never runs, so the bot reads no keyboard/mouse.
The host drives it via `HPPlayer.ServerBotDrive`, which runs the **same** `Movement.StepPlayer` a
human does, and fires abilities through the **same** `GameManager.Try*` a human's ServerRpc lands in.
There is deliberately **no parallel "AI movement" or "AI grab"** to drift out of parity. An owner-less,
client-authoritative `NetworkTransform` replicates the host's transform writes to clients unchanged
(verified in FishNet source: `controlledByClient = clientAuth && Owner.IsActive` → false with no
owner → the server moves and syncs it).

**The brain (`BigfootBot`) is intent only** — a host-only `MonoBehaviour` added at runtime by
`ServerBecomeBot` (so the shared player prefab is untouched and it never needs stamping). It decides a
direction + a couple of booleans; that's all. Its perception is the actual stealth game, not
distance-clairvoyance: **sight** is line-of-sight-gated and far longer against a lit flashlight;
**hearing** scales with the target's movement speed and is **silent for a crouching or still**
searcher; then it **remembers** a last-known position and searches it before giving up. Tuning
constants are all at the top of `BigfootBot.cs`.

**Roles are the normal deal.** The bot carries `WantsBigfoot = true` and the lone human `= false`, so
`DoStartMatch` hands the monster to the bot with no special-casing. A bot has no `Owner`, so it can't
receive the `TargetTeleport` RPC — it's placed server-side by `ServerBotPlace`, which also spins up
the brain once the role (and the sim's `IsBigfoot`) is settled. Solo auto-starts: `SpawnBigfootBot`
runs at host load, then `TrySoloStart` (polled from `OnTick`, before the phase guard) waits for both
players to appear in `HPPlayer.All` and calls `DoStartMatch` — no lobby.

**NavMesh.** `WorldBuilder.BuildNavMesh` bakes a runtime surface after each world build/reseed
(procedural world → no editor-baked mesh possible). Undergrowth is hidden during the bake so ~5,200
ferns don't shred it. The bot's **collision is still the shared sim**; the NavMesh only plans the
global route, so an imperfect bake degrades to "clips a route past a trunk the sim slides it around,"
never to walking through solid geometry.

> **Editor-verification gates (NONE of this has run):**
> - **Does the NavMesh bake, and what does it cost?** Runtime `BuildNavMesh` over ~2,400 tree meshes
>   is an unknown hitch on the owner's integrated GPU, and it runs on **every** client each reseed
>   even in co-op (clients bake a mesh only the host uses — a perf item to gate later).
> - **Does the bot actually move?** The owner-less-NetworkTransform-server-drive path is reasoned from
>   source, not observed.
> - **All AI tuning is first-guess** — sense ranges, hearing, sprint/grab distances, wander. Expect to
>   sit in `BigfootBot.cs` and tune once it's playable.
> - **Play-as-Bigfoot is stubbed** (greyed on the menu) — it needs CPU *searchers*, the larger job
>   (routing + filming a five-strong team).

## 6c. The lookout ladder + binoculars (no parity change)

The tower collider is **climbable** (`WorldData.Lookout`, `ClimbH = 9.5`), so the shared sim already
holds any player standing on the platform at `base + 9.5` and stops pushing them out of the footprint
up there — for every role. The only thing a searcher lacked was a way UP (Bigfoot scales it; searchers
can't). So the ladder is **entirely client-side** and touches nothing parity-locked:

- `WorldBuilder.BuildTower` aligns the platform MESH top to `ClimbH` (was 9.8, a ~0.5 m clip), builds
  a ladder on the map-centre-facing face, and exposes `LadderXZ` / `LadderBottomY` / `LadderTopY`.
- `HPPlayer` runs a small ladder state: a searcher presses jump alongside the ladder line to mount, W/S
  drive `_sim.FeetY` pinned to that line, and reaching the top nudges XZ onto the footprint where the
  sim's climbable-top logic takes over. It **replaces** `StepSim` for that frame — the sim never sees a
  half-climbed state, so there's nothing to desync.

Binoculars are presentation only: on the platform, holding the key zooms the camera (FOV 60→26) and
calls `PostFX.SetNightVision` (a big exposure lift + green cast + desaturation). Gated to
`OnLookoutPlatform`, dropped on leaving it, losing control, or mounting the ladder.

Both are **owner/client-side**; movement is still client-authoritative, so no server round-trip is
involved. If full movement prediction (N3) ever lands, the ladder state has to move into the
replicated step like everything else — note it here so it isn't missed.

> **Unverified (editor):** the ladder's mount/geometry (does jump-alongside actually catch, does the
> top step land you cleanly on the deck?), the platform/rail alignment, and the binocular look are all
> first-guess and have never run. The `B` key is a new rebindable action.

## 7a. Testing the whole game alone, on one PC

**Solo works by design.** `ServerStartMatch` picks Bigfoot from whoever opted in; with nobody opted
in it needs 2+ players, and with **one** player it assigns **no Bigfoot at all**. So:

| Setup | You are | Covers |
|---|---|---|
| 1 instance, lobby toggle **off** | a searcher, alone | world, trails, cave discovery, evidence + duffel, logs, sky/moon, HUD, perf |
| 1 instance, **"wants Bigfoot"** on | Bigfoot, alone | roar/leap/climb/cave travel, hair shedding, senses overlay |
| 2 instances | one of each | the interactions only: grab → spill, dazzle, filming, revive |

Most of what needs verifying is reachable **solo** — only grab/dazzle/film/revive need two.

**Two instances on one machine needs `runInBackground`.** Unity pauses an unfocused player, so the
moment you alt-tab, that instance stops ticking, FishNet stops sending and the connection times out
— which reads as "the build is broken" rather than as a setting. `GameSceneSetup` now enables it
(`EnableRunInBackground`). For the second instance, prefer **a standalone build over a second editor**:
it starts faster and you can shrink it, which matters on integrated graphics already pushing ~2,400
trees. Run it windowed and small:

```
HollowPines.exe -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

Host in the editor, JOIN from the build at `127.0.0.1`. Only the focused window takes input, so drive
one, alt-tab, drive the other — fine for verifying an interaction, useless for testing a chase.

**`N` skips to the next night** (host only, F3 overlay). Verifying anything per-night — the moon's
phase and arc, the escalation table, Eli's flash and Sam's battery refilling at dusk — otherwise
means sitting through two full nights to reach night 3. It runs the clock out rather than
duplicating the rollover, so a skipped night is identical to an elapsed one.

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
  **There are now THREE trees to sync, not one** — `Scripts/`, `Shaders/`, and `Sim/` (which comes
  from `csharp/HollowPines.Sim`, not from `unity/`). A sync script that only copies `Scripts/` will
  silently leave the shader or a new sim file behind, and the failure shows up as a magenta sky or a
  missing type rather than as a copy error.
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
