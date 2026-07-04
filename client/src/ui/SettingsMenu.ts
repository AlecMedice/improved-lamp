import { Settings, SettingsData, SETTINGS_RANGE } from "../core/Settings";

/**
 * The pause/settings overlay. Binds the range sliders to the Settings store and re-applies on every
 * change (brightness/volume/sensitivity take effect live). `onClose` re-locks the pointer (Game).
 */
export class SettingsMenu {
  isOpen = false;
  private overlay: HTMLElement;

  constructor(
    private settings: Settings,
    private apply: (d: SettingsData) => void,
    private onClose: () => void
  ) {
    this.overlay = this.el("settings-overlay");
    this.bindSlider("brightness", "set-brightness", "set-brightness-val", (v) => `${Math.round(v * 100)}%`);
    this.bindSlider("volume", "set-volume", "set-volume-val", (v) => `${Math.round(v * 100)}%`);
    this.bindSlider("sensitivity", "set-sensitivity", "set-sensitivity-val", (v) => `${v.toFixed(2)}×`);
    (this.el("settings-resume") as HTMLButtonElement).onclick = () => this.close();
    const gear = this.el("settings-gear") as HTMLButtonElement;
    gear.onclick = () => this.toggle();
    gear.style.display = "flex"; // reveal now that we're in-game
  }

  /** Hide the gear + overlay (e.g. at match end). */
  dispose() {
    this.overlay.style.display = "none";
    this.el("settings-gear").style.display = "none";
    this.isOpen = false;
  }

  private el(id: string): HTMLElement {
    const n = document.getElementById(id);
    if (!n) throw new Error(`settings element #${id} missing`);
    return n;
  }

  private bindSlider(key: keyof SettingsData, sliderId: string, valId: string, fmt: (v: number) => string) {
    const s = this.el(sliderId) as HTMLInputElement;
    const label = this.el(valId);
    const r = SETTINGS_RANGE[key];
    s.min = String(r.min);
    s.max = String(r.max);
    s.step = String(r.step);
    s.value = String(this.settings.data[key]);
    label.textContent = fmt(this.settings.data[key]);
    s.addEventListener("input", () => {
      this.settings.set(key, Number(s.value));
      label.textContent = fmt(this.settings.data[key]);
      this.apply(this.settings.data);
    });
  }

  open() {
    this.overlay.style.display = "flex";
    this.isOpen = true;
  }
  close() {
    this.overlay.style.display = "none";
    this.isOpen = false;
    this.onClose();
  }
  toggle() {
    if (this.isOpen) this.close();
    else this.open();
  }
}
