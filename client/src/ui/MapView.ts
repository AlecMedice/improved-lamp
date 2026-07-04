import { CAVES } from "../config";
import { LAKE, RV } from "../../../shared/sim";

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
  bigfoot: boolean; // the local player is Bigfoot (drives the legend)
};

// Fixed world landmarks drawn as labelled glyphs so the map reads for navigation.
const TRAILHEAD_Z = -21; // -(baseCampRadius 16 + 5); matches Environment.buildTrailhead
const LANDMARKS = [
  { x: 220, z: -230, kind: "tower" as const, label: "TOWER" },
  { x: LAKE.x, z: LAKE.z, kind: "lake" as const, label: "LAKE" },
  // The RV + trailhead sit inside the camp clearing — draw their glyphs but let the CAMP label
  // speak for the cluster (stacked text here is unreadable at map scale).
  { x: RV.x, z: RV.z, kind: "rv" as const, label: "" },
  { x: -2, z: TRAILHEAD_Z, kind: "trail" as const, label: "" },
  { x: 0, z: 0, kind: "camp" as const, label: "CAMP" },
];

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
  private legendSpans: HTMLElement[]; // You/Team/Tracks/Ping/Cave/Camp rows (toggled by role)
  private open_ = false;
  private bgCanvas?: HTMLCanvasElement;

  constructor() {
    this.overlay = byId("map-overlay");
    this.frame = byId("map-frame");
    this.hint = byId("map-hint");
    const canvas = byId("map-canvas") as HTMLCanvasElement;
    this.ctx = canvas.getContext("2d")!;
    this.legendSpans = Array.from(byId("map-legend").children) as HTMLElement[];

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
      ctx.fillStyle = "rgba(0,0,0,0.30)";
      ctx.fillRect(0, 0, S, S);
    } else {
      ctx.fillStyle = "rgba(18,22,18,0.7)";
      ctx.fillRect(0, 0, S, S);
    }

    this.drawGrid(ctx);
    this.drawLandmarks(ctx);

    // clue trail (hunters) — a fading breadcrumb line + dots
    if (d.clues.length) {
      ctx.strokeStyle = "rgba(230,180,120,0.35)";
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      d.clues.forEach((c, i) => {
        const p = toMap(c.x, c.z);
        if (i === 0) ctx.moveTo(p.x, p.y); else ctx.lineTo(p.x, p.y);
      });
      ctx.stroke();
      ctx.fillStyle = "rgba(235,190,130,0.9)";
      for (const c of d.clues) {
        const p = toMap(c.x, c.z);
        ctx.beginPath();
        ctx.arc(p.x, p.y, 2.2, 0, Math.PI * 2);
        ctx.fill();
      }
    }

    // stakeout pings (hunters) — a soft pulsing ring
    const pulse = 0.5 + 0.5 * Math.sin(performance.now() / 350);
    for (const pg of d.pings) {
      const p = toMap(pg.x, pg.z);
      ctx.strokeStyle = `rgba(255,226,74,${0.5 + 0.4 * pulse})`;
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(p.x, p.y, 6 + pulse * 3, 0, Math.PI * 2);
      ctx.stroke();
      disc(ctx, p.x, p.y, 2.5, "#ffe24a");
    }

    // teammates (hunters) — glowing cyan dots
    for (const o of d.others) {
      const p = toMap(o.x, o.z);
      glowDot(ctx, p.x, p.y, 4.5, "#7ad1ff");
    }

    // self — a heading triangle so you can read which way you face at a glance
    const me = toMap(d.ownX, d.ownZ);
    this.drawSelf(ctx, me.x, me.y, d.yaw);

    this.drawCompass(ctx);
    // Team/Ping/Tracks legend entries only mean something for hunters.
    this.legendSpans.forEach((s, i) => { s.style.display = d.bigfoot && i >= 1 && i <= 3 ? "none" : ""; });

    // Frame border on top of everything.
    ctx.strokeStyle = "rgba(255,255,255,0.22)";
    ctx.lineWidth = 2;
    ctx.strokeRect(1, 1, S - 2, S - 2);
  }

  /** Faint 100 m reference grid. */
  private drawGrid(ctx: CanvasRenderingContext2D) {
    const step = (100 / (HALF * 2)) * S; // 100 world-metres in px
    ctx.strokeStyle = "rgba(255,255,255,0.06)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = step; x < S; x += step) { ctx.moveTo(x, 0); ctx.lineTo(x, S); }
    for (let y = step; y < S; y += step) { ctx.moveTo(0, y); ctx.lineTo(S, y); }
    ctx.stroke();
  }

  private drawLandmarks(ctx: CanvasRenderingContext2D) {
    ctx.font = "bold 10px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    for (const lm of LANDMARKS) {
      const p = toMap(lm.x, lm.z);
      ctx.save();
      ctx.translate(p.x, p.y);
      switch (lm.kind) {
        case "camp": // campfire — orange flame triangle over a glow
          glowDot(ctx, 0, 0, 9, "rgba(255,150,60,0.9)");
          fillTri(ctx, 0, -1, 6, "#ffce6a");
          break;
        case "tower": // lookout — tan triangle (a peaked roof)
          fillTri(ctx, 0, -1, 6, "#d9c48a");
          ctx.strokeStyle = "rgba(0,0,0,0.5)"; ctx.lineWidth = 1; ctx.stroke();
          break;
        case "rv": // research RV — a small rounded rectangle
          roundRect(ctx, -6, -3.5, 12, 7, 2, "#d9d3c2");
          break;
        case "trail": // trailhead — a wooden post glyph
          ctx.fillStyle = "#caa068"; ctx.fillRect(-1, -5, 2, 10); ctx.fillRect(-4, -5, 8, 2.5);
          break;
        case "lake": // lake — label only (the ellipse is already baked)
          break;
      }
      if (lm.label) {
        ctx.fillStyle = "rgba(255,255,255,0.85)";
        ctx.strokeStyle = "rgba(0,0,0,0.7)";
        ctx.lineWidth = 3;
        const ly = lm.kind === "lake" ? 0 : lm.kind === "camp" ? -13 : 13; // camp label above its glow
        ctx.strokeText(lm.label, 0, ly);
        ctx.fillText(lm.label, 0, ly);
      }
      ctx.restore();
    }
  }

  private drawSelf(ctx: CanvasRenderingContext2D, x: number, y: number, yaw: number) {
    // Forward is world −Z (screen up) at yaw 0; rotate the triangle to match the look direction.
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(-yaw); // screen y is inverted, so negate to turn the right way
    ctx.beginPath();
    ctx.moveTo(0, -9);
    ctx.lineTo(6, 7);
    ctx.lineTo(0, 3);
    ctx.lineTo(-6, 7);
    ctx.closePath();
    ctx.fillStyle = "#ffffff";
    ctx.fill();
    ctx.strokeStyle = "rgba(0,0,0,0.6)";
    ctx.lineWidth = 1.5;
    ctx.stroke();
    ctx.restore();
  }

  private drawCompass(ctx: CanvasRenderingContext2D) {
    ctx.font = "bold 13px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    const marks: [string, number, number][] = [["N", S / 2, 14], ["S", S / 2, S - 14], ["E", S - 12, S / 2], ["W", 12, S / 2]];
    for (const [ch, x, y] of marks) {
      ctx.fillStyle = ch === "N" ? "#ff7a6e" : "rgba(255,255,255,0.75)";
      ctx.strokeStyle = "rgba(0,0,0,0.7)";
      ctx.lineWidth = 3;
      ctx.strokeText(ch, x, y);
      ctx.fillText(ch, x, y);
    }
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
/** A dot with a soft radial halo — for the player's teammates and lit landmarks. */
function glowDot(ctx: CanvasRenderingContext2D, x: number, y: number, r: number, color: string) {
  const g = ctx.createRadialGradient(x, y, 0, x, y, r * 2.4);
  g.addColorStop(0, color);
  g.addColorStop(1, "rgba(0,0,0,0)");
  ctx.fillStyle = g;
  ctx.beginPath();
  ctx.arc(x, y, r * 2.4, 0, Math.PI * 2);
  ctx.fill();
  disc(ctx, x, y, r * 0.6, color);
}
function fillTri(ctx: CanvasRenderingContext2D, cx: number, cy: number, r: number, color: string) {
  ctx.beginPath();
  ctx.moveTo(cx, cy - r);
  ctx.lineTo(cx + r * 0.9, cy + r * 0.8);
  ctx.lineTo(cx - r * 0.9, cy + r * 0.8);
  ctx.closePath();
  ctx.fillStyle = color;
  ctx.fill();
}
function roundRect(ctx: CanvasRenderingContext2D, x: number, y: number, w: number, h: number, r: number, fill: string) {
  ctx.beginPath();
  ctx.moveTo(x + r, y);
  ctx.arcTo(x + w, y, x + w, y + h, r);
  ctx.arcTo(x + w, y + h, x, y + h, r);
  ctx.arcTo(x, y + h, x, y, r);
  ctx.arcTo(x, y, x + w, y, r);
  ctx.closePath();
  ctx.fillStyle = fill;
  ctx.fill();
}
function byId(id: string): HTMLElement {
  const el = document.getElementById(id);
  if (!el) throw new Error(`map element #${id} missing`);
  return el;
}
