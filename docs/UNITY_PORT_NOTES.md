# Unity port — traps, conventions, and hard-won lessons

Durable engineering notes distilled from the Unity/FishNet port (July 2026). Everything here cost
real debugging time at least once. The dated work orders these came from are gone; this is the part
worth keeping.

For persona/evidence design, see [`CHARACTER_FUNC_DEV.md`](CHARACTER_FUNC_DEV.md).

> **How sections are named.** Every heading carries a short bracketed id — `[rng-lockstep]`,
> `[materials]`, `[bodies]` — and everything that references a section, in this file or from a code
> comment, uses that id. **Cite the id, never the position.**
>
> This replaced a `§5b`/`§6d`-style scheme on 2026-08-05. That scheme had stopped being merely opaque
> and become actively wrong: sections were appended in the order they were written rather than where
> they belonged, so the file ran `0, 1, 2, 3, 3b, 3c, 4, 5, 6, 7, 6b, 5b, 5c, 5d, 6d, 6c, 7a…`. The
> numbers did not sort, which meant they advertised an ordering the document did not have — and every
> new section forced a choice between renumbering ~90 references across 19 files or inventing another
> letter suffix. An id says what a section is *about*, so it survives reordering, survives rewording
> the heading's prose, and greps exactly (`rg "\[rng-lockstep\]"`).
>
> Other docs keep their own numbering: `GAME_DESIGN.md` §7.7 and `CHARACTER_FUNC_DEV.md` §3 are those
> files' internal schemes and are unaffected by this.

---

## [open-items] Open items inherited from the retired planning docs

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
  cadence and the per-frame stepping in [perf] reverts.**
  > **Deep snow rides on this, and so does the web build.** The drift slow (`Movement.DeepSnowDepth`)
  > is applied inside `StepPlayer`, i.e. by whoever simulates the player — which today is the owning
  > client. A hacked client can ignore it. This is *not* a Unity-only caveat, which the migration
  > plan originally assumed: `MountainRoom.applyMove` on the web build validates a move (bounds,
  > speed-gate token bucket, collision pushout, feet clamp) but never re-runs `StepPlayer` either, so
  > the web build is in exactly the same position. In both cases the speed gate still caps the
  > result, so the exploit is "not slowed", never "faster than legitimate" — the same trust level
  > `lakeHunterFactor` has always had. Adopting prediction closes it for Unity; the web build would
  > need `applyMove` to re-simulate rather than validate.
- **Lag compensation** for the filming/dazzle cone checks — FishNet ships `ColliderRollback`; the
  work is enabling it and rolling back at the right tick.

*Genuinely ours to build — FishNet does not cover it:*
- **Session/lobby semantics** — ready-up rules, reconnection grace, rejoining with your prior role.
  *Partly done (2026-07-20):* a **disconnect handler** exists — `HPPlayer.OnStopServer` →
  `GameManager.ServerForgetPlayer`, which scrubs the leaver from every per-player dictionary (as key
  AND as value: the teammate they were reviving, the Yeti dragging them) and **aborts the match to
  the lobby** with an on-screen reason if it becomes unplayable (Yeti gone, or no searchers left).
  Still **not** done: reconnection grace and rejoining with your prior role/state — a leaver is gone
  for good, mid-match.

**Steam is explicitly deferred** (owner call, 2026-07-19): the game runs on direct-IP Tugboat and
that is fine for friends-play. Steam relay is a transport swap plus Steamworks.NET, an app ID and
friend invites — revisit when shipping is actually on the table, not before.

**Known bugs, unverified against the Unity build:**
- ~~Fallen logs **slow** the player; the design calls for them to **block**.~~ **Fixed 2026‑07‑20.**
  `Collision.ResolveLogs` pushes a *grounded hunter* out of the log capsule in both sims; Yeti
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
`golden.json` and re-running the parity harness (see [parity-lock]).

---

## [fishnet-identity] FishNet stamps identity metadata from editor callbacks — scripted setup outruns it

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

## [handedness] Three.js is right-handed; Unity is left-handed. The shared sim is Three's.

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

## [parity-lock] The C# sim is parity-locked — fix around it, never through it

`csharp/Metoh.Sim` is verified against the TypeScript `shared/sim`. Do not "fix" gameplay by
editing it. Ability tunables live in `GameManager` constants (precedent: Wren's mark, the flash, the
battery gift, all the casting numbers). Presentation problems get renderer-side fixes.

Worked example — **the lake**. It is 120 m × 90 m and `World.HillHeight` is 14, so a flat water
plane at the centre's height floated metres above every lower fold of ground; players 90 m away were
rendered *underneath* it and the map looked flooded. Carving a basin into the terrain was not an
option: players stand on the sim's analytic height, so a visual-only basin leaves them walking on
invisible ground above the water. The fix was a **terrain-conforming water sheet** — same coverage as
`Collision.LakeDepth`, never above the land, and consistent with the rule that you wade rather than
swim. *(Since the Metoh re-theme the sheet is a frozen tarn — slick ice and breaking crust, with
pressure ridges over it — but the geometry and the `LakeDepth` slow underneath it are unchanged, so
this worked example still holds exactly.)*

Related: trees stand *in* the lake because `WorldData.BuildColliders` skips only the camp clearing
and cave mouths. `BuildForest` mirrors that RNG stream exactly, so skipping them visually would
leave invisible tree colliders in the water. Fixing it properly means a lake exclusion in **both**
sims plus a re-run parity check.

## [no-world-cache] The world is rebuilt at runtime now — nothing may cache a `GameWorld`

The host rolls a **per-session seed** (`GameManager.WorldSeed`) and clients rebuild the forest when it
arrives, so `WorldBuilder.World` is no longer a build-once constant. Two rules fall out, and both
failures are silent:

- **Never cache the world in a field.** `HPPlayer._world` and `GameManager._world` were
  `= WorldBuilder.EnsureWorld()` assignments in `Awake`/`OnStartClient`; both are now **properties**
  that read the static. A captured reference keeps stepping players against the *default* world's
  colliders — you collide with trees that aren't drawn and walk through ones that are.
- **Anything baked from the world must be invalidated with it.** `MapView` bakes a terrain image once
  (`_bg`); without `InvalidateBackground()` the map draws last session's ridges under this session's
  markers. Same failure class as the mirrored-map bug in [handedness]: internally consistent, quietly wrong.

The reseed itself is just "destroy the children, run the builders again" — every mesh is parented to
the `WorldBuilder` transform, while `PostFX`/`HPAudio` are *components* on that GameObject and so
survive (re-synthesizing the audio cues would cut the wind beds).

## [rng-lockstep] Adding a rejection to the tree loop is safe; adding a *draw* is not

`WorldData.BuildColliders` and `WorldBuilder.BuildForest` walk the same RNG stream in lockstep so the
rendered trunks land exactly on the invisible colliders. This pass added two rejections (lake, trail
corridor) to **both**. That is safe *only* because every `continue` sits **before** the scale and
rotation draws — rejecting a candidate consumes no extra numbers, so later candidates are unaffected.

Insert a `rand()` call, or move a rejection below the draws in one file and not the other, and the two
loops desync partway through: the first few hundred trees look right and the rest of the forest has
its colliders offset from its trunks. **Undergrowth deliberately uses its own stream**
(`seed ^ 0x5eedb115`) so clutter can be retuned freely without ever touching tree placement.

## [feedback] A ported mechanic isn't ported until its FEEDBACK is

Five separate "bugs" this session were working mechanics with missing or misleading feedback:

- **Roar spam** — the server refused the extra roars correctly, but the client played the roar sound
  on every click regardless, so it *sounded* uncooldowned. **Any predicted feedback needs the same
  gate as the request, or server authority is invisible to the player.**
- **"Stamina doesn't drain"** — it drained exactly as designed; the HUD printed the charge label only
  *during* cooldown, so a ready ability showed nothing at all. Every ability now states itself in
  both states.
- **"Cave system is lost"** — handler, map buttons and cooldown were all intact. The port had simply
  dropped the on-screen cue telling Yeti a mouth was usable, making the whole network
  undiscoverable.
- **START NEW GAME did nothing** — the click handler swallowed failures. It now checks
  `StartConnection()`'s bool, try/catches, `Debug.LogException`s, and shows the reason on screen.
  **A menu button that silently does nothing is the worst possible failure mode**, and the error
  surface is what made trap #1 diagnosable at all.
- **Lobby right-click "went static"** — correct behaviour (it hands over first-person control), but a
  first-person view of someone standing still is pixel-identical to a frozen image. Fixed by
  *blending* the camera over ~0.4 s so the motion itself signals the change.

## [camera-space] Camera ownership: world space vs local space

The camera is parented to the player for first-person and un-parented for the lobby cinematic. Two
traps came out of that, both worth re-reading before touching camera code:

- While un-parented, **local space *is* world space** — a `localPosition` write flings the camera to
  the world origin. Guard those writes on `_cam.transform.parent == transform`.
- Running the look handler inside the cinematic *and* falling through to the normal path applies the
  mouse delta **twice** (doubled sensitivity).

## [imgui-clamp] IMGUI panels must clamp to the window

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

## [perf] Performance: the browser was hiding the real cost

The web build caps its device pixel ratio (`QUALITY.pixelRatioCap`), so Three.js never rendered at
native resolution. **Unity does.** On integrated graphics at 2560×1600 that alone is the difference
between choppy and smooth — fill rate scales with the *square* of resolution.

`HPQuality` is the Unity counterpart: URP `renderScale` (**default 1.0 since the legibility pass —
see [legibility]**; live slider in the pause menu), MSAA off, shadow distance 55 → 30 m by tier. If more is needed, in order: **bloom** in `PostFX` (full-screen,
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
when FishNet prediction is adopted (see [open-items]), which owns the cadence itself.

## [yeti-bot] Single-player / the CPU Yeti bot

A legitimate **offline mode** (title → SINGLE PLAYER → PLAY AS SEARCHER): a lone human searcher vs a
CPU Yeti, no internet. It's also the fastest solo test harness. Architecture, because it's a
pattern worth reusing for future bots:

**A bot is just an HPPlayer spawned with no owner.** `Spawn(nob, null)` → `base.IsOwner` is false on
every machine including the host → `OwnerUpdate()` never runs, so the bot reads no keyboard/mouse.
The host drives it via `HPPlayer.ServerBotDrive`, which runs the **same** `Movement.StepPlayer` a
human does, and fires abilities through the **same** `GameManager.Try*` a human's ServerRpc lands in.
There is deliberately **no parallel "AI movement" or "AI grab"** to drift out of parity. An owner-less,
client-authoritative `NetworkTransform` replicates the host's transform writes to clients unchanged
(verified in FishNet source: `controlledByClient = clientAuth && Owner.IsActive` → false with no
owner → the server moves and syncs it).

**The brain (`YetiBot`) is intent only** — a host-only `MonoBehaviour` added at runtime by
`ServerBecomeBot` (so the shared player prefab is untouched and it never needs stamping). It decides a
direction + a couple of booleans; that's all. Its perception is the actual stealth game, not
distance-clairvoyance: **sight** is line-of-sight-gated and far longer against a lit flashlight;
**hearing** scales with the target's movement speed and is **silent for a crouching or still**
searcher; then it **remembers** a last-known position and searches it before giving up. Tuning
constants are all at the top of `YetiBot.cs`.

**How it finds you is snow prints, not clairvoyance.** The first version closed the gap by walking at
the nearest searcher's *true* position — omniscience with jitter on top. It now follows the
`TypeSnowPrint` clues searchers leave off-trail, which only the Yeti can see, so its knowledge is the
knowledge the fiction grants it. That makes the deep-snow mechanic cut both ways: stay on the packed
trails or in camp and the bot has genuinely nothing to follow. States resolve in priority order —
**DRAG** (haul a grabbed searcher away from the duffel before dropping them) → **DAZZLED** (break off
and leave the beam, since roar/grab are locked anyway) → **HUNT** → **SEARCH** → **TRACK** (the
freshest print, taking a crevasse if the trail is cold and far) → **PROWL** → **WANDER**. Target
choice prefers whoever is carrying proof, then whoever is bogged in a drift. The roar is held unless
it catches two or more, or the lone target is close enough that the grab should land.

The omniscient prowl survives as the **last-resort floor** behind the F3 `[P]` toggle, and it is worth
play-testing with it OFF: pure sight/hearing/tracks is arguably the better game, but with no prowl at
all a team hiding motionless in camp is never found and the night just runs out.

Two plumbing notes that are easy to trip over. `ClueMarker` registers in `All` on the **network**
callbacks, not the client ones, because the bot reads that registry server-side — on a listen host the
client callback runs too, which makes the difference invisible until it isn't. And crevasse
fast-travel needed a bot path: `TargetTeleport` is a TargetRpc to an owning client, so
`TryCaveTravel` now branches to `ServerBotTeleport` for a bot. Same guards, same authority, only the
delivery differs.

**Roles are the normal deal.** The bot carries `WantsYeti = true` and the lone human `= false`, so
`DoStartMatch` hands the monster to the bot with no special-casing. A bot has no `Owner`, so it can't
receive the `TargetTeleport` RPC — it's placed server-side by `ServerBotPlace`, which also spins up
the brain once the role (and the sim's `IsYeti`) is settled. Solo auto-starts: `SpawnYetiBot`
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
> - **All AI tuning is first-guess** — sense ranges, hearing, sprint/grab distances, wander, and the
>   whole tracking layer (print age window, freshness-vs-distance weighting, drag time, dazzle
>   break-off). Expect to sit in `YetiBot.cs` and tune once it's playable.
> - **Does print-tracking read as tracking?** The bot walking your trail is the intended feel, but
>   whether it looks like a predator following spoor or like a magnet is a play-test question, and
>   the freshness/distance weights are where that gets decided.
> - **Play-as-Yeti is now live but early** — see [searcher-bots]. The CPU searcher team exists as a working
>   shell; whether it is any fun to hunt is completely unmeasured.

## [materials] The realism pass — why it is materials, not models

The brief was "more realistic 3D game instead of just polygons". The instinct is to blame the
geometry, but the geometry was never the problem: a low-poly cone lit properly reads fine, and a
high-poly one lit flat still reads as plastic. The actual cause was that **every surface in the game
was `URP/Lit` with a flat base colour and smoothness 0.05**. A flat-shaded face takes one shade of
light across its whole area and dies there — no microstructure, no specular break-up, nothing for the
moon or a torch to catch. That is what "polygons" looks like.

So the pass is a **material and lighting** pass:

- **Procedural PBR maps** (`ProcTex.cs`). There is not one texture file in the repo and adding one
  would break "clone it and it runs", so the maps are synthesized at load from tileable value noise —
  snow grain, a finer snow detail layer, rock fracture, bark striation, ice pressure lines, canvas
  weave, matted fur. ~256 KB and a few ms each, once. The noise is genuinely periodic (the lattice
  hash wraps), because a non-tiling hash puts a visible seam every repeat across 800 m of terrain.
- **UVs and tangents on every generated mesh.** This was the blocker, and it is worth stating plainly:
  a normal map cannot bind without them, so no amount of material tuning would have done anything
  until `MeshUtil` and the hand-built terrain/tarn/trail meshes carried both. `RecalculateTangents`
  must follow the UVs, not precede them.
- **Per-class response.** Snow is moderately smooth (its glitter is a microfacet effect, not a
  colour); ice is the one genuinely glossy surface; rock, timber and fur are light sinks.
- **Trilight ambient with a bright ground term.** The single most important lighting change. Flat
  ambient lights every surface identically from every direction, which is precisely what flattens
  geometry into cardboard. Standing on snowpack, a startling share of the light on your face has
  bounced *up* — so the ground term is tinted toward the snow albedo and deliberately strong, and the
  undersides of branches and figures stop going dead.
- **Soft shadows** on the tier that can afford them. Hard shadows were defensible over a dark fogged
  forest floor; over open snow they are one of the loudest "this is a game" tells.
- **Split toning** — cool shadows, warm highlights. Pure grading, and the cheapest realism win here:
  real snow at night is lit by two sources of very different colour, and the eye reads that
  opposition as depth. A single ambient tint cannot produce it because the split happens across
  luminance.
- **Per-chunk colour jitter** on the forest. After flat shading, uniformity is the loudest tell:
  2,400 trunks in exactly one brown reads as instancing. Free, because each chunk is already its own
  draw and the SRP batcher batches by shader, not by material. Hashed from the cell index, never from
  an RNG stream — it must not touch the tree/collider lockstep ([rng-lockstep]).

**Cost is gated on the render-scale slider** (`HPQuality.HighDetail`), which now doubles as a detail
tier: anyone who has already pulled it down to buy frames is not silently charged for soft shadows
and a 55 m shadow distance too.

> **~~The one big win that is NOT in here, and cannot be.~~ Done 2026-08-01 — see [legibility].** SSAO is a
> URP *Renderer Feature* living on an untracked `.asset`, so it was written up here as a manual owner
> step. It was never carried out, which is the whole problem with that format. It is now applied by
> **Metoh → Configure Render Pipeline** (`Editor/RenderPipelineSetup.cs`) instead.

Two things fixed in passing, both pre-existing: `SetTimeOfDay` re-asserted `LightShadows.Hard` every
frame, so shadow quality could never actually be configured from anywhere; and the world leaked its
entire material set on every reseed (`new Material` is a native object Unity does not collect), which
was survivable at a couple of dozen and is not at ~200 after per-chunk tinting.

**Nothing in this pass has been seen.** Every value is reasoned from how the materials behave, not
observed — expect to sit in `ProcTex` (normal strengths, tiling) and the smoothness numbers and tune
by eye. The tiling scales in particular are the kind of thing that is obviously wrong the moment you
look and impossible to guess.

## [legibility] The legibility pass — and why none of [materials] had ever actually rendered

Owner report, 2026-08-01, after the realism pass shipped: *"everything looks way too much the same…
I want to be able to see things"* and *"it looks like a PS1 game"*. Both were true, and the second
one had almost nothing to do with the art.

**Two settings were cancelling the entire realism pass.**

| What | Was | Why it mattered |
|---|---|---|
| `HPSettings.RenderScale` | `0.7` | The scene rendered at 70% of the panel and was upscaled. Soft edges, crawling stair-steps, and the fine normal-map grain [materials] is built on dissolving before it reached a pixel. |
| `HPQuality.HighDetail` | `renderScale > 0.7f` | The shipping default was *exactly* `0.7`, so this was **false on every clean install**. Soft shadows and the 55 m shadow distance were never on. Nobody had ever seen the expensive tier. |

That is the lesson worth keeping: **a boundary condition set to the same value as a default is a
switch that is always off**, and it fails silently as an art problem rather than as a bug. The
threshold is now `>= 0.8`, deliberately *between* the two scales anyone runs. `RenderScale` defaults
to `1.0`, and because it is a PlayerPref the code default alone would have changed nothing for an
existing player — `HPSettings.SettingsVersion` migrates a saved `0.7` up once (and only `0.7`, so a
considered `0.5` is left alone).

**"Everything looks the same" was a value-range problem, not a hue problem.** Ground, trail and
drift all sat in the top fifth of the value scale, and the terrain was a single flat colour over all
800 m — a normal map varies a surface's *lighting*, but one albedo still averages back to one grey
at any distance. Fixes, in descending order of how much they changed:

- **`Shaders/Snowpack.shader`** — the ground is now snow blended against wind-stripped rock by
  **slope**, which puts genuine dark values on ridges and gully walls and is what makes terrain shape
  readable at range. Measure slope as the **gradient** (`length(n.xz)/n.y`), *not* `1 - n.y`: this
  terrain is gentle enough that `1 - n.y` never leaves the bottom 6% of its range and no threshold in
  it is tunable.
- **The deep-snow basin is now visible.** `Movement.DeepSnowDepth` slows searchers over roughly a
  third of the map and that zone was completely unmarked. The shader reads `Player.DriftHeight` /
  `DriftDepth` directly — one source of truth, no second copy — so the tint can't disagree with the
  slow it advertises. A routing choice you cannot see is an ambush, not a choice.
- **Value range widened**: bare rock `0x4a4f57` → trail `0x94a0ab` → basin `0xa8bccf` → snowpack
  `0xc9d6e2`. Hue separates the two middle ones (basin cold, trail warm) because they are close in
  value and telling them apart is a live gameplay question.
- Note the old `TrailCol` comment claimed it "must stay clearly lighter than `GroundCol`" while the
  shipped value was **darker**. The comment was wrong; what the trail needs is contrast in *either*
  direction, and darker is also what trodden snow does.

**Landmarks.** A ridgeline now renders in `NightSky.shader`, seeded from the world seed. It has to
live in the skybox for the same reason the moon does — fog kills anything past ~150 m, and real
geometry close enough to see would be inside the playable area. It gives an absolute compass every
player in a session shares. In-world: each crevasse gets an identity colour (mast + throat glow) so
the fast-travel network is *nameable* instead of five interchangeable grey lumps, trail masts carry
one colour per trail, and camp has the tallest mast on the map.

> Marker masts are **render-only**, which is a deliberate exception to the undergrowth rule in
> `BuildUndergrowth` ("anything tall enough to hide a player belongs in the sim"). A 7 cm pole hides
> nobody. Do not use this as precedent for anything with width.

**Light level raised** — ambient ~40%, moon ~30% (ratios between nights preserved), `AmbientBounce`
to 1.0, and a +0.35 stop base exposure. This is a real difficulty change: moonlight is what lets
searchers move without burning battery. It was still right, because at the old levels every contrast
cue above was invisible regardless of how well separated it was in albedo. **If night 3 now feels
too survivable, take it out of `MoonNights[2].Light`, not out of ambient** — losing the moon is the
escalation the design already has; flat ambient is what makes geometry read as cardboard.

**Geometry** (the part [materials] was wrong about). [materials] argued the problem was materials and not models,
and that was right about *surfaces* and wrong about *silhouettes*. Three smooth cones stacked on a
stick is a shape no tree has, and at night — fogged, backlit, at 60 m — the outline is very nearly
all the information reaching the player. `MeshUtil.Conifer` builds one tiered, jagged, drooping crown
instead; `MeshUtil.Rock` replaces every scaled-sphere boulder. Both take a `variant` index hashed
from the tree/cave index — **never from an RNG stream**, because the forest loop is in lockstep with
`WorldData.BuildColliders` ([rng-lockstep]). The low detail tier is *cheaper* than the three cones it replaced
(77 tris vs 96); the high tier spends 153.

> **Unverified (editor), all of it.** Same standing caveat as [materials], plus two specific risks: the
> `Metoh/Snowpack` shader has never been compiled by Unity (a compile failure falls back to
> `URP/Lit`, which will look flat and *not* log a warning — the `Shader.Find` guard only catches a
> missing file), and tree cost rose ~46% on the high tier at the same time render scale went to
> native. If frames are short, the F3 levers in [perf] order still apply.

## [bodies] Bodies, animation and weather — the graphics pass

Owner report, 2026-08-05: *"the yeti is literally 2 ovals on top of one another"*. Accurate, and an
understatement — it was **one** Unity primitive capsule scaled to 1.3/1.35/1.3, with two 0.12 m
spheres for eyes. Searchers were the same capsule at 0.8/0.9. And there was **no character animation
anywhere in the project**: no `Animator`, no `SkinnedMeshRenderer`, no procedural limb motion. Head-bob
was camera-only, so it did not exist for anyone *looking* at you. Every body slid across the snow.

**The [legibility] silhouette argument applies harder to creatures than it did to trees.** A material cannot put
arms on a pill. [materials] had duly built a stretched fur normal map and was applying it to a capsule.

### Jointed bodies without a rig (`Avatar.cs`, `MeshUtil.Lathe/Limb/Blob`)

No `.fbx`, no skeleton, no skinning — those would all break "clone it and it runs". A body is a
**hierarchy of separate meshes moved by transform**: a shoulder joint with an upper-arm mesh under it,
an elbow under that. At these distances it is indistinguishable from skinning **provided limb ends are
rounded** so neighbouring parts overlap through their range of motion. Flat-capped cylinders butted at
a joint show the join as a hard disc edge that swings independently, which reads as a doll made of parts.

`MeshUtil.Lathe` is the general surface-of-revolution the bodies are built from, with `xScale`/`zScale`
to squash it off-circular — a torso is far wider than it is deep, and a circular one is a barrel. It
**seals its own seam**: the wrap column duplicates column 0's position to carry different U, but
`RecalculateNormals` averages by vertex *index*, so those two co-located vertices light differently and
put a bright hairline down the body. Averaging the pair afterwards costs nothing. (`Rock` and `Conifer`
predate this and still have the unsealed seam; it shows less on a boulder.)

The Yeti is the **hunched bruiser** the owner picked. Two proportions carry the whole read and neither
is negotiable: the shoulder yoke is ~1.7x hip width (the ratio the eye uses to separate ape from
person, and it survives being reduced to a black shape in fog), and **the head sits ahead of the
shoulder line, not on top of it** — a head centred over the spine reads as upright and human no matter
how big the body is. Head height is anchored to the sim's 2.4 m Yeti eye height (1.7 m for searchers)
so the third-person figure and its own first-person camera agree about where its eyes are.

### The animation layer

Driven **entirely from data that is already replicated** — horizontal speed, body yaw, `Status`,
`Crouched`, `Filming`, `GrabberObjectId`. No new SyncVars, no new RPCs. It is strictly a *read* of state
the match already agreed on, so it cannot desync and cannot be cheated. The roar pose rides the
existing `RpcRoared`, which every client already receives.

Things worth not undoing:

- **Gait phase advances with ground covered, not with time.** This is why the feet do not skate when the
  per-night speed multipliers or the deep-snow slow change how fast a body is actually moving.
- **Speed and yaw rate are measured from the transform**, not read from the sim, so the owner and a
  remote go down one path and a remote's gait matches the motion *being drawn* rather than the motion
  its owner reported a network frame ago.
- **Knees bend off a rectified cosine offset from the leg swing**, which puts the bend on the recovery
  half of the stride. A knee that bends while the leg is planted is the classic tell of a cycle built
  from raw sine waves.
- **There is no head pitch to track** — the schema has only ever carried yaw. The head *leads the turn*
  instead, driven by yaw rate, which is replicated. Do not "fix" this by adding a pitch SyncVar for
  cosmetics alone.
- Damping is `1 - exp(-rate * dt)`, not `rate * dt`: the latter stiffens the whole rig at low framerates,
  which is exactly the machine this runs on.

### Weather (`Weather.cs`) — and the title card

The project had **zero particle systems**. Falling snow does real work beyond mood: without something
visibly moving between the camera and the fog wall, distance is unreadable, and this build's horror
geometry is largely about not being able to tell how far away something is. Breath vapour is a fair
positional tell on a searcher hiding with their torch off. Motes are what make a beam legible as a beam.

- Snow is **alpha-blended, never additive** — it occludes what is behind it, which is how it adds depth.
- The simulation box is 45 m and **re-centred on the camera every frame**: infinite snow for the cost of
  a small volume, since the fog closes at ~150 m anyway.
- **Breath is remotes-only.** The owner's head is inside their own camera; their breath would fog the
  game rather than the world.
- Bootstrapped from `WorldBuilder.Awake` and following `Camera.main`, so **the title cinematic flies
  through the same weather the match does**. A still-air title card in front of a snowing game
  advertises the exact seam this pass exists to close.

`TitleActors.cs` stages three searchers around the fire in the camp shot and the Yeti crossing the
corridor in the trail shot, driven by a synthetic `AvatarInput`. They are not players — nothing there
touches FishNet, `GameManager` or the sim. They are built and destroyed on the `SetTitleLighting`
transition, so connecting disposes them before the real bodies spawn.

### Torch (`TorchBeam.cs` + `Shaders/TorchBeam.shader`)

A spot light is invisible until it lands on something, so on open snowpack a torch produced a lit patch
with no shaft connecting it to a person. The beam is a **cone of geometry with an additive falloff**,
not volumetrics — additive because light adds and cannot darken what is behind it; an alpha-blended
beam washes the scene toward grey where it crosses a dark trunk and reads as fog on the lens. It covers
**26% of the light's range**: a shaft drawn to the full 90 m is a 54 m-wide cone of overdraw.

The torch now rides the **hand**, so a remote's beam swings with their arm. Only the **owner's** torch
casts shadows, and only on the high tier — five shadow-casting spots is five extra shadow maps a frame,
and you cannot see the shadows a teammate's beam casts 40 m away.

> **Unverified (editor), all of it** — same standing caveat as [materials] and [legibility], and the risk is higher here
> because this pass is mostly *motion*, which cannot be reasoned about from a still frame the way
> material response can. Specific risks: `Metoh/TorchBeam` has never been compiled by Unity (a compile
> failure falls back to magenta, and `Shader.Find` only catches a *missing* file); the limb rotation
> signs are derived in a comment rather than observed, so an arm swinging backwards is a sign flip, not
> a redesign; and the ability poses (roar/carry/film) are pure guesses at angles.
>
> **Known leak, pre-existing and slightly worsened here.** `ReleaseWorldMaterials` sweeps materials on
> reseed but nothing sweeps generated **meshes**, and this pass adds icicles, tents, guy lines and
> duffel parts to what leaks. It was NOT fixed in passing: a hierarchy sweep would have to exclude
> Unity's built-in primitive meshes, and destroying one of those breaks every primitive for the rest of
> the session — a far worse bug than the leak. Avatar meshes are exempt; `Avatar.Dispose` owns them.

## [searcher-bots] The CPU searchers (`SearcherBot`) — a shell, deliberately

Title → SINGLE PLAYER → **PLAY AS YETI** spawns four CPU searchers and hands the human the monster.
Construction is identical to the Yeti bot — server-owned `HPPlayer`s with no connection, flagged
`WantsYeti = false` so the normal `DoStartMatch` deal gives them searcher roles and `DealSpecialties`
gives each a distinct character with real specialty numbers. No special-casing: to the match they are
just searchers who never send input. `ServerBecomeBot` picks the brain by role and is re-entrant, so a
role swap between matches swaps brains instead of stacking them.

**Every searcher action needed a bot entry point**, for the same reason the Yeti's did: a human's
film / revive / collect / deposit / recover / flash / ping / mark all travel through `[ServerRpc]`,
which needs an owning connection. `ServerBot*` pass-throughs land in the identical `GameManager`
authority, so a CPU searcher is bound by the same range, cone, LOS, channel duration and cooldown
rules a person is.

**The ladder** (highest priority first, one rung owns each frame, and the F3 overlay prints which):
FLEE → REVIVE → BANK → FILM → COLLECT → INVESTIGATE → EXPLORE.

**Why a searcher is harder to write than the Yeti.** The Yeti's brain is a pursuit problem: one
target, close the distance. A searcher's is a resource problem with a fear layer, and the pieces pull
against each other — the torch is its only real sensor *and* a flare the Yeti sees from 80 m; carried
proof is a debt that grows the longer it is held; filming means pointing yourself at the thing hunting
you and standing still; and nobody wins alone, but clumping lets one roar take the whole team. A bot
that only optimises evidence walks into the Yeti's arms, and one that only avoids it never wins.

**What is genuinely shallow, in priority order for whoever fills it in:**
- **EXPLORE is random roam.** The single biggest gap. A real search would divide the map between
  teammates, sweep outward from camp, and prefer ground nobody has covered recently. Random roam is
  why a bot team reads as five people wandering rather than as a search party.
- **Almost no team coordination.** They do not spread out, or stage a rescue. Wren's trail marks and
  the stakeout ping exist and go unused (`ServerBotMark` / `ServerBotPing` are wired and never
  called). The one channel that does exist is the **grab call-out** (2026-08-01): a successful
  `TryGrab` fires `RpcSearcherTaken` to human searchers and `SearcherBot.OnTeammateTaken` to CPU
  ones, and REVIVE is now gated on *knowing* — a body within `DownSpotRange` (walked up on) or one a
  live call-out named. Before that, REVIVE scanned every player with no range limit, so a single grab
  summoned the entire CPU team from across the valley and handed the Yeti all of them around one
  body. The call-out grants knowledge, **not orders**: nothing tells a bot to go, and a fleeing or
  proof-carrying bot keeps doing that instead.
  > Still unmeasured: with four bots all told at once, whether *several* now converge anyway. That
  > is a tuning question (`TakenMemory`, and eventually whose job it is) rather than the structural
  > bug it replaced, and it is visible in the play-test log as REVIVE transitions.
- **FLEE always lights the torch and runs.** The actual stealth play — kill the light and break line
  of sight when it has *not yet* been seen — needs a "has it noticed me" estimate the shell lacks.
- **REVIVE ignores the incap timer and the Yeti standing over the body**, so it will happily walk into
  a guarded down and donate a second victim.
- **Specialties are dealt but never played.** Eli's flash, Sam's battery gift and Wren's marking are
  all reachable (`ServerBotFlash`, `ServerBotSetReviveTarget`, …) and none are used, so every bot
  currently plays the same generic searcher regardless of who it was dealt.

> Bug this surfaced, worth remembering: `ServerBotDrive` deliberately skipped writing `Battery` on the
> grounds that "the bot never lights a torch" — true while the only bot was the Yeti. CPU searchers do,
> and a stale battery makes them invisible to Sam's spare-battery scan, which skips anyone at ≥ 99%.
> When a second kind of bot appears, re-read the assumptions the first one baked in.

## [lookout] The lookout ladder + binoculars (no parity change)

The tower collider is **climbable** (`WorldData.Lookout`, `ClimbH = 9.5`), so the shared sim already
holds any player standing on the platform at `base + 9.5` and stops pushing them out of the footprint
up there — for every role. The only thing a searcher lacked was a way UP (Yeti scales it; searchers
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

## [solo-testing] Testing the whole game alone, on one PC

**Solo works by design.** `ServerStartMatch` picks Yeti from whoever opted in; with nobody opted
in it needs 2+ players, and with **one** player it assigns **no Yeti at all**. So:

| Setup | You are | Covers |
|---|---|---|
| 1 instance, lobby toggle **off** | a searcher, alone | world, trails, cave discovery, evidence + duffel, logs, sky/moon, HUD, perf |
| 1 instance, **"wants Yeti"** on | Yeti, alone | roar/leap/climb/cave travel, hair shedding, senses overlay |
| 2 instances | one of each | the interactions only: grab → spill, dazzle, filming, revive |

Most of what needs verifying is reachable **solo** — only grab/dazzle/film/revive need two.

**Two instances on one machine needs `runInBackground`.** Unity pauses an unfocused player, so the
moment you alt-tab, that instance stops ticking, FishNet stops sending and the connection times out
— which reads as "the build is broken" rather than as a setting. `GameSceneSetup` now enables it
(`EnableRunInBackground`). For the second instance, prefer **a standalone build over a second editor**:
it starts faster and you can shrink it, which matters on integrated graphics already pushing ~2,400
trees. Run it windowed and small:

```
Metoh.exe -screen-width 1280 -screen-height 720 -screen-fullscreen 0
```

Host in the editor, JOIN from the build at `127.0.0.1`. Only the focused window takes input, so drive
one, alt-tab, drive the other — fine for verifying an interaction, useless for testing a chase.

**`N` skips to the next night** (host only, F3 overlay). Verifying anything per-night — the moon's
phase and arc, the escalation table, Eli's flash and Sam's battery refilling at dusk — otherwise
means sitting through two full nights to reach night 3. It runs the clock out rather than
duplicating the rollover, so a skipped night is identical to an elapsed one.

## [dev-tools] Play-testing tools (F3 overlay + seed pin)

Two dev affordances exist specifically so a play-test produces *data* instead of impressions:

- **`F3` — diagnostics overlay** (`HPDebug`). Frame time + worst frame in the last second, render
  scale, the world seed, tree/trail/undergrowth/light counts, match phase, player count, tick rate.
  Number keys flip the **cost levers live, in the [perf] order**: `1` bloom, `2` prop lights,
  `3` undergrowth, `4` shadows. The point is that "it felt slow" doesn't distinguish four causes with
  four different fixes, and toggling beats rebuilding.
- **CPU Yeti levers, same overlay.** `O` pauses the bot where it stands (it keeps its state, stops
  moving, and cannot grab), `K` toggles its speed between `1.0x` and `0.5x`, and `P` cycles what it
  is allowed to know:

  | Mode | Fallback when nothing is perceived | Reads snow prints? |
  |---|---|---|
  | `HUNT` (default) | walks at a searcher's **true** position | yes |
  | `TRACK` | none — wanders | yes |
  | `RANDOM` | none — wanders | **no** |

  These exist because the two most common play-test reports about the bot are unfalsifiable at full
  speed. "It just beelines at me" is the `HUNT` fallback and is answered by pressing `P` — if it
  still finds you on `TRACK`, it tracked you. "It got stuck" needs the thing to hold still while you
  walk up to it, which is `O`. `K` separates *tracked* from merely *outran*.

  `YetiBot.AiMode` replaced the old `AggressiveProwl` bool (kept as a read-only alias). All three are
  **static**, so they apply to every bot and survive a reseed. `GameManager.ResetDevLevers` puts them
  back on the way to the **lobby** — both routes there, `ServerReturnToLobby` *and* `AbortToLobby` —
  rather than at match start, so a mode you set in the lobby survives into the match you set it for.
- **`J` — per-bot movement trace** (`[bot]` lines to the Console). **Off by default**: it fires per
  bot, and an editor `Debug.Log` captures a stack trace, so with five bots it was a steady tax on
  every frame. Turn it on only while chasing a stall.

  > The `[botAI]` guard trace is gone. It logged once a second forever ("remove once the bot is
  > confirmed hunting" — it is), and each bail reason now shows up in `DbgState` as `off: <reason>`,
  > which the overlay and the play-test log both already read. The `[look]` Console mirror is gone
  > for the same reason: it fired twice a second precisely while the overlay was measuring frame cost.
- **Seed pin** (title screen, under the dev persona strip). The forest is rolled per hosting session,
  so **a bug found in one map is otherwise unreproducible** — the map is gone when you restart. The
  overlay prints the live seed; paste it into the field to get that exact forest back. Blank/`0` =
  random. Ignored when joining, since the host owns the seed.

Note `4` writes `QualitySettings.shadowDistance`, the same knob `HPQuality` owns — re-applying
settings from the pause menu will overwrite it. Fine for a dev toggle, just don't read it as sticky.

## [play-log] The play-test log (`HPLog`)

`<project>/Logs/metoh-playtest.log`, with the run before it kept as `metoh-playtest.prev.log`. The
path is printed to the Console at startup and the file name is shown in the F3 footer.

It exists so play-test feedback can be *checked* rather than only believed. A report arrives as a
sentence about a symptom several seconds after its cause ("the Yeti got stuck when it let go"), and
answering it needs the timeline. Two line kinds share that timeline:

- `EVENT` — gameplay moments: `MATCH` (with the **seed**), `NIGHT`, `ROAR`, `GRAB`, `DROP`,
  `REVIVE`, `RECOVER`, `AI` (bot state *transitions*), `DEV` (an F3 lever moved).
- `UNITY` — everything that reached the Console, so an exception sits next to the gameplay that
  caused it. Errors and exceptions carry their stack; ordinary logs don't.

Three things about it are deliberate and worth not undoing:

- **It is not `Editor.log`.** That file is a build/import log with gameplay scattered through it, and
  the running editor holds it open. `HPLog` opens its own file with `FileShare.ReadWrite`, so it can
  be read live, from outside the editor, while a play-test is still running.
- **Leaving Play mode is not a quit.** `Application.quitting` never fires in the editor, which is
  where every play-test happens, so `Shutdown` is also hooked to `ExitingPlayMode` — otherwise the
  last second of a session, usually the interesting one, dies in the buffer.
- **`[look]` and `[bot]` are filtered out.** Both are throttled per-second heartbeats; unfiltered
  they are most of the file.

Buffered, flushed once a second and forced at night rollover and match end.

## [workflow] Workflow

- **Edit in the repo** (`unity/Assets/Metoh/`), then `robocopy /E` into
  `C:\Users\amedi\Metoh_port\Assets\Metoh`. Robocopy exit codes < 8 are success.
  **There are now THREE trees to sync, not one** — `Scripts/`, `Shaders/`, and `Sim/` (which comes
  from `csharp/Metoh.Sim`, not from `unity/`). A sync script that only copies `Scripts/` will
  silently leave the shader or a new sim file behind, and the failure shows up as a magenta sky or a
  missing type rather than as a copy error. There are now **four** shaders — `NightSky`, `Snowpack`
  (+ its `.hlsl`) and `TorchBeam`; a copy that misses one fails as an art problem, not an error.
  > **The live project path has moved before — check before you copy.** It is
  > `C:\Users\amedi\Metoh_port`, created fresh for the Metoh rebrand; `C:\Users\amedi\HollowPines`
  > no longer exists. The repo carries no `.meta` or `Library/`, so the live project is always a
  > separate tree, never a checkout.
- **Smoke-compile outside Unity** before handing over — a scratch csproj (netstandard2.1, LangVersion
  9, `ENABLE_INPUT_SYSTEM`, plus `UNITY_EDITOR` and `UnityEditor*.dll` for a second editor pass)
  against `Library/ScriptAssemblies/*.dll` and the Unity Managed DLLs. This has caught real errors
  repeatedly.
- **The editor log lives at `<project>/Logs/Editor.log`**, *not* the one in `AppData` (which goes
  stale with multiple editor instances). When something fails at runtime, read that file — the first
  error is usually several steps upstream of the reported one.
- **Re-run "Metoh → Set Up Game Scene (Mountain)"** whenever the scene gains a component or a
  spawnable prefab. The scene has no hand-made content; rebuilding it costs nothing.
- **Run "Metoh → Configure Render Pipeline" once per clone** (and after any URP upgrade). It adds the
  SSAO renderer feature and sets HDR colour grading, a 32³ LUT and 4 shadow cascades. These live on
  `.asset` files the repo does not track, so a fresh live project does not have them — and every one
  of them is the kind of setting whose absence looks like an art problem rather than a missing step.
  Idempotent, and it logs what it changed.
- `Metoh.Sim` collides with UnityEngine on `Collider`/`Collision` — qualify them.
- FishNet 4.7.2 does not compile on Unity 6000.5 unpatched; see [`../unity/fishnet-patches/`](../unity/fishnet-patches/README.md).

## [copy] Copy should read as capability, not as a stat block

Briefing cards derive every figure from the live constants so they can't drift — good — but raw
numbers are spec language, not player-facing copy. A card should say *"you can follow a trail long
after it's gone cold for everyone else"*, not *"clue window 22.5 s"*. Keep the derived values as the
source of truth and choose the phrasing from thresholds on them. **Only ever list abilities that
actually ship** — a card that teaches a control which does nothing is worse than a shorter card.
