import * as THREE from "three";
import { NET, FILM, NIGHT_SECONDS, CAVES, CAVE, ABILITY, MAP, PLAYER } from "../config";
import { Environment } from "../world/Environment";
import { ClueField } from "../world/ClueField";
import { PingField } from "../world/PingField";
import { LocalPlayer } from "../entities/LocalPlayer";
import { Input } from "./Input";
import { Network, SelfInfo } from "./Network";
import { HUD } from "../ui/HUD";
import { MapView } from "../ui/MapView";

/** Wires renderer + world + player + input + networking into one loop. */
export class Game {
  private renderer: THREE.WebGLRenderer;
  private scene = new THREE.Scene();
  private camera: THREE.PerspectiveCamera;
  private clock = new THREE.Clock();
  private env: Environment;
  private clues: ClueField;
  private pings?: PingField; // hunters only
  private player: LocalPlayer;
  private input: Input;
  private net: Network;
  private hud = new HUD();
  private map = new MapView();
  private canvas: HTMLCanvasElement;

  private readonly isBigfoot: boolean;
  private sendAccum = 0;
  private timeOfDay = 0;
  private serverTimeOfDay: number | null = null;
  private self: SelfInfo = { status: "active", filmProgress: 0, role: "searcher", slowed: false };
  private ended = false;
  private caveCooldown = 0;
  private roarCooldown = 0;
  private night = 1;
  private totalNights = 3;

  // reused scratch vectors (avoid per-frame allocation)
  private fwd = new THREE.Vector3();
  private toBf = new THREE.Vector3();

  constructor(canvas: HTMLCanvasElement, role: string, name: string) {
    this.isBigfoot = role === "bigfoot";
    this.canvas = canvas;

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
    // Bigfoot starts at a random cave; searchers start scattered around the campfire.
    const spawn = this.isBigfoot ? caveMouthSpawn() : campfireSpawn();
    this.player = new LocalPlayer(this.camera, this.env, role, spawn);

    if (this.isBigfoot) {
      // A constant dim floor so Bigfoot can navigate once the night turns black.
      this.scene.add(new THREE.HemisphereLight(0x34405e, 0x10131c, 0.5));
    }

    this.input = new Input(canvas);
    this.input.setLookHandler((dx, dy) => this.player.look(dx, dy));
    this.input.onPress("KeyM", () => this.toggleMap());
    if (!this.isBigfoot) this.input.onPress("KeyF", () => this.player.toggleFlashlight());
    this.map.onSelectCave = (i) => this.travelToCave(i);

    this.net = new Network(this.scene, role, name);
    this.net.onStatus = (s) => this.hud.setStatus(s);
    this.net.onPhase = (_phase, tod) => (this.serverTimeOfDay = tod);
    this.net.onNight = (n, total) => this.onNightChange(n, total);
    this.net.onFootage = (have, need) => this.hud.setFootage(have, need);
    this.net.onSelf = (info) => (this.self = info);
    this.net.onClueAdd = (c) => this.clues.add(c);
    this.net.onClueRemove = (id) => this.clues.remove(id);
    this.net.onEnd = (winner) => this.endMatch(winner);

    // Bigfoot abilities: right-click roar, left-click grab a frozen hunter.
    if (this.isBigfoot) {
      this.input.onMousePress(2, () => this.tryRoar());
      this.input.onMousePress(0, () => this.tryGrab());
    }

    // Hunters: stakeout pings (Q to mark where you stand, or click the map).
    if (!this.isBigfoot) {
      this.pings = new PingField(this.scene, this.env);
      this.net.onPingAdd = (id, x, z) => this.pings!.add(id, x, z);
      this.net.onPingRemove = (id) => this.pings!.remove(id);
      this.input.onPress("KeyQ", () => this.dropPing());
      this.map.onMapClick = (x, z) => this.dropPing(x, z);
    }
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

    const incapacitated = this.self.status === "incapacitated";
    const controlsLocked = this.self.status !== "active"; // frozen or incapacitated
    const locked = this.ended || controlsLocked || this.map.isOpen;

    this.player.externalSpeedMul = this.self.slowed ? PLAYER.slowFactor : 1;
    if (!locked) this.player.update(dt, this.input);
    else if (incapacitated) {
      // Bigfoot is dragging us — follow the server's authoritative position.
      const sp = this.net.getSelfPosition();
      if (sp) this.player.teleportTo(sp.x, sp.z);
    }

    // Night clock: prefer the server's; otherwise advance locally (offline/solo).
    if (this.serverTimeOfDay !== null) this.timeOfDay = this.serverTimeOfDay;
    else if (!this.ended) this.timeOfDay = Math.min(1, this.timeOfDay + dt / NIGHT_SECONDS);
    this.env.setTimeOfDay(this.timeOfDay);
    this.hud.setNight(this.night, this.totalNights, this.timeOfDay);

    this.env.update(t);
    this.clues.update(t);
    this.net.update(dt);

    this.hud.setStatusBanner(this.ended ? "active" : this.self.status);
    this.hud.setBlackout(incapacitated && !this.ended);

    // Bigfoot: ability readout + cave-travel prompt.
    if (this.isBigfoot && !this.ended) {
      this.roarCooldown = Math.max(0, this.roarCooldown - dt);
      this.hud.setAbility(this.roarCooldown > 0 ? `Roar: ${Math.ceil(this.roarCooldown)}s` : "Roar ready (right-click)");
      this.caveCooldown = Math.max(0, this.caveCooldown - dt);
      const caveReady = this.caveCooldown === 0 && this.nearestCaveIndex() >= 0;
      this.hud.setPrompt(caveReady && !this.map.isOpen ? "Press M — choose a cave to travel to" : null);
    }

    // Live map refresh while open.
    if (this.map.isOpen) {
      const hunter = !this.isBigfoot;
      this.map.refresh({
        ownX: this.player.position.x,
        ownZ: this.player.position.z,
        yaw: this.player.yawAngle,
        travelMode: this.isBigfoot && this.caveCooldown === 0 && this.nearestCaveIndex() >= 0,
        currentCave: this.nearestCaveIndex(),
        others: hunter ? this.net.getRemoteSearchers() : [],
        clues: hunter && this.clueVisionActive() ? this.clues.getRecentDots(MAP.clueWindow) : [],
        pings: hunter ? this.net.getPings() : [],
      });
    }

    // Filming (hunters only): hold right mouse to record; Bigfoot in frame builds footage.
    let recording = false;
    let inView = false;
    if (!this.isBigfoot) {
      if (!locked) {
        recording = this.input.isMouseDown(2);
        inView = recording && this.computeBigfootInView();
      }
      this.hud.setRecording(recording, inView); // recording=false hides the viewfinder
      this.hud.setFilmProgress(this.self.filmProgress);
    }

    // Stream our transform + intent to the server at a fixed rate.
    this.sendAccum += dt;
    if (this.sendAccum >= 1 / NET.sendHz) {
      this.sendAccum = 0;
      if (!locked) {
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

  /** Map only shows the trail when the hunter hears Bigfoot nearby or sees recent evidence. */
  private clueVisionActive(): boolean {
    const p = this.player.position;
    const bf = this.net.getBigfootPosition();
    if (bf) {
      const dx = bf.x - p.x;
      const dz = bf.z - p.z;
      if (dx * dx + dz * dz < MAP.hearRange * MAP.hearRange) return true;
    }
    return this.clues.hasRecentClueWithin(p.x, p.z, MAP.evidenceSight, MAP.clueWindow);
  }

  private tryRoar() {
    if (!this.isBigfoot || this.ended || this.map.isOpen || this.roarCooldown > 0) return;
    this.net.sendRoar();
    this.roarCooldown = ABILITY.roarCooldown;
  }

  private tryGrab() {
    if (!this.isBigfoot || this.ended || this.map.isOpen) return;
    this.net.sendGrab();
  }

  private onNightChange(night: number, total: number) {
    this.totalNights = total;
    if (night !== this.night) {
      this.night = night;
      if (!this.ended) this.hud.fade(() => {}); // brief fade between nights
    }
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
    return !this.env.lineBlocked(this.player.position, bf);
  }

  /** Index of a cave whose mouth Bigfoot is standing in, or -1. */
  private nearestCaveIndex(): number {
    const p = this.player.position;
    const r2 = CAVE.triggerRadius * CAVE.triggerRadius;
    for (let i = 0; i < CAVES.length; i++) {
      const dx = CAVES[i].x - p.x;
      const dz = CAVES[i].z - p.z;
      if (dx * dx + dz * dz <= r2) return i;
    }
    return -1;
  }

  /** Bigfoot picks a destination cave from the map and emerges from its mouth. */
  private travelToCave(i: number) {
    if (!this.isBigfoot || this.ended || this.caveCooldown > 0) return;
    const here = this.nearestCaveIndex();
    if (here < 0 || i === here || i < 0 || i >= CAVES.length) return;
    const dest = CAVES[i];
    const dl = Math.hypot(dest.x, dest.z) || 1;
    this.caveCooldown = CAVE.travelCooldown;
    this.closeMap(); // synchronous (keeps the pointer-lock user gesture)
    // Fade to black, hop at the darkest point, fade back in at the new cave.
    this.hud.fade(() => this.player.teleportTo(dest.x - (dest.x / dl) * 1.5, dest.z - (dest.z / dl) * 1.5));
  }

  /** Drop a stakeout ping at (x,z), or at the player's feet if not given. */
  private dropPing(x?: number, z?: number) {
    if (this.isBigfoot || this.ended || this.self.status !== "active") return;
    this.net.sendPing(x ?? this.player.position.x, z ?? this.player.position.z);
  }

  private toggleMap() {
    if (this.ended) return;
    if (this.map.isOpen) {
      this.closeMap();
    } else {
      this.map.open();
      this.hud.setPrompt(null);
      this.input.allowPointerLock = false;
      document.exitPointerLock();
    }
  }

  private closeMap() {
    this.map.close();
    this.input.allowPointerLock = true;
    if (!this.ended) this.canvas.requestPointerLock();
  }

  private endMatch(winner: string) {
    if (this.ended) return;
    this.ended = true;
    this.hud.setPrompt(null);
    this.map.close();
    document.exitPointerLock();
    const youWon = winner === (this.isBigfoot ? "bigfoot" : "hunters");
    const title = winner === "hunters" ? "The footage is secured" : "The forest keeps its secret";
    const body =
      winner === "hunters"
        ? "Enough solid video. The expedition makes it out with proof Bigfoot is real."
        : "Bigfoot outlasted three nights. The expedition goes home with nothing.";
    this.hud.showEnd(youWon ? "VICTORY" : "DEFEAT", `${title}. ${body}`);
  }

  private onResize() {
    this.camera.aspect = window.innerWidth / window.innerHeight;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(window.innerWidth, window.innerHeight);
  }
}

/** A scatter point around the campfire for searcher spawns. */
function campfireSpawn(): { x: number; z: number } {
  const a = Math.random() * Math.PI * 2;
  const r = 4 + Math.random() * 3;
  return { x: Math.cos(a) * r, z: Math.sin(a) * r };
}

/** A random cave mouth (offset toward map centre) for the Bigfoot spawn. */
function caveMouthSpawn(): { x: number; z: number } {
  const cave = CAVES[Math.floor(Math.random() * CAVES.length)];
  const dl = Math.hypot(cave.x, cave.z) || 1;
  return { x: cave.x - (cave.x / dl) * 1.5, z: cave.z - (cave.z / dl) * 1.5 };
}
