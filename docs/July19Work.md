# Work order (July 19): port the MAP and AUDIO subsystems to Unity

You are picking up the Unity port of Hollow Pines. The world, movement, and the full match loop
already run in Unity (see "Current state" below). Your job today is the two missing subsystems the
owner prioritized: **the map overlay** and **the audio engine** — plus the persona effects that
depend on them (Wren's map senses + trail mark, Theo's hearing + roar-direction HUD, Wren's quiet
footsteps). The TypeScript web build is the executable spec: when this doc is ambiguous, open the
referenced TS file and mirror its behavior.

## Current state (read this, don't rediscover it)

- **Unity project (do NOT edit files there directly):** `C:\Users\amedi\HollowPines`
  — Unity 6000.5.4f1, URP, FishNet 4.7.2 (locally patched for Unity 6.5 — see
  [`unity/fishnet-patches/`](../unity/fishnet-patches/README.md); don't touch `Assets/FishNet`).
- **Repo canonical sources:** `unity/Assets/HollowPines/Scripts/{Game,Net,Player,Editor}` — edit
  HERE, then sync to the project:
  ```powershell
  robocopy "<repo>\unity\Assets\HollowPines" "C:\Users\amedi\HollowPines\Assets\HollowPines" /E
  ```
  `Assets/HollowPines/Sim` in the project is a copy of `csharp/HollowPines.Sim` (parity-tested;
  treat as read-only — if the sim needs a change, that's out of scope today).
- **What already works** (`Scripts/Game/`): `WorldBuilder` (whole forest rendered from the shared
  sim), `HPPlayer` (owner-simulated `StepPlayer` movement — client-auth NetworkTransform for now —
  first-person camera, abilities, SyncVars), `GameManager` (the whole `ForestRoom.ts` loop as a
  host tick: clock/escalation/clues/roar/grab/revive/dazzle/filming/win), `ClueMarker`, `HPHud`
  (IMGUI). **Added July 18 evening:** `TrailMark` (Wren's mark, minus its map dot), Theo's
  roar-direction HUD arrow, `PostFX` (URP Volume: bloom/vignette/grain/ACES + Bigfoot's local
  exposure lift), Bigfoot's **senses overlay** (`V` — screen-space searcher blobs + own scent
  trail in `HPHud.DrawSenses`), teammate nametags, head-bob, remote REC bead, and the screen
  tints (frozen/incap/dazzle/night-rollover fade). Don't redo any of that; the smoke csprojs
  already reference the URP runtime DLLs. **Also added July 18:** `TitleMenu` (R.E.P.O.-style
  title card: START NEW GAME / JOIN GAME / SETTINGS / QUIT — it now drives connections in the
  Forest scene; `LocalNetworkHud` remains only for the old R1 cube scene) and `HPSettings`
  (PlayerPrefs: player name → replicated via `ServerSetName`, mouse sensitivity, master volume).
  **Audio note for you: `HPSettings.MasterVolume` already drives `AudioListener.volume`, so your
  audio engine inherits the volume slider for free — don't add a second volume path.** Cursor
  rule: lobby leaves the mouse free (RMB captures for walking); the match-start teleport locks it.
  **Final July 18 batch:** dusk-briefing card on match start, Esc pause overlay (settings + leave;
  `HPHud.PauseOpen` — ability input is implicitly blocked because the cursor unlocks), frozen/incap
  HUD countdowns (`HPPlayer.StatusEndsIn` SyncVar, server-updated), Bigfoot fog trade-off
  (`WorldBuilder.FogMul` 1.35 when local Bigfoot), and contextual prompts (vault/climb/revive)
  computed straight from the shared sim in `HPHud.DrawPrompts`.

### STOPPED HERE (July 18, out of usage) — ✅ ALL TIED OFF (July 19)

The last batch (compiles green, synced to the project) added **key rebinding**
(`HPKeybinds.cs` — PlayerPrefs action→Key map; HPPlayer reads all actions through it; rebind UI
on the TitleMenu settings page, capture polled in `TitleMenu.Update`) and two new replicated
values that are **written by the server but not yet DISPLAYED**:
1. `HPPlayer.RoarReadyIn` — show on Bigfoot's HUD ("roar in Ns" / "ROAR READY — RMB") next to
   the charge-cooldown label in `HPHud.DrawBigfoot`.
2. `HPPlayer.ReviveProgress01` — show % in the revive banners (`DrawStatusOverlay`: the downed
   player's "being revived..." line and the reviver's "Reviving X..." line).
Also still pending, all tiny: Bigfoot grab/drop prompts in `DrawPrompts` (nearest frozen within
3.5 m → "LMB — grab {name}"; if any `p.GrabberObjectId == me.ObjectId` → "LMB — drop");
HUD help/prompt strings still hardcode key names — use `HPKeybinds.Label(...)`; and set
`PlayerSettings.productName = "Hollow Pines"` in `GameSceneSetup.SetUpScene`. Editor menu **Hollow Pines → Set Up Game Scene (Forest)** rebuilds
  `Assets/Scenes/Forest.unity` + prefabs from scratch — extend that script for anything new the
  scene needs, then re-run it.
- **Verify before handing to the editor** — smoke-compile outside Unity (this caught real errors
  last session). Generate a throwaway csproj (netstandard2.1, LangVersion 9, define
  `ENABLE_INPUT_SYSTEM`) that includes `Assets\HollowPines\{Sim,Scripts\Game,Scripts\Net,Scripts\Player}\*.cs`
  and references `C:\Users\amedi\HollowPines\Library\ScriptAssemblies\{FishNet.Runtime,Unity.InputSystem}.dll`
  + all `C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Data\Managed\UnityEngine\UnityEngine*.dll`,
  then `C:\Users\amedi\dotnet\dotnet.exe build`. Add `UNITY_EDITOR` + `UnityEditor*.dll` refs in a
  second pass to check `Scripts\Editor`. (dotnet/node are NOT on this shell's PATH — full paths.)

### Codebase gotchas (each cost time once)

- `HollowPines.Sim` collides with UnityEngine on `Collider`/`Collision` — qualify
  (`UnityEngine.Collider`, `HollowPines.Sim.Collision`). `Sim.X` resolves to the sim namespace
  from inside `HollowPines.Game`.
- FishNet 4 SyncVars are `public readonly SyncVar<T> X = new SyncVar<T>(default)`; server writes
  `X.Value`. RPCs callable by non-owners need `[ServerRpc(RequireOwnership = false)]` with a
  trailing `NetworkConnection sender = null`; host check = `sender.IsHost`.
- New Input System only in this project (plus "Both" after our fix): poll
  `Keyboard.current`/`Mouse.current` inside `#if ENABLE_INPUT_SYSTEM`. IMGUI (OnGUI) is fine.
- **Never** call Unity-6.5-removed APIs: `Object.GetInstanceID()`, implicit `Scene.handle`↔int.
- Sim yaw convention: radians, forward `(-sin yaw, -cos yaw)`; Unity body rotY = `yaw*Rad2Deg+180`;
  `HPPlayer.SimYawFromTransform()` converts back.
- New spawnable prefabs must be created by the editor setup script (like `ClueMarker`) so FishNet's
  `DefaultPrefabObjects` picks them up on refresh.

---

## Task A — Map overlay (+ pings, cave travel, Wren's mark)

**Spec:** `client/src/ui/MapView.ts` + the map wiring in `client/src/core/Game.ts`
(open them; mirror behavior). Summary of the rules:

- **`M` toggles** a fullscreen top-down overlay (releases/relocks the mouse cursor).
- Both roles always see: **self** (position + facing wedge), **camp**, **cave mouths**, the map
  bounds. Searchers additionally see: **teammates**, **pings**, **marks** — and the **recent clue
  trail ONLY while "in contact"**: Bigfoot within `hearRange` (35 m, × Theo `hearRangeMul` 1.8) of
  the local player, OR any clue within `evidenceSight` (18 m, × Wren `evidenceSightMul` 2.0).
  While in contact, show only clues younger than `clueWindow` (15 s, × Wren `clueWindowMul` 1.5).
  Constants: `client/src/config.ts` `MAP`; specialty getters already exist in
  `HollowPines.Sim.Specialties`. Contact logic runs client-side off replicated state (clue spawn
  times are known client-side — record `Time.time` in `ClueMarker.OnStartClient`).
- **Bigfoot cave fast-travel:** standing in a cave mouth (`Caves.NearestCaveIndex >= 0`), click a
  DIFFERENT cave on the map → `ServerRpc CaveTravel(int index)` on `HPPlayer` → server validates
  (role bigfoot, status active, playing, actually in some mouth, `index` valid + != current,
  cooldown `CaveRules.TravelCooldown`) → `TargetTeleport` to `Caves.CaveEmergePoint(dest)` (y from
  `world.GetHeight`). All helpers exist in `HollowPines.Sim.Caves` — this is the port of
  `ForestRoom.ts`'s `caveTravel` handler (read it).
- **Pings (searcher stakeout markers):** `Q` drops a ping at your feet; clicking the open map drops
  it at that spot (one live ping per searcher — re-ping moves it). Server rules from
  `ForestRoom.ts`: `PING_LIFETIME = 35`s, `MAX_PINGS = 12`, clamp x/z to ±400, searchers only,
  status active. Implement as a spawned `PingBeacon` NetworkObject (like `ClueMarker`): in-world
  visual (thin light pillar + small emissive diamond, see `client/src/world/PingField.ts` for the
  look) + a map dot. Server tracks per-owner to replace, and expires by lifetime.
- **Wren's trail mark — ✅ ALREADY DONE (July 18 evening), except the map dot.** `TrailMark.cs`
  (spawned stake + orange flag visual), `GameManager.TryMark` (tracking-only, 8 s cooldown, 50 s
  lifetime, 24 cap, reset/expiry wired), `HPPlayer` `T`-key + `ServerMark` RPC, prefab + `_markPrefab`
  ref in `GameSceneSetup` (note its new `CreateSimplePrefab<T>` helper — reuse it for `PingBeacon`).
  **Your only remaining mark work: draw `TrailMark` instances as dots on the map overlay** (find
  them via `Object.FindObjectsByType<TrailMark>` or keep a static registry like `HPPlayer.All`).

**Implementation shape (suggested):**
1. `MapView.cs` (MonoBehaviour on GameSystems, IMGUI like `HPHud`): background = a `Texture2D`
   (~256²) generated once from the sim — height-shaded greens, lake ellipse blue, matching the
   world's palette; draw layers with `GUI.DrawTexture` dots/labels. World→map:
   `u = (x + 400) / 800` (world is ±400). Facing wedge from local yaw. Handle the `M` toggle +
   cursor lock handoff with `HPPlayer` (add a public "map open" flag it checks before locking).
2. `PingBeacon.cs` + `TrailMark.cs` (tiny NetworkBehaviours, visuals in `OnStartClient` — copy the
   `ClueMarker` pattern) + prefabs in `GameSceneSetup` + serialized refs on `GameManager`.
3. `GameManager`: ping/mark/caveTravel server logic (dictionaries + expiry in the existing
   `OnTick`, reset in `ResetMatchState`) and public entry points called from `HPPlayer` ServerRpcs.
4. `HPHud`: hint line for `M`/`Q`/`T` keys.

## Task B — Audio engine (procedural, no asset files)

**Spec:** `client/src/core/AudioEngine.ts` — the web build synthesizes every cue procedurally
(there are NO recordings; `client/public/audio/` is an empty override hook). Port the same
philosophy: generate PCM at startup into `AudioClip`s (`AudioClip.Create` + `SetData`, 44.1 kHz
mono float[]). Port each cue's synthesis recipe from `buildCues()` — they're short DSP fills
(filtered noise bursts, sine/saw sweeps, envelopes); translating them faithfully is the bulk of
the task. The 16 cues:

`roar, footstep_soft, footstep_heavy, branch_snap, flashlight_click, ping_drop, video_captured,
freeze_sting, grab_impact, cave_whoosh, night_sting, victory, defeat, revive_channel,
revive_success, heartbeat` — plus two looping beds: **wind** (always, volume ~0.1–0.2) and
**creek** (positional loop at the lake's near edge), and the **heartbeat system** (repeating cue
whose rate/volume scale as Bigfoot gets close — searchers only; range × Theo's `hearRangeMul`).

**Implementation shape (suggested):**
1. `HPAudio.cs` (MonoBehaviour on GameSystems, static `Instance`): clip generation, an
   `AudioSource` pool; `PlayOnce(name, volume)` (2D) and `PlayAt(name, worldPos, volume,
   minDistance)` (3D: `spatialBlend=1`, logarithmic rolloff, ground-snap y via
   `WorldBuilder.World.GetHeight`). Master volume constant ~0.85.
2. **Trigger wiring** (each maps 1:1 to a web call site — grep `audio.play` in `client/src` for
   volumes/ranges):
   - Own footsteps: `HPPlayer.StepSim` already gets `StepResult {Moving, Sprinting}` — add a step
     timer (`Player.StepIntervalWalk/Sprint` from sim constants); heavy variant for Bigfoot.
     **Wren:** her own emitted footsteps × `footstepVolumeMul` 0.5.
   - Remote footsteps: distance moved per frame on non-owned `HPPlayer`s → positional steps.
   - `GameManager.RpcRoared(pos)` → `PlayAt("roar", pos, 0.95, 30)` for everyone EXCEPT a roaring
     Bigfoot owner (they get `PlayOnce("roar", 0.9)`). **Theo's roar-direction HUD arrow is
     ✅ ALREADY DONE (July 18 evening)** — `HPHud.NotifyRoar` stores the position and
     `DrawRoarDirection` renders a fading bearing arrow on a ring for 10 s (sound specialty only);
     you only add the SOUND of the roar.
   - Status changes on the LOCAL player (subscribe `Status.OnChange`): frozen → `freeze_sting`,
     incap → `grab_impact`. `VideosCaptured` increase → `video_captured`. Winner set → `victory`
     or `defeat` (by role). Night rollover → `night_sting`.
   - `ClueMarker` branch-type spawn → positional `branch_snap` (vol 0.5, minDist 14).
   - Flashlight toggle → `flashlight_click`; ping drop → `ping_drop`; being-revived channel loop →
     `revive_channel`, revive completes → `revive_success`; cave travel → `cave_whoosh`.
3. Keep it engine-side: no gameplay logic in `HPAudio` — it only renders sounds from events/SyncVar
   changes it observes.
4. *(Stretch, skip if time-boxed out):* override hook — load `.wav` files from
   `StreamingAssets/audio/<cue>.wav` over generated clips, mirroring the web manifest design.

## Acceptance checklist (test in-editor: Host + a built .exe as client 2)

- [ ] `M` opens the map for both roles; searcher sees teammates/pings/marks always, clue trail
      only when near Bigfoot or fresh evidence; Bigfoot sees self/camp/caves only.
- [ ] Bigfoot in a cave mouth clicks another cave on the map → teleports to its emerge point;
      spam-clicking respects the 2 s cooldown; searchers can't cave-travel.
- [ ] `Q` ping: appears in-world + on teammates' maps, moves on re-ping, gone after 35 s.
- [ ] Wren (and only Wren) can `T`-mark; teammates see it 50 s. *(In-world part shipped July 18 —
      verify it in passing; the map dot is yours.)*
- [ ] Roar is audibly positional (pan/attenuate by where Bigfoot is); Theo gets the 10 s HUD arrow
      *(arrow shipped July 18 — verify in passing; the audio is yours)*.
- [ ] Footsteps: your own + remote players'; Bigfoot's heavier; Wren's quieter.
- [ ] Freeze/grab stings, footage chime, night sting, victory/defeat, heartbeat ramps as Bigfoot
      approaches a searcher.
- [ ] Both smoke-compile passes green; editor console clean; `Set Up Game Scene (Forest)` re-run
      succeeds and Play → Host → START MATCH works end-to-end.

When done: update `unity/README.md`'s "Not yet ported" list, the R4/R5 rows in
`docs/UNITY_MIGRATION.md`, and the memory file's port-status note.

---

## ✅ COMPLETED — July 19

Both tasks are in, both smoke-compile passes are green, and the docs above are updated. What
landed, and where:

**Task A — map** (`Scripts/Game/MapView.cs`, new): IMGUI fullscreen overlay on `M`, background
baked once from the sim (height-shaded floor + camp clearing + lake ellipse, lazily on first open),
100 m grid, compass, landmark labels, heading triangle. Searchers get teammates (downed ones show
red), pings, Wren's mark diamonds, and the clue trail with the full contact gate
(`ClueVisionActive` — Theo's `hearRangeMul`, Wren's `evidenceSightMul`/`clueWindowMul`);
`ClueMarker` now records `Born` and keeps a static registry for the age window. Bigfoot gets
numbered cave buttons that light up only while standing in a mouth and off cooldown.
- **Pings:** `PingBeacon.cs` (beam + ground ring, per PingField.ts) + `GameManager.TryPing`/
  `RemovePing`/`ExpirePings` — one live ping per hunter, 35 s, 12 cap, clamped to ±400, spawned
  through the new `_pingPrefab`. `Q` pings at your feet; clicking the open map pings there.
- **Cave travel:** `GameManager.TryCaveTravel` — the ForestRoom handler verbatim (role/status/
  phase/valid index/different cave/in-a-mouth/2 s cooldown), emerge point from the shared sim,
  `TargetTeleport` closes the map on arrival.
- New keybinds `HPAction.Map` (M) and `HPAction.Ping` (Q), rebindable on the title screen.

**Task B — audio** (`Scripts/Game/HPAudio.cs`, new): all 16 cues ported sample-for-sample from
`buildCues()` into `AudioClip`s at startup, a 20-source pool (2D `PlayOnce`, 3D `PlayAt` with
logarithmic rolloff + ground snap), the gusting wind bed, a positional creek at the lake's
camp-facing shore, and the retriggered proximity heartbeat (40 m → 10 m, scaled by Theo's hearing).
Wired: own footsteps (sim cadence, crouch-slowed, Wren halved), remote positional footsteps
(stride 2.3 Bigfoot / 1.7 searcher), roar (own = 2D, everyone else = positional from Bigfoot's real
spot), branch snaps on clue spawn, flashlight click, ping/mark, cave whoosh, freeze/grab stings,
revive channel + success, video captured, night sting, victory/defeat. Master volume still flows
through `HPSettings` → `AudioListener.volume` — no second volume path.

**Before you play:** re-run **Hollow Pines → Set Up Game Scene (Forest)**. The scene setup now adds
the `MapView` component and creates the `PingBeacon` prefab + `GameManager._pingPrefab` reference;
without a rebuild, pings won't spawn and the map won't open.

## Play-test fixes — July 19 (first time this build actually ran)

The owner ran the game for the first time; these came out of it. **Every fix below is
compile-verified and synced, but only the first two have been confirmed in-game.**

**1. Scene NetworkObjects had no SceneId — nothing worked. FIXED.**
Symptom: `NetworkObject [GameManager] ... is expected to be initialized but was not`, then a
`NullReferenceException` on START MATCH, and an empty lobby player list. Cause: FishNet assigns
SceneIds from editor callbacks that are **throttled to once per 250 ms**, and `GameSceneSetup`
builds the entire scene and saves it in one synchronous burst — so the GameManager was serialized
with SceneId unset, never initialized, and never ran `OnStartServer` (hence no player spawning).
Fix: `GameSceneSetup.AssignSceneIds(scene)` forces `NetworkObject.CreateSceneId(scene, force: true)`
by reflection just before the save (it's `internal`, and FishNet.Runtime is its own assembly; the
reflection failure path logs the manual menu fallback). Verified in the saved scene:
`SceneId: 3919511503`. **If you ever build scene NetworkObjects from a script, this bites you.**

**2. START NEW GAME failed silently.** `TitleMenu` now checks `StartConnection()`'s bool return,
wraps both calls in try/catch, `Debug.LogException`s the stack, and shows a red reason on screen.
A menu button that does nothing is the worst possible failure mode — don't reintroduce it.

**3. A/D strafe was inverted — a HANDEDNESS bug, not a typo. Read this before touching movement.**
The shared sim is the web build's, whose (x,z) basis is Three.js **right-handed**:
`forward = (-sin yaw, -cos yaw)`, `right = (cos yaw, -sin yaw)`. Unity's XZ plane is **left-handed**,
so a body rotated to match the sim's forward (`yaw*Rad2Deg + 180`) has right = `(-cos yaw, sin yaw)`
— the exact negative. Forward matches, right is mirrored: W/S feel correct while A/D are swapped.
No yaw remapping can fix both at once; the mirror is inherent. Reconciled at three points, all
cross-referenced in comments:
- `HPPlayer.StepSim` — A/D fed **crossed** into `MoveInput` (the single input-boundary fix).
- `HPHud.DrawRoarDirection` — Theo's bearing arrow negates the sim-right term, or it points to
  the wrong side of the screen.
- `MapView.ToMap` — the map **mirrors its x axis** (`(_half - x)`), `DrawSelf` rotates by `+yaw`,
  and `HandleClicks` inverts x back. Without this, strafing right slid your dot left on the map.
Server-side aim cones (filming/dazzle) only ever dot with **forward**, so they were never affected.

**4. Flashlight was weaker than the web build's.** It was parented to the body, so it couldn't aim
up or down, and its cone was too narrow — Three's `angle: 0.5` is a HALF-angle in radians (~57°
full cone), not Unity's full `spotAngle`. Now: `spotAngle 57 / inner 24 / range 60 / intensity 11`,
and for the **owner** it reparents to the camera on the first frame the camera exists (it isn't
assigned yet when `BuildVisuals` runs), so it points exactly where you look. Remotes keep it on the
body. Intensity is eyeball-tuned for URP — expect to nudge it.

**5. "Very slow" was TWO problems — low frame rate AND input latency. Both addressed.**

*Latency:* `HPPlayer.StepSim` used to run a fixed 20 Hz step and render `Lerp(_prevPos, _curPos)`
between the last two sim states, which parks the camera a full step (50 ms) in the PAST and looks
steppy whenever fps dips. The web build never did this — its `LocalPlayer` steps once per frame
with the real frame delta. Now Unity does too (`Dt = min(Time.deltaTime, MaxStepDt)`, hitch-clamped);
`_acc`/`_prevPos`/`_curPos` are gone. `StepPlayer` is pure and takes dt as input, so variable-dt is
safe. **This reverts when N3 prediction lands** — FishNet's tick loop owns the cadence then.

*Frame rate:* **the owner's machine is an Intel Iris Plus 655 (integrated) driving 2560×1600.**
That is ~4 Mpixels of URP with bloom, grain and shadows. The web build never hit this because
`config.ts` caps the device pixel ratio; Unity renders native by default. Added `HPQuality.cs`
(the Unity counterpart of the web `QUALITY` block): URP `renderScale` (default **0.7** — fill rate
scales with the square, so ~half the pixels), MSAA off, shadow distance 50 → 35 m. Exposed as
`HPSettings.RenderScale` (persisted, 0.4–1.0) with a **live slider in the Esc pause menu**, so
quality/perf can be traded without a rebuild. Moon light `LightShadows.Soft` → `Hard`.
If it's still slow, the next levers in order: **bloom** in `PostFX` (full-screen, multi-pass —
the most expensive single effect), then the 8 realtime point lights in `WorldBuilder`, then
`WORLD.TreeCount`. Do NOT start with the IMGUI HUD — it allocates only a handful of `GUIStyle`s
per frame and is not the bottleneck.

**6. Briefing card was unclickable — the cursor was locked. FIXED.**
`TargetTeleport` locks the cursor on match start, but the dusk briefing has a GOT IT button, so the
player was trapped behind an un-clickable card. The teleport `TargetRpc` and the `matchPhase`
SyncVar are separate messages with **no guaranteed arrival order**, so a one-shot unlock could be
undone by a teleport landing a frame later — `DrawBriefing` therefore re-frees the cursor *every
frame* while open (self-healing beats ordering). Added `HPHud.BriefingOpen` (HPPlayer's
click-to-recapture and Esc handler both respect it), `HPHud.DismissBriefing()`, and Space/Enter/Esc
as keyboard dismissals so a stuck cursor can never trap you again.

**7. Roar could be spammed. FIXED (client-side gate).** The server was correctly refusing the extra
roars — but `HandleAbilities` played the roar SOUND locally on every right-click regardless, so it
*sounded* uncooldowned. The web build gates client-side too (`Game.tryRoar` checks `roarCooldown`).
Added `HPPlayer.CanRoar()` / `RoarCooldownLeft` = max(server `RoarReadyIn`, local `_roarEchoUntil`),
the echo covering the SyncVar round trip. **Lesson: any predicted feedback needs the same gate as
the request, or the server's authority is invisible to the player.**

**8. "Stamina drain isn't working" — it IS working; it was invisible. Behaviour matches the web
build; only the HUD changed.** Findings, for the record:
- **Bigfoot's charge (Shift) costs NO stamina** in the web build either — it's purely
  cooldown-gated (`CHARGE` in `config.ts`: speedMul 1.9, duration 1.2 s, cooldown 6 s). Bigfoot
  stamina is spent ONLY by **leap** (`LeapStaminaCost` 30) and **climbing**
  (`ClimbStaminaDrain` 22/s). So as Bigfoot, holding Shift correctly does nothing to the bar.
- **Searcher sprint drains correctly** (18/s, regen 12/s, `Exhausted` latch until stamina ≥ 35).
- The real gap was feedback: the Bigfoot HUD printed a charge label ONLY during cooldown, so a
  ready charge showed nothing at all. Now every ability states itself in both states —
  `CHARGING!` / `Charge: Ns` / `Charge ready (Shift)`, `Leap ready (Space) −30` / `Leap: low
  stamina`, `ROAR READY` / `Roar in Ns` / `Roar: DAZZLED` — and the searcher stamina bar turns
  amber while sprinting, red when exhausted.
- **Open design question for the owner:** should charge cost stamina? It doesn't today (web
  parity), which makes Bigfoot's bar nearly static in normal play. Changing it is a GDD decision,
  not a port fix — don't do it unilaterally.

**9. Per-persona dusk briefing cards (owner request).** Each searcher now gets their own card
matching the Bigfoot one: name, job title, a background line lifted from `docs/STORY.md`, a
**YOUR EDGE** perk list, and the shared **THE JOB** objective. `HPHud.CardFor(specialty)` builds
them and **derives every number from the live constants** (`Specialties` multipliers ×
`MapView.ClueWindow/HearRange/EvidenceSight`, `GameManager.FilmRange/ReviveSeconds` — the latter
four made `public` for this), so a tuning change can never leave the card lying. Bigfoot's card
also grew a story line and a bulleted night plan.
**Rule followed here: only SHIPPED abilities are listed.** Eli's camera flash and Sam's battery
hand-off are designed but unimplemented (`CHARACTER_FUNC_DEV.md` §4) — printing them would teach a
control that does nothing. Add them to the cards in the same commit that implements them. Mara is
honest about having no mechanical edge yet.

**10. Title screen was too dark and too far away (owner request).** Camera orbit tightened from
26 m radius / 6.5 m height to **12 m / 3.2 m** around the campfire, so the fire and RV fill the
frame instead of reading as an empty field. Brightness is a new `WorldBuilder.TitleMode` applied
*inside* `SetTimeOfDay` (ambient ×2.6, moon ×3.2, fog ×0.35, sky ×1.7) — it must compose with the
palette rather than assign after it, because `GameManager.Update` drives the clock every frame
whether or not anyone is connected, and would otherwise overwrite it. Plus `PostFX.SetTitleBrightness`
(+1.15 EV, vignette 0.33 → 0.18). **`PostFX` now composes title-mode and Bigfoot-vision exposure in
`ApplyExposure()` instead of each calling `postExposure.Override` directly** — they used to clobber
each other. `TitleMenu.SetTitleLighting` toggles both on connect/disconnect.

**11. Cave fast-travel looked "lost in the transition" — it was INVISIBLE, not missing.** The
server handler, the map buttons and the cooldown were all wired; what the port dropped was the web
build's on-screen cue (`Game.ts` shows "Press M — choose a cave to travel to" while `caveReady`).
With no prompt, nothing tells Bigfoot a mouth is usable, so the whole network is undiscoverable.
`HPHud.DrawPrompts` now shows `[M] — travel to another cave` (or the recharging countdown) whenever
Bigfoot stands in a mouth. Also made `HPPlayer.RequestCaveTravel` mirror the server's validation
locally, so a request the host was always going to refuse no longer burns the 2 s local cooldown.
**Lesson (third time today — see #2 and #8): a ported mechanic isn't ported until its FEEDBACK is.**

**12. The three deferred persona abilities were built for real** (owner call — chose the full
evidence system over the cheaper options). Full write-up is in
**`docs/CHARACTER_FUNC_DEV.md` §8**; the short version:
- **Physical evidence** (`EvidenceMarker.cs`) — casts and hair shed along Bigfoot's trail, collected
  via a stationary hold-channel. **The win condition is now PROOF = footage + evidence.** Filming
  stays fast/fragile (a grab still erases all footage); evidence is slow but permanent. Bigfoot
  destroys uncollected evidence by walking back over its own trail.
- **Mara** finally has her specialty: she collects in half the time.
- **Eli's flash** (`G`) — 3 s dazzle in a tight aimed cone, 1/night, and it reveals him to Bigfoot
  for 5 s (`HPHud.DrawRevealed`, independent of the senses overlay).
- **Sam's spare battery** (hold interact) — +50 battery, 1/night. Note the `TargetGrantBattery`
  round-trip: this is the only place battery increases, and `ServerVitals` would otherwise undo it.
- Revive / collect / battery share one key; `HPPlayer.HoldActionTarget()` resolves exactly one and
  supplies the prompt text, so the HUD and the input can't disagree.
- **Balance is untested** — `EvidenceChance` (0.13), `EvidenceCollectSeconds` (4) and
  `EvidenceDestroyRadius` (2.2) are first guesses. Expect to retune after one real playtest.

**13. Briefing-card copy is an OPEN UX task** (owner note, logged in `CHARACTER_FUNC_DEV.md` §5b).
Cards currently derive every figure from live constants so they can't drift — good — but raw
numbers read as a stat block. They should say *"you can follow a trail long after it's gone cold
for everyone else"*, not *"clue window 22.5 s"*. Keep the derived values as the source of truth and
choose phrasing from thresholds on them. Applies to prompts and the `[H]` card too.

**14. Evidence reworked twice more after owner review — read `CHARACTER_FUNC_DEV.md` §8, not the
earlier bullets here.** Two corrections worth carrying forward:
- Bigfoot does **not shed casts** (a cast is made by a person from a track). It leaves **castable
  prints**; only Mara can work one; only 4 are live at a time; Bigfoot ruins them by treading on them.
- Proof is now **carried, then stored in a duffel by the RV** — an extraction loop. Carried proof is
  worth nothing and dies with you; **stored proof is permanent and Bigfoot cannot touch the bag.**
  A grab destroys only the victim's carry. Win = STORED >= 3.

**15. Caves rebuilt** — a mound cut into the hillside with a dark recess, an overhanging brow,
flanking pillars and spill rubble, facing map centre. The old three-boulders-in-a-row read as
scenery, which mattered because the entire fast-travel network hangs off recognising them.

**16. "Cannot start a new game" — prefab AssetPathHashes were 0. FIXED. This is the SECOND
instance of one root cause; treat it as a rule.**

Symptom: START NEW GAME showed `NullReferenceException` (from the error surface added in #2 —
which is the only reason this was diagnosable at all). The NRE was a red herring: the real error
was **earlier in the log**, at editor start:

```
ArgumentException: An item with the same key has already been added. Key: 0
  at FishNet.Managing.Object.DefaultPrefabObjects.Sort()
  at FishNet.Managing.NetworkManager.Awake()
```

Chain: `Sort()` builds a `Dictionary<ulong, NetworkObject>` keyed by `AssetPathHash`. **Two prefabs
created by `GameSceneSetup` still had hash 0**, so the second one collided → `NetworkManager.Awake()`
threw → the NetworkManager never registered → `InstanceFinder.NetworkManager` was null → the menu's
`ServerManager.StartConnection()` NRE'd. *The reported exception was three steps downstream of the
cause.*

**The rule:** FishNet stamps its identity metadata from **editor callbacks that assume a human pace**
— `AssetPathHash` for prefabs (prefab-import postprocessor) and `SceneId` for scene objects
(throttled to once per 250 ms). `GameSceneSetup` creates and consumes everything in one synchronous
run, so **both must be stamped explicitly**. There are now two methods doing exactly that:
`StampAssetPathHashes(prefabs)` (public API: `NetworkObject.SetAssetPathHash` +
`Hashing.GetStableHashU64`, mirroring `DefaultPrefabObjects.SetAssetPathHashes`) and
`AssignSceneIds(scene)` (reflection, see #1). **Any new prefab the setup script creates must go
through the array passed to `StampAssetPathHashes`.**

Manual escape hatch if this ever recurs: **Tools → Fish-Networking → Refresh Default Prefabs**.

*(Note: the smoke csprojs now also reference `GameKit.Dependencies.dll` for `GetStableHashU64`.)*

**17. Lobby cursor trapped the player. FIXED.** Right-click in the camp lobby *locked* the mouse, so
the lobby panel (START MATCH / LEAVE) became unclickable and the only way out was Esc — which opens
the pause menu, not an obvious escape. Now the lobby uses **hold-RMB-to-look, release-to-click**;
press-to-lock is kept only during an actual match. Rule of thumb: never take the pointer away on a
*press* while UI the player must click is on screen.

**18. Bigfoot's charge burst REMOVED; Bigfoot now sprints (owner call).** Roar plus a cooldown
speed ability was judged an unnecessary buff on top of the freeze. Bigfoot's `Sprint` is simply
enabled in `StepSim` and the sim's existing `BigfootSpeedMul` (1.22) makes it outrun a searcher's
sprint outright — **8.5 → ~10.4 m/s vs the searcher's 8.5**, with no sim change (the C# sim stays
parity-locked). Deleted: `ChargeSpeedMul/Duration/Cooldown`, `_chargeUntil/_chargeReadyAt`,
`Charging`, `ChargeReadyIn`, the charge HUD line and the charge branch in `CurrentModifiers`.
**Side benefit:** sprinting spends stamina, so Bigfoot's stamina bar finally means something — the
gap that made stamina look broken back in #8. Chases are now a resource decision, not a cooldown.
*If Bigfoot feels too slow/fast, the lever is `BigfootSpeedMul` in the shared sim (careful: it is
parity-tested against the TS build) or a Bigfoot-only `SpeedMul` in `CurrentModifiers` (safer).*

**19. Title subtitle rewritten.** "five went looking for proof — one of them is being hunted" read as
though a *searcher* were the quarry, inverting the premise. Now: **"five went looking for proof —
something in the pines was looking back."**

**20. The camp lobby keeps the title card's moving camera (owner request).** Clicking START NEW GAME
used to cut from a drifting cinematic to a motionless first-person shot. `TitleMenu.OrbitCamp(cam)`
is now public and `HPPlayer.UpdateLobbyCinematic` runs it during the lobby phase, un-parenting the
camera for the orbit and re-parenting on the way back. **Holding right-mouse takes control** (the
same gesture that grabs the pointer, per #17) and releasing returns the shot to the cinematic; match
start re-parents permanently. The owner loop early-outs while the cinematic drives, so look/move/
abilities can't fire behind it.

**21. IMGUI panels were fixed-size and clipped on a small window.** Not an editor artifact — the
briefing card was a hard-coded 560×400 box, so anything shorter than ~460 px cut it off (the owner's
Game view was 1133×528). All the panels now clamp to the window and centre: the briefing card also
**scrolls** its content with the GOT IT row pinned outside the scroll, so no line is ever
unreachable. Lobby and pause panels clamped the same way. **Rule: no fixed-pixel IMGUI panel without
a `Mathf.Min(..., Screen.height - margin)` clamp** — R5's real UI should use proper anchoring.

**22. Lobby right-click "went static" — correct behaviour, unreadable presentation.** Not an IMGUI
artifact: holding RMB hands over first-person control, and a first-person view of someone standing
still is pixel-for-pixel indistinguishable from a frozen image, so the hand-off looked like a
freeze. Fixed by making the transition legible rather than changing the rule:
- `HPPlayer._lobbyCam01` **blends** between the orbit pose and the first-person pose (SmoothStep,
  ~0.4 s) instead of cutting. The motion itself is the feedback that control changed hands.
- A **crosshair + "WASD to walk · release right-mouse to return to the view"** appear while control
  is held, so the state is stated outright.

Two ordering traps this created, both fixed — worth knowing if this code is touched again:
1. The blend drives the camera in **world space while un-parented**, but the end of `OwnerUpdate`
   writes `localPosition/localRotation`. Un-parented, local space *is* world space, so that write
   flung the camera to the origin. It is now guarded on `_cam.transform.parent == transform`.
2. Calling `HandleLook()` inside the cinematic *and* falling through to the normal path applied the
   mouse delta twice (doubled sensitivity). The cinematic now only reports whether it is driving.

**23. MAP: the baked terrain was mirrored relative to everything drawn on it. FIXED — this one
mattered.** Spotted in a screenshot: the lake ellipse sat left of centre while its own "LAKE" label
sat the *same distance right* of centre — an exact mirror, not drift. `ToMap` mirrors x on purpose
(screen-right = world −X, matching Unity's left-handed frame — the same handedness fact behind the
strafe fix in #3), but `EnsureTextures` baked the background **un-mirrored**. So the terrain was
flipped under every label, cave button, ping, mark and player dot. **A player navigating by that map
would have walked the wrong way.** Fix: bake with the column index flipped too
(`px[(BgRes-1-j) * BgRes + (BgRes-1-i)]`), so the texture and the vector layer share one orientation.
*Whenever the map's x convention is touched, BOTH the bake and `ToMap` have to move together —
plus `HandleClicks`, which inverts it back.*

**24. Map title overlapped the HUD's top bar.** The frame was centred on the whole screen, so on a
short window its title ran through the night/proof bar. It now centres in the space left after
reserving 62 px at top (HUD bar + title) and 46 px at bottom (hint line).

**25. "You flooded half the map" — the lake was a FLAT disc over 14 m of rolling terrain. FIXED.**
`WorldData.Lake` is 120 m × 90 m and `World.HillHeight` is **14**, but `BuildLake` drew one flat
ellipse at the height of the lake's *centre* (and at `+0.15`, where the web build uses `−0.25`). Any
ground lower than that centre ended up **underneath the water plane** — players standing 80–90 m
outside the lake were rendered below it, which reads as the whole map being submerged, with trees
sticking up through the "flood".

Terrain can't be carved to fit the water: `Terrain.MakeTerrain` is the parity-locked shared sim and
players stand on its analytic height, so a visual-only basin would leave them walking on invisible
ground above the surface. Instead the lake is now a **terrain-conforming sheet** (12 rings × 44
segments, `y = GetHeight(x,z) + lerp(0.16, 0.02, t)` so it feathers at the rim). It covers exactly
the ellipse `Collision.LakeDepth` slows you in, can never rise above the land, and matches what the
movement rules already say: you *wade*, you never swim.

**Trees standing in the lake are the sim's doing, not the renderer's.** `WorldData.BuildColliders`
skips only the camp clearing and cave mouths — there is no lake exclusion — and `BuildForest`
mirrors that RNG stream exactly. Skipping them visually would leave invisible tree colliders in the
water, which is worse. Fixing it properly means adding a lake skip to the **shared sim on both the
C# and TS sides** and re-running the parity check; with conforming water they now read as a flooded
stand rather than trees growing out of a floating plane, so this was left alone deliberately.

**Confirmed working in this pass:** the map's x-mirror fix (#23) — the LAKE label now sits on the
lake — and the player's own heading marker draws and tracks correctly.

**26. Map is now R.E.P.O.-style: open it and keep walking (owner request).** Default key **M → Tab**,
and `HPPlayer.StepSim`'s input gate became `cursorLocked || (MapView.IsOpen && !PauseOpen)` so WASD,
jump, sprint and crouch all keep working with the map up. Mouse LOOK stays gated on the locked
cursor — the map frees the pointer so caves and ping placement stay clickable — so you walk your
current heading while reading. The scrim dropped to 0.32 alpha and the frame to 58% of screen width
so peripheral vision survives; a game about being hunted must never freeze you to read a map.
Abilities and contextual prompts remain disabled while it's open (they gate on the real cursor lock).
**Pause is now the only overlay that fully stops the player.**

**Still not verified in-editor:** all audio, pings, cave travel, the casting loop and the duffel.
**Re-run "Set Up Game Scene (Forest)" before playing** — the scene needs the new `EvidenceMarker`
prefab and `GameManager._evidencePrefab`, or evidence will silently never spawn.
