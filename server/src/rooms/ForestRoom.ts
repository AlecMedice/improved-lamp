import { Room, Client } from "@colyseus/core";
import { GameState, Player, Clue } from "./schema/GameState";

/** Length of one match (a compressed night), in seconds. */
const MATCH_SECONDS = 600;
const WORLD_HALF = 200;

// --- Clue (hint) framework tuning ---
const STRIDE = 2.4; // metres Bigfoot travels between dropped footprints
const BRANCH_CHANCE = 0.18; // chance a footstep also snaps a nearby branch
const CLUE_LIFETIME = 50; // seconds before a track goes cold and disappears
const MAX_CLUES = 80; // hard cap on live clues

// --- Filming / catching tuning ---
const FILM_RANGE = 38; // server sanity range a hunter can film Bigfoot from
const FILM_SECONDS = 3.0; // seconds of Bigfoot-in-frame = one solid video
const FILM_DECAY = 0.5; // how fast an interrupted clip drains (fraction/sec)
const CATCH_RADIUS = 2.6; // how close Bigfoot must get to catch a hunter

/** timeOfDay threshold -> phase name. First threshold the time is *below* wins. */
const PHASES: Array<[number, string]> = [
  [0.15, "dusk"],
  [0.45, "nightfall"],
  [0.75, "midnight"],
  [0.95, "witching"],
  [Infinity, "dawn"],
];

type FilmFlag = { recording: boolean; inView: boolean };

export class ForestRoom extends Room<GameState> {
  maxClients = 6;

  private elapsed = 0;
  private clueSeq = 0;
  private clueAge = new Map<string, number>(); // clue id -> elapsed time when created
  private lastTrack = new Map<string, { x: number; z: number }>(); // bigfoot sid -> last footprint
  private filmFlags = new Map<string, FilmFlag>(); // hunter sid -> live recording flags

  onCreate() {
    this.setState(new GameState());

    // Clients stream their transform + intent here. v1: trust + clamp.
    this.onMessage("move", (client, data: any) => {
      const p = this.state.players.get(client.sessionId);
      if (!p || p.status !== "active" || !data) return;

      p.x = clamp(num(data.x, p.x), -WORLD_HALF, WORLD_HALF);
      p.y = clamp(num(data.y, p.y), -20, 60);
      p.z = clamp(num(data.z, p.z), -WORLD_HALF, WORLD_HALF);
      p.ry = num(data.ry, p.ry);
      if (typeof data.flashlightOn === "boolean") p.flashlightOn = data.flashlightOn;
      p.battery = clamp(num(data.battery, p.battery), 0, 100);
      p.stamina = clamp(num(data.stamina, p.stamina), 0, 100);

      const flag = this.filmFlags.get(client.sessionId) ?? { recording: false, inView: false };
      flag.recording = !!data.recording;
      flag.inView = !!data.inView;
      this.filmFlags.set(client.sessionId, flag);
      p.filming = flag.recording && p.role !== "bigfoot";
    });

    this.setSimulationInterval((dt) => this.update(dt), 1000 / 20);
    console.log("ForestRoom created.");
  }

  onJoin(client: Client, options: any) {
    const p = new Player();
    const hasBigfoot = [...this.state.players.values()].some((pl) => pl.role === "bigfoot");
    p.role = options?.role === "bigfoot" && !hasBigfoot ? "bigfoot" : "searcher";
    p.name = (options?.name as string) || (p.role === "bigfoot" ? "Bigfoot" : "Searcher");

    // Searchers start at the base-camp clearing; Bigfoot starts out in the trees.
    if (p.role === "bigfoot") {
      const a = Math.random() * Math.PI * 2;
      p.x = Math.cos(a) * 90;
      p.z = Math.sin(a) * 90;
    } else {
      p.x = (Math.random() - 0.5) * 8;
      p.z = 18 + (Math.random() - 0.5) * 4;
    }
    p.y = 0;

    this.state.players.set(client.sessionId, p);
    this.filmFlags.set(client.sessionId, { recording: false, inView: false });
    console.log(`${client.sessionId} joined as ${p.role} (${this.clients.length}/${this.maxClients}).`);
  }

  onLeave(client: Client) {
    this.state.players.delete(client.sessionId);
    this.filmFlags.delete(client.sessionId);
    this.lastTrack.delete(client.sessionId);
  }

  // ---------------------------------------------------------------------------

  private update(dtMs: number) {
    if (this.state.winner) return;
    const dt = dtMs / 1000;
    this.elapsed += dt;
    this.state.timeOfDay = Math.min(1, this.elapsed / MATCH_SECONDS);
    this.state.phase = phaseFor(this.state.timeOfDay);

    const bigfoots: Array<{ sid: string; p: Player }> = [];
    const hunters: Array<{ sid: string; p: Player }> = [];
    this.state.players.forEach((p, sid) => {
      if (p.role === "bigfoot") bigfoots.push({ sid, p });
      else hunters.push({ sid, p });
    });

    this.dropClues(bigfoots);
    this.expireClues();
    this.updateFilming(dt, hunters, bigfoots);
    this.updateCatching(hunters, bigfoots);
    this.evaluateWin(hunters, bigfoots);
  }

  /** Bigfoot leaves footprints (and the odd broken branch) as it walks. */
  private dropClues(bigfoots: Array<{ sid: string; p: Player }>) {
    for (const { sid, p } of bigfoots) {
      const last = this.lastTrack.get(sid);
      if (!last) {
        this.lastTrack.set(sid, { x: p.x, z: p.z });
        continue;
      }
      const dx = p.x - last.x;
      const dz = p.z - last.z;
      if (dx * dx + dz * dz < STRIDE * STRIDE) continue;

      this.addClue("footprint", p.x, p.z, p.ry);
      if (Math.random() < BRANCH_CHANCE) {
        this.addClue("branch", p.x + (Math.random() - 0.5) * 1.6, p.z + (Math.random() - 0.5) * 1.6, Math.random() * Math.PI * 2);
      }
      this.lastTrack.set(sid, { x: p.x, z: p.z });
    }
  }

  private addClue(ctype: string, x: number, z: number, ry: number) {
    if (this.state.clues.length >= MAX_CLUES) {
      const oldest = this.state.clues.shift();
      if (oldest) this.clueAge.delete(oldest.id);
    }
    const c = new Clue();
    c.id = "c" + this.clueSeq++;
    c.ctype = ctype;
    c.x = x;
    c.z = z;
    c.ry = ry;
    this.clueAge.set(c.id, this.elapsed);
    this.state.clues.push(c);
  }

  private expireClues() {
    for (let i = this.state.clues.length - 1; i >= 0; i--) {
      const c = this.state.clues[i];
      if (!c) continue;
      const born = this.clueAge.get(c.id) ?? this.elapsed;
      if (this.elapsed - born > CLUE_LIFETIME) {
        this.state.clues.splice(i, 1);
        this.clueAge.delete(c.id);
      }
    }
  }

  /** Accrue footage while a hunter records Bigfoot in-frame and within range. */
  private updateFilming(dt: number, hunters: Array<{ sid: string; p: Player }>, bigfoots: Array<{ sid: string; p: Player }>) {
    for (const { sid, p } of hunters) {
      if (p.status !== "active") continue;
      const flag = this.filmFlags.get(sid);
      const inRange = bigfoots.some(({ p: b }) => withinRange(p, b, FILM_RANGE));
      const gaining = !!flag?.recording && !!flag?.inView && inRange;

      if (gaining) {
        p.filmProgress += dt / FILM_SECONDS;
        if (p.filmProgress >= 1) {
          p.filmProgress = 0;
          this.state.videosCaptured++;
        }
      } else if (p.filmProgress > 0) {
        p.filmProgress = Math.max(0, p.filmProgress - dt * FILM_DECAY);
      }
    }
  }

  /** Bigfoot catches any active hunter it gets close enough to (after the dusk grace). */
  private updateCatching(hunters: Array<{ sid: string; p: Player }>, bigfoots: Array<{ sid: string; p: Player }>) {
    if (this.state.phase === "dusk") return;
    for (const { p: b } of bigfoots) {
      for (const { p: h } of hunters) {
        if (h.status !== "active") continue;
        if (withinRange(h, b, CATCH_RADIUS)) {
          h.status = "caught";
          h.filming = false;
          h.filmProgress = 0;
        }
      }
    }
  }

  private evaluateWin(hunters: Array<{ sid: string; p: Player }>, bigfoots: Array<{ sid: string; p: Player }>) {
    if (hunters.length === 0) return; // empty/forming room

    if (this.state.videosCaptured >= this.state.videosRequired) {
      this.state.winner = "hunters";
      return;
    }
    const activeHunters = hunters.filter(({ p }) => p.status === "active");
    if (bigfoots.length > 0 && activeHunters.length === 0) {
      this.state.winner = "bigfoot";
      return;
    }
    if (this.state.timeOfDay >= 1) {
      this.state.winner = "hunters"; // survived to dawn — the expedition escapes
    }
  }
}

function phaseFor(t: number): string {
  for (const [thr, name] of PHASES) if (t < thr) return name;
  return "dawn";
}
function withinRange(a: { x: number; z: number }, b: { x: number; z: number }, r: number): boolean {
  const dx = a.x - b.x;
  const dz = a.z - b.z;
  return dx * dx + dz * dz <= r * r;
}
function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}
function num(v: any, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}
