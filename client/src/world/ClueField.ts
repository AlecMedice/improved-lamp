import * as THREE from "three";
import { Environment } from "./Environment";
import { SENSES } from "../config";
import { CLUE_LIFETIME } from "../../../shared/sim";

/** Visual fade window (seconds) for a clue — the server's clue lifetime, shared so the two can't drift. */
const CLUE_FADE = CLUE_LIFETIME;

export type ClueData = { id: string; ctype: string; x: number; z: number; ry: number };

/**
 * Renders the hint trail Yeti leaves behind: low-poly footprints and broken
 * branches that the hunters track. Clues are spawned/removed by the server; this
 * just mirrors them and fades each one as its track goes cold.
 */
export class ClueField {
  private items = new Map<string, { obj: THREE.Object3D; born: number; halo: THREE.Mesh; ctype: string }>();
  private now = 0;
  private scentOn = false; // Yeti senses overlay: reveal the recent trail through the forest

  /**
   * Whether the local player is the Yeti. Snow prints are replicated to everyone (Colyseus state
   * is shared) and filtered HERE, at the renderer: a searcher's client receives its teammates'
   * prints and simply never builds a mesh for them.
   */
  constructor(private scene: THREE.Scene, private env: Environment, private isYeti = false) {}

  add(c: ClueData) {
    if (this.items.has(c.id)) return;
    if (c.ctype === "snowprint" && !this.isYeti) return; // searchers can't see their own tracks
    const obj = c.ctype === "snowprint"
      ? this.makeSnowPrint()
      : c.ctype === "branch" ? this.makeBranch() : this.makeFootprint();
    obj.position.set(c.x, this.env.getHeight(c.x, c.z) + 0.03, c.z);
    obj.rotation.y = c.ry;
    // A depthTest-off scent marker riding the clue — shown only when Yeti's senses are on.
    const halo = new THREE.Mesh(
      new THREE.SphereGeometry(0.16, 8, 6),
      new THREE.MeshBasicMaterial({ color: SENSES.scentColor, transparent: true, opacity: 0.85, depthTest: false, depthWrite: false })
    );
    halo.position.y = 0.35;
    halo.renderOrder = 999;
    halo.visible = this.scentOn;
    obj.add(halo);
    this.scene.add(obj);
    this.items.set(c.id, { obj, born: this.now, halo, ctype: c.ctype });
  }

  /** Toggle the Yeti scent-trail markers on every current clue. */
  setSensed(on: boolean) {
    this.scentOn = on;
    for (const { halo } of this.items.values()) halo.visible = on;
  }

  remove(id: string) {
    const entry = this.items.get(id);
    if (entry) {
      this.scene.remove(entry.obj);
      this.items.delete(id);
    }
  }

  /** (x,z) of clues seen within the last `maxAge` seconds — the map only shows recent tracks. */
  getRecentDots(maxAge: number, kind: "trail" | "snowprint" = "trail"): Array<{ x: number; z: number }> {
    const out: Array<{ x: number; z: number }> = [];
    for (const { obj, born, ctype } of this.items.values()) {
      // On the Yeti's client `items` holds BOTH its own trail and the searchers' prints, so this
      // has to discriminate — otherwise the map would draw the Yeti its own footprints as targets.
      if ((ctype === "snowprint") !== (kind === "snowprint")) continue;
      if (this.now - born <= maxAge) out.push({ x: obj.position.x, z: obj.position.z });
    }
    return out;
  }

  /** True if a recent clue sits within `range` of (x,z) — i.e. the hunter "sees evidence". */
  hasRecentClueWithin(x: number, z: number, range: number, maxAge: number): boolean {
    const r2 = range * range;
    for (const { obj, born, ctype } of this.items.values()) {
      if (ctype === "snowprint") continue; // a searcher's own tracks are not evidence of the Yeti
      if (this.now - born > maxAge) continue;
      const dx = obj.position.x - x;
      const dz = obj.position.z - z;
      if (dx * dx + dz * dz <= r2) return true;
    }
    return false;
  }

  /** Fade clues toward (but not to) invisible as they age — "the trail goes cold". */
  update(t: number) {
    this.now = t;
    for (const { obj, born } of this.items.values()) {
      const opacity = THREE.MathUtils.clamp(1 - (t - born) / CLUE_FADE, 0.18, 1);
      obj.traverse((o) => {
        const m = (o as THREE.Mesh).material as THREE.Material & { opacity?: number };
        if (m && m.opacity !== undefined) {
          m.transparent = true;
          m.opacity = opacity;
        }
      });
    }
  }

  private makeFootprint(): THREE.Mesh {
    const geo = new THREE.SphereGeometry(0.5, 8, 6);
    geo.scale(0.18, 0.03, 0.3); // a flat, elongated pad pointing along +/-z
    const mat = new THREE.MeshStandardMaterial({
      color: 0x241a12,
      roughness: 1,
      emissive: 0x140d08,
      emissiveIntensity: 0.6, // faintly visible at night; pops under a flashlight
      transparent: true,
    });
    return new THREE.Mesh(geo, mat);
  }

  /**
   * A searcher's boot pair pressed into deep snow. Only ever built on the Yeti's client.
   *
   * Deliberately smaller and colder than the Yeti's own footprint so the two never read as the
   * same trail: the Yeti has to be able to tell "someone walked here" from "this is where I walked".
   */
  private makeSnowPrint(): THREE.Object3D {
    const g = new THREE.Group();
    const mat = new THREE.MeshStandardMaterial({
      color: 0x14263a,
      roughness: 1,
      emissive: 0x0d1b2c,
      emissiveIntensity: 0.5,
      transparent: true,
    });
    for (const off of [-0.12, 0.12]) {
      const geo = new THREE.SphereGeometry(0.5, 8, 6);
      geo.scale(0.1, 0.02, 0.17);
      const pad = new THREE.Mesh(geo, mat);
      pad.position.set(off, 0, 0);
      g.add(pad);
    }
    return g;
  }

  private makeBranch(): THREE.Object3D {
    const g = new THREE.Group();
    const mat = new THREE.MeshStandardMaterial({ color: 0x4a3526, roughness: 1, transparent: true });
    const a = new THREE.Mesh(new THREE.CylinderGeometry(0.05, 0.06, 0.9, 6), mat);
    a.rotation.z = Math.PI / 2;
    a.position.y = 0.05;
    const b = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.05, 0.5, 6), mat);
    b.rotation.z = Math.PI / 2;
    b.rotation.y = 0.6; // snapped off at an angle
    b.position.set(0.35, 0.05, 0.12);
    g.add(a, b);
    return g;
  }
}
