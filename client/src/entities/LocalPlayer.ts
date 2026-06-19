import * as THREE from "three";
import { PLAYER } from "../config";
import { Input } from "../core/Input";
import { Environment } from "../world/Environment";

/** First-person controller + flashlight for the player at this keyboard. */
export class LocalPlayer {
  readonly camera: THREE.PerspectiveCamera;
  readonly flashlight: THREE.SpotLight;
  readonly position = new THREE.Vector3(0, 0, 18); // eye position
  readonly isBigfoot: boolean;
  battery = 100;
  stamina = 100;
  groundY = 0; // terrain height under the player (= feet height)

  private env: Environment;
  private eyeHeight: number;
  private speedMul: number;
  private yaw = 0;
  private pitch = 0;
  private flashlightOn = false;

  constructor(camera: THREE.PerspectiveCamera, env: Environment, role: string) {
    this.camera = camera;
    this.env = env;
    this.isBigfoot = role === "bigfoot";
    this.eyeHeight = this.isBigfoot ? 2.3 : PLAYER.eyeHeight;
    this.speedMul = this.isBigfoot ? PLAYER.bigfootSpeedMul : 1;

    this.groundY = env.getHeight(this.position.x, this.position.z);
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
  }

  look(dx: number, dy: number) {
    this.yaw -= dx * PLAYER.mouseSensitivity;
    this.pitch -= dy * PLAYER.mouseSensitivity;
    const lim = Math.PI / 2 - 0.05;
    this.pitch = Math.max(-lim, Math.min(lim, this.pitch));
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

    const moving = wish.lengthSq() > 0;
    const sprinting = moving && input.isDown("ShiftLeft") && this.stamina > 0;
    const speed = (sprinting ? PLAYER.sprintSpeed : PLAYER.walkSpeed) * this.speedMul;
    if (moving) this.position.addScaledVector(wish.normalize(), speed * dt);

    // Keep inside the world, push out of trees, then sit on the terrain.
    const half = 198;
    this.position.x = THREE.MathUtils.clamp(this.position.x, -half, half);
    this.position.z = THREE.MathUtils.clamp(this.position.z, -half, half);
    const resolved = this.env.resolveCollision(this.position.x, this.position.z, PLAYER.radius);
    this.position.x = resolved.x;
    this.position.z = resolved.z;
    this.groundY = this.env.getHeight(this.position.x, this.position.z);
    this.position.y = this.groundY + this.eyeHeight;
    this.camera.position.copy(this.position);

    // Resources.
    this.stamina = sprinting
      ? Math.max(0, this.stamina - PLAYER.staminaDrainPerSec * dt)
      : Math.min(100, this.stamina + PLAYER.staminaRegenPerSec * dt);

    if (this.flashlightOn) {
      this.battery = Math.max(0, this.battery - PLAYER.batteryDrainPerSec * dt);
      if (this.battery <= 0) {
        this.flashlightOn = false;
        this.flashlight.intensity = 0;
      }
    }
  }
}
