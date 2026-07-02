import { Room, Client } from "@colyseus/core";
import { GameState, Player, Clue, Ping } from "./schema/GameState";
import { WORLD, PLAYER, CAVE, generateCaves, makeWorld, resolveCollision } from "../../../shared/sim";

// --- Night / match structure ---
// One night runs 8pm -> 8am in this many real seconds. Overridable via env for quick test matches.
const NIGHT_SECONDS = Number(process.env.NIGHT_SECONDS) || 600;
const TOTAL_NIGHTS = 3; // Bigfoot wins by surviving this many nights
const WORLD_HALF = WORLD.size / 2; // ±400 on x/z

// --- Clue (hint) framework tuning ---
const STRIDE = 2.4; // metres Bigfoot travels between dropped footprints
const BRANCH_CHANCE = 0.18; // chance a footstep also snaps a nearby branch
const CLUE_LIFETIME = 50; // seconds before a track goes cold and disappears
const MAX_CLUES = 80; // hard cap on live clues

// --- Hunter ping (stakeout markers) tuning ---
const PING_LIFETIME = 35; // seconds a ping stays before fading off the map
const MAX_PINGS = 12; // hard cap (one per hunter, but stay safe)

// --- Filming (hunters win) tuning ---
const FILM_RANGE = 38; // server sanity range a hunter can film Bigfoot from
const FILM_SECONDS = 3.0; // seconds of Bigfoot-in-frame = one solid video
const FILM_DECAY = 0.5; // how fast an interrupted clip drains (fraction/sec)

// --- Bigfoot offense (roar -> grab -> drag) tuning ---
const ROAR_RADIUS = 25; // hunters within this of Bigfoot freeze in fear
const ROAR_COOLDOWN = 25; // seconds between roars (night-1 baseline; scaled by escalation)
const FREEZE_SECONDS = 30; // how long a roared hunter is frozen in place (baseline)
const GRAB_RADIUS = 3.5; // how close Bigfoot must be to grab a frozen hunter
const INCAP_SECONDS = 60; // how long a grabbed hunter is incapacitated (and draggable)
const SLOW_SECONDS = 30; // movement-slow window after recovering from incapacitation

// --- Per-night escalation -------------------------------------------------------------------
// Each night the forest grows bolder. Indexed by nightNumber-1 (clamped to the last entry).
// Multipliers against the baselines above; tune freely. `speed/battery/stamina` are replicated
// to clients (they own that math in v1); `roarCd/freeze/clueLife` are enforced server-side.
const ESCALATION = [
  /* Night 1 */ { speed: 1.0, battery: 1.0, stamina: 1.0, roarCd: 1.0, freeze: 1.0, clueLife: 1.0 },
  /* Night 2 */ { speed: 1.1, battery: 1.25, stamina: 1.15, roarCd: 0.85, freeze: 1.1, clueLife: 0.8 },
  /* Night 3 */ { speed: 1.22, battery: 1.55, stamina: 1.3, roarCd: 0.7, freeze: 1.2, clueLife: 0.65 },
];

/** timeOfDay threshold -> phase name. First threshold the time is *below* wins. */
const PHASES: Array<[number, string]> = [
  [0.15, "dusk"],
  [0.45, "nightfall"],
  [0.75, "midnight"],
  [0.95, "witching"],
  [Infinity, "dawn"],
];

/**
 * Cave entrances — Bigfoot spawns at one. Seed-derived from the shared sim so the server,
 * every client, and the collision world all agree on the layout (previously each side
 * rolled its own Math.random() set, so caves silently disagreed everywhere).
 */
const CAVES = generateCaves(WORLD.seed);

// --- Server-authoritative movement validation -------------------------------------------------
// The client predicts movement locally and streams its result; the server re-validates each move
// against the shared world and corrects out-of-bounds results (phasing through trees, teleporting).
const SPEED_GATE_MARGIN = 1.6; // generous headroom over sprint speed (diagonal + downhill + jitter)
const SPEED_GATE_BASE = 2.0; // metres of always-allowed slack per move (covers bursts / first move)
const Y_BELOW_TOL = 1.0; // how far below terrain a feet-y may sit (sampling slop) before clamping
const Y_ABOVE_TOL = 3.75; // how far above terrain a feet-y may sit (covers Bigfoot's leap apex ~2.82 m + headroom)

type FilmFlag = { recording: boolean; inView: boolean };

export class ForestRoom extends Room<GameState> {
  maxClients = 6;

  /** Shared deterministic world — terrain + colliders the server validates movement against. */
  private world = makeWorld(WORLD.seed);

  private elapsed = 0; // global time, for clue/ping/ability aging
  private nightElapsed = 0; // time within the current night

  private clueSeq = 0;
  private clueAge = new Map<string, number>(); // clue id -> elapsed when created
  private lastTrack = new Map<string, { x: number; z: number }>(); // bigfoot sid -> last footprint
  private filmFlags = new Map<string, FilmFlag>(); // hunter sid -> live recording flags
  private lastMoveMs = new Map<string, number>(); // sid -> Date.now() of last accepted move (speed gate)
  private caveReadyAt = new Map<string, number>(); // bigfoot sid -> elapsed when cave travel is ready

  private pingSeq = 0;
  private pingAge = new Map<string, number>(); // ping id -> elapsed when created
  private pingOwner = new Map<string, string>(); // hunter sid -> their current ping id

  private roarReadyAt = new Map<string, number>(); // bigfoot sid -> elapsed when roar is ready
  private frozenUntil = new Map<string, number>(); // hunter sid -> elapsed when freeze ends
  private incapUntil = new Map<string, number>(); // hunter sid -> elapsed when incapacitation ends
  private slowUntil = new Map<string, number>(); // hunter sid -> elapsed when slow ends
  private grabbedBy = new Map<string, string>(); // hunter sid -> bigfoot sid currently dragging
  private devRoles = new Map<string, string>(); // sid -> "bigfoot"|"searcher" (dev URL param override)

  onCreate() {
    this.setState(new GameState());

    // Clients predict + stream their transform; the server re-validates and corrects (server-authoritative).
    this.onMessage("move", (client, data: any) => {
      if (this.state.matchPhase !== "playing") return; // ignore stray moves in lobby/results
      const p = this.state.players.get(client.sessionId);
      if (!p || p.status !== "active" || !data) return; // frozen/incapacitated players don't self-move

      this.applyMove(client.sessionId, p, data); // bounds + speed gate + collision + terrain feet
      p.ry = num(data.ry, p.ry); // camera aim is trusted (standard for FPS netcode)
      if (typeof data.flashlightOn === "boolean") p.flashlightOn = data.flashlightOn;
      p.battery = clamp(num(data.battery, p.battery), 0, 100);
      p.stamina = clamp(num(data.stamina, p.stamina), 0, 100);

      const flag = this.filmFlags.get(client.sessionId) ?? { recording: false, inView: false };
      flag.recording = !!data.recording;
      flag.inView = !!data.inView;
      this.filmFlags.set(client.sessionId, flag);
      p.filming = flag.recording && p.role !== "bigfoot";
    });

    // Bigfoot fast-travels between cave mouths (validated server-side; replaces the old client self-teleport).
    this.onMessage("caveTravel", (client, data: any) => {
      if (this.state.matchPhase !== "playing") return;
      const p = this.state.players.get(client.sessionId);
      if (!p || p.role !== "bigfoot" || p.status !== "active" || !data) return;
      const dest = Number(data.index);
      if (!Number.isInteger(dest) || dest < 0 || dest >= CAVES.length) return;
      const here = this.nearestCaveIndex(p.x, p.z); // must be standing in *some* mouth
      if (here < 0 || here === dest) return;
      if (this.elapsed < (this.caveReadyAt.get(client.sessionId) ?? 0)) return; // cooldown
      this.caveReadyAt.set(client.sessionId, this.elapsed + CAVE.travelCooldown);
      const c = CAVES[dest];
      const dl = Math.hypot(c.x, c.z) || 1;
      p.x = c.x - (c.x / dl) * 8; // emerge 8 m toward map centre (outside the boulder horseshoe)
      p.z = c.z - (c.z / dl) * 8;
      p.y = this.world.getHeight(p.x, p.z);
      this.lastMoveMs.set(client.sessionId, Date.now()); // reset the speed gate around the jump
    });

    // Hunters drop a stakeout ping (from the map or where they stand). One per hunter.
    this.onMessage("ping", (client, data: any) => {
      const p = this.state.players.get(client.sessionId);
      if (!p || p.role === "bigfoot" || p.status !== "active" || !data) return;
      const x = clamp(num(data.x, 0), -WORLD_HALF, WORLD_HALF);
      const z = clamp(num(data.z, 0), -WORLD_HALF, WORLD_HALF);
      this.addPing(client.sessionId, x, z);
    });

    // Bigfoot roars: freezes every active hunter within ROAR_RADIUS for FREEZE_SECONDS.
    this.onMessage("roar", (client) => {
      const bf = this.state.players.get(client.sessionId);
      if (!bf || bf.role !== "bigfoot") return;
      if (this.elapsed < (this.roarReadyAt.get(client.sessionId) ?? 0)) return;
      const e = this.esc();
      this.roarReadyAt.set(client.sessionId, this.elapsed + ROAR_COOLDOWN * e.roarCd);
      this.state.players.forEach((h, sid) => {
        if (h.role === "bigfoot" || h.status !== "active") return;
        if (withinRange(h, bf, ROAR_RADIUS)) {
          h.status = "frozen";
          this.frozenUntil.set(sid, this.elapsed + FREEZE_SECONDS * e.freeze);
        }
      });
      // Diegetic: every client hears the roar from Bigfoot's real position (carries beyond
      // the freeze radius). The roaring client suppresses its own echo locally.
      this.broadcast("roar", { x: bf.x, z: bf.z, by: client.sessionId });
    });

    // Bigfoot grabs (left-click): grab the nearest frozen hunter, or drop the one being dragged.
    this.onMessage("grab", (client) => {
      const bf = this.state.players.get(client.sessionId);
      if (!bf || bf.role !== "bigfoot") return;

      // Already dragging someone? This drops them (they stay incapacitated where left).
      let released = false;
      for (const [hsid, bsid] of this.grabbedBy) {
        if (bsid === client.sessionId) {
          this.grabbedBy.delete(hsid);
          released = true;
        }
      }
      if (released) return;

      // Otherwise grab the nearest frozen hunter in range -> incapacitate + erase footage.
      let best: string | null = null;
      let bestD = GRAB_RADIUS * GRAB_RADIUS;
      this.state.players.forEach((h, sid) => {
        if (h.status !== "frozen") return;
        const d = dist2(h, bf);
        if (d <= bestD) {
          bestD = d;
          best = sid;
        }
      });
      if (!best) return;

      const hunter = this.state.players.get(best)!;
      hunter.status = "incapacitated";
      hunter.filming = false;
      hunter.filmProgress = 0;
      this.frozenUntil.delete(best);
      this.incapUntil.set(best, this.elapsed + INCAP_SECONDS);
      this.grabbedBy.set(best, client.sessionId);
      this.state.videosCaptured = 0; // all the team's footage is erased
    });

    // Host starts the match: assign roles (one random Bigfoot if 2+), spawn, begin night 1.
    this.onMessage("startMatch", (client) => {
      if (client.sessionId !== this.state.hostId || this.state.matchPhase !== "lobby") return;
      const sids = [...this.state.players.keys()];
      // Start with a random Bigfoot (null = solo, everyone gets searcher).
      let bigfootSid: string | null = sids.length >= 2 ? sids[Math.floor(Math.random() * sids.length)] : null;

      // Apply ?devRole overrides: first dev-bigfoot request wins; demote the random pick if needed.
      for (const [sid, dr] of this.devRoles) {
        if (!this.state.players.has(sid)) continue;
        if (dr === "bigfoot" && bigfootSid !== sid) {
          bigfootSid = sid; // force this player to be Bigfoot
          break;
        }
        if (dr === "searcher" && bigfootSid === sid) {
          // Pick any other player as Bigfoot instead.
          const other = sids.find((s) => s !== sid);
          bigfootSid = other ?? null;
          break;
        }
      }

      this.state.players.forEach((p, sid) => {
        p.role = sid === bigfootSid ? "bigfoot" : "searcher";
        this.spawnPlayer(p);
      });
      this.resetMatchState();
      this.state.matchPhase = "playing";
      console.log(`Match started by ${client.sessionId}; Bigfoot = ${bigfootSid ?? "(solo)"}.`);
    });

    // Host returns everyone to the lobby after a result.
    this.onMessage("returnToLobby", (client) => {
      if (client.sessionId !== this.state.hostId || this.state.matchPhase !== "results") return;
      this.resetMatchState();
      this.state.matchPhase = "lobby";
    });

    this.setSimulationInterval((dt) => this.update(dt), 1000 / 20);
    console.log("ForestRoom created.");
  }

  onJoin(client: Client, options: any) {
    // Players land in the lobby as searchers; roles are assigned when the host starts.
    const p = new Player();
    p.role = "searcher";
    p.name = (options?.name as string) || "Searcher";
    p.connected = true;
    this.state.players.set(client.sessionId, p);
    this.filmFlags.set(client.sessionId, { recording: false, inView: false });
    if (this.state.hostId === "") this.state.hostId = client.sessionId;
    const devRole = options?.devRole;
    if (devRole === "bigfoot" || devRole === "searcher") this.devRoles.set(client.sessionId, devRole);
    console.log(`${client.sessionId} joined the lobby (${this.clients.length}/${this.maxClients}).`);
  }

  async onLeave(client: Client, consented?: boolean) {
    const sid = client.sessionId;
    const p = this.state.players.get(sid);

    // Mid-match disconnects get a grace period to reconnect (unless they left on purpose).
    if (p && !consented && this.state.matchPhase === "playing") {
      p.connected = false;
      try {
        await this.allowReconnection(client, 20);
        p.connected = true; // same sessionId resumes; sid-keyed timers stay valid
        return;
      } catch {
        // fell through the grace window — remove them below
      }
    }
    this.removePlayer(sid);
  }

  /** Drop a player and all their per-session state; reassign the host if they were it. */
  private removePlayer(sid: string) {
    this.state.players.delete(sid);
    this.filmFlags.delete(sid);
    this.lastTrack.delete(sid);
    this.removePing(sid);
    this.frozenUntil.delete(sid);
    this.incapUntil.delete(sid);
    this.slowUntil.delete(sid);
    this.roarReadyAt.delete(sid);
    this.grabbedBy.delete(sid);
    this.lastMoveMs.delete(sid);
    this.caveReadyAt.delete(sid);
    this.devRoles.delete(sid);
    // If a leaving Bigfoot was dragging hunters, free them (they stay incapacitated in place).
    for (const [hsid, bsid] of this.grabbedBy) if (bsid === sid) this.grabbedBy.delete(hsid);
    // Hand the host role to any remaining player.
    if (this.state.hostId === sid) {
      const next = this.state.players.keys().next();
      this.state.hostId = next.done ? "" : next.value;
    }
  }

  /** Place a player at their role's start point. */
  private spawnPlayer(p: Player) {
    if (p.role === "bigfoot") {
      const cave = CAVES[Math.floor(Math.random() * CAVES.length)];
      const dl = Math.hypot(cave.x, cave.z) || 1;
      // 8 m toward map centre — outside the boulder horseshoe (boulders extend ~3–4 m).
      p.x = cave.x - (cave.x / dl) * 8;
      p.z = cave.z - (cave.z / dl) * 8;
    } else {
      p.x = (Math.random() - 0.5) * 8;
      p.z = 18 + (Math.random() - 0.5) * 4;
    }
    p.y = this.world.getHeight(p.x, p.z); // feet on the terrain (camp ~0; caves vary)
    p.status = "active";
    p.slowed = false;
    p.filming = false;
    p.filmProgress = 0;
  }

  // --- Movement validation (server-authoritative) ----------------------------

  /**
   * Validate + apply a client-sent position. Clamp to world bounds, gate the per-move
   * displacement against a generous max speed (anti-teleport / speedhack), push out of any
   * collider the client tried to occupy (anti-phasing), then seat the feet on the terrain.
   * The corrected result is written to the player; the client reconciles toward it.
   */
  private applyMove(sid: string, p: Player, data: any) {
    let rx = clamp(num(data.x, p.x), -WORLD_HALF, WORLD_HALF);
    let rz = clamp(num(data.z, p.z), -WORLD_HALF, WORLD_HALF);

    // Max-displacement gate, relative to this player's last accepted position.
    const now = Date.now();
    const last = this.lastMoveMs.get(sid) ?? now;
    const dtSec = Math.min(1, Math.max(0, (now - last) / 1000)); // clamp; a long gap isn't travel budget
    this.lastMoveMs.set(sid, now);
    const allowed = this.maxSpeedFor(p) * dtSec + SPEED_GATE_BASE;
    const dx = rx - p.x;
    const dz = rz - p.z;
    const dist = Math.hypot(dx, dz);
    if (dist > allowed && dist > 1e-6) {
      const k = allowed / dist; // pull the step back along the requested direction
      rx = p.x + dx * k;
      rz = p.z + dz * k;
    }

    // Push out of any tree / RV / cave boulder / tower the client tried to occupy.
    const resolved = resolveCollision(this.world.colliders, rx, rz, PLAYER.radius);
    p.x = resolved.x;
    p.z = resolved.z;

    // Feet sit on the terrain (allow a small jump arc above, a touch of sampling slop below).
    const groundY = this.world.getHeight(p.x, p.z);
    p.y = clamp(num(data.y, groundY), groundY - Y_BELOW_TOL, groundY + Y_ABOVE_TOL);
  }

  /** Upper-bound movement speed for the gate (role + per-night escalation; generous margin). */
  private maxSpeedFor(p: Player): number {
    const roleMul = p.role === "bigfoot" ? PLAYER.bigfootSpeedMul * this.state.bigfootSpeedMul : 1;
    return PLAYER.sprintSpeed * roleMul * SPEED_GATE_MARGIN;
  }

  /** Index of a cave whose mouth (x,z) is within, or -1. */
  private nearestCaveIndex(x: number, z: number): number {
    const r2 = CAVE.triggerRadius * CAVE.triggerRadius;
    for (let i = 0; i < CAVES.length; i++) {
      const dx = CAVES[i].x - x;
      const dz = CAVES[i].z - z;
      if (dx * dx + dz * dz <= r2) return i;
    }
    return -1;
  }

  /** Clear the night clock, footage, clues, pings, and all status timers for a fresh match. */
  private resetMatchState() {
    this.state.winner = "";
    this.state.nightNumber = 1;
    this.state.timeOfDay = 0;
    this.state.phase = "dusk";
    this.state.videosCaptured = 0;
    this.elapsed = 0;
    this.nightElapsed = 0;
    this.state.clues.clear();
    this.state.pings.clear();
    this.clueAge.clear();
    this.lastTrack.clear();
    this.pingAge.clear();
    this.pingOwner.clear();
    this.roarReadyAt.clear();
    this.frozenUntil.clear();
    this.incapUntil.clear();
    this.slowUntil.clear();
    this.grabbedBy.clear();
    this.lastMoveMs.clear();
    this.caveReadyAt.clear();
  }

  // ---------------------------------------------------------------------------

  /** This night's escalation multipliers (nightNumber-1, clamped to the last entry). */
  private esc() {
    return ESCALATION[Math.min(this.state.nightNumber, ESCALATION.length) - 1];
  }

  private update(dtMs: number) {
    if (this.state.matchPhase !== "playing") return; // no clock in lobby/results
    const dt = dtMs / 1000;
    this.elapsed += dt;
    this.nightElapsed += dt;

    // Night clock: 8pm -> 8am. Daylight is skipped; we just roll to the next night.
    this.state.timeOfDay = Math.min(1, this.nightElapsed / NIGHT_SECONDS);
    this.state.phase = phaseFor(this.state.timeOfDay);
    let nightsComplete = false;
    if (this.state.timeOfDay >= 1) {
      if (this.state.nightNumber < TOTAL_NIGHTS) {
        this.state.nightNumber++;
        this.nightElapsed = 0;
        this.state.timeOfDay = 0;
      } else {
        nightsComplete = true;
      }
    }

    // Publish this night's escalation so clients apply the same multipliers we do.
    const e = this.esc();
    this.state.bigfootSpeedMul = e.speed;
    this.state.batteryDrainMul = e.battery;
    this.state.staminaDrainMul = e.stamina;
    this.state.roarCooldownSec = ROAR_COOLDOWN * e.roarCd;

    const bigfoots: Array<{ sid: string; p: Player }> = [];
    const hunters: Array<{ sid: string; p: Player }> = [];
    this.state.players.forEach((p, sid) => {
      if (p.role === "bigfoot") bigfoots.push({ sid, p });
      else hunters.push({ sid, p });
    });

    this.dropClues(bigfoots);
    this.expireClues();
    this.expirePings();
    this.updateStatuses();
    this.updateFilming(dt, hunters, bigfoots);
    this.evaluateWin(hunters, nightsComplete);
  }

  /** Tick freeze/incapacitation/slow timers and drag incapacitated hunters along. */
  private updateStatuses() {
    this.state.players.forEach((p, sid) => {
      if (p.role === "bigfoot") return;

      if (p.status === "frozen") {
        if (this.elapsed >= (this.frozenUntil.get(sid) ?? 0)) {
          p.status = "active";
          this.frozenUntil.delete(sid);
        }
      } else if (p.status === "incapacitated") {
        const grabber = this.grabbedBy.get(sid);
        if (grabber) {
          const bf = this.state.players.get(grabber);
          if (bf) {
            p.x = bf.x;
            p.z = bf.z;
            p.y = bf.y;
          } else {
            this.grabbedBy.delete(sid);
          }
        }
        if (this.elapsed >= (this.incapUntil.get(sid) ?? 0)) {
          p.status = "active";
          this.incapUntil.delete(sid);
          this.grabbedBy.delete(sid);
          this.slowUntil.set(sid, this.elapsed + SLOW_SECONDS); // 25% slow for a while after
        }
      }

      const su = this.slowUntil.get(sid);
      if (su !== undefined && this.elapsed < su) p.slowed = true;
      else {
        if (su !== undefined) this.slowUntil.delete(sid);
        if (p.slowed) p.slowed = false;
      }
    });
  }

  private evaluateWin(hunters: Array<{ sid: string; p: Player }>, nightsComplete: boolean) {
    if (hunters.length === 0) return; // empty/forming room
    if (this.state.videosCaptured >= this.state.videosRequired) {
      this.state.winner = "hunters";
    } else if (nightsComplete) {
      this.state.winner = "bigfoot"; // survived all the nights without being filmed enough
    }
    if (this.state.winner) this.state.matchPhase = "results"; // freezes the clock; host can rematch
  }

  // --- Clues -----------------------------------------------------------------

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
    const lifetime = CLUE_LIFETIME * this.esc().clueLife; // trail goes cold sooner on later nights
    for (let i = this.state.clues.length - 1; i >= 0; i--) {
      const c = this.state.clues[i];
      if (!c) continue;
      const born = this.clueAge.get(c.id) ?? this.elapsed;
      if (this.elapsed - born > lifetime) {
        this.state.clues.splice(i, 1);
        this.clueAge.delete(c.id);
      }
    }
  }

  // --- Pings -----------------------------------------------------------------

  private addPing(owner: string, x: number, z: number) {
    this.removePing(owner); // one active ping per hunter — re-pinging moves it
    if (this.state.pings.length >= MAX_PINGS) {
      const old = this.state.pings.shift();
      if (old) {
        this.pingAge.delete(old.id);
        this.clearOwnerByPing(old.id);
      }
    }
    const ping = new Ping();
    ping.id = "p" + this.pingSeq++;
    ping.x = x;
    ping.z = z;
    this.state.pings.push(ping);
    this.pingAge.set(ping.id, this.elapsed);
    this.pingOwner.set(owner, ping.id);
  }

  private removePing(owner: string) {
    const id = this.pingOwner.get(owner);
    if (!id) return;
    this.removePingById(id);
    this.pingOwner.delete(owner);
  }

  private removePingById(id: string) {
    for (let i = 0; i < this.state.pings.length; i++) {
      if (this.state.pings[i]?.id === id) {
        this.state.pings.splice(i, 1);
        break;
      }
    }
    this.pingAge.delete(id);
  }

  private clearOwnerByPing(id: string) {
    for (const [owner, pid] of this.pingOwner) {
      if (pid === id) {
        this.pingOwner.delete(owner);
        break;
      }
    }
  }

  private expirePings() {
    for (let i = this.state.pings.length - 1; i >= 0; i--) {
      const ping = this.state.pings[i];
      if (!ping) continue;
      const born = this.pingAge.get(ping.id) ?? this.elapsed;
      if (this.elapsed - born > PING_LIFETIME) {
        this.state.pings.splice(i, 1);
        this.pingAge.delete(ping.id);
        this.clearOwnerByPing(ping.id);
      }
    }
  }

  // --- Filming ---------------------------------------------------------------

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
}

function phaseFor(t: number): string {
  for (const [thr, name] of PHASES) if (t < thr) return name;
  return "dawn";
}
function withinRange(a: { x: number; z: number }, b: { x: number; z: number }, r: number): boolean {
  return dist2(a, b) <= r * r;
}
function dist2(a: { x: number; z: number }, b: { x: number; z: number }): number {
  const dx = a.x - b.x;
  const dz = a.z - b.z;
  return dx * dx + dz * dz;
}
function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}
function num(v: any, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}
