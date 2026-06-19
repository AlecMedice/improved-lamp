import * as THREE from "three";
import { Environment } from "./Environment";

/** Visual fade window (seconds) for a clue — roughly matches the server's CLUE_LIFETIME. */
const CLUE_FADE = 50;

export type ClueData = { id: string; ctype: string; x: number; z: number; ry: number };

/**
 * Renders the hint trail Bigfoot leaves behind: low-poly footprints and broken
 * branches that the hunters track. Clues are spawned/removed by the server; this
 * just mirrors them and fades each one as its track goes cold.
 */
export class ClueField {
  private items = new Map<string, { obj: THREE.Object3D; born: number }>();
  private now = 0;

  constructor(private scene: THREE.Scene, private env: Environment) {}

  add(c: ClueData) {
    if (this.items.has(c.id)) return;
    const obj = c.ctype === "branch" ? this.makeBranch() : this.makeFootprint();
    obj.position.set(c.x, this.env.getHeight(c.x, c.z) + 0.03, c.z);
    obj.rotation.y = c.ry;
    this.scene.add(obj);
    this.items.set(c.id, { obj, born: this.now });
  }

  remove(id: string) {
    const entry = this.items.get(id);
    if (entry) {
      this.scene.remove(entry.obj);
      this.items.delete(id);
    }
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
