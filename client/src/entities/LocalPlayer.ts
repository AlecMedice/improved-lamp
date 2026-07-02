import * as THREE from "three";
import { PLAYER, BIGFOOT_VISION } from "../config";
import { Input } from "../core/Input";
import { Environment } from "../world/Environment";
import { AudioEngine } from "../core/AudioEngine";
import { stepPlayer, type PlayerSimState, type MoveInput, type StepResult, type StepModifiers, type World } from "../../../shared/sim";

/**
 * First-person controller + flashlight for the player at this keyboard. Movement physics now
 * lives in the shared sim (`stepPlayer`) so the client and the authoritative server agree;
 * this class owns the camera, look, flashlight, head-bob, footstep audio — the presentation.
 */
export class LocalPlayer {
  readonly camera: THREE.PerspectiveCamera;
  readonly flashlight: THREE.SpotLight;
  /** Bigfoot's dim short-range night sight (rides the camera; undefined for hunters). */
  readonly visionLight?: THREE.SpotLight;
  readonly position = new THREE.Vector3(); // eye position (derived from sim each frame)
  readonly isBigfoot: boolean;
  externalSpeedMul = 1; // set by Game (e.g. 0.75 while slowed after incapacitation)

  chargeMul = 1; // Bigfoot charge burst (1 = not charging; set by Game while a charge window is open)

  // Per-night escalation (set by Game from server-replicated multipliers; 1 = night-1 baseline).
  nightSpeedMul = 1; // Bigfoot grows faster on later nights (hunters stay 1)
  batteryDrainMul = 1; // flashlight drains faster on later nights
  staminaDrainMul = 1; // sprinting tires you faster on later nights

  /** Authoritative-shaped physics state, advanced by the shared stepPlayer. */
  readonly sim: PlayerSimState;

  private env: Environment;
  private world: World;
  private audio?: AudioEngine;
  private pitch = 0; // look pitch (yaw lives in sim.yaw); camera-only
  private bobPhase = 0;
  private bobY = 0;
  private stepTimer = 0;

  constructor(camera: THREE.PerspectiveCamera, env: Environment, role: string, spawn: { x: number; z: number; yaw?: number }, audio?: AudioEngine) {
    this.camera = camera;
    this.env = env;
    this.world = env.simWorld;
    this.audio = audio;
    this.isBigfoot = role === "bigfoot";
    const eyeHeight = this.isBigfoot ? 2.3 : PLAYER.eyeHeight;
    const gy = env.getHeight(spawn.x, spawn.z);

    this.sim = {
      x: spawn.x,
      z: spawn.z,
      feetY: gy,
      groundY: gy,
      vy: 0,
      grounded: true,
      yaw: spawn.yaw ?? 0,
      stamina: 100,
      exhausted: false,
      battery: 100,
      curEye: eyeHeight,
      flashlightOn: false,
      isBigfoot: this.isBigfoot,
      eyeHeight,
    };
    this.position.set(spawn.x, gy + eyeHeight, spawn.z);

    // The flashlight rides the camera and points where you look (hunters use it).
    this.flashlight = new THREE.SpotLight(0xfff2d6, 0, 60, 0.5, 0.4, 1.5);
    this.flashlight.position.set(0, 0, 0);
    this.flashlight.target.position.set(0, 0, -1);
    camera.add(this.flashlight, this.flashlight.target);

    // Bigfoot has no flashlight; instead a dim, short-range sight cone rides the camera so it can
    // see a near bubble but loses the far scene to darkness (weaker/shorter than a flashlight).
    if (this.isBigfoot) {
      const v = BIGFOOT_VISION;
      this.visionLight = new THREE.SpotLight(0xcfe0ff, v.intensity, v.range, v.angle, v.penumbra, 1.2);
      this.visionLight.position.set(0, 0, 0);
      this.visionLight.target.position.set(0, 0, -1);
      camera.add(this.visionLight, this.visionLight.target);
    }
  }

  get isFlashlightOn() {
    return this.sim.flashlightOn;
  }
  get yawAngle() {
    return this.sim.yaw;
  }
  get battery() {
    return this.sim.battery;
  }
  get stamina() {
    return this.sim.stamina;
  }
  get feetY() {
    return this.sim.feetY;
  }
  get groundY() {
    return this.sim.groundY;
  }

  toggleFlashlight() {
    if (this.isBigfoot) return; // Bigfoot has no flashlight
    if (this.sim.battery <= 0 && !this.sim.flashlightOn) return;
    this.sim.flashlightOn = !this.sim.flashlightOn;
    this.flashlight.intensity = this.sim.flashlightOn ? 140 : 0;
    this.audio?.playFlashlightToggle();
  }

  look(dx: number, dy: number) {
    this.sim.yaw -= dx * PLAYER.mouseSensitivity;
    this.pitch -= dy * PLAYER.mouseSensitivity;
    const lim = Math.PI / 2 - 0.05;
    this.pitch = Math.max(-lim, Math.min(lim, this.pitch));
  }

  /**
   * Softly ease toward a server-corrected (x,z). Unlike teleportTo this blends rather than
   * snapping, so a small authoritative correction (collision/speed clamp) is smoothed out.
   */
  correctTo(x: number, z: number, ease: number) {
    this.sim.x += (x - this.sim.x) * ease;
    this.sim.z += (z - this.sim.z) * ease;
    this.sim.groundY = this.env.getHeight(this.sim.x, this.sim.z);
    if (this.sim.grounded) this.sim.feetY = this.sim.groundY;
    this.position.set(this.sim.x, this.sim.feetY + this.sim.curEye, this.sim.z);
    this.camera.position.copy(this.position);
    this.camera.position.y += this.bobY;
  }

  /** Instantly move to a new (x,z) — used by Bigfoot's cave fast-travel. */
  teleportTo(x: number, z: number, yaw?: number) {
    const gy = this.env.getHeight(x, z);
    this.sim.x = x;
    this.sim.z = z;
    this.sim.groundY = gy;
    this.sim.feetY = gy; // land on arrival — never mid-jump
    this.sim.vy = 0;
    this.sim.grounded = true;
    if (yaw !== undefined) this.sim.yaw = yaw;
    this.position.set(x, gy + this.sim.curEye, z);
    this.camera.position.copy(this.position);
  }

  /** Build the movement command from the keyboard — also the payload streamed to the server. */
  buildInput(input: Input, dt: number): MoveInput {
    const space = input.isDown("Space");
    return {
      w: input.isDown("KeyW"),
      s: input.isDown("KeyS"),
      a: input.isDown("KeyA"),
      d: input.isDown("KeyD"),
      yaw: this.sim.yaw,
      // Space is a leap for Bigfoot (stamina-gated bound) and a normal jump for hunters; for hunters
      // it also engages a vault when standing on a fallen log (the sim picks vault over jump there).
      jump: !this.isBigfoot && space,
      leap: this.isBigfoot && space,
      vault: !this.isBigfoot && space,
      sprint: input.isDown("ShiftLeft"),
      crouch: input.isDown("ControlLeft") || input.isDown("ControlRight"),
      dt,
    };
  }

  update(dt: number, input: Input) {
    const cmd = this.buildInput(input, dt);
    // Compose the sim modifiers from the incapacitation slow + server-replicated escalation.
    const mods: StepModifiers = {
      speedMul: this.externalSpeedMul * this.nightSpeedMul * this.chargeMul,
      batteryDrainMul: this.batteryDrainMul,
      staminaDrainMul: this.staminaDrainMul,
    };
    const res = stepPlayer(this.sim, cmd, this.world, mods);
    this.applyPresentation(dt, cmd, res);
  }

  /** Camera, head-bob, footsteps, flashlight visuals — everything that is client-only. */
  private applyPresentation(dt: number, cmd: MoveInput, res: StepResult) {
    this.camera.rotation.set(this.pitch, this.sim.yaw, 0, "YXZ");
    this.position.set(this.sim.x, this.sim.feetY + this.sim.curEye, this.sim.z);

    // Head-bob: sinusoidal vertical offset on the camera only (only while walking on the ground).
    const bobFreq = res.sprinting ? PLAYER.bobFreqSprint : PLAYER.bobFreqWalk;
    const bobAmp = res.sprinting ? PLAYER.bobAmpSprint : PLAYER.bobAmpWalk;
    if (res.moving && this.sim.grounded) {
      this.bobPhase += dt * bobFreq * Math.PI * 2;
      this.bobY += (Math.sin(this.bobPhase) * bobAmp - this.bobY) * Math.min(1, dt * 10);
    } else {
      this.bobY += (0 - this.bobY) * Math.min(1, dt * 8);
    }

    this.camera.position.copy(this.position);
    this.camera.position.y += this.bobY;

    // Footstep audio: only on the ground; slower, quieter cadence while crouching (stealth).
    if (this.audio) {
      if (res.moving && this.sim.grounded) {
        this.stepTimer -= dt;
        if (this.stepTimer <= 0) {
          this.audio.playFootstep(res.sprinting, this.isBigfoot);
          this.stepTimer = res.sprinting
            ? PLAYER.stepIntervalSprint
            : PLAYER.stepIntervalWalk * (cmd.crouch ? 1.6 : 1);
        }
      } else {
        this.stepTimer = 0;
      }
    }

    // Sync the flashlight beam if the sim turned it off (battery drained to 0).
    if (!this.sim.flashlightOn) this.flashlight.intensity = 0;
  }
}
