import * as THREE from "three";
import { PLAYER, ESCALATION } from "../config";
import { Input } from "../core/Input";
import { Environment } from "../world/Environment";
import { AudioManager } from "../core/Audio";

/** First-person controller + flashlight for the player at this keyboard. */
export class LocalPlayer {
  readonly camera: THREE.PerspectiveCamera;
  readonly flashlight: THREE.SpotLight;
  readonly position = new THREE.Vector3(); // eye position (set from spawn)
  readonly isBigfoot: boolean;
  battery = 100;
  stamina = 100;
  groundY = 0; // terrain height under the player
  feetY = 0; // actual feet height (above groundY while jumping); this is what we network
  externalSpeedMul = 1; // set by Game (e.g. 0.75 while slowed after incapacitation)
  night = 1; // current night (set by Game) — drives per-night escalation

  private env: Environment;
  private audio?: AudioManager;
  private eyeHeight: number;
  private speedMul: number;
  private yaw = 0;
  private pitch = 0;
  private flashlightOn = false;
  private exhausted = false; // true once stamina hits 0, until it recovers past the threshold
  private bobPhase = 0;
  private bobY = 0;
  private stepTimer = 0;
  private vy = 0; // vertical velocity while airborne
  private grounded = true;
  private curEye: number; // current (eased) eye height — lerps toward standing/crouched

  constructor(camera: THREE.PerspectiveCamera, env: Environment, role: string, spawn: { x: number; z: number; yaw?: number }, audio?: AudioManager) {
    this.camera = camera;
    this.env = env;
    this.audio = audio;
    this.isBigfoot = role === "bigfoot";
    this.eyeHeight = this.isBigfoot ? 2.3 : PLAYER.eyeHeight;
    this.curEye = this.eyeHeight;
    this.speedMul = this.isBigfoot ? PLAYER.bigfootSpeedMul : 1;
    this.yaw = spawn.yaw ?? 0;

    this.position.set(spawn.x, 0, spawn.z);
    this.groundY = env.getHeight(spawn.x, spawn.z);
    this.feetY = this.groundY;
    this.position.y = this.groundY + this.eyeHeight;

    // The flashlight rides the camera and points where you look (hunters use it).
    this.flashlight = new THREE.SpotLight(0xfff2d6, 0, 60, 0.5, 0.4, 1.5);
    this.flashlight.position.set(0, 0, 0);
    this.flashlight.target.position.set(0, 0, -1);
    camera.add(this.flashlight, this.flashlight.target);
  }

  get isFlashlightOn() {
    return this.flashlightOn;
  }
  get yawAngle() {
    return this.yaw;
  }

  toggleFlashlight() {
    if (this.isBigfoot) return; // Bigfoot has no flashlight
    if (this.battery <= 0 && !this.flashlightOn) return;
    this.flashlightOn = !this.flashlightOn;
    this.flashlight.intensity = this.flashlightOn ? 140 : 0;
    this.audio?.playFlashlightToggle();
  }

  look(dx: number, dy: number) {
    this.yaw -= dx * PLAYER.mouseSensitivity;
    this.pitch -= dy * PLAYER.mouseSensitivity;
    const lim = Math.PI / 2 - 0.05;
    this.pitch = Math.max(-lim, Math.min(lim, this.pitch));
  }

  /** Instantly move to a new (x,z) — used by Bigfoot's cave fast-travel. */
  teleportTo(x: number, z: number, yaw?: number) {
    this.position.x = x;
    this.position.z = z;
    this.groundY = this.env.getHeight(x, z);
    this.feetY = this.groundY; // land on arrival — never mid-jump
    this.vy = 0;
    this.grounded = true;
    if (yaw !== undefined) this.yaw = yaw;
    this.position.y = this.groundY + this.curEye;
    this.camera.position.copy(this.position);
  }

  update(dt: number, input: Input) {
    this.camera.rotation.set(this.pitch, this.yaw, 0, "YXZ");

    // Movement is relative to yaw only (so looking up/down doesn't fly you around).
    const forward = new THREE.Vector3(-Math.sin(this.yaw), 0, -Math.cos(this.yaw));
    const right = new THREE.Vector3(Math.cos(this.yaw), 0, -Math.sin(this.yaw));
    const wish = new THREE.Vector3();
    if (input.isDown("KeyW")) wish.add(forward);
    if (input.isDown("KeyS")) wish.sub(forward);
    if (input.isDown("KeyD")) wish.add(right);
    if (input.isDown("KeyA")) wish.sub(right);

    const crouching = input.isDown("ControlLeft") || input.isDown("ControlRight");
    const moving = wish.lengthSq() > 0;
    const sprinting = moving && input.isDown("ShiftLeft") && !this.exhausted && !crouching;
    let speed = (sprinting ? PLAYER.sprintSpeed : PLAYER.walkSpeed) * this.speedMul * this.externalSpeedMul;
    if (crouching) speed *= PLAYER.crouchSpeedMul;
    // Escalation: Bigfoot grows faster each night.
    if (this.isBigfoot) speed *= 1 + ESCALATION.bigfootSpeedPerNight * (this.night - 1);

    // Terrain obstacles: fallen logs slow hunters only; lake slows everyone (less so Bigfoot).
    if (!this.isBigfoot) {
      const logOvl = this.env.logOverlap(this.position.x, this.position.z, PLAYER.radius);
      if (logOvl > 0) speed *= THREE.MathUtils.lerp(1, PLAYER.logSlowFactor, logOvl);
    }
    const lakeDep = this.env.lakeDepth(this.position.x, this.position.z);
    if (lakeDep > 0) {
      speed *= THREE.MathUtils.lerp(1, this.isBigfoot ? PLAYER.lakeBigfootFactor : PLAYER.lakeHunterFactor, lakeDep);
    }

    if (moving) this.position.addScaledVector(wish.normalize(), speed * dt);

    // Keep inside the world, push out of trees, then sit on the terrain.
    const half = 398;
    this.position.x = THREE.MathUtils.clamp(this.position.x, -half, half);
    this.position.z = THREE.MathUtils.clamp(this.position.z, -half, half);
    // Save intended position (post-clamp) so the step check can compare it.
    const ix = this.position.x;
    const iz = this.position.z;
    const resolved = this.env.resolveCollision(ix, iz, PLAYER.radius);
    const wasPushed = (resolved.x - ix) ** 2 + (resolved.z - iz) ** 2 > 1e-4;
    this.position.x = resolved.x;
    this.position.z = resolved.z;
    this.groundY = this.env.getHeight(this.position.x, this.position.z);

    // Auto-step: if a collider pushed us back but the terrain at the intended spot
    // is only a small rise, lift over it rather than sliding around the obstacle.
    if (wasPushed && this.grounded && moving) {
      const destGY = this.env.getHeight(ix, iz);
      const rise = destGY - this.groundY;
      if (rise >= 0 && rise <= PLAYER.stepHeight) {
        this.position.x = ix;
        this.position.z = iz;
        this.groundY = destGY;
      }
    }

    // Vertical: jump + gravity. feetY is the true feet height; >= groundY while airborne.
    if (this.grounded && input.isDown("Space") && !crouching) {
      this.vy = PLAYER.jumpSpeed;
      this.grounded = false;
    }
    if (this.grounded) {
      this.feetY = this.groundY; // ride the terrain as it rises/falls
    } else {
      this.vy -= PLAYER.gravity * dt;
      this.feetY += this.vy * dt;
      if (this.feetY <= this.groundY) {
        this.feetY = this.groundY;
        this.vy = 0;
        this.grounded = true;
      }
    }

    // Crouch: ease the eye height toward standing or crouched.
    const targetEye = crouching ? this.eyeHeight * PLAYER.crouchFactor : this.eyeHeight;
    this.curEye += (targetEye - this.curEye) * Math.min(1, dt * PLAYER.eyeLerp);
    this.position.y = this.feetY + this.curEye;

    // Head-bob: sinusoidal vertical offset on the camera only (only while walking on the ground).
    const bobFreq = sprinting ? PLAYER.bobFreqSprint : PLAYER.bobFreqWalk;
    const bobAmp = sprinting ? PLAYER.bobAmpSprint : PLAYER.bobAmpWalk;
    if (moving && this.grounded) {
      this.bobPhase += dt * bobFreq * Math.PI * 2;
      this.bobY += (Math.sin(this.bobPhase) * bobAmp - this.bobY) * Math.min(1, dt * 10);
    } else {
      this.bobY += (0 - this.bobY) * Math.min(1, dt * 8);
    }

    this.camera.position.copy(this.position);
    this.camera.position.y += this.bobY;

    // Footstep audio: only on the ground; slower, quieter cadence while crouching (stealth).
    if (this.audio) {
      if (moving && this.grounded) {
        this.stepTimer -= dt;
        if (this.stepTimer <= 0) {
          this.audio.playFootstep(sprinting);
          this.stepTimer = sprinting
            ? PLAYER.stepIntervalSprint
            : PLAYER.stepIntervalWalk * (crouching ? 1.6 : 1);
        }
      } else {
        this.stepTimer = 0;
      }
    }

    // Escalation: gear drains faster each night (battery + sprint stamina).
    const drainEsc = 1 + ESCALATION.hunterDrainPerNight * (this.night - 1);

    // Resources. Hitting 0 stamina exhausts you: no sprinting until it recovers past a threshold.
    this.stamina = sprinting
      ? Math.max(0, this.stamina - PLAYER.staminaDrainPerSec * drainEsc * dt)
      : Math.min(100, this.stamina + PLAYER.staminaRegenPerSec * dt);
    if (this.stamina <= 0) this.exhausted = true;
    else if (this.exhausted && this.stamina >= PLAYER.staminaRecover) this.exhausted = false;

    if (this.flashlightOn) {
      this.battery = Math.max(0, this.battery - PLAYER.batteryDrainPerSec * drainEsc * dt);
      if (this.battery <= 0) {
        this.flashlightOn = false;
        this.flashlight.intensity = 0;
      }
    }
  }
}
