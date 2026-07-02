import * as THREE from "three";

/**
 * All game audio. Hybrid by design: every one-shot cue is *synthesized procedurally* so the game
 * has full sound with no asset files, but any cue can be overridden by a real recording dropped
 * into `client/public/audio/` (listed in `audio/manifest.json`). See that folder's README.
 *
 * Spatial cues (roars, branch-snaps, remote footsteps) use THREE.PositionalAudio so they pan and
 * fall off from their world position; UI / own-action cues are non-positional. The ambient bed
 * (gusting wind + a proximity creek) and a searcher dread heartbeat run continuously. One shared
 * AudioContext (the listener's) drives everything; the listener rides the camera (set in Game).
 */
export type CueName =
  | "roar"
  | "footstep_soft"
  | "footstep_heavy"
  | "branch_snap"
  | "flashlight_click"
  | "ping_drop"
  | "video_captured"
  | "freeze_sting"
  | "grab_impact"
  | "cave_whoosh"
  | "night_sting"
  | "victory"
  | "defeat"
  | "revive_channel"
  | "revive_success"
  | "heartbeat";

const CUES: CueName[] = [
  "roar", "footstep_soft", "footstep_heavy", "branch_snap", "flashlight_click", "ping_drop",
  "video_captured", "freeze_sting", "grab_impact", "cave_whoosh", "night_sting", "victory",
  "defeat", "revive_channel", "revive_success", "heartbeat",
];

type PlayOpts = { volume?: number; refDistance?: number; rolloff?: number };

export class AudioEngine {
  readonly listener = new THREE.AudioListener();
  /** Optional terrain-height sampler so positional cues sit on the ground (set by Game). */
  groundSampler?: (x: number, z: number) => number;

  private ctx: AudioContext;
  private buffers = new Map<string, AudioBuffer>();
  private started = false;

  private ambienceStarted = false;
  private creekGain?: GainNode;
  private hbIntensity = 0;
  private hbTimer = 0;

  constructor(private scene: THREE.Scene) {
    this.ctx = this.listener.context as AudioContext;
    this.listener.setMasterVolume(0.85); // headroom; many cues can overlap
    this.buildCues();
    void this.loadOverrides();

    // Browsers start the context suspended until a user gesture. Resume on the first input
    // anywhere (covers the lobby → game handoff); Game.start() also calls resume() directly.
    const wake = () => this.resume();
    window.addEventListener("pointerdown", wake, { once: true });
    window.addEventListener("keydown", wake, { once: true });
  }

  /** Lift the autoplay gate and bring up the ambient bed. Idempotent. */
  resume() {
    if (this.ctx.state === "suspended") void this.ctx.resume();
    if (!this.started) {
      this.started = true;
      this.startAmbience();
    }
  }

  /** Non-positional one-shot (UI / your own actions). */
  playOnce(name: CueName, opts: PlayOpts = {}) {
    if (!this.started) return;
    const buf = this.buffers.get(name);
    if (!buf) return;
    const src = this.ctx.createBufferSource();
    src.buffer = buf;
    const g = this.ctx.createGain();
    g.gain.value = opts.volume ?? 1;
    src.connect(g).connect(this.listener.getInput());
    src.start();
  }

  /** Positional one-shot at world (x,z): pans + falls off from there. Auto-cleans up. */
  playAt(name: CueName, x: number, z: number, opts: PlayOpts = {}) {
    if (!this.started) return;
    const buf = this.buffers.get(name);
    if (!buf) return;
    const obj = new THREE.Object3D();
    obj.position.set(x, this.groundSampler ? this.groundSampler(x, z) + 1.2 : 1.2, z);
    this.scene.add(obj);

    const pa = new THREE.PositionalAudio(this.listener);
    pa.setBuffer(buf);
    pa.setRefDistance(opts.refDistance ?? 12);
    pa.setRolloffFactor(opts.rolloff ?? 1.1);
    pa.setVolume(opts.volume ?? 1);
    obj.add(pa);
    pa.play();

    window.setTimeout(() => {
      try { pa.stop(); } catch { /* already stopped */ }
      pa.disconnect();
      this.scene.remove(obj);
    }, buf.duration * 1000 + 150);
  }

  // --- convenience wrappers (match the call sites in LocalPlayer / Game) ------

  /** Local player's own footstep (quiet; heavy = Bigfoot). */
  playFootstep(sprinting: boolean, heavy = false) {
    this.playOnce(heavy ? "footstep_heavy" : "footstep_soft", { volume: heavy ? 0.3 : sprinting ? 0.22 : 0.15 });
  }
  playFlashlightToggle() {
    this.playOnce("flashlight_click", { volume: 0.5 });
  }

  /** Hunters' tension bed: 0 = silent (Bigfoot far), rising to ~1 (Bigfoot on top of you). */
  setHeartbeat(intensity: number) {
    this.hbIntensity = clamp01(intensity);
  }

  /** Set how loud the creek is, 0..1 (driven by the player's distance to the water). */
  setCreekProximity(level: number) {
    if (!this.creekGain) return;
    this.creekGain.gain.setTargetAtTime(clamp01(level) * 0.16, this.ctx.currentTime, 0.25);
  }

  /** Drive the retriggered heartbeat; call once per frame from the game loop. */
  update(dt: number) {
    if (this.hbIntensity > 0.02) {
      this.hbTimer -= dt;
      if (this.hbTimer <= 0) {
        this.hbTimer = lerp(1.3, 0.42, this.hbIntensity); // beats quicken with dread
        this.playOnce("heartbeat", { volume: 0.25 + 0.55 * this.hbIntensity });
      }
    } else {
      this.hbTimer = 0;
    }
  }

  // --- ambient bed (gusting wind + proximity creek) --------------------------

  private startAmbience() {
    if (this.ambienceStarted) return;
    this.ambienceStarted = true;
    const ctx = this.ctx;
    const out = this.listener.getInput();

    // Wind: brown noise through a lowpass, with a slow LFO on the gain so it gusts.
    const windSrc = ctx.createBufferSource();
    windSrc.buffer = this.noiseBuffer(3, true);
    windSrc.loop = true;
    const windFilter = ctx.createBiquadFilter();
    windFilter.type = "lowpass";
    windFilter.frequency.value = 520;
    const windGain = ctx.createGain();
    windGain.gain.value = 0.05;
    windSrc.connect(windFilter).connect(windGain).connect(out);
    windSrc.start();

    const gust = ctx.createOscillator();
    gust.frequency.value = 0.08; // ~12s gust cycle
    const gustDepth = ctx.createGain();
    gustDepth.gain.value = 0.03;
    gust.connect(gustDepth).connect(windGain.gain);
    gust.start();

    // Creek: white noise through a bandpass (babbling water), gain set by proximity.
    const creekSrc = ctx.createBufferSource();
    creekSrc.buffer = this.noiseBuffer(3, false);
    creekSrc.loop = true;
    const creekFilter = ctx.createBiquadFilter();
    creekFilter.type = "bandpass";
    creekFilter.frequency.value = 1100;
    creekFilter.Q.value = 0.7;
    this.creekGain = ctx.createGain();
    this.creekGain.gain.value = 0;
    creekSrc.connect(creekFilter).connect(this.creekGain).connect(out);
    creekSrc.start();
  }

  /** Looping noise buffer: white, or integrated to brown (smoother, wind-like). */
  private noiseBuffer(seconds: number, brown: boolean): AudioBuffer {
    const len = Math.ceil(this.ctx.sampleRate * seconds);
    const buf = this.ctx.createBuffer(1, len, this.ctx.sampleRate);
    const d = buf.getChannelData(0);
    let last = 0;
    for (let i = 0; i < len; i++) {
      const w = Math.random() * 2 - 1;
      if (brown) {
        last = (last + 0.02 * w) / 1.02;
        d[i] = last * 3.5;
      } else {
        d[i] = w;
      }
    }
    return buf;
  }

  // --- hybrid override loading ----------------------------------------------

  /** Look for `audio/manifest.json` listing override filenames; swap any that decode. Silent on miss. */
  private async loadOverrides() {
    const base = (import.meta.env.BASE_URL as string) || "/";
    let names: string[];
    try {
      const res = await fetch(`${base}audio/manifest.json`);
      if (!res.ok) return; // no manifest -> pure procedural, no 404 spam
      names = (await res.json()) as string[];
    } catch {
      return;
    }
    await Promise.all(
      names
        .filter((n): n is CueName => (CUES as string[]).includes(n))
        .map(async (name) => {
          try {
            const res = await fetch(`${base}audio/${name}.ogg`);
            if (!res.ok) return;
            const decoded = await this.ctx.decodeAudioData(await res.arrayBuffer());
            this.buffers.set(name, decoded);
          } catch {
            /* keep the procedural cue */
          }
        })
    );
  }

  // --- procedural synthesis --------------------------------------------------

  /** Allocate a mono buffer of `dur` seconds and let `fill` write the samples. */
  private synth(dur: number, fill: (d: Float32Array, sr: number) => void): AudioBuffer {
    const sr = this.ctx.sampleRate;
    const len = Math.max(1, Math.floor(dur * sr));
    const b = this.ctx.createBuffer(1, len, sr);
    fill(b.getChannelData(0), sr);
    return b;
  }

  private buildCues() {
    // Bigfoot's signature: a descending growl — sawtooth + amplitude-modulated lowpassed noise.
    this.buffers.set(
      "roar",
      this.synth(1.6, (d, sr) => {
        let lp = 0, ph = 0;
        for (let i = 0; i < d.length; i++) {
          const t = i / sr, p = t / (d.length / sr);
          const f = 150 * Math.pow(0.42, p); // 150 -> ~63 Hz
          ph += f / sr;
          const saw = (ph % 1) * 2 - 1;
          lp += 0.06 * (Math.random() * 2 - 1 - lp);
          const growl = 0.6 + 0.4 * Math.sin(2 * Math.PI * 22 * t);
          const env = Math.min(1, t / 0.12) * Math.exp(-Math.max(0, t - 0.12) * 1.6);
          d[i] = (saw * 0.55 + lp * 0.8 * growl) * env * 0.9;
        }
      })
    );

    // Footsteps: short lowpassed-noise thuds; heavier = lower cutoff, longer, louder (Bigfoot).
    const step = (dur: number, cut: number, gain: number) =>
      this.synth(dur, (d, sr) => {
        let lp = 0;
        for (let i = 0; i < d.length; i++) {
          const t = i / sr;
          lp += cut * (Math.random() * 2 - 1 - lp);
          d[i] = lp * Math.exp((-t / dur) * 6) * gain;
        }
      });
    this.buffers.set("footstep_soft", step(0.16, 0.05, 0.5));
    this.buffers.set("footstep_heavy", step(0.24, 0.03, 0.95));

    // Branch snap: a sharp crack with a small secondary crackle.
    this.buffers.set(
      "branch_snap",
      this.synth(0.14, (d, sr) => {
        for (let i = 0; i < d.length; i++) {
          const t = i / sr;
          const env = Math.exp(-t * 60) + 0.4 * Math.exp(-Math.max(0, t - 0.04) * 90);
          d[i] = (Math.random() * 2 - 1) * env * 0.7;
        }
      })
    );

    // Flashlight: a tiny click transient.
    this.buffers.set(
      "flashlight_click",
      this.synth(0.05, (d, sr) => {
        for (let i = 0; i < d.length; i++) d[i] = (Math.random() * 2 - 1) * Math.exp((-i / sr) * 240) * 0.5;
      })
    );

    // Ping: a soft two-tone blip.
    this.buffers.set("ping_drop", this.twoTone(0.3, 880, 1320, 0.12, 16, 0.4));

    // Captured video: a bright rising triad.
    this.buffers.set("video_captured", this.arp([660, 880, 1175], 0.12, 9, 0.35));

    // Freeze: a dissonant detuned riser with tremolo (you were roared).
    this.buffers.set(
      "freeze_sting",
      this.synth(0.9, (d, sr) => {
        const dur = d.length / sr;
        for (let i = 0; i < d.length; i++) {
          const t = i / sr, p = t / dur;
          const f = 180 + 220 * p;
          const s1 = (t * f % 1) * 2 - 1;
          const s2 = (t * f * 1.06 % 1) * 2 - 1; // dissonant detune
          const trem = 0.7 + 0.3 * Math.sin(2 * Math.PI * 9 * t);
          d[i] = (s1 + s2) * 0.25 * trem * Math.min(1, t / 0.1) * (1 - p * 0.2);
        }
      })
    );

    // Grab: a low pitch-down thump that submerges into lowpassed noise (you were taken).
    this.buffers.set(
      "grab_impact",
      this.synth(0.7, (d, sr) => {
        let lp = 0;
        for (let i = 0; i < d.length; i++) {
          const t = i / sr, p = t / (d.length / sr);
          const f = 90 * Math.pow(0.5, p);
          const thump = Math.sin(2 * Math.PI * f * t) * Math.exp(-t * 4);
          lp += 0.03 * (Math.random() * 2 - 1 - lp);
          d[i] = (thump * 0.85 + lp * Math.exp(-t * 2) * 0.5) * 0.9;
        }
      })
    );

    // Cave fast-travel: an airy noise swell.
    this.buffers.set(
      "cave_whoosh",
      this.synth(0.7, (d, sr) => {
        let lp = 0;
        for (let i = 0; i < d.length; i++) {
          const t = i / sr, p = t / (d.length / sr);
          lp += 0.08 * (Math.random() * 2 - 1 - lp);
          d[i] = lp * Math.sin(Math.PI * p) * 0.7;
        }
      })
    );

    // New night: an ominous low swell (root + minor third + octave).
    this.buffers.set(
      "night_sting",
      this.synth(1.2, (d, sr) => {
        for (let i = 0; i < d.length; i++) {
          const t = i / sr;
          const env = Math.min(1, t / 0.3) * Math.exp(-Math.max(0, t - 0.6) * 1.5);
          const s = Math.sin(2 * Math.PI * 70 * t) + 0.7 * Math.sin(2 * Math.PI * 84 * t) + 0.4 * Math.sin(2 * Math.PI * 140 * t);
          d[i] = s * 0.2 * env;
        }
      })
    );

    this.buffers.set("victory", this.arp([523, 659, 784, 1047], 0.18, 5, 0.3));
    this.buffers.set("defeat", this.arp([392, 349, 311, 233], 0.22, 3.5, 0.3));

    // Revive channel: a soft pulsing tick emitted while holding a teammate's revive.
    this.buffers.set(
      "revive_channel",
      this.synth(0.12, (d, sr) => {
        for (let i = 0; i < d.length; i++) {
          const t = i / sr;
          d[i] = Math.sin(2 * Math.PI * 520 * t) * Math.min(1, t / 0.01) * Math.exp(-t * 24) * 0.3;
        }
      })
    );
    // Revive success: a warm rising triad (your teammate is back up).
    this.buffers.set("revive_success", this.arp([523, 698, 880], 0.13, 7, 0.32));

    // Heartbeat: a low lub-dub.
    this.buffers.set(
      "heartbeat",
      this.synth(0.5, (d, sr) => {
        for (let i = 0; i < d.length; i++) {
          const t = i / sr;
          let s = Math.sin(2 * Math.PI * 55 * t) * Math.exp(-t * 18);
          const u = t - 0.14;
          if (u > 0) s += 0.8 * Math.sin(2 * Math.PI * 50 * u) * Math.exp(-u * 20);
          d[i] = s * 0.9;
        }
      })
    );
  }

  /** Sequential sine "arpeggio" of `freqs`, each segment `seg` seconds, decaying at `dk`. */
  private arp(freqs: number[], seg: number, dk: number, gain: number): AudioBuffer {
    return this.synth(seg * freqs.length, (d, sr) => {
      for (let i = 0; i < d.length; i++) {
        const t = i / sr;
        const k = Math.min(freqs.length - 1, Math.floor(t / seg));
        const u = t - k * seg;
        d[i] = Math.sin(2 * Math.PI * freqs[k] * t) * Math.exp(-u * dk) * gain;
      }
    });
  }

  /** Two sine blips back-to-back (f1 then f2 at `split` seconds), each decaying at `dk`. */
  private twoTone(dur: number, f1: number, f2: number, split: number, dk: number, gain: number): AudioBuffer {
    return this.synth(dur, (d, sr) => {
      for (let i = 0; i < d.length; i++) {
        const t = i / sr;
        const s =
          t < split
            ? Math.sin(2 * Math.PI * f1 * t) * Math.exp(-t * dk)
            : Math.sin(2 * Math.PI * f2 * (t - split)) * Math.exp(-(t - split) * dk);
        d[i] = s * gain;
      }
    });
  }
}

function clamp01(v: number): number {
  return Math.max(0, Math.min(1, v));
}
function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t;
}
