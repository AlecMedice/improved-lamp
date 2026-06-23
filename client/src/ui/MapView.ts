import { CAVES } from "../config";

const S = 600; // internal canvas resolution (px); CSS scales it to fit
const HALF = 400; // world half-extent (world spans -HALF..HALF on x/z)

type Dot = { x: number; z: number };

export type MapData = {
  ownX: number;
  ownZ: number;
  yaw: number;
  travelMode: boolean; // Bigfoot is in a cave mouth and may pick a destination
  currentCave: number; // index of the cave the player is standing in, or -1
  others: Dot[]; // teammates to show (already filtered by role)
  clues: Dot[]; // clue trail to show (already filtered by role)
  pings: Dot[]; // stakeout pings to show (already filtered by role)
};

/**
 * Full-screen top-down map (toggled with M). Shows the local player, base camp,
 * and caves for everyone; teammates + the clue trail for hunters. For Bigfoot in
 * a cave mouth, the caves become clickable fast-travel destinations.
 */
export class MapView {
  onSelectCave: (index: number) => void = () => {};
  onMapClick: (x: number, z: number) => void = () => {};

  private overlay: HTMLElement;
  private frame: HTMLElement;
  private ctx: CanvasRenderingContext2D;
  private hint: HTMLElement;
  private caveButtons: HTMLButtonElement[] = [];
  private open_ = false;
  private bgCanvas?: HTMLCanvasElement;

  constructor() {
    this.overlay = byId("map-overlay");
    this.frame = byId("map-frame");
    this.hint = byId("map-hint");
    const canvas = byId("map-canvas") as HTMLCanvasElement;
    this.ctx = canvas.getContext("2d")!;

    // Click the dark backdrop (outside the frame) to close.
    this.overlay.addEventListener("click", (e) => {
      if (e.target === this.overlay) this.close();
    });

    // Click inside the map → world (x,z). Used by hunters to drop a ping.
    canvas.addEventListener("click", (e) => {
      const r = this.frame.getBoundingClientRect();
      const x = ((e.clientX - r.left) / r.width) * (HALF * 2) - HALF;
      const z = ((e.clientY - r.top) / r.height) * (HALF * 2) - HALF;
      this.onMapClick(x, z);
    });

    // One marker-button per cave, positioned in % so it scales with the frame.
    CAVES.forEach((c, i) => {
      const b = document.createElement("button");
      b.className = "map-cave";
      b.style.left = `${((c.x + HALF) / (HALF * 2)) * 100}%`;
      b.style.top = `${((c.z + HALF) / (HALF * 2)) * 100}%`;
      b.textContent = String(i + 1);
      b.addEventListener("click", () => this.onSelectCave(i));
      this.frame.appendChild(b);
      this.caveButtons.push(b);
    });
  }

  /** Provide the pre-baked terrain image from Environment — drawn as the map background. */
  setBakedMap(canvas: HTMLCanvasElement): void {
    this.bgCanvas = canvas;
  }

  get isOpen() {
    return this.open_;
  }
  open() {
    this.open_ = true;
    this.overlay.style.display = "flex";
  }
  close() {
    this.open_ = false;
    this.overlay.style.display = "none";
  }

  refresh(d: MapData) {
    this.caveButtons.forEach((b, i) => {
      const isCurrent = i === d.currentCave;
      const selectable = d.travelMode && !isCurrent;
      b.classList.toggle("current", isCurrent);
      b.classList.toggle("selectable", selectable);
      b.disabled = !selectable;
    });
    this.hint.textContent = d.travelMode
      ? "Click a cave to emerge there"
      : d.currentCave >= 0
        ? "Cave system on cooldown…"
        : "Bigfoot: stand in a cave mouth to fast-travel";

    const ctx = this.ctx;
    ctx.clearRect(0, 0, S, S);
    if (this.bgCanvas) {
      ctx.drawImage(this.bgCanvas, 0, 0);
      // Slight dark veil so dynamic markers stay readable over the terrain colours.
      ctx.fillStyle = "rgba(0,0,0,0.28)";
      ctx.fillRect(0, 0, S, S);
    } else {
      ctx.fillStyle = "rgba(18,22,18,0.7)";
      ctx.fillRect(0, 0, S, S);
    }
    ctx.strokeStyle = "rgba(255,255,255,0.22)";
    ctx.lineWidth = 2;
    ctx.strokeRect(1, 1, S - 2, S - 2);

    // base camp (centre)
    const camp = toMap(0, 0);
    disc(ctx, camp.x, camp.y, 7, "#ffae5e");

    // clue trail (hunters)
    ctx.fillStyle = "rgba(230,180,120,0.85)";
    for (const c of d.clues) {
      const p = toMap(c.x, c.z);
      ctx.beginPath();
      ctx.arc(p.x, p.y, 2.2, 0, Math.PI * 2);
      ctx.fill();
    }

    // stakeout pings (hunters)
    for (const pg of d.pings) {
      const p = toMap(pg.x, pg.z);
      ctx.strokeStyle = "#ffe24a";
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(p.x, p.y, 6, 0, Math.PI * 2);
      ctx.stroke();
      disc(ctx, p.x, p.y, 2.5, "#ffe24a");
    }

    // teammates (hunters)
    for (const o of d.others) {
      const p = toMap(o.x, o.z);
      disc(ctx, p.x, p.y, 4, "#7ad1ff");
    }

    // self + heading
    const me = toMap(d.ownX, d.ownZ);
    disc(ctx, me.x, me.y, 5, "#ffffff");
    ctx.strokeStyle = "#ffffff";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(me.x, me.y);
    ctx.lineTo(me.x - Math.sin(d.yaw) * 15, me.y - Math.cos(d.yaw) * 15);
    ctx.stroke();
  }
}

function toMap(x: number, z: number) {
  return { x: ((x + HALF) / (HALF * 2)) * S, y: ((z + HALF) / (HALF * 2)) * S };
}
function disc(ctx: CanvasRenderingContext2D, x: number, y: number, r: number, color: string) {
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(x, y, r, 0, Math.PI * 2);
  ctx.fill();
}
function byId(id: string): HTMLElement {
  const el = document.getElementById(id);
  if (!el) throw new Error(`map element #${id} missing`);
  return el;
}
