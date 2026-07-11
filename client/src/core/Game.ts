import * as THREE from "three";
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer.js";
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass.js";
import { ShaderPass } from "three/examples/jsm/postprocessing/ShaderPass.js";
import { OutputPass } from "three/examples/jsm/postprocessing/OutputPass.js";
import { NET, FILM, NIGHT_SECONDS, CAVES, CAVE, ABILITY, CHARGE, REVIVE, MAP, PLAYER, BIGFOOT_VISION, SENSES, POST, QUALITY, isMobile } from "../config";

/** Screen-space vignette + moving film grain, composited after bloom (replaces the old CSS vignette). */
const VignetteGrainShader = {
  uniforms: { tDiffuse: { value: null }, time: { value: 0 }, vignette: { value: POST.vignette }, grain: { value: POST.grain } },
  vertexShader: /* glsl */ `varying vec2 vUv; void main(){ vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
  fragmentShader: /* glsl */ `
    uniform sampler2D tDiffuse; uniform float time; uniform float vignette; uniform float grain; varying vec2 vUv;
    float hash(vec2 p){ p = fract(p * vec2(123.34, 345.45)); p += dot(p, p + 34.345); return fract(p.x * p.y); }
    void main(){
      vec4 c = texture2D(tDiffuse, vUv);
      float r = length(vUv - 0.5);
      float vig = smoothstep(0.85, 0.30, r * vignette); // 1 at centre -> 0 at the edges
      c.rgb *= mix(0.32, 1.0, vig);
      c.rgb += (hash(vUv * vec2(1920.0, 1080.0) + time) - 0.5) * grain; // subtle moving grain
      gl_FragColor = c;
    }`,
};
import { Environment } from "../world/Environment";
import { ClueField } from "../world/ClueField";
import { PingField } from "../world/PingField";
import { LocalPlayer } from "../entities/LocalPlayer";
import { RemotePlayer } from "../entities/RemotePlayer";
import {
  climbSupport, nearestCaveIndex, caveEmergePoint, SPECIALTY_IDS,
  staminaMax as staminaMaxFor, staminaDrainMul as staminaDrainMulFor,
  clueWindowMul, evidenceSightMul, hearRangeMul, reviveMul,
} from "../../../shared/sim";
import { Input } from "./Input";
import { Network, SelfInfo, EscalationInfo } from "./Network";
import { Room } from "colyseus.js";
import { HUD } from "../ui/HUD";
import { Settings, SettingsData } from "./Settings";
import { Keybinds } from "./Keybinds";
import { SettingsMenu } from "../ui/SettingsMenu";
import { Briefing } from "../ui/Briefing";
import { MapView } from "../ui/MapView";
import { AudioEngine } from "./AudioEngine";

// Client reconciliation tuning: how the local player eases toward the server's authoritative
// position. IGNORE absorbs ordinary prediction-vs-latency lag (our prediction is slightly ahead
// of the server's last-known state); only larger disagreements ease, and a teleport-grade gap snaps.
const RECONCILE_IGNORE = 1.5; // metres of disagreement tolerated before correcting
const RECONCILE_SNAP = 8; // metres beyond which we snap instead of easing
const RECONCILE_EASE = 8; // ease rate (× dt) when blending out a correction

/** Wires renderer + world + player + input + networking into one loop. */
export class Game {
  private renderer: THREE.WebGLRenderer;
  private composer!: EffectComposer; // post-processing chain (bloom + vignette/grain)
  private fxPass!: ShaderPass; // the vignette/grain pass (time + per-phase vignette uniforms)
  private baseExposure: number; // role-based tone-mapping exposure; the brightness setting scales this
  private settings = new Settings();
  private keybinds = new Keybinds();
  private settingsMenu!: SettingsMenu;
  private briefing = new Briefing();
  private showPerf = new URLSearchParams(location.search).has("perf");
  private perfFps = 60;
  private perfTimer = 0;
  private previews: RemotePlayer[] = []; // dev-only avatar previews (window.__previewAvatars)
  private previewT = 0;
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
  private self: SelfInfo = { status: "active", filmProgress: 0, role: "searcher", slowed: false, dazzled: false, specialty: "", characterName: "" };
  private escStaminaDrain = 1; // latest per-night stamina-drain escalation (composed with specialty in applySpecialtyMods)
  private ended = false;
  private caveCooldown = 0;
  private traveling = false; // suspends local control + move-sends during a cave hop
  private roarCooldown = 0;
  private roarCooldownSec = ABILITY.roarCooldown; // effective, from server escalation
  private chargeTimer = 0; // remaining seconds of an active charge burst (drives chargeMul)
  private chargeCooldown = 0; // remaining seconds until the next charge is ready
  private sensesOn = false; // Bigfoot predator-vision overlay (toggle with V)
  private reviveProgress = 0; // 0..1 local estimate of the teammate revive being held (server-authoritative)
  private reviveTickTimer = 0; // spacing for the revive channel cue while holding
  private reviveWasFull = false; // guards the one-shot success cue
  private night = 1;
  private totalNights = 3;

  private audio: AudioEngine;

  // Audio bookkeeping.
  private prevStatus = "active"; // detect freeze/incap transitions for stings
  private prevFootage = 0; // detect a banked video for the capture ding

  // reused scratch vectors (avoid per-frame allocation)
  private fwd = new THREE.Vector3();
  private toBf = new THREE.Vector3();

  constructor(canvas: HTMLCanvasElement, role: string, name: string, room?: Room) {
    this.isBigfoot = role === "bigfoot";
    this.canvas = canvas;

    const mobile = isMobile();
    this.renderer = new THREE.WebGLRenderer({ canvas, antialias: !mobile, powerPreference: "high-performance" });
    // Pixel ratio is the biggest fragment-cost lever; cap it (lower on hi-dpi mobile).
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, mobile ? QUALITY.pixelRatioCapMobile : QUALITY.pixelRatioCap));
    this.renderer.setSize(window.innerWidth, window.innerHeight);
    this.renderer.info.autoReset = false; // we render several composer passes per frame; reset once/frame
    this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
    // Per-client render (scenes don't leak). Bigfoot no longer gets a blanket brightness buff —
    // its night sight is the dim short-range vision cone (see LocalPlayer); exposure stays near
    // the searcher's so the far scene goes dark beyond that cone.
    this.baseExposure = this.isBigfoot ? BIGFOOT_VISION.exposure : 1.15;
    this.renderer.toneMappingExposure = this.baseExposure;

    this.camera = new THREE.PerspectiveCamera(72, window.innerWidth / window.innerHeight, 0.1, 900);
    this.scene.add(this.camera); // so the flashlight (a child) renders

    // Post-processing: scene -> bloom (bright sources glow) -> vignette/grain -> tone-mapping output.
    this.composer = new EffectComposer(this.renderer);
    this.composer.addPass(new RenderPass(this.scene, this.camera));
    const bloom = new UnrealBloomPass(
      new THREE.Vector2(window.innerWidth, window.innerHeight),
      POST.bloomStrength, POST.bloomRadius, POST.bloomThreshold
    );
    this.composer.addPass(bloom);
    this.fxPass = new ShaderPass(VignetteGrainShader);
    this.composer.addPass(this.fxPass);
    this.composer.addPass(new OutputPass()); // applies renderer tone mapping + sRGB at the end

    // Audio: the listener rides the camera so positional cues pan/fall off correctly.
    this.audio = new AudioEngine(this.scene);
    this.camera.add(this.audio.listener);

    this.env = new Environment(this.scene);
    this.audio.groundSampler = (x, z) => this.env.getHeight(x, z);
    this.map.setBakedMap(this.env.generateMapCanvas(600, 400));
    this.clues = new ClueField(this.scene, this.env);
    // Online: use the server-assigned spawn. Offline/solo: pick one locally.
    const spawn = spawnFor(role, room);
    this.player = new LocalPlayer(this.camera, this.env, role, spawn, this.audio);

    if (this.isBigfoot) {
      // A very faint floor so Bigfoot isn't in pitch black outside its vision cone — but low
      // enough that distance still falls to darkness (the cone is the real sight now).
      this.scene.add(new THREE.HemisphereLight(0x34405e, 0x10131c, 0.12));
    }

    this.input = new Input(canvas, this.keybinds);
    this.input.setLookHandler((dx, dy) => this.player.look(dx, dy));
    this.input.onAction("map", () => this.toggleMap());
    this.input.onPress("Escape", () => this.toggleSettings()); // fixed, not rebindable
    // Debug persona hot-swap: `\` cycles the searcher's character. Only bound when ?devSpecialty is
    // present (a debug session); the server rejects it unless ALLOW_DEV_ROLE, so it's inert in prod.
    if (!this.isBigfoot && new URLSearchParams(location.search).has("devSpecialty")) {
      this.input.onPress("Backslash", () => {
        const i = SPECIALTY_IDS.indexOf(this.self.specialty as (typeof SPECIALTY_IDS)[number]);
        this.net.sendDebugSetSpecialty(SPECIALTY_IDS[(i + 1) % SPECIALTY_IDS.length]);
      });
    }
    if (!this.isBigfoot) this.input.onAction("flashlight", () => this.player.toggleFlashlight());
    this.map.onSelectCave = (i) => this.travelToCave(i);

    // Settings + rebindable controls, persisted; Resume re-locks the pointer.
    this.settingsMenu = new SettingsMenu(
      this.settings,
      this.keybinds,
      this.input,
      (d) => this.applySettings(d),
      () => { this.input.allowPointerLock = true; if (!this.ended) this.canvas.requestPointerLock(); }
    );
    this.applySettings(this.settings.data);

    // Perf readout hook (always available for tooling; the on-screen overlay needs ?perf).
    (window as any).__perf = () => ({
      fps: Math.round(this.perfFps),
      draws: this.renderer.info.render.calls,
      tris: this.renderer.info.render.triangles,
      caveLights: this.env.litCaveLights,
      trees: this.env.visibleTrees,
      pixelRatio: this.renderer.getPixelRatio(),
    });
    if (this.showPerf) { const el = document.getElementById("perf"); if (el) el.style.display = "block"; }
    // Dev-only: drop a hunter + Bigfoot avatar in front of the camera for art/proportion QA.
    (window as any).__previewAvatars = () => this.spawnPreviewAvatars();

    this.net = new Network(this.scene, role, name, room, this.audio);
    this.net.onStatus = (s) => this.hud.setStatus(s);
    this.net.onPhase = (_phase, tod) => (this.serverTimeOfDay = tod);
    this.net.onNight = (n, total) => this.onNightChange(n, total);
    this.net.onFootage = (have, need) => this.onFootage(have, need);
    this.net.onSelf = (info) => {
      this.self = info;
      this.hud.setPersona(info.characterName, info.specialty); // reflects the deal + any debug hot-swap
      this.applySpecialtyMods(); // persona may have changed (deal / hot-swap) → refresh stamina mods
    };
    this.net.onClueAdd = (c) => {
      this.clues.add(c);
      if (c.ctype === "branch") this.audio.playAt("branch_snap", c.x, c.z, { volume: 0.5, refDistance: 14 });
    };
    this.net.onClueRemove = (id) => this.clues.remove(id);
    this.net.onEnd = (winner) => this.endMatch(winner);
    // Host reset us to the lobby — reload back into the waiting room.
    this.net.onReturnToLobby = () => location.reload();
    // Another player's roar, from its real position (carries far: big ref distance, low rolloff).
    this.net.onRoar = (x, z) => this.audio.playAt("roar", x, z, { volume: 0.95, refDistance: 30, rolloff: 0.7 });
    this.net.onEscalation = (e) => this.applyEscalation(e);

    // Bigfoot abilities: right-click roar, left-click grab a frozen hunter, sprint-key charge, senses.
    if (this.isBigfoot) {
      this.input.onMousePress(2, () => this.tryRoar());
      this.input.onMousePress(0, () => this.tryGrab());
      this.input.onAction("sprint", () => this.tryCharge());
      this.input.onAction("senses", () => this.toggleSenses());
    }

    // Hunters: stakeout pings (Q to mark where you stand, or click the map).
    if (!this.isBigfoot) {
      this.pings = new PingField(this.scene, this.env);
      this.net.onPingAdd = (id, x, z) => this.pings!.add(id, x, z);
      this.net.onPingRemove = (id) => this.pings!.remove(id);
      this.input.onAction("ping", () => this.dropPing());
      this.map.onMapClick = (x, z) => this.dropPing(x, z);
    }
    void this.net.connect();

    this.hud.setFootage(0, 3);
    this.hud.setRole(this.isBigfoot ? "bigfoot" : "searcher");
    window.addEventListener("resize", () => this.onResize());
  }

  start() {
    this.audio.resume(); // called from the start gesture — lifts the autoplay gate
    this.renderer.setAnimationLoop(() => this.frame());
    // Dusk briefing first (any key begins); then the drip of one-line reminders.
    this.briefing.show(this.isBigfoot, this.keybinds, this.input, () => {
      if (!this.ended) this.canvas.requestPointerLock();
      this.startTutorial();
    });
  }

  /** Brief, role-specific control hints shown one at a time at the start of a match. */
  private startTutorial() {
    const hints = this.isBigfoot
      ? [
          "You ARE Bigfoot. WASD to move · mouse to look · you're faster than they are.",
          "Right-click to ROAR — freezes nearby hunters in place.",
          "Left-click to GRAB a frozen hunter — erases the team's footage.",
          "Stand in a cave mouth, press M, and pick a cave to fast-travel.",
          "Survive 3 nights to win. Each night you grow stronger.",
        ]
      : [
          "WASD to move · mouse to look · Shift to sprint · Ctrl to crouch.",
          "F toggles your flashlight — watch the battery.",
          "Hold Right-Mouse to film Bigfoot — fill the bar 3 times to win.",
          "M opens the map · Q drops a stakeout ping for your team.",
          "Follow footprints and broken branches. Don't get caught.",
        ];
    let i = 0;
    const show = () => {
      if (this.ended || i >= hints.length) {
        this.hud.setTutorial(null);
        return;
      }
      this.hud.setTutorial(hints[i++]);
      window.setTimeout(show, 4500);
    };
    show();
  }

  /** Dev QA: spawn a hunter + Bigfoot avatar a few metres in front of the camera, facing it. */
  private spawnPreviewAvatars() {
    const fwd = new THREE.Vector3();
    this.camera.getWorldDirection(fwd);
    fwd.y = 0;
    fwd.normalize();
    const right = new THREE.Vector3().crossVectors(fwd, new THREE.Vector3(0, 1, 0)).normalize();
    const base = this.camera.position.clone().addScaledVector(fwd, 6);
    const faceRy = Math.atan2(fwd.x, fwd.z); // turn to look back at the camera (−Z local faces it)
    for (const [role, off] of [["searcher", -1.4], ["bigfoot", 1.6]] as const) {
      const rp = new RemotePlayer(this.scene, role);
      const p = base.clone().addScaledVector(right, off);
      const y = this.env.getHeight(p.x, p.z);
      (rp as any).__base = { x: p.x, y, z: p.z, ry: faceRy };
      rp.setTarget(p.x, y, p.z, faceRy, false);
      this.previews.push(rp);
    }
  }

  /** Rock the preview avatars back and forth so the walk cycle animates in place. */
  private updatePreviews(dt: number) {
    if (this.previews.length === 0) return;
    this.previewT += dt;
    const sway = Math.sin(this.previewT * 2) * 0.4; // ±0.4 m along each avatar's facing
    for (const rp of this.previews) {
      const b = (rp as any).__base as { x: number; y: number; z: number; ry: number };
      const fx = -Math.sin(b.ry);
      const fz = -Math.cos(b.ry);
      rp.setTarget(b.x + fx * sway, b.y, b.z + fz * sway, b.ry, false);
      rp.update(dt);
    }
  }

  private frame() {
    const dt = Math.min(0.05, this.clock.getDelta());
    const t = this.clock.elapsedTime;

    const incapacitated = this.self.status === "incapacitated";
    const controlsLocked = this.self.status !== "active"; // frozen or incapacitated
    const locked = this.ended || controlsLocked || this.map.isOpen || this.traveling || this.settingsMenu.isOpen || this.briefing.isOpen;

    this.player.externalSpeedMul = this.self.slowed ? PLAYER.slowFactor : 1;
    if (!locked) {
      this.player.update(dt, this.input);
      this.reconcile(dt); // ease toward the server's authoritative (validated) position
    } else if (incapacitated) {
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
    this.env.updateLightBudget(this.player.position.x, this.player.position.z, QUALITY.maxCaveLights);
    this.env.updateForestLOD(this.player.position.x, this.player.position.z);
    this.clues.update(t);
    this.net.update(dt);
    this.updatePreviews(dt);

    // Creek ambience swells as you near the water (audible within ~30 m).
    const creekDist = this.env.distanceToCreek(this.player.position.x, this.player.position.z);
    this.audio.setCreekProximity(1 - creekDist / 30);
    this.updateAudio(dt);

    this.hud.setStatusBanner(this.ended ? "active" : this.self.status);
    this.hud.setBlackout(incapacitated && !this.ended);

    // Bigfoot: charge burst timing (drives the sim speed multiplier + UI) + senses overlay.
    if (this.isBigfoot) {
      this.chargeTimer = Math.max(0, this.chargeTimer - dt);
      this.chargeCooldown = Math.max(0, this.chargeCooldown - dt);
      this.player.chargeMul = this.chargeTimer > 0 ? CHARGE.speedMul : 1;
      this.net.refreshSenses(this.sensesOn, this.player.position.x, this.player.position.z, SENSES.range);
    }

    // Bigfoot: ability readout + cave-travel prompt.
    if (this.isBigfoot && !this.ended) {
      this.roarCooldown = Math.max(0, this.roarCooldown - dt);
      // A searcher's sustained flashlight blinds Bigfoot: cut the sight cone and lock roar/grab.
      if (this.player.visionLight) this.player.visionLight.intensity = this.self.dazzled ? 0 : BIGFOOT_VISION.intensity;
      if (this.self.dazzled) {
        this.hud.setAbility("DAZZLED — blinded by a flashlight, can't roar or grab");
      } else {
        const roarText = this.roarCooldown > 0 ? `Roar: ${Math.ceil(this.roarCooldown)}s` : "Roar ready (right-click)";
        const leapText = this.player.stamina >= PLAYER.leapStaminaCost ? "Leap ready (space)" : "Leap: low stamina";
        const chargeText = this.chargeTimer > 0 ? "Charging!" : this.chargeCooldown > 0 ? `Charge: ${Math.ceil(this.chargeCooldown)}s` : "Charge ready (shift)";
        this.hud.setAbility(`${roarText} · ${leapText} · ${chargeText}`);
      }
      this.caveCooldown = Math.max(0, this.caveCooldown - dt);
      const caveReady = this.caveCooldown === 0 && this.nearestCaveIndex() >= 0;
      // Prompt priority: cave fast-travel, else a hint when stood against a climbable structure.
      const w = this.env.simWorld;
      const near = climbSupport(w.climbables, w.getHeight,
        this.player.position.x, this.player.position.z, PLAYER.radius, PLAYER.climbReach);
      this.hud.setPrompt(
        caveReady && !this.map.isOpen ? "Press M — choose a cave to travel to"
          : near && !this.map.isOpen ? "Hold Space to climb"
            : null
      );
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
        clues: hunter && this.clueVisionActive() ? this.clues.getRecentDots(MAP.clueWindow * clueWindowMul(this.self.specialty)) : [],
        pings: hunter ? this.net.getPings() : [],
        bigfoot: this.isBigfoot,
      });
    }

    // Filming (hunters only): hold right mouse to record; Bigfoot in frame builds footage.
    let recording = false;
    let inView = false;
    // Revive (hunters only): hold E near a downed teammate to free them (server-authoritative).
    let reviving = false;
    let reviveTarget = "";
    if (!this.isBigfoot) {
      if (!locked) {
        recording = this.input.isMouseDown(2);
        inView = recording && this.computeBigfootInView();
      }
      this.hud.setRecording(recording, inView); // recording=false hides the viewfinder
      this.hud.setFilmProgress(this.self.filmProgress);

      const target = !locked
        ? this.net.getIncapTeammate(this.player.position.x, this.player.position.z, REVIVE.radius)
        : null;
      const holdingE = target !== null && this.input.isActionDown("interact");
      if (holdingE) {
        reviving = true;
        reviveTarget = target!.sid;
        this.reviveProgress = Math.min(1, this.reviveProgress + dt / (REVIVE.seconds * reviveMul(this.self.specialty)));
        this.reviveTickTimer -= dt;
        if (this.reviveTickTimer <= 0) {
          this.audio.playOnce("revive_channel", { volume: 0.5 });
          this.reviveTickTimer = 0.22;
        }
        if (this.reviveProgress >= 1 && !this.reviveWasFull) {
          this.audio.playOnce("revive_success", { volume: 0.5 });
          this.reviveWasFull = true;
        }
        this.hud.setPrompt("Reviving teammate…");
      } else {
        this.reviveProgress = Math.max(0, this.reviveProgress - dt * 2); // bleed off when not holding
        this.reviveTickTimer = 0;
        this.reviveWasFull = false;
        // Prompt priority: an in-range revive, else a hint that our flashlight is dazzling Bigfoot.
        const dazzling = !locked && this.player.isFlashlightOn && this.computeBigfootInView();
        this.hud.setPrompt(
          target ? "Hold E to revive teammate" : dazzling ? "Blinding Bigfoot — hold the light on it" : null
        );
      }
      this.hud.setReviveProgress(this.reviveProgress);
    }

    // Stream our transform + intent to the server at a fixed rate.
    this.sendAccum += dt;
    if (this.sendAccum >= 1 / NET.sendHz) {
      this.sendAccum = 0;
      if (!locked) {
        this.net.sendMove({
          x: this.player.position.x,
          y: this.player.feetY,
          z: this.player.position.z,
          ry: this.player.yawAngle,
          flashlightOn: this.player.isFlashlightOn,
          battery: this.player.battery,
          stamina: this.player.stamina,
          recording,
          inView,
          reviving,
          reviveTarget,
        });
      }
    }

    this.hud.setBattery(this.player.battery);
    this.hud.setStamina((this.player.stamina / this.player.staminaMax) * 100); // % of this persona's max
    this.hud.setBeam(!this.isBigfoot && this.player.isFlashlightOn); // beam mask + lens grime while lit

    // Drive the post FX: moving grain + a vignette that tightens toward the dead of night.
    this.fxPass.uniforms.time.value = t;
    const nightDepth = Math.sin(Math.min(1, this.timeOfDay) * Math.PI); // 0 at dusk/dawn, 1 at midnight
    this.fxPass.uniforms.vignette.value = POST.vignette + POST.vignetteNight * nightDepth;
    this.renderer.info.reset(); // count draws across all composer passes for this frame
    this.composer.render();

    if (this.showPerf) {
      this.perfFps += (1 / Math.max(dt, 1e-3) - this.perfFps) * 0.1;
      this.perfTimer -= dt;
      if (this.perfTimer <= 0) {
        this.perfTimer = 0.25;
        const r = this.renderer.info.render;
        const el = document.getElementById("perf");
        if (el) el.textContent =
          `${Math.round(this.perfFps)} fps\n${r.calls} draws · ${Math.round(r.triangles / 1000)}k tris\ntrees ${this.env.visibleTrees} · cave lights ${this.env.litCaveLights} · dpr ${this.renderer.getPixelRatio()}`;
      }
    }
  }

  /**
   * Reconcile the local player toward the server's authoritative position. The server validates
   * every move (collision, speed, terrain); when it corrects us we ease the error out, or snap on a
   * teleport-grade gap. Small deltas are ignored so ordinary lag between our prediction and the
   * server's slightly-older state doesn't rubber-band normal movement.
   */
  private reconcile(dt: number) {
    const sp = this.net.getSelfPosition();
    if (!sp) return; // offline/solo, or not yet in state
    const dx = sp.x - this.player.position.x;
    const dz = sp.z - this.player.position.z;
    const d2 = dx * dx + dz * dz;
    if (d2 < RECONCILE_IGNORE * RECONCILE_IGNORE) return; // server agrees closely enough
    if (d2 > RECONCILE_SNAP * RECONCILE_SNAP) this.player.teleportTo(sp.x, sp.z); // gross desync
    else this.player.correctTo(sp.x, sp.z, Math.min(1, dt * RECONCILE_EASE));
  }

  /** Map only shows the trail when the hunter hears Bigfoot nearby or sees recent evidence. */
  private clueVisionActive(): boolean {
    const p = this.player.position;
    const bf = this.net.getBigfootPosition();
    // Specialty-scaled senses: Theo (Sound) hears farther; Wren (Tracking) sees clues farther, for longer.
    const hearRange = MAP.hearRange * hearRangeMul(this.self.specialty);
    if (bf) {
      const dx = bf.x - p.x;
      const dz = bf.z - p.z;
      if (dx * dx + dz * dz < hearRange * hearRange) return true;
    }
    const sight = MAP.evidenceSight * evidenceSightMul(this.self.specialty);
    const window = MAP.clueWindow * clueWindowMul(this.self.specialty);
    return this.clues.hasRecentClueWithin(p.x, p.z, sight, window);
  }

  /** Freeze/incap stings on our own state change + the searcher proximity heartbeat. */
  private updateAudio(dt: number) {
    if (this.self.status !== this.prevStatus) {
      if (this.self.status === "frozen") this.audio.playOnce("freeze_sting", { volume: 0.85 });
      else if (this.self.status === "incapacitated") this.audio.playOnce("grab_impact", { volume: 0.95 });
      this.prevStatus = this.self.status;
    }
    if (!this.isBigfoot) {
      let intensity = 0;
      const bf = this.ended ? null : this.net.getBigfootPosition();
      if (bf) {
        const d = Math.hypot(bf.x - this.player.position.x, bf.z - this.player.position.z);
        intensity = THREE.MathUtils.clamp((40 - d) / 30, 0, 1); // 40m silent -> 10m pounding
      }
      this.audio.setHeartbeat(intensity);
    }
    this.audio.update(dt);
  }

  /** Footage tally → HUD, plus a confirmation ding whenever a new video is banked. */
  private onFootage(have: number, need: number) {
    if (have > this.prevFootage) this.audio.playOnce("video_captured", { volume: 0.6 });
    this.prevFootage = have;
    this.hud.setFootage(have, need);
  }

  /** Apply the server's per-night escalation to the local player + roar UI. */
  private applyEscalation(e: EscalationInfo) {
    this.player.nightSpeedMul = this.isBigfoot ? e.bigfootSpeedMul : 1; // hunters pressured via drain
    this.player.batteryDrainMul = e.batteryDrainMul;
    this.escStaminaDrain = e.staminaDrainMul;
    this.roarCooldownSec = e.roarCooldownSec;
    this.applySpecialtyMods(); // fold the specialty in on top of the per-night escalation
  }

  /** Compose per-night escalation with the local searcher's specialty into the movement mods. */
  private applySpecialtyMods() {
    this.player.staminaMax = staminaMaxFor(this.self.specialty); // Sam (Endurance): 150
    this.player.staminaDrainMul = this.escStaminaDrain * staminaDrainMulFor(this.self.specialty); // Sam: ×0.85
  }

  private tryRoar() {
    if (!this.isBigfoot || this.ended || this.map.isOpen || this.roarCooldown > 0 || this.self.dazzled) return;
    this.net.sendRoar();
    this.audio.playOnce("roar", { volume: 0.9 }); // our own roar, up close
    this.roarCooldown = this.roarCooldownSec;
  }

  private tryGrab() {
    if (!this.isBigfoot || this.ended || this.map.isOpen || this.self.dazzled) return;
    this.net.sendGrab();
    this.audio.playOnce("grab_impact", { volume: 0.5 }); // the swing
  }

  private tryCharge() {
    if (!this.isBigfoot || this.ended || this.map.isOpen || this.chargeCooldown > 0) return;
    this.net.sendCharge(); // server opens the speed-gate window; we predict the burst locally
    this.chargeTimer = CHARGE.duration;
    this.chargeCooldown = CHARGE.duration + CHARGE.cooldown;
    this.audio.playOnce("cave_whoosh", { volume: 0.5 }); // a lunging whoosh
  }

  private toggleSenses() {
    if (!this.isBigfoot) return;
    this.sensesOn = !this.sensesOn;
    this.clues.setSensed(this.sensesOn); // scent trail (Bigfoot's own recent tracks, through walls)
    this.audio.playOnce("flashlight_click", { volume: 0.35 });
    this.hud.setTutorial(this.sensesOn ? "SENSES ON — prey & scent revealed (V)" : null);
  }

  private onNightChange(night: number, total: number) {
    this.totalNights = total;
    if (night !== this.night) {
      this.night = night;
      if (!this.ended) {
        this.hud.fade(() => {}); // brief fade between nights
        this.audio.playOnce("night_sting", { volume: 0.7 });
        if (night > 1) this.showNightNote(night);
      }
    }
  }

  /** Flash a short escalation note when a new night begins. */
  private showNightNote(night: number) {
    const note = this.isBigfoot
      ? `Night ${night} — you move faster and bolder now.`
      : `Night ${night} — Bigfoot is faster, and your gear drains quicker.`;
    this.hud.setTutorial(note);
    window.setTimeout(() => this.hud.setTutorial(null), 5000);
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

  /** Index of a cave whose mouth Bigfoot is standing in, or -1 (shared with the server's validation). */
  private nearestCaveIndex(): number {
    return nearestCaveIndex(CAVES, this.player.position.x, this.player.position.z);
  }

  /** Bigfoot picks a destination cave from the map and emerges from its mouth. */
  private travelToCave(i: number) {
    if (!this.isBigfoot || this.ended || this.caveCooldown > 0) return;
    const here = this.nearestCaveIndex();
    if (here < 0 || i === here || i < 0 || i >= CAVES.length) return;
    this.caveCooldown = CAVE.travelCooldown;
    this.traveling = true; // suspend local control + move-sends; the server moves us authoritatively
    this.net.sendCaveTravel(i); // server validates the jump and is authoritative for it
    this.closeMap(); // synchronous (keeps the pointer-lock user gesture)
    // Fade to black, hop at the darkest point (matching the server), fade back in at the new cave.
    // Shared helper -> exact same emerge spot + heading the server places us at (into the forest).
    const emerge = caveEmergePoint(CAVES[i]);
    this.audio.playOnce("cave_whoosh", { volume: 0.6 });
    this.hud.fade(() => {
      this.player.teleportTo(emerge.x, emerge.z, emerge.yaw);
      this.traveling = false;
    });
  }

  /** Drop a stakeout ping at (x,z), or at the player's feet if not given. */
  private dropPing(x?: number, z?: number) {
    if (this.isBigfoot || this.ended || this.self.status !== "active") return;
    this.net.sendPing(x ?? this.player.position.x, z ?? this.player.position.z);
    this.audio.playOnce("ping_drop", { volume: 0.5 });
  }

  /** Apply the settings live: brightness scales exposure, volume → audio, sensitivity → look. */
  private applySettings(d: SettingsData) {
    this.renderer.toneMappingExposure = this.baseExposure * d.brightness;
    this.audio.setMasterVolume(d.volume);
    this.player.sensitivityMul = d.sensitivity;
  }

  /** Open/close the settings overlay. Opening frees the pointer; Resume (onClose) re-locks it. */
  private toggleSettings() {
    if (this.ended) return;
    if (this.map.isOpen) this.closeMap();
    if (this.settingsMenu.isOpen) {
      this.settingsMenu.close();
    } else {
      this.settingsMenu.open();
      this.input.allowPointerLock = false;
      document.exitPointerLock();
    }
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
    this.settingsMenu.dispose();
    document.exitPointerLock();
    const youWon = winner === (this.isBigfoot ? "bigfoot" : "hunters");
    this.audio.setHeartbeat(0);
    this.audio.playOnce(youWon ? "victory" : "defeat", { volume: 0.7 });
    const title = winner === "hunters" ? "The footage is secured" : "The forest keeps its secret";
    const body =
      winner === "hunters"
        ? "Enough solid video. The expedition makes it out with proof Bigfoot is real."
        : "Bigfoot outlasted three nights. The expedition goes home with nothing.";
    this.hud.showEnd(youWon ? "VICTORY" : "DEFEAT", `${title}. ${body}`);
    // The host can send everyone back to the lobby for another match.
    if (this.net.isHost()) this.hud.showHostRematch(() => this.net.sendReturnToLobby());
  }

  private onResize() {
    this.camera.aspect = window.innerWidth / window.innerHeight;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(window.innerWidth, window.innerHeight);
    this.composer.setSize(window.innerWidth, window.innerHeight);
  }
}

/** Spawn point for the local player: server-assigned when online, local fallback when solo. */
function spawnFor(role: string, room?: Room): { x: number; z: number; yaw?: number } {
  if (room) {
    const self = (room.state as any).players?.get(room.sessionId);
    if (self) {
      const yaw = role === "bigfoot" ? Math.atan2(self.x, self.z) : 0;
      return { x: self.x, z: self.z, yaw };
    }
  }
  return role === "bigfoot" ? caveMouthSpawn() : campfireSpawn();
}

/** A scatter point around the campfire for searcher spawns. */
function campfireSpawn(): { x: number; z: number } {
  const a = Math.random() * Math.PI * 2;
  const r = 4 + Math.random() * 3;
  return { x: Math.cos(a) * r, z: Math.sin(a) * r };
}

/** A random cave mouth (offset toward map centre) for the Bigfoot spawn. */
function caveMouthSpawn(): { x: number; z: number; yaw: number } {
  const cave = CAVES[Math.floor(Math.random() * CAVES.length)];
  const dl = Math.hypot(cave.x, cave.z) || 1;
  // atan2(cave.x, cave.z) points the camera away from the cave (toward map centre).
  return { x: cave.x - (cave.x / dl) * 8, z: cave.z - (cave.z / dl) * 8, yaw: Math.atan2(cave.x, cave.z) };
}
