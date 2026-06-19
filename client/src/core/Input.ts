/** Keyboard + pointer-lock mouse input (incl. mouse buttons). Intentionally tiny. */
export class Input {
  private keys = new Set<string>();
  private buttons = new Set<number>();
  private onTap: Record<string, () => void> = {};
  private onLook: (dx: number, dy: number) => void = () => {};
  locked = false;
  allowPointerLock = true; // disabled while a menu/map is open

  constructor(private canvas: HTMLElement) {
    window.addEventListener("keydown", (e) => {
      if (!this.keys.has(e.code)) this.onTap[e.code]?.(); // fire once per press
      this.keys.add(e.code);
    });
    window.addEventListener("keyup", (e) => this.keys.delete(e.code));

    canvas.addEventListener("click", () => {
      if (this.allowPointerLock) canvas.requestPointerLock();
    });
    canvas.addEventListener("contextmenu", (e) => e.preventDefault()); // RMB = record, not menu
    window.addEventListener("mousedown", (e) => this.buttons.add(e.button));
    window.addEventListener("mouseup", (e) => this.buttons.delete(e.button));

    document.addEventListener("pointerlockchange", () => {
      this.locked = document.pointerLockElement === canvas;
      if (!this.locked) this.buttons.clear();
    });
    document.addEventListener("mousemove", (e) => {
      if (this.locked) this.onLook(e.movementX, e.movementY);
    });
  }

  isDown(code: string): boolean {
    return this.keys.has(code);
  }
  /** Mouse button: 0 = left, 2 = right. */
  isMouseDown(button: number): boolean {
    return this.buttons.has(button);
  }
  onPress(code: string, fn: () => void) {
    this.onTap[code] = fn;
  }
  setLookHandler(fn: (dx: number, dy: number) => void) {
    this.onLook = fn;
  }
}
