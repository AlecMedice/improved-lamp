import { Keybinds, Action, ACTIONS } from "./Keybinds";

/** Keyboard + pointer-lock mouse input (incl. mouse buttons). Actions resolve through Keybinds. */
export class Input {
  private keys = new Set<string>();
  private buttons = new Set<number>();
  private onTap: Record<string, () => void> = {}; // raw-code taps (e.g. Escape — not rebindable)
  private onActionTap: Partial<Record<Action, () => void>> = {};
  private onBtnTap: Record<number, () => void> = {};
  private onLook: (dx: number, dy: number) => void = () => {};
  private capture: ((code: string) => void) | null = null; // pending rebind capture
  locked = false;
  allowPointerLock = true; // disabled while a menu/map is open
  suspended = false; // ignore all key input (e.g. while the dusk briefing is up)

  constructor(private canvas: HTMLElement, private keybinds: Keybinds) {
    window.addEventListener("keydown", (e) => {
      // Rebind capture: swallow the next key press and hand it to the UI (Escape cancels).
      if (this.capture) {
        const cb = this.capture;
        this.capture = null;
        e.preventDefault();
        cb(e.code === "Escape" ? "" : e.code); // "" = cancelled
        return;
      }
      if (this.suspended) return; // briefing up — swallow keys (its own listener handles dismiss)
      if (!this.keys.has(e.code)) {
        this.onTap[e.code]?.(); // fire once per press (raw code)
        for (const a of ACTIONS) if (this.keybinds.code(a) === e.code) this.onActionTap[a]?.(); // action taps
      }
      this.keys.add(e.code);
    });
    window.addEventListener("keyup", (e) => this.keys.delete(e.code));

    canvas.addEventListener("click", () => {
      if (this.allowPointerLock) canvas.requestPointerLock();
    });
    canvas.addEventListener("contextmenu", (e) => e.preventDefault()); // RMB = record, not menu
    window.addEventListener("mousedown", (e) => {
      if (!this.buttons.has(e.button)) this.onBtnTap[e.button]?.(); // fire once per press
      this.buttons.add(e.button);
    });
    window.addEventListener("mouseup", (e) => this.buttons.delete(e.button));

    document.addEventListener("pointerlockchange", () => {
      this.locked = document.pointerLockElement === canvas;
      if (!this.locked) this.buttons.clear();
    });
    document.addEventListener("mousemove", (e) => {
      if (this.locked) this.onLook(e.movementX, e.movementY);
    });
  }

  /** Raw key held (by KeyboardEvent.code). Used for fixed keys like Escape. */
  isDown(code: string): boolean {
    return this.keys.has(code);
  }
  /** Is the key bound to `action` currently held? */
  isActionDown(action: Action): boolean {
    const code = this.keybinds.code(action);
    return code !== "" && this.keys.has(code);
  }
  /** Mouse button: 0 = left, 2 = right. */
  isMouseDown(button: number): boolean {
    return this.buttons.has(button);
  }
  /** Fire once when a fixed raw key goes down (e.g. Escape). */
  onPress(code: string, fn: () => void) {
    this.onTap[code] = fn;
  }
  /** Fire once when the key bound to `action` goes down. */
  onAction(action: Action, fn: () => void) {
    this.onActionTap[action] = fn;
  }
  /** Fire once when a mouse button goes down (0 = left, 2 = right). */
  onMousePress(button: number, fn: () => void) {
    this.onBtnTap[button] = fn;
  }
  setLookHandler(fn: (dx: number, dy: number) => void) {
    this.onLook = fn;
  }
  /** Grab the next key press (for rebinding). Escape cancels; the captured key doesn't fire actions. */
  captureNext(cb: (code: string) => void) {
    this.capture = cb;
  }
}
