# Phase X: Temporary Graphics & UI Generation Guide

## Project Context
**Game:** Hollow Pines
**Genre:** Indie Horror
**Architecture:** TypeScript (Multiplayer / Room-based) with an eventual migration to Unity.
**Current State:** Requires temporary, lightweight graphical assets and UI elements to establish atmosphere, verify scale, and test mechanics before committing to heavy 3D assets and textures.

## Instructions for Claude Design
When generating assets for this phase, adhere strictly to the following constraints:
* **Format:** Output clean, modular SVG code or procedural HTML5 Canvas scripts.
* **Compatibility:** Assets must be usable directly in a vanilla TypeScript/HTML/CSS environment.
* **Portability:** Design UI elements (SVGs) so they can be easily exported and implemented as Unity Canvas UI Sprites in the future.
* **Aesthetic:** Tense, atmospheric, horror-focused, minimalist.

---

## Asset Generation Prompts

### 1. The Map & Ping System (`MapView.ts` & `PingField.ts`)
**Goal:** Upgrade the tracking and mapping mechanics from basic shapes to an immersive UI.
**Prompt for Claude:** > "Generate a clean, SVG-based topographic map interface with a subtle CRT monitor glow effect. Include CSS animations for a sweeping radar line and fading circular pings to represent tracking data. The design should look like a handheld tracking device used in a horror survival game."

### 2. HUD Icon Sets (`HUD.ts` & `ClueField.ts`) — ✅ SHIPPED
_Inline white-line SVG icons on the HUD pills (`client/index.html`): flashlight + **segmented** battery,
hiking boot (stamina), film-camera (clip), magnifier (footage). Structural strokes, CSS drop-shadow._
**Goal:** Provide clear, atmospheric player feedback for survival mechanics.
**Prompt for Claude:** > "Create a minimalist, white-line SVG HUD icon set for an indie horror game. Include the following specific icons: 
> 1. A flashlight with a segmented battery indicator.
> 2. A stylized hiking boot representing a stamina meter.
> 3. A glowing journal or magnifying glass for clue collection. 
> Ensure the SVGs are scalable and rely on structural lines rather than filled textures so they can be easily styled with CSS drop-shadows."

### 3. Atmospheric 360-Degree Skyboxes (`ForestRoom.ts`)
**Goal:** Establish mood and depth in the environment before rendering full 3D trees.
**Prompt for Claude:** > "Write a procedural HTML5 Canvas script that generates a dark, 360-degree panoramic skybox. The canvas should feature silhouetted pine trees of varying heights against an overcast, foggy gradient backdrop. The visual style should be eerie and tense, acting as an atmospheric backdrop for a deep forest environment."

### 4. Scale-Accurate Silhouettes (`caves.ts` & Player scaling)
**Goal:** Test line-of-sight, collision, and movement speeds with accurately scaled 2D stand-ins.
**Prompt for Claude:** > "Generate precise, orthogonal vector silhouettes (front and side profiles) using SVG for two character types:
> 1. A standard human explorer.
> 2. A massive, hulking Bigfoot-style cryptid.
> The SVGs should be appropriately scaled relative to one another to serve as exact reference sheets and 2D bounding-box stand-ins for a game engine."

### 5. Camera Overlays & Lighting Masks — ✅ SHIPPED
_Screen-space DOM/CSS layers in `client/index.html` (below the HUD): always-on dark **vignette**, a
flashlight **beam mask** (bright centre → black periphery, toggled with the light via `HUD.setBeam`),
and a procedural **dirt-on-lens** grime (SVG `feTurbulence`, faint always, brighter while lit)._
**Goal:** Obscure placeholder textures and heighten tension using screen-space effects.
**Prompt for Claude:** > "Generate a set of SVG and CSS-based screen overlays for a horror game. Include:
> 1. A dark vignette edge effect.
> 2. A radial gradient mask that simulates a harsh flashlight beam, leaving the periphery in black.
> 3. A subtle 'dirt on lens' SVG texture that only appears highlighted within the illuminated areas."
