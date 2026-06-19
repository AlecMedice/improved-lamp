import * as THREE from "three";
import { Environment } from "./Environment";

/**
 * In-world beacons for hunter stakeout pings: a bright vertical beam + ground ring
 * at each ping, so the team can actually navigate to a marked spot. Driven by the
 * shared ping state (created/removed by the server); hunters only.
 */
export class PingField {
  private items = new Map<string, THREE.Object3D>();

  constructor(private scene: THREE.Scene, private env: Environment) {}

  add(id: string, x: number, z: number) {
    if (this.items.has(id)) return;
    const g = new THREE.Group();

    const beam = new THREE.Mesh(
      new THREE.CylinderGeometry(0.12, 0.12, 14, 8),
      new THREE.MeshBasicMaterial({ color: 0xffe24a, transparent: true, opacity: 0.4 })
    );
    beam.position.y = 7;

    const ring = new THREE.Mesh(
      new THREE.RingGeometry(0.6, 0.9, 18),
      new THREE.MeshBasicMaterial({ color: 0xffe24a, transparent: true, opacity: 0.7, side: THREE.DoubleSide })
    );
    ring.rotation.x = -Math.PI / 2;
    ring.position.y = 0.12;

    g.add(beam, ring);
    g.position.set(x, this.env.getHeight(x, z), z);
    this.scene.add(g);
    this.items.set(id, g);
  }

  remove(id: string) {
    const obj = this.items.get(id);
    if (obj) {
      this.scene.remove(obj);
      this.items.delete(id);
    }
  }
}
