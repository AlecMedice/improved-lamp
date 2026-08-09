# Yeti — graphics pass handoff

**Session:** 2026-08-08, ~21:45–22:00 EDT (owner-imposed deadline: stop at 10:00 pm)
**Branch:** `yeti_port`
**Status:** three changes written, **compiled clean**, **synced to the live project**, not committed.

This document exists so the next session does not have to re-derive any of it.

**Verified 2026-08-08 22:03 EDT**, after the deadline, by the headless rebuild `CLAUDE.md` mandates:

```
Scripts + Shaders robocopy → C:\Users\amedi\Metoh_port   exit 3 (success)
Unity 6000.5.4f1 -batchmode -executeMethod ... SetUpScene   61s, return code 0
  error CS ................ 0
  Shader error / Parse .... 0
  Snowpack.hlsl ........... imported (ShaderIncludeImporter)
  Snowpack.shader ......... imported (ShaderImporter)
  warnings ................ 4, all pre-existing in vendored FishNet
```

So the shader parses (no silent flat-snow fallback), `urp.supportsHDR` is a real settable
property in this URP version, and the live project now contains all three changes. **What remains
unverified is how it LOOKS** — batchmode proves compilation, not behaviour, and never enters Play
mode. Nobody has yet seen a frame of the ice glitter.

> **Lifecycle — this is a handoff, not a standing doc.** `CLAUDE.md` promises that everything in
> `docs/` is current, so this file has to earn that. It is current exactly until its "Start here"
> steps are done and its changes are compiled, copied, and committed or reverted. **At that point
> fold anything durable into `UNITY_NOTES.md` and delete this file** — the same treatment
> `July19Work.md` got. A handoff doc left lying around after it is consumed is the stale plan
> `CLAUDE.md` says does not exist here.

---

## Start here

Compilation and sync are **done** (see the verification block above). One thing remains, and it
is the only thing that can settle it.

### Look at it in Play mode

Batchmode proves the code compiles and the shaders parse. It does not enter Play mode and it
renders nothing, so **the entire visual question is still open.** Open `Metoh_port` in the editor
and walk around at night with the torch on.

What to judge, in order:

1. **Is there sparkle at all?** If not, work down "If the glitter does not show up" below —
   though note that its two most likely causes (wrong binary, failed shader compile) are now
   ruled out, which moves HDR and the fade distance to the top of the list.
2. **Is it too much?** This is the likeliest problem. `_SparkleStrength` defaults to `1.4` on a
   `0..4` range and was picked blind, with no frame ever rendered. Drag it live on the terrain
   material. `0` disables the effect entirely without touching code.
3. **Does it survive the exposure paths?** Untested and easy to get wrong: the Yeti's role
   exposure adds `+0.9` stops and the tower binoculars add `+2.0` with a green filter. Both lift
   emission along with everything else, so glitter tuned on a searcher's screen may be blown out
   through the binoculars. Check all three.

### Then commit

Nothing here is committed — that is the owner's call, every time. The working tree also carries
the separate, unrelated `ICharacterBody` refactor (see "Reviewed and found clean"), so **stage
deliberately rather than `git add -A`** if these are meant to land as distinct commits.

---

## The three changes

### 1. Saturation clobber — a real bug, fixed

`unity/Assets/Metoh/Scripts/Game/PostFX.cs`

`Awake()` set `saturation` to `-2f`, carrying a long comment explaining why the previous `-6`
was wrong (a near-monochrome palette was fighting the only surfaces the world has to
distinguish itself with — the blue basin, the warm trail, the tents and prayer flags).

`ApplyExposure()` then re-overrode saturation to the literal `-6f` on every call. It is called
from `SetYetiVision` and `SetTitleBrightness`, i.e. the moment a role is assigned — so **the
tuned value never survived to a single frame of actual play.** The tuning pass had never once
been seen.

Fix: one `BaseSaturation = -2f` constant, read by both sites.

```csharp
private const float BaseSaturation = -2f;
...
_colorAdjustments.saturation.Override(_nightVision ? -55f : BaseSaturation);
```

This one is low-risk and independently valuable — if you have to revert anything below, keep this.

---

### 2. Ice glitter on the snowpack — the visual change

`unity/Assets/Metoh/Shaders/Snowpack.hlsl` (the maths) and `Snowpack.shader` (properties + wiring)

**Why this and not something else.** It is the one cue that says "snow" and that a smoothness
value can never produce. Real snowpack is a field of ice facets at a scale far below a pixel;
a normal map cannot represent them, because averaging a million random facets into one texel is
exactly what destroys the effect. What you see is a sparse scatter of individual points that
flare and die as you move — **it is the motion that reads, not the brightness.**

**How it works.** A lattice of micro-facets, one per cell, each with a fixed hashed orientation,
lit only when it happens to point back at the viewer.

- **View direction, not a half-vector.** This is the physically right call *in this game
  specifically*: every meaningful light is a torch held at the camera, so light and view are the
  same ray. It also makes the glitter a torch-reveal — it appears where you point, which is free
  atmosphere for a game about sweeping a beam across a dark valley.
- **Sparse (`h > 0.86`, ~14% of cells).** A glint in every cell is a uniform shimmer, which is
  the cheap-looking failure this is trying to avoid.
- **Rounded inside its cell** (`falloff`) so it is a point of light, not a lit square.
- **Tight lobe** (`pow(..., 90)`) so a facet either catches you or does not. A soft lobe smears
  every glint into haze and loses the flicker that carries the whole effect.
- **Grazing weight** — snow sparkles hardest looking down a slope away from you, not at your boots.
- **Fades out by 45 m**, and is suppressed on exposed rock (`1 - rock`).

**It rides `surface.emission`, deliberately.** The facets are sub-pixel, so there is no normal for
a real specular highlight to sit on. Emission also means bloom picks it up — a glint that does not
bleed is a white dot, not a spark. This is why change #3 matters.

**Structural notes for whoever edits this next:**

- `SurfaceMix` gained a `half3 emission` field. `MixSnowpack` always writes it (initialised to `0`
  before the branch), so both callers are safe.
- The **DepthNormals** pass also calls `MixSnowpack` and discards the emission. This relies on the
  compiler dead-code-eliminating the glitter maths in that pass. It should — nothing else consumes
  `o.emission` there — but if the depth-normals pass ever shows up hot in a profile, that is the
  first thing to check, and the fix is a `#define` guard around the block rather than a second copy
  of the function. **Do not split `MixSnowpack` into two functions**; the file header explains why
  (the lit and depth-normals passes must produce the identical normal or SSAO occludes against a
  surface that is not the one being drawn, and the ground grows a faint crawling crust).
- `falloff` is deliberately not named `point` — `point` is an HLSL geometry-shader primitive
  keyword, and using it as a variable is a compile error on some targets and silently fine on
  others. I hit this and renamed it. Do not rename it back.

**Tuning dials** (all material properties, so they drag live in the inspector):

| Property | Default | What it does |
|---|---|---|
| `_SparkleStrength` | `1.4` (range 0–4) | **The main dial.** Set `0` to disable entirely. |
| `_SparkleDensity` | `55` | Facets per metre. Higher = finer, more numerous. |
| `_SparkleColor` | `(0.85, 0.93, 1.0)` | Slightly blue-white. Ice, not sunlight. |

Peak emission is roughly `1.3` with the defaults, which sits above the bloom threshold of `0.85`
in `PostFX` — that is intentional and is what makes glints bleed. If you retune the bloom
threshold, expect the glitter's character to change with it; they are coupled.

---

### 3. HDR asserted — the one I would most want kept

`unity/Assets/Metoh/Scripts/Game/HPQuality.cs`, in the existing `!_appliedOnce` block

```csharp
urp.supportsHDR = true;
```

Nothing in this repo guarantees HDR. It is a checkbox on the URP pipeline asset, which lives in
`Metoh_port` and is therefore **outside version control** — nothing here can stop it being switched
off, and switching it off guts the look without producing a single error.

Everything the project does with highlights depends on colours being allowed above 1.0:

- bloom has nothing to threshold at `0.85`
- ACES has no highlight roll-off left to do
- split toning's warm highlight tint has no highlights to tint
- the torch beam and the Yeti's eyeshine stop blooming
- the new ice glitter clips to flat white dots

This is very likely not broken today — URP defaults HDR on. The point is that its failure mode is
*"everything looks slightly cheap and no single thing looks broken"*, which is the hardest possible
thing to diagnose from a play-test report. One line makes it impossible. `HPQuality` already
reaches into the URP asset to set `msaaSampleCount`, so this costs nothing new architecturally.

The `[HPQuality]` startup log now includes `HDR on`, so it is visible in any log the owner sends.

---

## Reviewed and found clean — do not redo this

I audited two things looking for bugs and found none. Recorded so the next session does not spend
its budget re-covering the same ground.

**The uncommitted `ICharacterBody` refactor** (`CharacterBody.cs`, `CharacterAnchors.cs`, and the
`Avatar` → `ICharacterBody` swaps in `HPPlayer.cs` / `TitleActors.cs`). Checked the things that
break silently in that kind of change:

- `Avatar` implements every interface member — `HeadAnchor`, `TorchAnchor`, `Tick`, `TriggerRoar`,
  `SetVisible`, `SetTint`, `Dispose`. No missing implementation.
- Both call sites that reach past the interface for a `Transform` (`HPPlayer.cs:1555` and
  `TitleActors.cs:83`, both parenting a torch light) use `TorchAnchor`, which exists on both
  `Avatar` and `ModelBody`. `ModelBody` falls back to `HeadAnchor` when an imported model has no
  `TorchHand` anchor, so a model missing anchors degrades instead of null-dereferencing.
- The `_bodyMat.color` → `_avatar.SetTint()` change in the specialty-recolour path is correct,
  including its every-frame change guard.

**`MeshUtil.Torus`** — correct, including the non-obvious part. The two seam-sealing loops overlap
at four corner vertices, and because the second loop consumes the first loop's already-averaged
values, all four converge on the same normal rather than one clobbering the other. Winding, index
bounds, and the wrapped hash (`rw`/`sw`, so seam partners get identical jitter and do not split)
all hold up.

---

## If the glitter does not show up

The usual top two causes are **already ruled out** as of the verification above: the code is in
`Metoh_port`, and the shader compiled and imported. If someone re-syncs or edits the shader,
re-check them. Otherwise start at #3.

1. ~~**The change is not in the build.**~~ Ruled out — robocopy'd, exit 3.
2. ~~**The shader did not compile.**~~ Ruled out — 0 shader/parse errors, both files imported.
3. **HDR is off** on the live project's URP asset. Change #3 now forces it on at startup, so this
   should self-heal, but confirm `HDR on` appears in the `[HPQuality]` log line. (Batchmode proved
   `urp.supportsHDR` exists and is settable; it did not prove the assignment runs at startup.)
4. **You are looking at rock or at the far field.** Glitter is suppressed on rock and fades to
   nothing by 45 m by design.
5. **You are not moving.** The effect is motion — a still screenshot will under-sell it badly.
   Judge it while walking with the torch on, not from a static frame.

---

## Not investigated

Honest list of what I did *not* get to, so nothing here is mistaken for a considered
recommendation. These are unexplored leads, not analysed proposals:

- Height fog / a fog gradient for the basin. `RenderSettings.fog` is on and configured in
  `WorldBuilder.BuildLighting`, but I never read that block properly.
- Whether the URP soft-shadow *quality* tier (distinct from `LightShadows.Soft`) is set anywhere.
  `HPQuality.ApplyShadowQuality` sets the light's mode but nothing sets the pipeline-level quality.
- Anything in `NightSky.shader`, `TorchBeam.shader`, `Weather.cs`, or `ProcTex.cs`. I did not open
  them.
- Whether the glitter reads well against the Yeti's brighter per-role exposure (`+0.9` stops) or
  through the binocular night-vision path (`+2.0` stops and a green filter). Both lift emission
  along with everything else and neither was tested.

---

## Working agreements carried into this session

- **Nothing is committed.** Committing is the owner's call, every time.
- The owner is fluent in TypeScript/web and newer to Unity — name Unity concepts explicitly rather
  than assuming them, and give editor steps click-by-click when the editor is involved.
- `docs/UNITY_NOTES.md` sections are cited by **bracketed id** (`[workflow]`, `[materials]`,
  `[bodies]`, `[rng-lockstep]`), never by number.
- Per-instance variation must be **hashed from an index, never drawn from an RNG stream**
  (`[rng-lockstep]`). The glitter follows this — every facet is hashed from its cell id.
