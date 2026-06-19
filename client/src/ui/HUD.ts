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
  setFootage(have: number, need: number) {
    this.el("footage").textContent = `${have} / ${need}`;
  }
  setPhase(phase: string, timeOfDay: number) {
    this.el("phase").textContent = phase.toUpperCase();
    this.el("clock").textContent = `${Math.round(timeOfDay * 100)}%`;
  }

  /** Show role-appropriate objective text and hide the filming UI for Bigfoot. */
  setRole(role: string) {
    const bigfoot = role === "bigfoot";
    this.el("objective").textContent = bigfoot
      ? "Catch the searchers before they film you 3 times"
      : "Hold right-mouse to film Bigfoot — get 3 clips, don't get caught";
    if (bigfoot) this.el("film-pill").style.display = "none";
  }

  /** recording = RMB held; locked = Bigfoot is in frame (footage is building). */
  setRecording(recording: boolean, locked: boolean) {
    this.el("viewfinder").style.display = recording ? "block" : "none";
    const rec = this.el("rec");
    rec.classList.toggle("locked", locked);
    rec.textContent = locked ? "● REC — IN FRAME" : "● REC — find Bigfoot";
  }
  setFilmProgress(p: number) {
    const w = `${Math.round(p * 100)}%`;
    this.el("film-fill").style.width = w; // HUD pill bar
    this.el("film-fill-vf").style.width = w; // viewfinder bar
  }
  setCaught(caught: boolean) {
    this.el("caught-banner").style.display = caught ? "block" : "none";
  }

  /** Contextual action hint (e.g. cave fast-travel). Pass null to hide. */
  setPrompt(text: string | null) {
    const el = this.el("prompt");
    if (text) {
      el.textContent = text;
      el.style.display = "block";
    } else {
      el.style.display = "none";
    }
  }

  /** Quick fade-to-black; runs `midAction` at full black, then fades back in. */
  fade(midAction: () => void) {
    const el = this.el("fade");
    el.style.opacity = "1";
    window.setTimeout(() => {
      midAction();
      el.style.opacity = "0";
    }, 170); // matches the CSS transition on #fade
  }

  showEnd(title: string, message: string) {
    this.el("viewfinder").style.display = "none";
    this.el("caught-banner").style.display = "none";
    this.el("end-title").textContent = title;
    this.el("end-msg").textContent = message;
    this.el("end-overlay").style.display = "flex";
  }
}
