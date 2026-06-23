export class AudioManager {
  private ctx: AudioContext;
  private creekGain?: GainNode; // ambient creek level, driven by proximity
  private ambienceStarted = false;

  constructor() {
    this.ctx = new AudioContext();
    // Browsers start the context suspended until a user gesture. Resume on the first
    // input and bring up the ambient bed then (also un-gates one-shot sounds).
    const wake = () => {
      void this.ctx.resume();
      this.startAmbience();
    };
    window.addEventListener("pointerdown", wake, { once: true });
    window.addEventListener("keydown", wake, { once: true });
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

  /** Start the continuous wind + creek loops. Idempotent; safe to call repeatedly. */
  startAmbience(): void {
    if (this.ambienceStarted) return;
    this.ambienceStarted = true;
    const ctx = this.ctx;

    // Wind: brown noise through a lowpass, with a slow LFO on the gain so it gusts.
    const windSrc = ctx.createBufferSource();
    windSrc.buffer = this.noiseBuffer(3, true);
    windSrc.loop = true;
    const windFilter = ctx.createBiquadFilter();
    windFilter.type = "lowpass";
    windFilter.frequency.value = 520;
    const windGain = ctx.createGain();
    windGain.gain.value = 0.05;
    windSrc.connect(windFilter);
    windFilter.connect(windGain);
    windGain.connect(ctx.destination);
    windSrc.start();

    const gust = ctx.createOscillator();
    gust.frequency.value = 0.08; // ~12s gust cycle
    const gustDepth = ctx.createGain();
    gustDepth.gain.value = 0.03;
    gust.connect(gustDepth);
    gustDepth.connect(windGain.gain);
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
    creekSrc.connect(creekFilter);
    creekFilter.connect(this.creekGain);
    this.creekGain.connect(ctx.destination);
    creekSrc.start();
  }

  /** Set how loud the creek is, 0..1 (driven by the player's distance to the water). */
  setCreekProximity(level: number): void {
    if (!this.creekGain) return;
    const target = Math.max(0, Math.min(1, level)) * 0.16;
    this.creekGain.gain.setTargetAtTime(target, this.ctx.currentTime, 0.25);
  }

  playFootstep(sprinting: boolean): void {
    const ctx = this.ctx;
    const dur = 0.12;
    const buf = ctx.createBuffer(1, Math.ceil(ctx.sampleRate * dur), ctx.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;

    const src = ctx.createBufferSource();
    src.buffer = buf;

    const filter = ctx.createBiquadFilter();
    filter.type = "lowpass";
    filter.frequency.value = 350;

    const gain = ctx.createGain();
    const vol = sprinting ? 0.22 : 0.15;
    gain.gain.setValueAtTime(vol, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + dur);

    src.connect(filter);
    filter.connect(gain);
    gain.connect(ctx.destination);
    src.start();
  }

  /**
   * Spatial footstep for Bigfoot heard by a nearby hunter.
   * @param distance - metres from hunter to Bigfoot
   * @param pan      - stereo pan (-1 = hard left, 0 = centre, +1 = hard right)
   */
  playBigfootFootstep(distance: number, pan: number): void {
    const ctx = this.ctx;
    const maxDist = 35; // matches MAP.hearRange
    const vol = Math.max(0, (1 - distance / maxDist) ** 2) * 0.4;
    if (vol <= 0) return;

    const dur = 0.2;
    const buf = ctx.createBuffer(1, Math.ceil(ctx.sampleRate * dur), ctx.sampleRate);
    const data = buf.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;

    const src = ctx.createBufferSource();
    src.buffer = buf;

    // Bigfoot's steps are heavier — cut off much lower than hunter steps.
    const filter = ctx.createBiquadFilter();
    filter.type = "lowpass";
    filter.frequency.value = 180;

    const panner = ctx.createStereoPanner();
    panner.pan.value = Math.max(-1, Math.min(1, pan));

    const gain = ctx.createGain();
    gain.gain.setValueAtTime(vol, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + dur);

    src.connect(filter);
    filter.connect(panner);
    panner.connect(gain);
    gain.connect(ctx.destination);
    src.start();
  }

  playFlashlightToggle(): void {
    const ctx = this.ctx;
    const osc = ctx.createOscillator();
    osc.type = "square";
    osc.frequency.value = 900;

    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.12, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.04);

    osc.connect(gain);
    gain.connect(ctx.destination);
    osc.start();
    osc.stop(ctx.currentTime + 0.04);
  }
}
