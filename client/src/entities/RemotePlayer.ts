import * as THREE from "three";

/** A networked other player — a smooth low-poly avatar that interpolates toward server state. */
export class RemotePlayer {
  readonly group = new THREE.Group();
  readonly isBigfoot: boolean;

  private target = new THREE.Vector3();
  private targetYaw = 0;
  private flashlight: THREE.SpotLight;
  private recLight?: THREE.Mesh; // small red marker shown while this hunter records

  constructor(private scene: THREE.Scene, role: string) {
    this.isBigfoot = role === "bigfoot";
    const color = this.isBigfoot ? 0x5b4636 : 0x6a7b8c;
    const h = this.isBigfoot ? 2.6 : 1.8;

    const mat = new THREE.MeshStandardMaterial({ color, roughness: 1 });
    const body = new THREE.Mesh(new THREE.CapsuleGeometry(this.isBigfoot ? 0.6 : 0.35, h - 0.8, 6, 10), mat);
    body.position.y = h / 2;
    const head = new THREE.Mesh(new THREE.SphereGeometry(this.isBigfoot ? 0.45 : 0.28, 12, 10), mat);
    head.position.y = h - 0.1;
    this.group.add(body, head);

    if (this.isBigfoot) {
      // Faint eye-shine so hunters can spot Bigfoot in the dark (and then film it).
      const eyeMat = new THREE.MeshBasicMaterial({ color: 0xffe27a });
      const eyeGeo = new THREE.SphereGeometry(0.06, 8, 6);
      for (const dx of [-0.16, 0.16]) {
        const eye = new THREE.Mesh(eyeGeo, eyeMat);
        eye.position.set(dx, h - 0.05, -0.4);
        this.group.add(eye);
      }
    } else {
      // Hunter's recording tally light (hidden unless filming) — tension for Bigfoot.
      this.recLight = new THREE.Mesh(
        new THREE.SphereGeometry(0.09, 8, 6),
        new THREE.MeshBasicMaterial({ color: 0xff2a2a })
      );
      this.recLight.position.set(0, h + 0.25, 0);
      this.recLight.visible = false;
      this.group.add(this.recLight);
    }

    this.flashlight = new THREE.SpotLight(0xfff2d6, 0, 50, 0.5, 0.4, 1.5);
    this.flashlight.position.set(0, h - 0.3, 0);
    this.flashlight.target.position.set(0, h - 0.5, -2);
    this.group.add(this.flashlight, this.flashlight.target);

    scene.add(this.group);
  }

  /** Server sends feet-height y, so place the group directly there. */
  setTarget(x: number, y: number, z: number, ry: number, flashlightOn: boolean) {
    this.target.set(x, y, z);
    this.targetYaw = ry;
    this.flashlight.intensity = flashlightOn ? 90 : 0;
  }

  setFilming(on: boolean) {
    if (this.recLight) this.recLight.visible = on;
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
