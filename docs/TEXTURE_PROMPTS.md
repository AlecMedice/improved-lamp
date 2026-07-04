# Hollow Pines — texture generation prompt pack

Copy‑paste prompts for an image AI (Midjourney, DALL·E, SDXL, Firefly, or a dedicated PBR‑texture tool)
to make **higher‑quality textures** for the game. Today every surface is a flat colour / vertex‑coloured
`MeshStandardMaterial` — no image files at all — so adding tileable maps is a real step up while keeping
the stylized low‑poly, dusk‑to‑dawn Pacific‑NW look.

Read the two setup sections first (they apply to every prompt), then grab the material you need.

---

## A. What to ask for (output requirements)

For **every** texture:
- **Seamless / tileable** (repeats with no visible seam) unless noted “atlas”.
- **Flat, even lighting — no baked shadows, highlights, or ambient occlusion in the base colour.** The
  engine lights the scene at dusk→night→dawn; baked lighting fights that and looks wrong.
- **Square, power‑of‑two:** 1024×1024 for most surfaces, 2048 for the big terrain sheet.
- **Stylized, hand‑painted, matte** — low‑to‑mid‑frequency detail. Avoid photoreal grit and busy
  high‑frequency noise; it clashes with the smooth‑shaded low‑poly meshes and the film‑grain post FX.
- Stay in the **dusk palette** below so textures read together under the fog + ACES tone mapping.

**Maps to request (PBR).** `MeshStandardMaterial` uses these slots:
- `map` — **base colour / albedo** (sRGB). The one map every material needs.
- `normalMap` — surface relief (linear data map). High value on bark, rock, fabric, fur.
- `roughnessMap` — matte vs. glossy (linear). Matters for water, the RV, wet rock.
- `aoMap` — crevice shadowing (needs a 2nd UV set; optional, low priority here).

If your tool only outputs a base colour image (Midjourney/DALL·E), generate the albedo, then derive
`normalMap`/`roughnessMap` from it with a free tool (Materialize, NormalMap‑Online, or an SDXL PBR
pipeline). Dedicated texture tools can emit the whole set at once — ask for “PBR set, seamless”.

## B. Style preamble (prepend to every prompt)

```
Seamless tileable hand-painted low-poly game texture, stylized (not photorealistic),
flat even studio lighting with no baked shadows or highlights, matte finish,
subtle low-frequency detail, muted Pacific Northwest dusk palette, top-down flat view,
1024x1024. Negative: seams, tiling artifacts, baked shadows, strong highlights, text,
watermark, high-contrast noise, people, glare, perspective, vignette.
```

Swap the resolution to `2048x2048` for the terrain sheet. For **atlas/sprite** items (foliage, faces)
drop “seamless tileable” and say what's noted in that entry.

### Palette (hex — pulled from `client/src/config.ts` + `Environment.ts`)
| Surface | Colour(s) |
|---|---|
| Forest floor (wet→ridge) | `#30421f` `#385826` `#425d33` `#525a3d` |
| Camp clearing grass | `#619030` |
| Tree trunk / bark | `#4a3a2c` |
| Conifer needles | `#33503a` |
| Rock / boulder | `#6a6a73` |
| Bushes | `#3a6028` `#4a7835` |
| Water (lake / creek) | `#2a5a6a` `#1a7aaa` |
| Fallen log | `#5a4030` |
| Structure wood | `#6a4a2c` `#7a5a3a` |
| RV shell / trim | `#d9d3c2` `#7a8a6a` |
| Bigfoot fur | `#5b4636` (dark face `#3f3024`) |
| Hunter jacket / pants / skin | `#6a7b8c` `#3c4657` `#caa889` |

---

## C. Environment materials

### 1. Forest floor (terrain) — **2048, seamless**, highest priority
Tiles across the whole ground; today it's vertex‑coloured only.
```
<style preamble, 2048x2048> forest floor ground texture: damp dark soil, scattered
pine needles and short moss patches, a few small twigs and leaves, deep muted greens
fading to earthy brown, colours #385826 #30421f #425d33, even and walkable, no rocks,
no large features, gentle organic variation that tiles seamlessly.
```
Wire as `map` (+ derived `normalMap`, low strength). Set `repeat` ~ (40, 40) across the 800 m map.

### 2. Tree bark (conifer trunk) — seamless, cylindrical
```
<style preamble> conifer tree bark, vertical furrowed ridges, weathered grey-brown,
base colour #4a3a2c, soft hand-painted grooves, tiles vertically along a trunk.
```
`map` + strong `normalMap`. Wrap vertically; `repeat` ~ (1, 3) on the trunk cylinder.

### 3. Conifer foliage — **atlas, NOT seamless**
For a needle‑cluster card or crown detail.
```
<style preamble, drop "seamless tileable"> stylized conifer needle cluster on transparent
background, clumped dark blue-green pine needles, colour #33503a, flat matte, PNG alpha,
low-poly game foliage sprite.
```
Use as `map` on the crown (with `alphaTest`) or as a subtle detail — keep the smooth‑cone silhouette.

### 4. Rock / boulder — seamless
Cave boulders + shore stones (`IcosahedronGeometry`).
```
<style preamble> stylized granite rock face, faceted low-poly feel, cool grey with faint
moss in crevices, base colour #6a6a73, matte, subtle chips, tiles seamlessly.
```
`map` + `normalMap` + `roughnessMap` (fairly rough, damp sheen in cracks).

### 5. Bush foliage — seamless or small atlas
```
<style preamble> stylized shrub leaves, small rounded matte leaves in two greens
#3a6028 and #4a7835, dense clumped foliage, low-poly game texture.
```

### 6. Still water (lake) & stream (creek) — seamless, tiling
```
<style preamble> calm dark forest lake water surface, gentle stylized ripples, deep
teal #2a5a6a with faint cool reflections, semi-glossy, no sky reflection baked in,
tiles seamlessly.
```
`map` + `roughnessMap` (low roughness / high gloss); optionally a scrolling `normalMap` for ripples.
Creek variant: brighter `#1a7aaa`, thin directional flow lines.

### 7. Structure & log wood — seamless plank + bark‑log
Tower, trailhead, RV stripe; and the fallen logs.
```
<style preamble> weathered wooden planks, vertical grain, muted brown #6a4a2c with
lighter #7a5a3a highlights, matte, hand-painted, tiles seamlessly.
```
Log variant: round bark log, mossy top, base `#5a4030`.

### 8. RV shell panel — seamless
```
<style preamble> vintage camper van body panel, cream painted metal #d9d3c2 with a
sage green stripe #7a8a6a, faint panel seams and rivets, matte, tiles horizontally.
```

---

## D. Character materials

> The avatars are **procedural low‑poly rigs** right now (capsule‑built limbs, no UVs). Textures need a
> UV‑unwrapped mesh, so these belong with the **authored/skinned character models** (the remaining
> Phase 6 art item). Generate them alongside that mesh work; a flat albedo per body part is enough to
> start.

### 9. Bigfoot fur — seamless
```
<style preamble> matted shaggy dark fur, coarse clumped strands, muddy brown #5b4636,
matte, stylized creature pelt, tiles seamlessly.
```
`map` + strong `normalMap` for strand relief. Face patch variant: darker `#3f3024`, leathery skin.

### 10. Hunter kit — seamless / small parts
```
<style preamble> worn outdoor rain jacket fabric, slate blue-grey #6a7b8c, soft matte
weave, subtle seams, tiles seamlessly.
```
Pants variant `#3c4657` (rugged canvas); face/hands as a small skin swatch `#caa889` (no tiling needed).

---

## E. Wiring a texture into the game (Three.js)

Example for the terrain in `client/src/world/Environment.ts` (drop the file in `client/public/textures/`):

```ts
const tex = new THREE.TextureLoader().load("/textures/forest_floor_albedo.png");
tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
tex.repeat.set(40, 40);          // world is 800 m; ~20 m per tile
tex.colorSpace = THREE.SRGBColorSpace;  // albedo only; leave normal/roughness in linear (default)
const nrm = new THREE.TextureLoader().load("/textures/forest_floor_normal.png");
nrm.wrapS = nrm.wrapT = THREE.RepeatWrapping; nrm.repeat.copy(tex.repeat);

const mat = new THREE.MeshStandardMaterial({
  map: tex, normalMap: nrm, roughness: 1, metalness: 0,
  vertexColors: true,            // keep the existing height/clearing tint as a multiply
});
```

Checklist when adding any texture:
- **Albedo → `SRGBColorSpace`; normal/roughness/AO → linear** (the default; don't set sRGB on data maps).
- `RepeatWrapping` + a `repeat` tuned so the tile is ~a few metres — too small looks noisy, too big looks flat.
- Keep files reasonable (1–2 MB PNG, or KTX2/basis if you add a compressor later); they ship in the client bundle.
- Re‑check under **all three night stops** (dusk / deep night / dawn) — a texture that pops at dusk can
  vanish in the dark. Verify with `client && npx tsc --noEmit && npx vite build`, then eyeball in‑game.
- Textures don't touch `shared/sim` (pure) — they're renderer‑only, client‑side.
