import * as THREE from "three";
import { PLAYER } from "../config";

/** A networked other player — a smooth low-poly avatar that interpolates toward server state. */
export class RemotePlayer {
  readonly group = new THREE.Group();
  private target = new THREE.Vector3();
  private targetYaw = 0;
  private flashlight: THREE.SpotLight;

  constructor(private scene: THREE.Scene, role: string) {
    const isBig = role === "bigfoot";
    const color = isBig ? 0x5b4636 : 0x6a7b8c;
    const h = isBig ? 2.6 : 1.8;

    const mat = new THREE.MeshStandardMaterial({ color, roughness: 1 });
    const body = new THREE.Mesh(new THREE.CapsuleGeometry(isBig ? 0.6 : 0.35, h - 0.8, 6, 10), mat);
    body.position.y = h / 2;
    const head = new THREE.Mesh(new THREE.SphereGeometry(isBig ? 0.45 : 0.28, 12, 10), mat);
    head.position.y = h - 0.1;
    this.group.add(body, head);

    this.flashlight = new THREE.SpotLight(0xfff2d6, 0, 50, 0.5, 0.4, 1.5);
    this.flashlight.position.set(0, h - 0.3, 0);
    this.flashlight.target.position.set(0, h - 0.5, -2);
    this.group.add(this.flashlight, this.flashlight.target);

    scene.add(this.group);
  }

  /** Server sends eye-height y; place feet on the ground by subtracting eye height. */
  setTarget(x: number, y: number, z: number, ry: number, flashlightOn: boolean) {
    this.target.set(x, y - PLAYER.eyeHeight, z);
    this.targetYaw = ry;
    this.flashlight.intensity = flashlightOn ? 90 : 0;
  }

  update(dt: number) {
    const k = Math.min(1, dt * 10);
    this.group.position.lerp(this.target, k);
    let d = this.targetYaw - this.group.rotation.y;
    d = Math.atan2(Math.sin(d), Math.cos(d)); // shortest arc
    this.group.rotation.y += d * k;
  }

  dispose() {
    this.scene.remove(this.group);
  }
}
