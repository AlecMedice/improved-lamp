/** Player settings, persisted to localStorage. Applied by Game (exposure/audio/look). */
export type SettingsData = {
  brightness: number; // gamma/brightness — multiplies tone-mapping exposure (the "too dark" fix)
  volume: number; // master audio volume (0..1)
  sensitivity: number; // mouse-look sensitivity multiplier
};

/** Slider bounds for the settings UI (also used to clamp loaded/typed values). */
export const SETTINGS_RANGE = {
  brightness: { min: 0.5, max: 2, step: 0.05 },
  volume: { min: 0, max: 1, step: 0.05 },
  sensitivity: { min: 0.3, max: 2.5, step: 0.05 },
} as const;

const DEFAULTS: SettingsData = { brightness: 1, volume: 0.85, sensitivity: 1 };
const KEY = "hollowpines.settings";
const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v));

export class Settings {
  readonly data: SettingsData;

  constructor() {
    this.data = this.load();
  }

  private load(): SettingsData {
    try {
      const raw = localStorage.getItem(KEY);
      if (raw) {
        const p = JSON.parse(raw) as Partial<SettingsData>;
        const merged = { ...DEFAULTS, ...p };
        // Clamp anything out of range (older/corrupt values).
        (Object.keys(SETTINGS_RANGE) as (keyof SettingsData)[]).forEach((k) => {
          merged[k] = clamp(Number(merged[k]), SETTINGS_RANGE[k].min, SETTINGS_RANGE[k].max);
        });
        return merged;
      }
    } catch {
      /* corrupt/unavailable storage — fall back to defaults */
    }
    return { ...DEFAULTS };
  }

  set<K extends keyof SettingsData>(key: K, value: number) {
    this.data[key] = clamp(value, SETTINGS_RANGE[key].min, SETTINGS_RANGE[key].max);
    try {
      localStorage.setItem(KEY, JSON.stringify(this.data));
    } catch {
      /* storage full/blocked — keep the in-memory value */
    }
  }
}
