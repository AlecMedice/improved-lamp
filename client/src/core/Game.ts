import * as THREE from "three";
import { NET, FILM, MATCH_SECONDS } from "../config";
import { Environment } from "../world/Environment";
import { ClueField } from "../world/ClueField";
import { LocalPlayer } from "../entities/LocalPlayer";
import { Input } from "./Input";
import { Network, SelfInfo } from "./Network";
import { HUD } from "../ui/HUD";

/** Wires renderer + world + player + input + networking into one loop. */
export class Game {
  private renderer: THREE.WebGLRenderer;
  private scene = new THREE.Scene();
  private camera: THREE.PerspectiveCamera;
  private clock = new THREE.Clock();
  private env: Environment;
  private clues: ClueField;
  private player: LocalPlayer;
  private input: Input;
  private net: Network;
  private hud = new HUD();

  private readonly isBigfoot: boolean;
  private sendAccum = 0;
  private timeOfDay = 0;
  private serverTimeOfDay: number | null = null;
  private serverPhase: string | null = null;
  private self: SelfInfo = { status: "active", filmProgress: 0, role: "searcher" };
  private ended = false;

  // reused scratch vectors (avoid per-frame allocation)
  private fwd = new THREE.Vector3();
  private toBf = new THREE.Vector3();

  constructor(canvas: HTMLCanvasElement, role: string, name: string) {
    this.isBigfoot = role === "bigfoot";

    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.setSize(window.innerWidth, window.innerHeight);
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    // Bigfoot sees better in the dark (its own render only — scenes are per-client).
    this.renderer.toneMappingExposure = this.isBigfoot ? 1.7 : 1.15;

    this.camera = new THREE.PerspectiveCamera(72, window.innerWidth / window.innerHeight, 0.1, 600);
    this.scene.add(this.camera); // so the flashlight (a child) renders

    this.env = new Environment(this.scene);
    this.clues = new ClueField(this.scene, this.env);
    this.player = new LocalPlayer(this.camera, this.env, role);

    if (this.isBigfoot) {
      // A constant dim floor so Bigfoot can navigate once the night turns black.
      this.scene.add(new THREE.HemisphereLight(0x34405e, 0x10131c, 0.5));
    }

    this.input = new Input(canvas);
    this.input.setLookHandler((dx, dy) => this.player.look(dx, dy));
    if (!this.isBigfoot) this.input.onPress("KeyF", () => this.player.toggleFlashlight());

    this.net = new Network(this.scene, role, name);
    this.net.onStatus = (s) => this.hud.setStatus(s);
    this.net.onPhase = (phase, tod) => {
      this.serverPhase = phase;
      this.serverTimeOfDay = tod;
    };
    this.net.onFootage = (have, need) => this.hud.setFootage(have, need);
    this.net.onSelf = (info) => (this.self = info);
    this.net.onClueAdd = (c) => this.clues.add(c);
    this.net.onClueRemove = (id) => this.clues.remove(id);
    this.net.onEnd = (winner) => this.endMatch(winner);
    void this.net.connect();

    this.hud.setFootage(0, 3);
    this.hud.setRole(this.isBigfoot ? "bigfoot" : "searcher");
    window.addEventListener("resize", () => this.onResize());
  }

  start() {
    this.renderer.setAnimationLoop(() => this.frame());
  }

  private frame() {
    const dt = Math.min(0.05, this.clock.getDelta());
    const t = this.clock.elapsedTime;

    const caught = this.self.status !== "active";
    if (!this.ended && !caught) this.player.update(dt, this.input);

    // Day/night: prefer the server's clock; otherwise advance locally (offline/solo).
    if (this.serverTimeOfDay !== null) this.timeOfDay = this.serverTimeOfDay;
    else if (!this.ended) this.timeOfDay = Math.min(1, this.timeOfDay + dt / MATCH_SECONDS);
    this.env.setTimeOfDay(this.timeOfDay);
    this.hud.setPhase(this.serverPhase ?? phaseFor(this.timeOfDay), this.timeOfDay);

    this.env.update(t);
    this.clues.update(t);
    this.net.update(dt);

    // Filming (hunters only): hold right mouse to record; Bigfoot in frame builds footage.
    let recording = false;
    let inView = false;
    if (!this.isBigfoot) {
      if (!this.ended && !caught) {
        recording = this.input.isMouseDown(2);
        inView = recording && this.computeBigfootInView();
      }
      this.hud.setRecording(recording, inView); // recording=false hides the viewfinder
      this.hud.setFilmProgress(this.self.filmProgress);
    }
    this.hud.setCaught(caught && !this.ended);

    // Stream our transform + intent to the server at a fixed rate.
    this.sendAccum += dt;
    if (this.sendAccum >= 1 / NET.sendHz) {
      this.sendAccum = 0;
      if (!this.ended && !caught) {
        this.net.sendMove({
          x: this.player.position.x,
          y: this.player.groundY,
          z: this.player.position.z,
          ry: this.player.yawAngle,
          flashlightOn: this.player.isFlashlightOn,
          battery: this.player.battery,
          stamina: this.player.stamina,
          recording,
          inView,
        });
      }
    }

    this.hud.setBattery(this.player.battery);
    this.hud.setStamina(this.player.stamina);
    this.renderer.render(this.scene, this.camera);
  }

  /** Is Bigfoot within range, centred in frame, and not hidden behind a trunk? */
  private computeBigfootInView(): boolean {
    const bf = this.net.getBigfootPosition();
    if (!bf) return false;
    this.toBf.copy(bf).setY(bf.y + FILM.aimHeight).sub(this.player.position);
    const dist = this.toBf.length();
    if (dist > FILM.range) return false;
    this.toBf.divideScalar(dist); // normalize
    this.camera.getWorldDirection(this.fwd);
    if (this.fwd.dot(this.toBf) < Math.cos(THREE.MathUtils.degToRad(FILM.halfFovDeg))) return false;
    return !this.env.lineBlockedByTrees(this.player.position, bf);
  }

  private endMatch(winner: string) {
    if (this.ended) return;
    this.ended = true;
    document.exitPointerLock();
    const youWon = winner === (this.isBigfoot ? "bigfoot" : "hunters");
    const title = winner === "hunters" ? "The footage is secured" : "The forest goes quiet";
    const body =
      winner === "hunters"
        ? "Three solid videos. The expedition makes it out with proof Bigfoot is real."
        : "The expedition is lost to the dark. Bigfoot keeps its secret.";
    this.hud.showEnd(youWon ? "VICTORY" : "DEFEAT", `${title}. ${body}`);
  }

  private onResize() {
    this.camera.aspect = window.innerWidth / window.innerHeight;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(window.innerWidth, window.innerHeight);
  }
}

/** Client-side phase lookup (used when running offline without the server). */
function phaseFor(t: number): string {
  if (t < 0.15) return "dusk";
  if (t < 0.45) return "nightfall";
  if (t < 0.75) return "midnight";
  if (t < 0.95) return "witching";
  return "dawn";
}
