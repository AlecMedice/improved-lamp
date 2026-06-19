/** Updates the DOM overlay declared in index.html. */
export class HUD {
  private el(id: string): HTMLElement {
    const node = document.getElementById(id);
    if (!node) throw new Error(`HUD element #${id} missing`);
    return node;
  }

  setBattery(v: number) {
    this.el("battery-fill").style.width = `${v}%`;
  }
  setStamina(v: number) {
    this.el("stamina-fill").style.width = `${v}%`;
  }
  setStatus(s: string) {
    this.el("net-status").textContent = s;
  }
  setEvidence(have: number, need: number) {
    this.el("evidence").textContent = `${have} / ${need}`;
  }
  setPhase(phase: string, timeOfDay: number) {
    this.el("phase").textContent = phase.toUpperCase();
    this.el("clock").textContent = `${Math.round(timeOfDay * 100)}%`;
  }
}
