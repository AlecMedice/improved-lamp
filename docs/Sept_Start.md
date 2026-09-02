# Sept_Start — where Metoh is, and how it got here

A handoff written on **2026-09-01**, for an agent picking this up cold. It assumes you have read
`CLAUDE.md` and nothing else. Everything here is in real identifiers and real file paths, and the
bracketed ids (`[bodies]`, `[materials]`, `[workflow]`) are section ids in `docs/UNITY_NOTES.md` —
cite them by id, never by position.

---

## 1. The one thing that will waste your day if you skip it

**Editing this repo does not change what the owner plays.**

| | |
|---|---|
| `unity/Assets/Metoh/` (here) | source only — **not an openable Unity project**. No `Packages/`, no `ProjectSettings/` |
| `C:\Users\amedi\Metoh_port` | the real Unity project the editor opens. Not a git repo. **Never create or recreate it** |

Code crosses that gap only by an explicit `robocopy`, and there are **three trees**, not one:

```powershell
robocopy 'unity\Assets\Metoh\Scripts' 'C:\Users\amedi\Metoh_port\Assets\Metoh\Scripts' /E
robocopy 'unity\Assets\Metoh\Shaders' 'C:\Users\amedi\Metoh_port\Assets\Metoh\Shaders' /E
robocopy 'csharp\Metoh.Sim'           'C:\Users\amedi\Metoh_port\Assets\Metoh\Sim'      /E
```

`/E`, never `/MIR`. Robocopy exit codes below 8 are success. A sync that misses `Shaders/` fails as a
magenta sky, not as a copy error.

This gap once silently swallowed five days of work (2026-08-01 → 08-06): two passes were committed
and pushed without Unity ever compiling them. The owner's bug reports in that window were **accurate
about the build they had**; the wrong assumption was ours. So: **a play-test report is evidence about
a specific binary — establish which one before reasoning about it.** `[workflow]` has the three
checks that pin it down without opening Unity.

## 2. How to prove your work compiles

You cannot press ▶ from here, but you can run the real compiler. Editor must be closed (it holds
`Temp/UnityLockfile`):

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.4f1\Editor\Unity.exe' -batchmode -quit -nographics -projectPath 'C:\Users\amedi\Metoh_port' -logFile <log> -executeMethod Metoh.EditorTools.GameSceneSetup.SetUpScene
```

Then check **all three**: `grep -c "error CS"` is 0, `grep -i "Shader error"` is empty, **and the
line `Mountain scene wired` is present**. That last one is not optional — a run has been observed to
compile cleanly, enter `Begin MonoManager ReloadAssembly` and exit without ever invoking
`SetUpScene`, with a zero error count the whole way. A green light from that run means nothing.

This proves compilation and shader import. It does **not** enter Play mode.

## 3. What the game is

Asymmetric 1-vs-5 horror. Five **searchers** hunt a Himalayan valley for proof of the **Yeti** (the
sixth player). Three nights, 8pm→8am. Searchers win by capturing 3 solid videos; the Yeti wins by
surviving. `docs/GAME_DESIGN.md` is the rules source of truth, `docs/STORY.md` has the world and the
five named characters, `docs/CHARACTER_FUNC_DEV.md` has the specialty system.

It was re-themed from "Hollow Pines" (a Pacific-NW Bigfoot hunt) in Aug 2026 to escape overlap with
the Steam game *BIGFOOT*. **The web build keeps its forest visuals on purpose** — only identifiers
were renamed there; web visuals are abandoned. The Unity build carries the snow re-theme, and that is
where all new work lands.

There are two codebases and both are live:

- **Web** (`client/`, `server/`, `shared/sim/`) — Three.js + Colyseus, TypeScript. Still the
  **behavioural spec**. Its tests are the fast way to check sim changes.
- **Unity + FishNet** (`unity/`, `csharp/`) — the desktop build, where gameplay work goes.

`shared/sim/` (TypeScript) and `csharp/Metoh.Sim` (C#) are **the same simulation, twice**, and they
are **parity-locked**: `csharp/Parity` replays a golden fixture and must print `PARITY OK`. If you
change sim behaviour you regenerate the fixture and then run the harness, in that order. A
byte-identical regen after a behaviour change means the fixture did not exercise your change — add a
probe, do not assume you are safe.

**The parity lock is a real constraint on graphics work**, not just on gameplay: it is why §5's
terrain fix had to be mesh resolution rather than a gentler height curve.

## 4. Where it stands right now

Phases 1–5 are done: server-authoritative movement with reconciliation, per-night escalation, lobby
and reconnection, the full asymmetry pass (Yeti leap/charge/climb/senses, searcher revive/dazzle/
vault), post-processing, settings, key rebinding, the dusk briefing. `docs/ROADMAP.md` has the map.

Open work sits in three files, and they are not interchangeable:

- **`docs/Aug_15_Bug.md`** — the current bug backlog. A full line-by-line audit of the Unity build on
  2026-08-15. The critical/high findings were fixed that day and are listed at the bottom **for
  reference only**; everything above that line is still open, ranked, with file references. Read it
  before starting a Unity pass.
- **`docs/Aug_14.md`** — the day log for the CPU-brain rewrite (belief + utility, replacing two
  priority ladders) and two graphics passes. Read it for what is untested and still open. It
  supersedes a planning doc that was deleted before ever being committed, so unlike the retired logs
  there is no git history to fall back on.
- **`docs/UNITY_NOTES.md`** — traps, conventions and remaining work. **Read before touching Unity.**

### The standing caveat, which you are about to inherit

**Nothing since 2026-08-08 has run a frame in Play mode.** Every pass since then — the AI rewrite,
the camp/trail/lookout graphics, the Aug-15 audit fixes, the searcher rebuild, and the whole
2026-09-01 overhaul below — is *compile-verified only*. That is a real gap and it compounds: each
pass is tuned by eye against numbers rather than against a frame.

**A Play-mode session on `C:\Users\amedi\Metoh_port` is the gate on everything else.** If you are
choosing what to do next and nobody has played it, that is the answer.

## 5. What changed on 2026-09-01 (the most recent pass)

One owner session, escalating: the Yeti looked like *"a crazy person running through a forest"*, then
*"a muppet"*, then *"in no way shape or form the abominable snowman"*; the searchers *"all look
terrible — we need some actual people, hair"*; the flashlight put *"a halo around the whole screen"*;
*"I still go under snow walking back to the camper from a hill"*; the camp *"looks like it was made in
the 80s"*; *"the fire smoke is boxes of black"*.

**Seven complaints, five of them were bugs.** That is the transferable lesson from this pass: a
report phrased as taste is worth diagnosing as a defect first. Full detail is in `UNITY_NOTES.md`
under *"The character + camp overhaul, 2026-09-01"*; the short version:

- **The Yeti** was dark brown (`0x2a2018`) — a Sasquatch colour the Himalayan re-theme never touched
  — walked upright with human counter-swinging arms, had a visible neck, and had ellipsoids for hands
  and feet. It is now dirty ivory with dark bare hide at muzzle/hands/feet, drops onto its knuckles
  above a jog (`quad` in `Avatar.Tick`), has a trapezius hump where the neck gap was, and has built
  hands and feet.
- **The searchers** had a dark blob for a face inside a closed hood. They now have real heads, hair
  and skin keyed to the five named characters, and gloves and boots with actual shape.
- **The torch halo** was the beam cone being viewed from its own apex — the holder's camera sits
  ~0.5 m behind it, so a 62° cone covered their whole view additively. Fixed in
  `Shaders/TorchBeam.shader` with an axis fade, a near fade and a depth soft-fade.
- **Walking under the snow** was mesh resolution, and it was measured, not guessed: the camp
  flattening smoothstep is the sharpest curvature in the world and 192 terrain segments resolved it
  with three quads. **0.52 m of sink at the camp ring on the shipping seed**, 1.07 m on seed 999.
  Now 512 segments → 0.097 m.
- **The black smoke boxes** were a URP material bug that had *already been solved once*: `new
  Material(shader)` starts opaque and the `_Surface`/`_Blend` floats are editor-only. `Weather` had
  found and fixed this on snowflakes; the campfire never got it. The fix now lives in one place,
  `MeshUtil.ParticleMaterial` / `MakeTransparent`.
- **The camp** was flat boxes — normal-mapped, which cannot rescue a shape. Board cladding, corner
  posts, hull ribs on the snowcat, snow banked at the footings, and a real duffel.
- **The flags** were solid unlit cubes that never moved. Now cloth sheets with a wind vertex shader
  (`Shaders/Flag.shader`) whose gust term deliberately matches `Sway.hlsl`'s.

Two new tools came out of it and you should reach for both:

- **`MeshUtil.MeshGroup`** welds several generated meshes into one. Renderers are the expensive unit
  in this project, not triangles — a hand with five digits costs what the sphere it replaced did.
  Anything built from more than about three lathes should use it.
- **`MeshUtil.ParticleMaterial` / `MakeTransparent` / `SetSoftParticles`** — build every runtime
  transparent material through these. There is no other correct way to do it in URP from code.

Also corrected: `RenderPipelineSetup.cs`'s header claimed the pipeline had never been configured.
That was true on 2026-08-15 and is **no longer true** — verified against the live `.asset` files.
HDR grading, the depth texture and the tuned SSAO are all on. Those settings live outside version
control, so re-verify against the files rather than trusting any comment, including this one.

## 6. Conventions that are not negotiable

- **Everything is generated at runtime. There are no asset files.** "Clone it and it runs" is the
  property that keeps this project working, and it is why bodies are jointed procedural meshes rather
  than a rigged FBX. The seam for imported models already exists (`ICharacterBody`, `[import]`) — use
  it rather than breaking the property.
- **Anything varying per-instance is hashed from an index, never drawn from an RNG stream**
  (`[rng-lockstep]`). Every client must build the same world. Adding a `rand()` call in the wrong
  place perturbs the forest stream and desyncs the map for everyone.
- **New Unity geometry must carry UVs and tangents** or it silently renders flat under the normal
  maps `[materials]` is built on. `MeshUtil` does this; hand-built meshes must do it themselves.
- **"Materials, not mesh density" is true of surfaces, not silhouettes.** At night, fogged, the
  outline is nearly all the player gets — so shapes that read as primitives have to be rebuilt.
  `[legibility]`, `[bodies]`.
- **Keep `shared/` pure** — no Three.js, no DOM, no decorators.
- Colyseus 0.15 uses **legacy decorators**. Do not "modernize" the server tsconfig.
- **Never auto-commit.** Committing is the owner's call, every time.

## 7. Working style the owner has asked for

On collaborative design or spec work — story, mechanics, planning — **pause at decision points and
ask with selectable options** (the AskUserQuestion tool; the owner prefers picking to free-form).
Surface one decision, let it be answered, proceed. Straightforward implementation work does not need
this: just build.

Docs stay in **engineering vocabulary** — real identifiers, real section ids — even when the audience
is not an engineer. No exec-speak translation layer.

## 8. If you want a starting point

1. **Play it.** Everything above is compile-verified and unplayed, and the list of things tuned by
   eye is now long. `[workflow]`'s checks tell you which binary you are looking at.
2. Then `docs/Aug_15_Bug.md`, top down. It is ranked and nothing in it blocks a play-test.
3. The specific things from the 2026-09-01 pass most likely to be wrong, in order: whether the Yeti's
   hands actually reach the ground at full charge (there is a derived 58°/1.52 m coupling in
   `Avatar.Tick` that will float them if either moves), whether the torch beam is now too faint from
   the holder's own view (`_AxisFloor`, currently 0.16), whether an ivory Yeti is too well camouflaged
   against snowpack at range, and whether the flag ripple reads at 13 cm trail scale when it was tuned
   at 62 cm mast scale.
