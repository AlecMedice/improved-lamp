# Aug 15 — the open bug list

A full line-by-line read of the Unity build (`unity/Assets/Metoh/`, the four shaders, and the shared
sim in `csharp/Metoh.Sim`) on 2026-08-15. The **critical and high** findings were fixed the same day
and are listed at the bottom for reference only; everything above that line is **still open**.

Nothing here was found by playing — this was a read, then a headless compile to prove the fixes
build ([workflow]). Where a finding says "reproduced", it means exactly that and says how.

Coverage note: pure geometry and DSP bodies were sampled rather than read end to end — ProcTex's
noise synthesis, HPAudio's remaining cue recipes, WorldBuilder's camp/hut/brazier construction,
MeshUtil's remaining primitives, the Snowpack/NightSky pass bodies, and `csharp/Parity`. They contain
no control flow that the sampled parts didn't already cover, but they are not audited.

---

## Correctness

### [medium] The revive bar jumps down the instant the channel breaks
`GameManager.UpdateRevives` accrues progress against `ReviveSeconds * ReviveMul(reviver)` but the
decay branch publishes `v / ReviveSeconds` — the *base* four seconds. For Sam (`ReviveMul` 0.6, so
`needed` is 2.4 s) a bar sitting at 96% snaps to 57% the moment you stop holding, then decays from
there. Divide by the same `needed`, or store it alongside the progress.

`GameManager.cs`, in the `decayed` loop.

### [medium] Two searchers on one hair sample collect it at double rate
`UpdateCasting` walks `_collectIntent` and each entry does `prog = _collectProgress[nob] + dt` and
writes it straight back, so the second collector reads the value the first just wrote and adds `dt`
again. Casting is specialty-gated to one person so it never shows there; **hair is open to anyone**,
which is the whole reason hair exists. Either advance each evidence object once per tick, or decide
that co-operative collection is a feature and say so in the comment.

### [medium] `CollectProgress01` freezes instead of bleeding off
Set only on the advancing branch of `UpdateCasting`. A searcher holding the key just outside
`CastRadius` sees the bar stuck at its last value while the server quietly decays the real progress;
it only snaps to zero when the intent is dropped entirely. The bar is lying about a mechanic whose
whole design is "it bleeds off, it doesn't reset".

### [medium] The grab swing plays while dazzled
`HPPlayer.HandleAbilities` gates the roar client-side on `CanRoar()` — which checks `Dazzled` —
precisely because of the [feedback] lesson about predicted feedback needing the request's gate. The
grab immediately below it fires `ServerGrab()` and `PlayOnce(GrabImpact)` unconditionally, and
`GameManager.TryGrab` refuses while dazzled. So a dazzled Yeti hears itself swing at nothing. Same
bug, same file, ten lines apart.

### [medium] F3's `[4] shadows` lever writes a knob URP does not read
`HPDebug.SetShadows` sets `QualitySettings.shadowDistance`; `HPQuality` owns `urp.shadowDistance`,
and under URP those are not the same property. Even where it does land, toggling off and back on
leaves the distance at a hard-coded 35 m regardless of whether the tier wants 55 (high) or 30 (low),
so the lever silently retunes the thing it was only supposed to toggle. Route it through
`HPQuality` and let the tier decide the value.

### [medium] `RenderPipelineSetup` reports "nothing to change" on the run that fixes SSAO
`EnsureSsao` returns 1 only when it **creates** the feature. `TuneSsao` runs either way and is not
counted, so when the renderer already carries the URP template's own SSAO — which the file's header
states is the live project's actual condition, with the wrong intensity, falloff and AfterOpaque
setting — the one run that repairs it prints *"Already configured — nothing to change"*. That is the
misreport this file was written to prevent, one level up.

### [low] `HPHud`'s "keep holding" banner fires for bystanders
`DrawStatusOverlay` gates the *"Reviving X… keep holding"* line on `p.BeingRevived.Value`, which is
true when **anyone** is reviving them. A second searcher standing within 4 m of a rescue they are
not performing is told to keep holding a key they are not pressing.

### [low] `DrawPrompts` dereferences `WorldBuilder.World` unchecked
`HPHud.DrawPrompts` reads `world.Caves`, `world.Climbables`, `world.FallenLogs` and `world.GetHeight`
straight off `WorldBuilder.World` with no null guard, twenty lines after `DrawSearcher` correctly
checks `w != null` for the same static. In practice `WorldBuilder.Awake` has always run first; it is
an inconsistency waiting for the first code path that doesn't.

### [low] The map opens on top of the dusk briefing
`MapView.Update` gates on `HPHud.PauseOpen` but not `HPHud.BriefingOpen`, so Tab during the briefing
opens the map underneath it and the two fight over the cursor — `DrawBriefing` re-frees it every
frame, `MapView.Close` re-locks it.

### [low] Missing null guards inconsistent with the rest of the file
`HPPlayer.FindByObjectId` and `HPPlayer.NearestIncapTeammate` iterate `All` without the `p == null`
check that every other loop over that list has. A destroyed-but-still-listed entry throws
`MissingReferenceException` rather than being skipped.

### [low] `YetiBot`'s dazzle guard is always true, and its first break-off runs from camp
`Time.time >= _breakOffUntil - DazzleBreakSeconds` is satisfied whenever `_breakOffUntil` is within
one break-off window of now, which it always is — so the escape direction is recomputed on every
decide tick instead of once per dazzle. Separately, with nothing sensed yet `_lastKnown` is still
`Vector3.zero`, so the very first dazzle of a match sends the Yeti fleeing directly away from the
world origin — i.e. away from camp — rather than away from the beam.

### [low] `Weather` reads the detail tier once, at construction
`BuildSnow`, `BuildDrift` and `AttachMotes` sample `HPQuality.HighDetail` when they build. The pause
menu's quality slider moves render scale, shadows and AA live but not particle budgets. And
`Weather.Awake` is driven from `WorldBuilder.Awake`, which may run before `HPSettings.Load()`, in
which case it reads the `true` default rather than the saved tier.

### [low] Two dead expressions in `Weather`
`AttachBreath` has `yeti ? 0.02f : 0.02f` (fixed in passing on 2026-08-15 — noted here only because
it points at the second one). `AttachMotes` sets `shape.length` on a `ParticleSystemShapeType.Cone`,
where it is ignored — `ConeVolume` is the shape that uses length, so the motes fill a flat disc at
the aperture rather than the length of the beam.

### [low] The pacing governor counts requests, not landed grabs
`YetiBot.TryAbilities` sets `_backoffUntil` whenever a frozen searcher is inside `GrabRadius`,
regardless of whether `GameManager.TryGrab` accepted it — and `TryAbilities` runs every frame, not
per decide tick. A refused grab (already hauling someone, dazzled) still buys the searchers 18 s of
eased pressure.

### [low] `HPKeybinds` allows two actions on one key
No duplicate detection in `UpdateCapture`, so binding sprint to `E` leaves revive there too and both
fire. `ResetDefaults` also calls `PlayerPrefs.Save()` once per action rather than once.

---

## Performance

### [medium] `LineBlocked` and `ResolveCollision` are O(2,400) with no broad phase
`Metoh.Sim.Collision` walks every collider in the world on every call, and there are ~2,400.
`ResolveCollision` runs once per `StepPlayer` per player per frame. `LineBlocked` runs from
`BotPerception.Blocked` (five brains × ~six candidates at 10 Hz) and twice per hunter per 20 Hz tick
from `GameManager.ConeVisible` (filming and dazzle). That is on the order of a million circle tests a
second on the host, all match.

The sim is parity-locked so this cannot be fixed *through* it — but a Unity-side uniform grid built
once per world, handing each query a pre-filtered slice of the same collider list, is a fix *around*
it, which is what [parity-lock] asks for. The sim keeps its exact arithmetic; it just sees fewer
candidates.

### [medium] Per-tick allocations in the authoritative loop
`GameManager.LivePlayers()` allocates a fresh `List` and is called about four times per tick (the
tick body, `UpdateCasting`, `UpdateRevives`, `UpdateStatuses`) plus two `FindAll`s; `UpdateRevives`
allocates a `HashSet` and two `List`s per tick; `UpdateCasting` the same plus one more inside its
finished loop. At 20 Hz that is roughly 200 short-lived collections a second for the whole match, on
the machine [perf] is written about. A reused scratch list on the manager removes essentially all of
it.

### [medium] IMGUI style churn
32 `new GUIStyle(GUI.skin.label)` inside `HPHud.OnGUI` and 6 in `MapView`, several of them **inside
per-item loops** (`HPHud.DrawRevealed`, `MapView.DrawProofPiles`). OnGUI runs at least twice a frame,
so that is 60–80 style allocations per frame for as long as the HUD is up. `HPDebug.EnsureStyles` is
the pattern to copy.

### [low] `MapView.InvalidateBackground()` drops a texture without destroying it
Sets `_bg = null` and leaves the 256×256 RGBA32 `Texture2D` behind — 256 KB per reseed.

### [low] Shared per-variant meshes are invisible to the material sweep
`BuildForest`'s `trunk` / `crowns[]` / `crownsStunted[]` and `BuildUndergrowth`'s `drift` / `scree` /
`pole` / `flag` are built, combined into chunk meshes, and then referenced by nothing — so
`ReleaseWorldMaterials`, which finds things by walking renderers, cannot see them. Thirteen meshes
per reseed. The ~700 **combined chunk** meshes are the bigger half of the same gap: they are on
renderers, so their materials are swept, but nothing sweeps a mesh. This is the leak [bodies] records
as known and deliberately unfixed; it is worth re-costing now that the number is 700 rather than a
few dozen icicles.

### [low] `SearcherBot` samples speeds it never reads
`BotPerception.SampleSpeeds` maintains per-player speed history for the hearing model. Searchers have
no hearing model and never call `SpeedOf`, so that is five per-frame dictionary sweeps over every
player, for nothing. Left in place on 2026-08-15 because the ordering contract belongs to the method
rather than to its current callers — but it should either be used or dropped.

### [low] `BotPerception`'s speed dictionaries never evict
`_lastPos` and `_speed` are keyed by `HPPlayer` and nothing removes an entry when a player despawns,
so each brain holds a strong managed reference to every player object the session has ever had.

### [low] `HPSettings.Apply()` runs twice a frame from the pause menu
`HPHud.DrawPause` calls it unconditionally in OnGUI, and `Apply` re-writes the URP render scale,
re-asserts shadow quality and re-runs `PostFX.ApplyCameraSettings()` (which does a `Camera.main`
lookup and a `GetUniversalAdditionalCameraData`). Only needed when a slider actually moved.

---

## UI / polish

### [low] The lobby's "I want to play Yeti" toggle ignores the replicated value
`HPHud.DrawLobby` starts `_wantsYeti` at `false` regardless of `me.WantsYeti.Value`, which the solo
paths set server-side and `ServerReturnToLobby` never clears. The box can disagree with what the
match will actually deal you.

### [low] Fixed-pixel IMGUI that doesn't clamp
Against the [imgui-clamp] rule: `HPHud` line ~351 (a 580 px top bar), ~1265 (a 560 px help card),
~1141 (the prompt box), plus several `Screen.height - N` offsets that go negative on a short window.
The briefing, pause, lobby and intermission panels were all clamped properly; these were missed.
(The title screen's own panels were clamped in the same-day menu rewrite; `HPHud` is what is left.)

### [low] `UtilityChooser`'s histogram credits startup time to a sentinel
`Chosen` starts as `"—"` with `_chosenAt` at 0, so the first real transition banks everything since
scene load into `"—"`. The histogram is the instrument the whole AI rewrite is tuned against
(`LogBotSummary`), so its denominator being inflated by lobby time is worth a two-line fix.

### [low] Cosmetic: two statements at column 0
`GameSceneSetup.SetUpScene` lines 43 and 95 (`SetInputHandlingToBoth();`, `AssignDefaultPrefabObjects(nm);`).

---

## Toolchain

### [medium, observed once] A headless rebuild can compile cleanly and never run `SetUpScene`
Running the [workflow] command:

```powershell
& '...\Unity.exe' -batchmode -quit -nographics -projectPath 'C:\Users\amedi\Metoh_port' `
    -logFile <log> -executeMethod Metoh.EditorTools.GameSceneSetup.SetUpScene
```

one run compiled the changed scripts, entered `Begin MonoManager ReloadAssembly`, and **exited
without ever invoking `SetUpScene`** — the log simply stops mid-reload. Re-running the identical
command completed normally and logged `[GameSceneSetup] Mountain scene wired`.

**This is a trap for exactly the check the workflow prescribes.** The prescribed verification is
`grep -c "error CS" <log>` and `grep -i "Shader error"`, and on that run **both were 0** — because
compilation genuinely had succeeded. So the command reported a clean build while the scene was never
rebuilt and the shaders were never re-imported. Verifying a change that way gets a green light from a
run that did half the job.

**Observed once**, on 2026-08-15, on the run that recompiled that day's whole change set. A later
rebuild in the same session — one new file plus edits — did *not* reproduce it: both passes wired. So
this is not simply "the first run after any change", and the trigger is not pinned down; a large
recompile, or a timing race in the reload, are both consistent with one observation and neither is
established.

Cheap mitigation regardless of cause: **require the `Mountain scene wired` line, not just a zero
error count**, and re-run if it is absent. [workflow] currently tells you to grep for errors only,
which is what made this invisible. A real fix would be `SetUpScene` detecting a post-compile domain
reload and re-scheduling itself.

Not, on current evidence, the owner's 2026-08-15 report that *"every time it rebuilds the scene, the
first loading screen is always broken"* — that one is about **Play mode in the editor**, which this
does not cover, and it is described as happening *every* time. Still open; see below.

### [diagnosed + fixed] "The first loading screen is always broken" = async shader compilation
Owner report, 2026-08-15: *"every time it rebuilds the scene the first loading screen is always
broken"*, with a screenshot of the title card carrying **rectangular black and stale tiles** across
the sky and the right-hand treeline; then, minutes later, *"I just spawned in as solo yeti and all
the trees were neon blue until I alt-tabbed away"*.

**One cause for both.** With the editor's **asynchronous shader compilation** on (the Unity default),
a draw whose shader variant is not compiled yet does not wait. Unity substitutes a flat placeholder
colour — the neon blue — or, for something like the skybox, skips the draw entirely, leaving whatever
was already in the framebuffer. That is the blocky black-and-stale regions: not corruption, just
areas nothing wrote that frame. Compilation completes in the background, so it always heals itself
after a few seconds or an alt-tab, which is exactly what makes it read as a flaky renderer rather
than as a build step. It fires on the first load after any shader reimport — i.e. every scene
rebuild, and every time one of the four Metoh shaders is touched.

The three candidate explanations were separated by evidence rather than argument, from
`Metoh_port/Logs/Editor.log` after the reported session:

| Theory | Verdict |
|---|---|
| Stale back-buffer during a long `WorldBuilder.Awake` | **Ruled out.** `[boot] total 719 ms` / `808 ms` — the pre-first-frame window is under a second. |
| A silent `Shader.Find` fallback (flat sky, rigid forest) | **Ruled out.** `BootReport` was added the same day to make exactly this loud, and it printed nothing. |
| Async shader compilation | **Consistent with all of it** — per-object placeholder colour, per-region skipped draws, self-healing on a delay, triggered by reimport. |

**Fixed** by `RenderPipelineSetup.DisableAsyncShaderCompilation`, asserted from both
`Metoh → Configure Render Pipeline` and `Metoh → Set Up Game Scene` (the latter being the command
that causes the reimport, and the one that actually gets run). The editor now STALLS while variants
compile instead of drawing garbage. Same wait either way — but the screen stays honest, and a stall
is unambiguous where a neon tree is indistinguishable from a real shader bug. Editor-only; a built
player compiles ahead and was never affected.

Worth knowing for later: the TreeSway fix on the same day added the full URP keyword block to that
shader, which multiplies its variant count considerably. That is the right trade (the forest had no
soft shadows, cookies, AO or additional lights without it), but it makes the first compile after a
shader edit noticeably slower, and it is now a stall you can see rather than garbage you can't
diagnose. If iterating on shaders becomes painful, async can be flipped back on in
Edit → Preferences → Asset Pipeline — nothing depends on it being off. A `ShaderVariantCollection`
warmup is the real answer if this ever matters for a shipped build.

---

## Fixed 2026-08-15 (listed for reference, not open)

Verified with a headless rebuild: 0 `error CS`, 0 shader/parse errors, scene wired.

| | What it was |
|---|---|
| **Weather died on the first host** | `Weather` parented its emitters to the WorldBuilder transform, and `Rebuild()` destroys that transform's children. The host rolls a seed on start, so the reseed fired on the first match of every session and silently killed snow and spindrift for the rest of the run; `ReleaseWorldMaterials` took the shared particle material with it, and breath and torch motes with that. Now on its own scene root with a self-healing `EnsureSystems`, matching HPAudio. |
| **The forest had no normal maps, soft shadows, cookies or point lights** | `TreeSway.shader` declared `_BumpMap` and never sampled it, and its keyword block was missing `_SHADOWS_SOFT`, `_ADDITIONAL_LIGHT_SHADOWS`, `_LIGHT_COOKIES`, `_SCREEN_SPACE_OCCLUSION` and `_FORWARD_PLUS`/`_CLUSTER_LIGHT_LOOP` — under Forward+ that last one meant the campfire, duffel lamp, crevasse glows and every torch lit nothing on a trunk. The promised DepthNormals pass did not exist. All present now, mirroring Snowpack. |
| **Clue/print/pile/ping/mark visuals leaked every mesh and material** | Order of ten thousand undestroyed native objects a match. Meshes are now shared (every footprint is geometrically identical); materials are destroyed on despawn where they must stay per-instance for the cold-trail fade. |
| **Battery desynced from night 2** | `TargetTeleport` rebuilt the local sim with a full battery while `ServerVitals` refuses to let the replicated value rise, so your HUD showed a working torch that was *off* to every other client, to the Yeti's torch-sight range and to Sam's battery scan. The sim now inherits both replicated resources, on all three placement paths. |
| **Snow prints pointed backwards** | `DropSnowPrints` passed the raw Unity yaw where `ClueMarker` expects sim yaw, laying every boot print 180° out. Direction of travel is the entire reason the Yeti reads them. |
| **CPU searchers got stuck crouched and recording** | `DoFilm` set both and only `DoFlee` ever undid them, so a bot that filmed then swept stayed at half speed and — because crouch suppresses `LeavesSnowPrints` — stopped leaving the tracks the Yeti hunts by. Actuation is now reset on every action transition. |
| **`YetiBot` sampled speeds after the phase guards** | The exact ordering `BotPerception.SampleSpeeds` documents against; the 30 s intermission meant a free sprint-range hearing sweep at every night rollover. |
| **Solo mode flags survived a failed host** | `SoloAsYetiPending` is consumed in a callback a failed host never reaches, so a refused port left it armed and the next co-op host spawned four CPU searchers into the lobby. Both flags are now set and rolled back inside `StartHost`. |
| **The `[H]` controls card could not be opened** | Polled `wasPressedThisFrame` inside OnGUI, which runs twice a frame, so the toggle flipped an even number of times and netted to nothing. Moved to `Update`. |
| **A half-started host left no UI at all** | Server up + client refused meant `TitleMenu` early-outed on `Connected()` while `HPHud` needs a started *client* — a forest, no menu, and an error string that could never be drawn. The server is now rolled back. |
| **`_skyMat` leaked one material per reseed** | Excluded from the sweep by design, then orphaned by `BuildSky` overwriting it. |
| **`BuildMarkerMast`/`AddBox` allocated per instance** | ~450 materials and ~450 meshes for masts alone, several times the "~200 per rebuild" `ReleaseWorldMaterials` was written against. Both cache per build now. |
| **Team claims were taken during scoring** | Merely *considering* a rescue reserved it for 3 s, so a bot that thought about REVIVE and chose FLEE still locked everyone out of the body. Claims moved to the act phase, where re-asserting them each frame gives the intended hold-while-working semantics. |
| **The title screen's settings sliders didn't apply until you left the page** | `DrawSettings` moved `MasterVolume` and `RenderScale` and never called `HPSettings.Apply()`, so the volume slider was silent while the *identical* pause-menu slider was live. `SliderRow` now takes an `applyLive` flag. |
| **The dev seed field flushed PlayerPrefs on every keystroke** | `HPSettings.Save()` ends in `PlayerPrefs.Save()`, a disk write, per character typed. Writes are now deferred behind a `_prefsDirty` flag and flushed on a page change, a host start, or quit. |
| **`UtilityChooser` could stick in an inapplicable action** | `Consider` drops scores ≤ 0, so a dead action was *absent* rather than low — and the incumbent lookup defaulted to 0, making challengers clear `CommitBonus` against a rung that had fallen away. |

Also added: `BootReport`, which turns every silent `Shader.Find` fallback into an on-screen line over
the title backdrop. The rule ("if a fallback fires, treat it as an error, not a degraded mode") was
already written down in [legibility]; nothing enforced it, and the failure it describes always
presents as an art problem. See the toolchain item above — this is the same family.
