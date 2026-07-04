import { Settings, SettingsData, SETTINGS_RANGE } from "../core/Settings";
import { Keybinds, ACTIONS, ACTION_LABELS, codeLabel } from "../core/Keybinds";
import { Input } from "../core/Input";

/**
 * The pause/settings overlay: live-applied sliders (brightness/volume/sensitivity) + a rebindable
 * controls list (click a key, press the new one). `onClose` re-locks the pointer (Game).
 */
export class SettingsMenu {
  isOpen = false;
  private overlay: HTMLElement;
  private controls: HTMLElement;
  private capturing = false;

  constructor(
    private settings: Settings,
    private keybinds: Keybinds,
    private input: Input,
    private apply: (d: SettingsData) => void,
    private onClose: () => void
  ) {
    this.overlay = this.el("settings-overlay");
    this.controls = this.el("settings-controls");
    this.bindSlider("brightness", "set-brightness", "set-brightness-val", (v) => `${Math.round(v * 100)}%`);
    this.bindSlider("volume", "set-volume", "set-volume-val", (v) => `${Math.round(v * 100)}%`);
    this.bindSlider("sensitivity", "set-sensitivity", "set-sensitivity-val", (v) => `${v.toFixed(2)}×`);
    (this.el("settings-resume") as HTMLButtonElement).onclick = () => this.close();
    (this.el("settings-reset-keys") as HTMLButtonElement).onclick = () => { this.keybinds.reset(); this.renderControls(); };
    const gear = this.el("settings-gear") as HTMLButtonElement;
    gear.onclick = () => this.toggle();
    gear.style.display = "flex"; // reveal now that we're in-game
    this.renderControls();
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

  /** Rebuild the controls list — one row per action with a click-to-rebind key button. */
  private renderControls() {
    this.controls.textContent = "";
    for (const action of ACTIONS) {
      const row = document.createElement("div");
      row.className = "set-key-row";
      const name = document.createElement("span");
      name.textContent = ACTION_LABELS[action];
      const btn = document.createElement("button");
      btn.className = "keybtn";
      const code = this.keybinds.code(action);
      btn.textContent = code ? codeLabel(code) : "—";
      btn.onclick = () => {
        if (this.capturing) return;
        this.capturing = true;
        btn.textContent = "press a key…";
        btn.classList.add("capturing");
        this.input.captureNext((newCode) => {
          this.capturing = false;
          if (newCode) this.keybinds.set(action, newCode); // "" = cancelled (Esc)
          this.renderControls();
        });
      };
      row.append(name, btn);
      this.controls.append(row);
    }
  }

  open() {
    this.overlay.style.display = "flex";
    this.isOpen = true;
  }
  close() {
    if (this.capturing) return; // don't close mid-rebind
    this.overlay.style.display = "none";
    this.isOpen = false;
    this.onClose();
  }
  toggle() {
    if (this.isOpen) this.close();
    else this.open();
  }

  /** Hide the gear + overlay (e.g. at match end). */
  dispose() {
    this.overlay.style.display = "none";
    this.el("settings-gear").style.display = "none";
    this.isOpen = false;
  }
}
