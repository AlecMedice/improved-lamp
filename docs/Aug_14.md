# 2026-08-14 — AI rewrite and two graphics passes

Written for whoever picks this up next. Three streams of work landed on `yeti_port` in one day, all
in the Unity build. **None of it is committed**, and **none of it has been play-tested** — every
number in it is a first guess that compiled, which is not the same as a number that works.

Deeper write-ups live where the work does: `UNITY_NOTES.md` **[camp-gfx]** and **[tower-trees-caves]**
for the graphics. This file is the high-level map, plus everything from the retired `AI Rewrite.md`
plan that was worth keeping.

---

## 1. The AI rewrite

**The report:** "searchers beeline for the yeti… it's like an AI game from the 80s, basic if-else."
Accurate, and the cause was not the number of states. **A pursuit function over perfect information
always produces a straight line**, and both brains had one.

### Three separate omniscience leaks (all closed)

1. **Clue scoring** was `-age*10 - dist*0.15` — one second of freshness outweighed 66 metres of
   walking. The newest clue is *wherever the Yeti is standing right now*, so this wasn't following a
   trail, it was homing on a live position.
2. **Clue visibility had no distance gate at all.** `FreshestTrailClue` scanned every marker on the
   map, so a searcher 700 m away "saw" a footprint. This was the biggest one and it was only found
   during implementation. Clues now need `EvidenceSight` range **and** line of sight, cut to 35% with
   the torch off — which is also what finally makes the torch a real sensor rather than a status icon.
3. **The Yeti's fallback.** Its *perception* was already honest (LOS sight, torch beacon range,
   speed-scaled hearing, silent crouch) and abilities always required it. Only the fallback cheated:
   with nothing sensed, `Mode.Hunt` walked at `NearestSearcherRaw()` — true position, through walls.
   The old code knew; that mode's own comment called it "omniscience, dressed up with jitter."

> Correcting the record: an early diagnosis here said the Yeti had "no perception of any kind." That
> overstated it and it matters for scoping — the Yeti needed a new *fallback*, not new senses.

### What replaced it

New files under `Scripts/Game/AI/`: `BeliefMap`, `BotPerception`, `TeamBlackboard`, `UtilityAction`,
`BotProfile`, `BotRandom`. `SearcherBot` and `YetiBot` rewritten around them.

- **`BeliefMap`** — a 32x32 occupancy filter. Sightings collapse it, rumours blur it, **negative
  information** (looked there, nothing there) is the cheap big win that stops bots re-checking cleared
  ground, and **diffusion** turns a stale sighting into a growing search area instead of a stale
  waypoint. That third rule is what makes a bot look like it is reasoning about where you went.
- **Utility scoring replaced both fixed ladders**, with commitment + dwell so bots don't dither.
- **`TeamBlackboard`** — claims, call-outs and swept ground. **Belief is private, intent is shared.**
  Pooling belief would give four bodies one pair of eyes, which reads as wrong as omniscience.
- **`BotProfile`** — per-character weights **derived from the `Specialties` table**, not invented, so
  retuning `FilmRangeMul` moves Eli's behaviour automatically.

### Behaviours that did not exist before

`RECOVER` (a grab spills proof into a `ProofPile` and **nothing ever went back for it** —
`ServerBotRecoverPile` was wired and never called), `OVERWATCH` (second body at a rescue holds a torch
on the Yeti instead of adding hands to the same torso), and for the Yeti `GUARD` / `AMBUSH` / `STALK`
plus a **pressure governor** that eases off ~18 s after a grab. That last one is pacing, not mercy: a
predator that converts every success into the next hunt deletes the searching the game is about.

Specialty abilities finally fire — Eli's flash, Wren's marks, stakeout pings on the belief peak.

### Why four searchers looked like one

`CampSpot()` returns an **8 m x 4 m box**, and every bot was placed by it. Four bots spawned on top of
each other, then ran identical deterministic decisions, so they never separated. At night in fog that
is one silhouette — which is why the owner believed only one searcher was AI. Now fanned on a ring at
golden-angle bearings, facing outward.

> For the record: **neither solo mode fields exactly one AI searcher.** PLAY AS SEARCHER spawns one
> bot and it is the *Yeti*; PLAY AS YETI spawns **four** (`SoloSearcherBots = 4`).

### Pass 6 — half done

Difficulty tiers landed as **belief quality** (`BotDifficulty`, F3 `[U]`): an easy Yeti half-trusts
cues and its picture blurs fast. Same behaviours at every tier, so you cannot spot the difficulty by
watching it move — only by how often it is right. Costs no behaviour code.

Measurement landed too: every action transition logs its top-3 scores, and `GameManager.LogBotSummary`
writes a per-bot time-in-state histogram at match end.

**Not built: the automated soak harness.** Running N matches headlessly needs play-mode automation
(FishNet start, lifecycle, teardown) that does not exist here, and a harness that silently runs zero
matches is worse than none. It is also the wrong order — soaking numbers that are about to change
after the first play-test is wasted work.

---

## 2. Camp and trails

- **The campfire was one static emissive cone and a constant light.** Now logs, ember bed, flame /
  spark / smoke particles, scorch disc, and flicker from **summed sines rather than `Random`** —
  random flicker jumps every frame (reads as a failing bulb) and is frame-rate dependent.
- **There is no RV.** It became a plank hut in the re-theme; `WorldData.Rv` survives only as the name
  of the seeded transform and its parity-locked collider box. Hut gained a door, window reveal,
  stovepipe with smoke, roof snow load, icicles.
- **A derelict snowcat** was added — camp was all timber/canvas/snow, three soft matte materials, so
  nothing caught light differently from anything else.
- **Trails read as decals for three reasons, all fixed:** a flat ribbon floating 4 cm up with a hard
  edge; sharing `SnowNormal` with virgin powder so packed trail had *identical* micro-relief; and
  perfect uniformity along their length. Now a trodden channel with berms (two submeshes, packed
  centre / powder shoulders), a new `PackedSnowNormal`, and scattered bootprints, grit and sled ruts.

---

## 3. Lookout, trees, caves

- **The ladder was square sticks** — rails and rungs were both `TaperedCylinder(..., 4)`, and 4
  segments is a square. Now round tapered stiles, inset rungs, standoff brackets, safety hoop.
- **The tower had no cross-bracing** — four unbraced verticals holding a platform 10 m up. Bracing is
  also the cheapest silhouette win: a lattice is what a fire lookout *reads* as at distance.
- **The tower lamp had no mesh** — light pouring out of empty air. Now an iron brazier reusing
  `Campfire` at 0.62 scale.
- **Binoculars became an object** (`TowerViewer`) — the coin-op scenic-overlook viewer, owner's
  reference. **Mounted, not carried, and that is a design decision**: glassing is the reward for
  climbing, so a pocketable pair would delete the tower's reason to exist.
- **Tree sway** (`Metoh/TreeSway` + `Sway.hlsl`) — see the constraints section below.
- **Cave mouths were never holes.** Convex twice (a scaled sphere, then a lumpier rock). Convex
  geometry bulges toward the viewer, so it can only read as a dark rock — lumpiness cannot fix
  topology. `MeshUtil.Throat` builds inward-facing faces with **reversed windings**. Plus translucent
  lip, rime, icicles at varying depths *inside* the bore, and sinking threshold mist.

---

## 4. Bugs found in passing — the most reusable part of this document

- **Non-uniform transform scale on normal-mapped geometry.** Shears tangent space (the normal map is
  read through a skew) and stretches UV tiling anisotropically. Cave mound was 13 : 7.2 : 11, brow
  6 : 1. `MeshUtil.Rock` now takes scale params and bakes proportions into the mesh.
- **`AddBox` UV stretch.** It scaled a `PrimitiveType.Cube`, whose faces each carry 0..1 UVs, so
  tiling was per-*face* not per-*metre*: the hut's plank grain ran ~3x wider on the long walls than
  the ends, from one material. Affected every structure in the game. Fixed by `MeshUtil.MetricBox`.
- **Adjacent `System.Random` seeds produce correlated sequences.** Bots spawn in a loop so they get
  adjacent `ObjectId`s — feeding those in raw would have silently reintroduced the very correlation
  the per-bot stream exists to break. `BotRandom.Mix` avalanches first.
- **`Lathe` builds along +Y**, so leaning objects inward needs yaw `180 - a`, not `-a`. The intuitive
  guess points everything outward and builds a fountain instead of a fire.

---

## 5. Constraints that shaped the work — read before changing any of it

- **[parity-lock] / [rng-lockstep].** The AI rewrite touches **no sim files** — no regen needed for
  any of it. The wreck's collider *did* touch both sims, and the safe pattern is worth copying: place
  it from **fixed constants** and append the collider **after** the tree loop, so no random number is
  consumed and the forest stream is untouched. The regen proved it — colliders 2423 → 2425,
  climbable 19 → 21, and crucially **`first3` unchanged**, which is the evidence the tree stream
  didn't move.
- **Trees are merged per chunk (`CombineInstance`), not GPU-instanced.** This is why sway had to be a
  vertex shader: there is no per-tree transform left to rotate, and moving the chunk slides ~40 trees
  as a rigid slab. The shader needs data the merged mesh doesn't carry — sway weight measured from
  *that tree's own base* (chunk-space height would leave uphill trees rigid and make downhill ones
  thrash) and a per-tree phase (or the stand leans in unison and reads as the ground tilting).
  `BakeSwayData` recovers both after the merge.
- **The sway displacement lives in a shared `.hlsl` on purpose.** Forward, ShadowCaster and DepthOnly
  must apply it identically; drift means shadows sliding off trunks and SSAO haloing empty air, and
  each pass looks correct in isolation.
- **A shader that fails to COMPILE returns a valid object and renders magenta** — `Shader.Find` only
  catches a *missing* file. Always verify passes actually appear in the shader-compiler log.
  `MeshUtil.Sway` falls back to a plain Lit material, because a silently rigid forest beats a magenta
  one.
- **The animation layer adds no SyncVar and no RPC.** `TowerViewer` aims from already-replicated
  position and yaw rather than from the local-only `_glassing` bool. Pitch isn't replicated, so the
  head holds a fixed cant rather than inventing a value.
- **Trail channel depth is visual only** (5 cm). Terrain height is parity-locked and is what players
  actually stand on; anything deeper reads as a mismatch between your feet and what you see.

---

## 6. Still open

- **Automated AI soak harness** (Pass 6's missing half) — do it *after* a play-test says the weights
  are roughly right.
- **The duffel never visibly fills** as proof banks — identical at 0/3 and 3/3. A [feedback] problem,
  and the one real gap in otherwise-good evidence bags.
- **Searcher hearing.** Currently none, deliberately. The old argument was that it would make bots
  better at detection than the human they stand in for — which holds for a hard always-on check but
  is weaker now that cues feed a *decaying belief*. A play-test question, not a code one.
- **The web build has an unmodelled obstacle** — it renders no wreck but shares the collider. Same
  trade the RV/hut split already makes, but worth knowing.
- **`CLAUDE.md`'s `docs/` list** doesn't mention this file yet.

### Tuning knobs most likely to be wrong, in order

1. `SearcherBot` action base scores — especially the `SWEEP` floor (0.22). Too high and bots wander
   past evidence; too low and they never leave a lead they cannot find.
2. `UnlitSightFactor` (0.35) — decides whether the torch-vs-exposure trade is real or cosmetic.
3. `CommitBonus` (0.12) / `MinDwell` (0.9 s) — dithering shows in the log as rapid transitions.
4. `PressureBackoffSeconds` (18) — the tell is a night that feels relentless or empty.
5. `AmbushConfidence` (0.05) — uniform belief is ~0.001, so this is ~50x uniform.
6. Tree sway `_WindStrength` — 0.26 crowns, 0.10 trunks. Untested, and the first thing to look at.

---

## 7. State as of end of day

**Verified:** Unity headless `SetUpScene` compile clean (0 `error CS`, 0 shader errors, all three
`Metoh/TreeSway` passes confirmed in the compiler log) · client tsc + build · server tsc · 39/39
vitest · **PARITY OK** · repo↔live 0 files out of sync.

**Not verified:** anything about how it plays or looks in motion. Nothing here has run a frame.

**Uncommitted:** 12 modified files, 6 new paths. Per the owner's standing preference, committing is
always their call.

**Live-project reminder:** editing this repo does not change what the owner plays. Three `robocopy`
trees into `C:\Users\amedi\Metoh_port`, and use `/E`, **never** `/MIR` — the latter deletes the live
`.meta` files and churns every GUID.
