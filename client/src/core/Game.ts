import * as THREE from "three";
import { NET } from "../config";
import { Environment } from "../world/Environment";
import { LocalPlayer } from "../entities/LocalPlayer";
import { Input } from "./Input";
import { Network } from "./Network";
import { HUD } from "../ui/HUD";

/** Wires renderer + world + player + input + networking into one loop. */
export class Game {
  private renderer: THREE.WebGLRenderer;
  private scene = new THREE.Scene();
  private camera: THREE.PerspectiveCamera;
  private clock = new THREE.Clock();
  private env: Environment;
  private player: LocalPlayer;
  private input: Input;
  private net: Network;
  private hud = new HUD();
  private sendAccum = 0;

  constructor(canvas: HTMLCanvasElement, role: string, name: string) {
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(window.innerWidth, window.innerHeight);
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    this.renderer.toneMappingExposure = 1.15;

    this.camera = new THREE.PerspectiveCamera(72, window.innerWidth / window.innerHeight, 0.1, 600);
    this.scene.add(this.camera); // so the flashlight (a child) renders

    this.env = new Environment(this.scene);
    this.player = new LocalPlayer(this.camera, this.env);

    this.input = new Input(canvas);
    this.input.setLookHandler((dx, dy) => this.player.look(dx, dy));
    this.input.onPress("KeyF", () => this.player.toggleFlashlight());

    this.net = new Network(this.scene, role, name);
    this.net.onStatus = (s) => this.hud.setStatus(s);
    this.net.onPhase = (phase, tod) => this.hud.setPhase(phase, tod);
    void this.net.connect();

    this.hud.setEvidence(0, 3);
    window.addEventListener("resize", () => this.onResize());
  }

  start() {
    this.renderer.setAnimationLoop(() => this.frame());
  }

  private frame() {
    const dt = Math.min(0.05, this.clock.getDelta());
    this.player.update(dt, this.input);
    this.env.update(this.clock.elapsedTime);
    this.net.update(dt);

    // Stream our transform to the server at a fixed rate.
    this.sendAccum += dt;
    if (this.sendAccum >= 1 / NET.sendHz) {
      this.sendAccum = 0;
      this.net.sendMove({
        x: this.player.position.x,
        y: this.player.position.y,
        z: this.player.position.z,
        ry: this.player.yawAngle,
        flashlightOn: this.player.isFlashlightOn,
        battery: this.player.battery,
        stamina: this.player.stamina,
      });
    }

    this.hud.setBattery(this.player.battery);
    this.hud.setStamina(this.player.stamina);
    this.renderer.render(this.scene, this.camera);
  }

  private onResize() {
    this.camera.aspect = window.innerWidth / window.innerHeight;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(window.innerWidth, window.innerHeight);
  }
}
