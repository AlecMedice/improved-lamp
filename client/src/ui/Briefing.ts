import { Keybinds, codeLabel } from "../core/Keybinds";
import { Input } from "../core/Input";

/**
 * The dusk briefing shown once at the start of night 1: role-tailored objective + the essential
 * controls (read live from Keybinds, so they reflect any rebinds). Dismissed by any key or a click.
 */
export class Briefing {
  isOpen = false;
  private overlay = document.getElementById("briefing-overlay") as HTMLElement;

  show(isYeti: boolean, keybinds: Keybinds, input: Input, onDismiss: () => void) {
    const k = (a: Parameters<Keybinds["code"]>[0]) => codeLabel(keybinds.code(a));
    const move = `${k("forward")} ${k("left")} ${k("back")} ${k("right")}`;
    const title = isYeti ? "You are Yeti" : "Searcher briefing";
    const objective = isYeti
      ? "Survive three nights. Hunt the searchers, drag them off, and erase their footage — just don't get filmed."
      : "Capture three solid videos of the Yeti before the third dawn. Track its prints and broken crust — and don't get caught alone.";
    // Deep snow, stated as capability rather than as numbers (UNITY_PORT_NOTES §9).
    const snowLine = isYeti
      ? "The drifts don't slow you, and the searchers can't help printing their way across them — read the tracks."
      : "The valleys lie deep and slow you down; the wind-scoured high ground is faster going. Watch for DEEP SNOW.";
    const rows: [string, string][] = isYeti
      ? [
          ["Move / look", `${move} · Mouse`],
          ["Roar (freeze) · Grab", "Right-click · Left-click"],
          ["Leap / climb a structure", k("jump")],
          ["Charge (burst dash)", k("sprint")],
          ["Senses overlay", k("senses")],
          ["Map · cave fast-travel", k("map")],
        ]
      : [
          ["Move / look", `${move} · Mouse`],
          ["Sprint · crouch", `${k("sprint")} · ${k("crouch")}`],
          ["Flashlight (dazzles Yeti)", k("flashlight")],
          ["Film Yeti", "Hold Right-click"],
          ["Revive a teammate", `Hold ${k("interact")}`],
          ["Map · stakeout ping", `${k("map")} · ${k("ping")}`],
        ];

    const list = rows.map(([label, keys]) => `<div class="brief-row"><span>${label}</span><b>${keys}</b></div>`).join("");
    this.overlay.innerHTML = `
      <div class="brief-card">
        <div class="brief-kicker">Night 1 · 8:00 PM</div>
        <h1>${title}</h1>
        <p class="tag">${objective}</p>
        <p class="tag">${snowLine}</p>
        <div class="brief-grid">${list}</div>
        <div class="brief-foot">Adjust or rebind anything with the ⚙ / <kbd>Esc</kbd> menu · <b>press any key to begin</b></div>
      </div>`;
    this.overlay.style.display = "flex";
    this.isOpen = true;
    input.suspended = true;

    const dismiss = () => {
      window.removeEventListener("keydown", dismiss);
      this.overlay.removeEventListener("click", dismiss);
      this.overlay.style.display = "none";
      this.isOpen = false;
      input.suspended = false;
      onDismiss();
    };
    window.addEventListener("keydown", dismiss);
    this.overlay.addEventListener("click", dismiss);
  }
}
