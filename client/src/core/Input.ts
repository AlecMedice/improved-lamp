/** Keyboard + pointer-lock mouse input. Intentionally tiny and readable. */
export class Input {
  private keys = new Set<string>();
  private onTap: Record<string, () => void> = {};
  private onLook: (dx: number, dy: number) => void = () => {};
  locked = false;

  constructor(private canvas: HTMLElement) {
    window.addEventListener("keydown", (e) => {
      if (!this.keys.has(e.code)) this.onTap[e.code]?.(); // fire once per press
      this.keys.add(e.code);
    });
    window.addEventListener("keyup", (e) => this.keys.delete(e.code));

    canvas.addEventListener("click", () => canvas.requestPointerLock());
    document.addEventListener("pointerlockchange", () => {
      this.locked = document.pointerLockElement === canvas;
    });
    document.addEventListener("mousemove", (e) => {
      if (this.locked) this.onLook(e.movementX, e.movementY);
    });
  }

  isDown(code: string): boolean {
    return this.keys.has(code);
  }
  onPress(code: string, fn: () => void) {
    this.onTap[code] = fn;
  }
  setLookHandler(fn: (dx: number, dy: number) => void) {
    this.onLook = fn;
  }
}
