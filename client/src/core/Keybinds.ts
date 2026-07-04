/** Rebindable actions and their key bindings, persisted to localStorage. Consumed by Input. */
export const ACTIONS = [
  "forward", "back", "left", "right", "jump", "sprint",
  "crouch", "flashlight", "map", "ping", "interact", "senses",
] as const;
export type Action = (typeof ACTIONS)[number];

/** Human-readable action names for the rebinding UI. */
export const ACTION_LABELS: Record<Action, string> = {
  forward: "Move forward",
  back: "Move back",
  left: "Strafe left",
  right: "Strafe right",
  jump: "Jump / leap / climb / vault",
  sprint: "Sprint / charge",
  crouch: "Crouch",
  flashlight: "Flashlight",
  map: "Map",
  ping: "Stakeout ping",
  interact: "Revive / interact",
  senses: "Senses overlay",
};

const DEFAULTS: Record<Action, string> = {
  forward: "KeyW", back: "KeyS", left: "KeyA", right: "KeyD", jump: "Space", sprint: "ShiftLeft",
  crouch: "ControlLeft", flashlight: "KeyF", map: "KeyM", ping: "KeyQ", interact: "KeyE", senses: "KeyV",
};

const KEY = "hollowpines.keybinds";

/** Pretty label for a KeyboardEvent.code (e.g. "KeyW" -> "W", "ShiftLeft" -> "Shift"). */
export function codeLabel(code: string): string {
  if (code.startsWith("Key")) return code.slice(3);
  if (code.startsWith("Digit")) return code.slice(5);
  if (code.startsWith("Arrow")) return { ArrowUp: "↑", ArrowDown: "↓", ArrowLeft: "←", ArrowRight: "→" }[code] ?? code;
  const named: Record<string, string> = {
    Space: "Space", ShiftLeft: "L-Shift", ShiftRight: "R-Shift", ControlLeft: "L-Ctrl", ControlRight: "R-Ctrl",
    AltLeft: "L-Alt", AltRight: "R-Alt", Tab: "Tab", Enter: "Enter", Backquote: "`", CapsLock: "Caps",
  };
  return named[code] ?? code;
}

export class Keybinds {
  binds: Record<Action, string>;

  constructor() {
    this.binds = this.load();
  }

  private load(): Record<Action, string> {
    try {
      const raw = localStorage.getItem(KEY);
      if (raw) return { ...DEFAULTS, ...(JSON.parse(raw) as Partial<Record<Action, string>>) };
    } catch {
      /* corrupt/unavailable — fall back to defaults */
    }
    return { ...DEFAULTS };
  }

  private save() {
    try {
      localStorage.setItem(KEY, JSON.stringify(this.binds));
    } catch {
      /* storage blocked — keep in-memory */
    }
  }

  code(action: Action): string {
    return this.binds[action];
  }

  /** Rebind an action to a key code; if the code is already used elsewhere, that action is cleared. */
  set(action: Action, code: string) {
    for (const a of ACTIONS) if (this.binds[a] === code) this.binds[a] = ""; // no duplicate bindings
    this.binds[action] = code;
    this.save();
  }

  reset() {
    this.binds = { ...DEFAULTS };
    this.save();
  }
}
