import * as THREE from "three";
import { AudioEngine } from "../core/AudioEngine";
import { SENSES } from "../config";

/** A timestamped server snapshot for interpolation. */
type Snapshot = { t: number; x: number; y: number; z: number; ry: number };

// Render remotes slightly behind the latest snapshot and interpolate between the two that bracket
// "now − delay". At the server's 20 Hz patch rate this turns chase-the-latest jitter into smooth,
// constant-velocity motion. A jump larger than SNAP_DIST is a teleport (cave travel) — snap instead.
const INTERP_DELAY = 0.1; // seconds rendered behind the newest snapshot (~2 patches of slack)
const SNAP_DIST = 3; // metres; a larger step is a teleport, not movement

/** Proportions for an articulated low-poly avatar. All lengths in metres; the rig sits feet-on-ground. */
type Build = {
  hipY: number; hipX: number; legLen: number; legR: number;
  torsoLen: number; torsoR: number; shoulderY: number; shoulderX: number;
  armLen: number; armR: number; headR: number; headY: number;
  hunch: number; // forward pitch of the upper body (Bigfoot leans; hunters stand)
};

const HUNTER_BUILD: Build = {
  hipY: 0.9, hipX: 0.16, legLen: 0.66, legR: 0.12,
  torsoLen: 0.62, torsoR: 0.26, shoulderY: 1.55, shoulderX: 0.30,
  armLen: 0.6, armR: 0.09, headR: 0.23, headY: 1.86, hunch: 0,
};
const BIGFOOT_BUILD: Build = {
  hipY: 1.15, hipX: 0.26, legLen: 0.82, legR: 0.19,
  torsoLen: 0.98, torsoR: 0.46, shoulderY: 2.18, shoulderX: 0.52,
  armLen: 1.05, armR: 0.16, headR: 0.34, headY: 2.5, hunch: 0.32,
};

/** A networked other player — a smooth low-poly avatar that interpolates toward server state. */
export class RemotePlayer {
  readonly group = new THREE.Group();
  readonly isBigfoot: boolean;

  private buffer: Snapshot[] = []; // recent server snapshots, oldest → newest
  private flashlight: THREE.SpotLight;
  private recLight?: THREE.Mesh; // small red marker shown while this hunter records
  private statusIcon?: THREE.Mesh; // floats above a frozen/incapacitated hunter
  private statusMat?: THREE.MeshBasicMaterial;
  private senseSilhouette?: THREE.Group; // depthTest-off glow so the local Bigfoot sees prey through trees
  private beingRevived = false; // pulse the incap icon while a teammate is reviving this hunter
  private pulseT = 0;

  // Articulated rig — limb pivots swung by a ground-locked walk cycle.
  private rig = new THREE.Group();
  private legL!: THREE.Group;
  private legR!: THREE.Group;
  private armL!: THREE.Group;
  private armR!: THREE.Group;
  private armBase = 0; // resting forward pitch of the arms (Bigfoot's hang forward)
  private animPhase = 0; // radians; one full cycle = two strides
  private speed = 0; // smoothed ground speed (m/s), drives swing amplitude
  private idleT = 0; // free-running clock for the idle breathing sway

  // Positional footsteps: accrue ground distance covered and emit a step each stride.
  private stepDist = 0;
  private lastX = 0;
  private lastZ = 0;
  private readonly stride: number;

  constructor(private scene: THREE.Scene, role: string, private audio?: AudioEngine) {
    this.isBigfoot = role === "bigfoot";
    this.stride = this.isBigfoot ? 2.3 : 1.7; // Bigfoot's gait is longer/heavier
    const b = this.isBigfoot ? BIGFOOT_BUILD : HUNTER_BUILD;
    this.armBase = b.hunch * 0.7; // arms follow the lean

    this.group.add(this.rig);
    this.buildRig(b);

    if (this.isBigfoot) {
      // Faint eye-shine so hunters can spot Bigfoot in the dark (and then film it). Rides the head.
      const eyeMat = new THREE.MeshBasicMaterial({ color: 0xffe27a });
      const eyeGeo = new THREE.SphereGeometry(0.055, 8, 6);
      const head = this.rig.getObjectByName("head")!;
      for (const dx of [-0.14, 0.14]) {
        const eye = new THREE.Mesh(eyeGeo, eyeMat);
        eye.position.set(dx, 0.04, -b.headR - 0.02); // front of the face (−Z is forward)
        head.add(eye);
      }
    } else {
      // Hunter's recording tally light (hidden unless filming) — tension for Bigfoot.
      this.recLight = new THREE.Mesh(
        new THREE.SphereGeometry(0.09, 8, 6),
        new THREE.MeshBasicMaterial({ color: 0xff2a2a })
      );
      this.recLight.position.set(0, b.headY + 0.4, 0);
      this.recLight.visible = false;
      this.group.add(this.recLight);

      // Status icon: cyan when frozen (a grab target), dim red when incapacitated.
      this.statusMat = new THREE.MeshBasicMaterial({ color: 0x7fe0ff });
      this.statusIcon = new THREE.Mesh(new THREE.OctahedronGeometry(0.16), this.statusMat);
      this.statusIcon.position.set(0, b.headY + 0.7, 0);
      this.statusIcon.visible = false;
      this.group.add(this.statusIcon);

      // Sense silhouette: a slightly-inflated capsule/head drawn on top (depthTest off) so the local
      // Bigfoot's predator vision reveals this hunter through trees. Hidden until senses are on.
      const senseMat = new THREE.MeshBasicMaterial({
        color: SENSES.hunterColor, transparent: true, opacity: SENSES.hunterOpacity,
        depthTest: false, depthWrite: false,
      });
      const sBody = new THREE.Mesh(new THREE.CapsuleGeometry(0.42, 1.0, 6, 10), senseMat);
      sBody.position.y = b.hipY;
      const sHead = new THREE.Mesh(new THREE.SphereGeometry(0.34, 12, 10), senseMat);
      sHead.position.y = b.headY;
      this.senseSilhouette = new THREE.Group();
      this.senseSilhouette.add(sBody, sHead);
      this.senseSilhouette.renderOrder = 999; // composite after the scene so it shows through geometry
      this.senseSilhouette.visible = false;
      this.group.add(this.senseSilhouette);
    }

    this.flashlight = new THREE.SpotLight(0xfff2d6, 0, 50, 0.5, 0.4, 1.5);
    this.flashlight.position.set(0, b.shoulderY, 0);
    this.flashlight.target.position.set(0, b.shoulderY - 0.2, -2);
    this.group.add(this.flashlight, this.flashlight.target);

    scene.add(this.group);
  }

  /**
   * Build the articulated avatar: two legs + two arms on pivots, a torso and a head under an
   * "upper body" group (leant forward for Bigfoot). Smooth-shaded capsules/spheres keep the
   * low-poly-but-not-blocky house style. Limbs pivot at the top so a rotation swings them.
   */
  private buildRig(b: Build) {
    const bf = this.isBigfoot;
    // Palettes: hunters read as a person (jacket/pants/skin/boots); Bigfoot is fur with a dark face.
    const jacket = new THREE.MeshStandardMaterial({ color: bf ? 0x5b4636 : 0x6a7b8c, roughness: 1 });
    const pants = new THREE.MeshStandardMaterial({ color: bf ? 0x4a382a : 0x3c4657, roughness: 1 });
    const skin = new THREE.MeshStandardMaterial({ color: bf ? 0x3f3024 : 0xcaa889, roughness: 1 });

    // A capsule limb whose TOP sits at the group origin, so rotating the group swings it from the joint.
    const limb = (len: number, r: number, mat: THREE.Material) => {
      const geo = new THREE.CapsuleGeometry(r, len, 5, 8);
      geo.translate(0, -(len / 2 + r), 0);
      return new THREE.Mesh(geo, mat);
    };

    // Legs (swing under the rig so they stay upright even when the torso leans).
    for (const side of [-1, 1] as const) {
      const pivot = new THREE.Group();
      pivot.position.set(side * b.hipX, b.hipY, 0);
      pivot.add(limb(b.legLen, b.legR, pants));
      this.rig.add(pivot);
      if (side < 0) this.legL = pivot; else this.legR = pivot;
    }

    // Upper body (torso + head + arms) — leant forward by `hunch` for Bigfoot.
    const upper = new THREE.Group();
    upper.position.y = b.hipY;
    upper.rotation.x = b.hunch;
    this.rig.add(upper);

    const torsoGeo = new THREE.CapsuleGeometry(b.torsoR, b.torsoLen, 6, 12);
    const torso = new THREE.Mesh(torsoGeo, jacket);
    torso.position.y = (b.shoulderY - b.hipY) * 0.5;
    if (bf) torso.scale.set(1.15, 1, 0.8); // barrel chest, front-to-back slab
    upper.add(torso);

    const head = new THREE.Mesh(new THREE.SphereGeometry(b.headR, 14, 12), skin);
    head.name = "head";
    head.position.y = b.headY - b.hipY;
    if (bf) { head.scale.set(1, 0.92, 1.1); head.position.z = -0.05; } // heavy brow, jutting muzzle
    upper.add(head);

    // Arms hang from the shoulders (Bigfoot's are long and rest forward).
    const localShoulderY = b.shoulderY - b.hipY;
    for (const side of [-1, 1] as const) {
      const pivot = new THREE.Group();
      pivot.position.set(side * b.shoulderX, localShoulderY, 0);
      pivot.rotation.x = this.armBase;
      pivot.add(limb(b.armLen, b.armR, bf ? skin : jacket));
      upper.add(pivot);
      if (side < 0) this.armL = pivot; else this.armR = pivot;
    }
  }

  /** Server sends feet-height y, so place the group directly there. Buffered for interpolation. */
  setTarget(x: number, y: number, z: number, ry: number, flashlightOn: boolean) {
    const now = performance.now() / 1000;
    const last = this.buffer[this.buffer.length - 1];
    // Teleport (cave travel / first packet) → drop history so we snap rather than slide across the map.
    if (last && Math.hypot(x - last.x, z - last.z) > SNAP_DIST) this.buffer.length = 0;
    this.buffer.push({ t: now, x, y, z, ry });
    if (this.buffer.length > 12) this.buffer.shift();
    this.flashlight.intensity = flashlightOn ? 90 : 0;
  }

  setFilming(on: boolean) {
    if (this.recLight) this.recLight.visible = on;
  }

  setStatus(status: string) {
    if (!this.statusIcon || !this.statusMat) return;
    if (status === "frozen") {
      this.statusIcon.visible = true;
      this.statusMat.color.setHex(0x7fe0ff);
    } else if (status === "incapacitated") {
      this.statusIcon.visible = true;
      this.statusMat.color.setHex(0xff5a5a);
    } else {
      this.statusIcon.visible = false;
    }
  }

  /** Local Bigfoot's senses overlay: reveal this hunter's silhouette through the environment. */
  setSensed(on: boolean) {
    if (this.senseSilhouette) this.senseSilhouette.visible = on;
  }

  /** A teammate is reviving this downed hunter — pulse the red icon so allies can see the rescue. */
  setBeingRevived(on: boolean) {
    this.beingRevived = on;
    if (!on && this.statusIcon) this.statusIcon.scale.setScalar(1);
  }

  update(dt: number) {
    this.applyInterpolation();
    this.tickFootsteps();
    this.animate(dt);
    if (this.beingRevived && this.statusIcon) {
      this.pulseT += dt;
      this.statusIcon.scale.setScalar(1 + 0.4 * Math.sin(this.pulseT * 10));
    }
  }

  /** Place the avatar at the interpolated position for "now − INTERP_DELAY". */
  private applyInterpolation() {
    const buf = this.buffer;
    if (buf.length === 0) return;
    if (buf.length === 1) {
      this.group.position.set(buf[0].x, buf[0].y, buf[0].z);
      this.group.rotation.y = buf[0].ry;
      return;
    }
    const renderT = performance.now() / 1000 - INTERP_DELAY;
    const newest = buf[buf.length - 1];
    let a = buf[0];
    let b = newest;
    if (renderT <= buf[0].t) {
      b = a; // older than anything we have — hold the oldest
    } else if (renderT >= newest.t) {
      a = newest; // newer than anything — hold the latest (no extrapolation drift)
    } else {
      for (let i = 0; i < buf.length - 1; i++) {
        if (renderT >= buf[i].t && renderT <= buf[i + 1].t) {
          a = buf[i];
          b = buf[i + 1];
          break;
        }
      }
    }
    const span = b.t - a.t;
    const k = span > 1e-4 ? Math.max(0, Math.min(1, (renderT - a.t) / span)) : 0;
    this.group.position.set(a.x + (b.x - a.x) * k, a.y + (b.y - a.y) * k, a.z + (b.z - a.z) * k);
    let d = b.ry - a.ry;
    d = Math.atan2(Math.sin(d), Math.cos(d)); // shortest arc
    this.group.rotation.y = a.ry + d * k;
  }

  /**
   * Walk cycle. The phase is locked to ground distance covered (like footsteps), so it never
   * desyncs from motion and freezes cleanly when the avatar stops; a small idle sway keeps a
   * standing avatar alive. Legs swing opposite each other, arms counter the legs, and the body
   * bobs a touch each step. Amplitude scales with speed so a slow creep barely moves.
   */
  private animate(dt: number) {
    this.idleT += dt;
    const walk = Math.min(1, this.speed / (this.isBigfoot ? 3.5 : 3.0)); // 0 idle → 1 at cruising pace
    const legAmp = 0.7 * walk;
    const armAmp = 0.5 * walk;
    const s = Math.sin(this.animPhase);
    const idle = Math.sin(this.idleT * 1.6) * 0.05 * (1 - walk); // gentle breathing at rest

    this.legL.rotation.x = s * legAmp;
    this.legR.rotation.x = -s * legAmp;
    this.armL.rotation.x = this.armBase - s * armAmp;
    this.armR.rotation.x = this.armBase + s * armAmp;
    // Bob twice per cycle (once per footfall) plus the resting breath.
    this.rig.position.y = Math.abs(Math.sin(this.animPhase)) * 0.05 * walk + idle;
  }

  /** Emit a positional footstep each stride of ground covered (cadence scales with speed). */
  private tickFootsteps() {
    const x = this.group.position.x;
    const z = this.group.position.z;
    const moved = Math.hypot(x - this.lastX, z - this.lastZ);
    this.lastX = x;
    this.lastZ = z;

    if (moved > 3) return; // a jump this large is a teleport (cave travel / first frame), not a step

    // Advance the walk cycle with ground distance (π per stride → 2π = a full left+right cycle) and
    // track a smoothed speed for the swing amplitude. Assumes ~60 fps for the m/s estimate; it only
    // gates amplitude, so the exact rate is not critical.
    this.animPhase += moved * (Math.PI / this.stride);
    this.speed += (moved * 60 - this.speed) * 0.15;

    if (!this.audio) return;
    this.stepDist += moved;
    if (this.stepDist >= this.stride) {
      this.stepDist = 0;
      this.audio.playAt(this.isBigfoot ? "footstep_heavy" : "footstep_soft", x, z, {
        volume: this.isBigfoot ? 0.7 : 0.4,
        refDistance: 7,
      });
    }
  }

  dispose() {
    this.scene.remove(this.group);
  }
}
