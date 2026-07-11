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

  /** Night counter + an in-fiction clock running 8pm -> 8am. */
  setNight(night: number, total: number, timeOfDay: number) {
    this.el("phase").textContent = `NIGHT ${night}/${total}`;
    this.el("clock").textContent = clockTime(timeOfDay);
  }

  /** Show the local searcher's assigned character (name + specialty). Pass empty to hide (Bigfoot/unassigned). */
  setPersona(characterName: string, specialty: string) {
    const pill = this.el("persona");
    if (characterName) {
      pill.textContent = specialty ? `${characterName} · ${specialty}` : characterName;
      pill.style.display = "";
    } else {
      pill.style.display = "none";
    }
  }

  /** Theo (Sound): a lingering arrow pointing toward a recent roar's origin (screen-relative). Pass null to hide. */
  setRoarDirection(angleRad: number | null) {
    const el = this.el("roar-dir");
    if (angleRad === null) {
      el.style.display = "none";
      return;
    }
    el.style.display = "flex";
    this.el("roar-dir-arrow").style.transform = `rotate(${angleRad}rad)`;
  }

  /** Show role-appropriate objective text and hide the filming UI for Bigfoot. */
  setRole(role: string) {
    const bigfoot = role === "bigfoot";
    this.el("objective").textContent = bigfoot
      ? "Survive 3 nights. Right-click ROAR to freeze hunters, left-click GRAB a frozen one."
      : "Film Bigfoot 3 times to win — track its footprints, and don't get caught.";
    if (bigfoot) {
      this.el("film-pill").style.display = "none";
      this.el("revive-pill").style.display = "none";
    } else {
      this.el("ability").style.display = "none";
    }
  }

  /** Revive progress bar (0..1) shown while the local hunter is reviving a teammate; hidden at 0. */
  setReviveProgress(p: number) {
    const pill = this.el("revive-pill");
    pill.style.display = p > 0 ? "" : "none";
    this.el("revive-fill").style.width = `${Math.round(p * 100)}%`;
  }

  /** Bigfoot's ability readout (roar cooldown). Pass null to clear. */
  setAbility(text: string | null) {
    const el = this.el("ability");
    if (text) {
      el.textContent = text;
      el.style.display = "";
    } else {
      el.style.display = "none";
    }
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
  /** Banner for the local hunter's fear/incapacitation state. */
  setStatusBanner(status: string) {
    const el = this.el("status-banner");
    if (status === "frozen") {
      el.textContent = "FROZEN — paralyzed by Bigfoot's roar. Can't move!";
      el.style.display = "block";
    } else if (status === "incapacitated") {
      el.textContent = "INCAPACITATED — Bigfoot has you. Your footage is lost.";
      el.style.display = "block";
    } else {
      el.style.display = "none";
    }
  }

  /** Heavy fade held over the screen while incapacitated (the "fade out"). */
  setBlackout(on: boolean) {
    this.el("blackout").style.opacity = on ? "0.92" : "0";
  }

  /** Flashlight beam mask + lens grime — on while a hunter's flashlight is lit. */
  setBeam(on: boolean) {
    this.el("beam").classList.toggle("on", on);
    this.el("lens-dirt").classList.toggle("lit", on);
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

  /** Transient onboarding / escalation hint, centred low on screen. Pass null to hide. */
  setTutorial(text: string | null) {
    const el = this.el("tutorial");
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
    this.el("status-banner").style.display = "none";
    this.el("blackout").style.opacity = "0";
    this.el("end-title").textContent = title;
    this.el("end-msg").textContent = message;
    this.el("end-lobby").style.display = "none"; // host-only; shown by showHostRematch
    this.el("end-overlay").style.display = "flex";
  }

  /** Reveal the host's "Return to lobby" button on the results screen. */
  showHostRematch(onReturn: () => void) {
    const el = this.el("end-lobby") as HTMLButtonElement;
    el.style.display = "block";
    el.onclick = onReturn;
  }
}

/** Format match time-of-night (0..1) as an in-fiction clock from 8:00 PM to 8:00 AM. */
function clockTime(timeOfDay: number): string {
  const totalMin = (20 * 60 + timeOfDay * 12 * 60) % (24 * 60);
  const h24 = Math.floor(totalMin / 60);
  const m = Math.floor(totalMin % 60);
  const ampm = h24 >= 12 ? "PM" : "AM";
  const h12 = ((h24 + 11) % 12) + 1;
  return `${h12}:${String(m).padStart(2, "0")} ${ampm}`;
}
