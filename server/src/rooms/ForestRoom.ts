import { Room, Client } from "@colyseus/core";
import { GameState, Player, Clue, Ping } from "./schema/GameState";
import {
  WORLD, PLAYER, CAVE, CLUE_LIFETIME, generateCaves, makeWorld, resolveCollision, lineBlocked, climbSupport,
  nearestCaveIndex, caveEmergePoint, dealSpecialties, CHARACTER_NAME, isSpecialtyId, type SpecialtyId,
} from "../../../shared/sim";
import { refillAllowance, gateStep, staminaCeiling, filmVisible } from "./antiCheat";

// dev-role URL override (?devRole=bigfoot) is a test convenience — never trust it in production.
// Honor it only when explicitly allowed, or outside a production build.
const ALLOW_DEV_ROLE = process.env.ALLOW_DEV_ROLE === "1" || process.env.NODE_ENV !== "production";

// --- Night / match structure ---
// One night runs 8pm -> 8am in this many real seconds. Overridable via env for quick test matches.
const NIGHT_SECONDS = Number(process.env.NIGHT_SECONDS) || 600;
const TOTAL_NIGHTS = 3; // Bigfoot wins by surviving this many nights
const WORLD_HALF = WORLD.size / 2; // ±400 on x/z

// --- Clue (hint) framework tuning ---
const STRIDE = 2.4; // metres Bigfoot travels between dropped footprints
const BRANCH_CHANCE = 0.18; // chance a footstep also snaps a nearby branch
const MAX_CLUES = 80; // hard cap on live clues

// --- Hunter ping (stakeout markers) tuning ---
const PING_LIFETIME = 35; // seconds a ping stays before fading off the map
const MAX_PINGS = 12; // hard cap (one per hunter, but stay safe)

// --- Filming (hunters win) tuning ---
const FILM_RANGE = 38; // server sanity range a hunter can film Bigfoot from
const FILM_AIM_COS = Math.cos(0.6); // hunter must be facing within ~34 deg of Bigfoot (generous vs the client's 18 deg 3D cone, since we only have yaw)
const FILM_SECONDS = 3.0; // seconds of Bigfoot-in-frame = one solid video
const FILM_DECAY = 0.5; // how fast an interrupted clip drains (fraction/sec)

// --- Bigfoot offense (roar -> grab -> drag) tuning ---
const ROAR_RADIUS = 25; // hunters within this of Bigfoot freeze in fear
const ROAR_COOLDOWN = 25; // seconds between roars (night-1 baseline; scaled by escalation)
const FREEZE_SECONDS = 30; // how long a roared hunter is frozen in place (baseline)
const GRAB_RADIUS = 3.5; // how close Bigfoot must be to grab a frozen hunter
const INCAP_SECONDS = 60; // how long a grabbed hunter is incapacitated (and draggable)
const SLOW_SECONDS = 30; // movement-slow window after recovering from incapacitation

// --- Bigfoot charge (a short forward burst to close distance) ---
const CHARGE_SPEED_MUL = 1.9; // burst multiplier over sprint speed during the window
const CHARGE_DURATION = 1.2; // seconds the burst lasts
const CHARGE_COOLDOWN = 6; // seconds after the burst ends before another charge

// --- Searcher defense (revive a downed teammate) ---
const REVIVE_RADIUS = 3.5; // how close an active hunter must stand to revive an incapacitated one
const REVIVE_SECONDS = 4; // seconds of holding the revive before the teammate is freed
const REVIVE_DECAY = 2; // progress bleeds off this many x real-time when nobody is reviving

// --- Searcher defense (dazzle Bigfoot with a sustained flashlight beam) ---
const DAZZLE_RANGE = 40; // max distance the beam deters from (< a flashlight's 60 m reach)
const DAZZLE_AIM_COS = Math.cos(0.38); // beam must be centred within ~22 deg of Bigfoot
const DAZZLE_FILL_SECONDS = 1.2; // sustained on-target time before Bigfoot is dazzled
const DAZZLE_SECONDS = 3; // how long the dazzle (sight-cut + roar/grab lock) lingers after the beam
const DAZZLE_DECAY = 2; // fill bleeds off this many x real-time once the beam leaves

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
// Distance allowance is a token bucket refilled at max speed and capped at this burst, so spamming
// many tiny moves can't earn free speed (an old per-message flat slack did — see applyMove).
const SPEED_GATE_BURST = 3.0; // metres of accumulated slack the bucket can hold (covers first move / jitter)
const Y_BELOW_TOL = 1.0; // how far below terrain a feet-y may sit (sampling slop) before clamping
// How far above terrain a feet-y may sit — derived from the leap apex so it can't silently drift if
// leapSpeed/gravity change: apex = v^2 / 2g, plus headroom for sampling slop.
const Y_ABOVE_TOL = (PLAYER.leapSpeed * PLAYER.leapSpeed) / (2 * PLAYER.gravity) + 0.9; // ~3.72 m
const STAMINA_SLACK = 2; // points of stamina-regen slack per move (jitter/rounding) in the resource envelope

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
  private moveAllowance = new Map<string, number>(); // sid -> distance token bucket (metres) for the speed gate
  private caveReadyAt = new Map<string, number>(); // bigfoot sid -> elapsed when cave travel is ready

  private pingSeq = 0;
  private pingAge = new Map<string, number>(); // ping id -> elapsed when created
  private pingOwner = new Map<string, string>(); // hunter sid -> their current ping id

  private roarReadyAt = new Map<string, number>(); // bigfoot sid -> elapsed when roar is ready
  private frozenUntil = new Map<string, number>(); // hunter sid -> elapsed when freeze ends
  private incapUntil = new Map<string, number>(); // hunter sid -> elapsed when incapacitation ends
  private slowUntil = new Map<string, number>(); // hunter sid -> elapsed when slow ends
  private grabbedBy = new Map<string, string>(); // hunter sid -> bigfoot sid currently dragging
  private reviveIntent = new Map<string, string>(); // reviver sid -> the incap teammate sid they're reviving
  private reviveProgress = new Map<string, number>(); // incap target sid -> seconds of revive accrued
  private dazzleFill = new Map<string, number>(); // bigfoot sid -> seconds of sustained flashlight on it
  private dazzledUntil = new Map<string, number>(); // bigfoot sid -> elapsed when the dazzle wears off
  private chargingUntil = new Map<string, number>(); // bigfoot sid -> elapsed while a charge burst is active
  private chargeReadyAt = new Map<string, number>(); // bigfoot sid -> elapsed when the next charge is ready
  private devRoles = new Map<string, string>(); // sid -> "bigfoot"|"searcher" (dev URL param override)
  private devSpecialties = new Map<string, SpecialtyId>(); // sid -> forced specialty (dev ?devSpecialty override)

  onCreate() {
    this.setState(new GameState());
    this.state.totalNights = TOTAL_NIGHTS; // the schema default + this constant are the same knob; publish it once

    if (!ALLOW_DEV_ROLE) console.log("ForestRoom: devRole override disabled (set ALLOW_DEV_ROLE=1 to enable).");

    // Clients predict + stream their transform; the server re-validates and corrects (server-authoritative).
    this.onMessage("move", (client, data: any) => {
      if (this.state.matchPhase !== "playing") return; // ignore stray moves in lobby/results
      const p = this.state.players.get(client.sessionId);
      if (!p || p.status !== "active" || !data) return; // frozen/incapacitated players don't self-move

      // Elapsed since this player's last accepted move — the regen budget for the resource envelope
      // (read before applyMove updates lastMoveMs; both see the same previous timestamp).
      const now = Date.now();
      const dtSec = Math.min(1, Math.max(0, (now - (this.lastMoveMs.get(client.sessionId) ?? now)) / 1000));

      this.applyMove(client.sessionId, p, data); // bounds + speed gate + collision + terrain feet
      p.ry = num(data.ry, p.ry); // camera aim is trusted (standard for FPS netcode)
      if (typeof data.flashlightOn === "boolean") p.flashlightOn = data.flashlightOn;

      // Resource envelope (anti-cheat): the client simulates drain/regen, but the server bounds what it
      // may report. Battery never regenerates (no pickups yet), so it can only decrease; a dead battery
      // forces the light off. Stamina may regen, but no faster than the sim's regen rate.
      p.battery = clamp(Math.min(num(data.battery, p.battery), p.battery), 0, 100);
      if (p.battery <= 0) p.flashlightOn = false;
      const maxStamina = staminaCeiling(p.stamina, PLAYER.staminaRegenPerSec, dtSec, STAMINA_SLACK);
      p.stamina = clamp(Math.min(num(data.stamina, p.stamina), maxStamina), 0, 100);

      const flag = this.filmFlags.get(client.sessionId) ?? { recording: false, inView: false };
      flag.recording = !!data.recording;
      flag.inView = !!data.inView;
      this.filmFlags.set(client.sessionId, flag);
      p.filming = flag.recording && p.role !== "bigfoot";

      // Held-action revive intent (validated in updateRevives, like filming — no separate RPC).
      const reviveTarget = typeof data.reviveTarget === "string" ? data.reviveTarget : "";
      if (data.reviving && reviveTarget && p.role !== "bigfoot") this.reviveIntent.set(client.sessionId, reviveTarget);
      else this.reviveIntent.delete(client.sessionId);
    });

    // Bigfoot fast-travels between cave mouths (validated server-side; replaces the old client self-teleport).
    this.onMessage("caveTravel", (client, data: any) => {
      if (this.state.matchPhase !== "playing") return;
      const p = this.state.players.get(client.sessionId);
      if (!p || p.role !== "bigfoot" || p.status !== "active" || !data) return;
      const dest = Number(data.index);
      if (!Number.isInteger(dest) || dest < 0 || dest >= CAVES.length) return;
      const here = nearestCaveIndex(CAVES, p.x, p.z); // must be standing in *some* mouth
      if (here < 0 || here === dest) return;
      if (this.elapsed < (this.caveReadyAt.get(client.sessionId) ?? 0)) return; // cooldown
      this.caveReadyAt.set(client.sessionId, this.elapsed + CAVE.travelCooldown);
      const emerge = caveEmergePoint(CAVES[dest]); // shared: same spot + heading the client fades in to
      p.x = emerge.x;
      p.z = emerge.z;
      p.y = this.world.getHeight(p.x, p.z);
      p.ry = emerge.yaw; // face back into the forest, matching the client's exit yaw
      this.resetSpeedGate(client.sessionId); // don't clamp the jump as a speedhack
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
      if (this.elapsed < (this.dazzledUntil.get(client.sessionId) ?? 0)) return; // blinded — can't roar
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
      if (this.elapsed < (this.dazzledUntil.get(client.sessionId) ?? 0)) return; // blinded — can't grab
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

    // Bigfoot charges: opens a short speed-gate window so a forward burst isn't clamped as a speedhack.
    this.onMessage("charge", (client) => {
      const bf = this.state.players.get(client.sessionId);
      if (!bf || bf.role !== "bigfoot" || bf.status !== "active") return;
      if (this.elapsed < (this.chargeReadyAt.get(client.sessionId) ?? 0)) return; // cooldown
      this.chargingUntil.set(client.sessionId, this.elapsed + CHARGE_DURATION);
      this.chargeReadyAt.set(client.sessionId, this.elapsed + CHARGE_DURATION + CHARGE_COOLDOWN);
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
      this.assignSpecialties(); // deal a random (distinct) character to each searcher
      this.state.matchPhase = "playing";
      console.log(`Match started by ${client.sessionId}; Bigfoot = ${bigfootSid ?? "(solo)"}.`);
    });

    // Host returns everyone to the lobby after a result.
    this.onMessage("returnToLobby", (client) => {
      if (client.sessionId !== this.state.hostId || this.state.matchPhase !== "results") return;
      this.resetMatchState();
      this.state.matchPhase = "lobby";
    });

    // Debug: hot-swap the caller's character mid-match so a tester can feel all five in one run.
    // Test convenience only — gated behind ALLOW_DEV_ROLE like ?devRole/?devSpecialty (off in production).
    this.onMessage("debugSetSpecialty", (client, data: any) => {
      if (!ALLOW_DEV_ROLE || this.state.matchPhase !== "playing") return;
      const p = this.state.players.get(client.sessionId);
      if (!p || p.role === "bigfoot") return;
      const id = data?.id;
      if (!isSpecialtyId(id)) return;
      p.specialty = id;
      p.characterName = CHARACTER_NAME[id];
      this.devSpecialties.set(client.sessionId, id); // sticks across a re-deal within the session
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
    if (ALLOW_DEV_ROLE && (devRole === "bigfoot" || devRole === "searcher")) this.devRoles.set(client.sessionId, devRole);
    const devSpecialty = options?.devSpecialty;
    if (ALLOW_DEV_ROLE && isSpecialtyId(devSpecialty)) this.devSpecialties.set(client.sessionId, devSpecialty);
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
    this.reviveIntent.delete(sid);
    this.reviveProgress.delete(sid);
    this.dazzleFill.delete(sid);
    this.dazzledUntil.delete(sid);
    this.chargingUntil.delete(sid);
    this.chargeReadyAt.delete(sid);
    this.lastMoveMs.delete(sid);
    this.moveAllowance.delete(sid);
    this.caveReadyAt.delete(sid);
    this.devRoles.delete(sid);
    this.devSpecialties.delete(sid);
    // If a leaving Bigfoot was dragging hunters, free them (they stay incapacitated in place).
    for (const [hsid, bsid] of this.grabbedBy) if (bsid === sid) this.grabbedBy.delete(hsid);
    // Drop any revive intent aimed at the leaver (their target vanished).
    for (const [rsid, tsid] of this.reviveIntent) if (tsid === sid) this.reviveIntent.delete(rsid);
    // Hand the host role to any remaining player.
    if (this.state.hostId === sid) {
      const next = this.state.players.keys().next();
      this.state.hostId = next.done ? "" : next.value;
    }
  }

  /** Place a player at their role's start point. */
  private spawnPlayer(p: Player) {
    if (p.role === "bigfoot") {
      const emerge = caveEmergePoint(CAVES[Math.floor(Math.random() * CAVES.length)]);
      p.x = emerge.x; // outside the boulder horseshoe (boulders extend ~3–4 m)
      p.z = emerge.z;
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

  /** Deal each searcher a random (distinct) character specialty; honour any ?devSpecialty forces. Bigfoot gets none. */
  private assignSpecialties() {
    const searcherSids: string[] = [];
    this.state.players.forEach((p, sid) => { if (p.role !== "bigfoot") searcherSids.push(sid); });
    const forced: Record<string, SpecialtyId | undefined> = {};
    for (const sid of searcherSids) forced[sid] = this.devSpecialties.get(sid);
    const deal = dealSpecialties(searcherSids, forced, Math.random);
    this.state.players.forEach((p, sid) => {
      const id = deal[sid];
      if (p.role === "bigfoot" || !id) {
        p.specialty = "";
        p.characterName = "";
      } else {
        p.specialty = id;
        p.characterName = CHARACTER_NAME[id];
      }
    });
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

    // Max-displacement gate: a token bucket of travel distance, refilled at this player's max speed and
    // capped at SPEED_GATE_BURST. Charging the bucket over *time* (not granting a flat slack per message)
    // means spamming many tiny moves can't accumulate free distance — total travel stays speed-bounded.
    const now = Date.now();
    const last = this.lastMoveMs.get(sid) ?? now;
    const dtSec = Math.min(1, Math.max(0, (now - last) / 1000)); // clamp; a long gap isn't travel budget
    this.lastMoveMs.set(sid, now);
    const budget = refillAllowance(
      this.moveAllowance.get(sid) ?? SPEED_GATE_BURST, this.maxSpeedFor(sid, p), dtSec, SPEED_GATE_BURST
    );
    const gated = gateStep(p.x, p.z, rx, rz, budget);
    rx = gated.x;
    rz = gated.z;
    this.moveAllowance.set(sid, budget - gated.spent);

    // Push out of any tree / RV / cave boulder / tower the client tried to occupy. Climb-aware for
    // Bigfoot only: at/above a climbable's top it isn't pushed out (standing on it, not through it).
    // Hunters are always pushed out (2D), so a spoofed feet-y can't slip a hunter inside a structure.
    const claimedY = num(data.y, p.y);
    const bigfoot = p.role === "bigfoot";
    const resolved = bigfoot
      ? resolveCollision(this.world.colliders, rx, rz, PLAYER.radius, claimedY, this.world.getHeight)
      : resolveCollision(this.world.colliders, rx, rz, PLAYER.radius);
    p.x = resolved.x;
    p.z = resolved.z;

    // Feet sit on the terrain (allow a small jump arc above, a touch of sampling slop below).
    // When Bigfoot is scaling/perched on a climbable, raise the accepted feet ceiling to its top.
    const groundY = this.world.getHeight(p.x, p.z);
    const support = bigfoot
      ? climbSupport(this.world.climbables, this.world.getHeight, p.x, p.z, PLAYER.radius, PLAYER.climbReach)
      : null;
    const floor = (support && support.over ? support.top : groundY) - Y_BELOW_TOL; // perched -> stand on top
    const ceil = (support ? support.top : groundY) + Y_ABOVE_TOL; // scaling/perched -> allow the height
    p.y = clamp(claimedY, floor, ceil);
  }

  /** Upper-bound movement speed for the gate (role + per-night escalation + charge burst; generous margin). */
  private maxSpeedFor(sid: string, p: Player): number {
    const roleMul = p.role === "bigfoot" ? PLAYER.bigfootSpeedMul * this.state.bigfootSpeedMul : 1;
    const chargeMul = this.elapsed < (this.chargingUntil.get(sid) ?? 0) ? CHARGE_SPEED_MUL : 1;
    return PLAYER.sprintSpeed * roleMul * chargeMul * SPEED_GATE_MARGIN;
  }

  /** Reset the movement speed gate around a legitimate server-side teleport (cave jump, spawn). */
  private resetSpeedGate(sid: string) {
    this.lastMoveMs.set(sid, Date.now());
    this.moveAllowance.set(sid, SPEED_GATE_BURST);
  }

  /** Clear the night clock, footage, clues, pings, and all status timers for a fresh match. */
  private resetMatchState() {
    // Clear dealt personas (startMatch re-deals after this; returnToLobby leaves the lobby persona-less).
    this.state.players.forEach((p) => { p.specialty = ""; p.characterName = ""; });
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
    this.reviveIntent.clear();
    this.reviveProgress.clear();
    this.dazzleFill.clear();
    this.dazzledUntil.clear();
    this.chargingUntil.clear();
    this.chargeReadyAt.clear();
    this.lastMoveMs.clear();
    this.moveAllowance.clear();
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
      if (this.state.nightNumber < this.state.totalNights) {
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
    this.updateRevives(dt);
    this.updateStatuses();
    this.updateDazzle(dt, hunters, bigfoots);
    this.updateFilming(dt, hunters, bigfoots);
    this.evaluateWin(hunters, nightsComplete);
  }

  /**
   * Accumulate revive progress from active hunters holding the revive on a downed teammate in range.
   * On completion the target returns to `active` (post-incap slowed), interrupting Bigfoot's drag and
   * footage pressure. Progress decays when nobody is actively reviving; `beingRevived` is replicated.
   */
  private updateRevives(dt: number) {
    const revivedThisTick = new Set<string>();

    for (const [reviverSid, targetSid] of this.reviveIntent) {
      const reviver = this.state.players.get(reviverSid);
      const target = this.state.players.get(targetSid);
      if (!reviver || reviver.role === "bigfoot" || reviver.status !== "active") continue;
      if (!target || target.status !== "incapacitated") continue;
      if (dist2(reviver, target) > REVIVE_RADIUS * REVIVE_RADIUS) continue;

      const prog = (this.reviveProgress.get(targetSid) ?? 0) + dt;
      revivedThisTick.add(targetSid);
      if (prog >= REVIVE_SECONDS) {
        target.status = "active"; // freed early — same post-incap recovery as a natural wake-up
        this.incapUntil.delete(targetSid);
        this.grabbedBy.delete(targetSid);
        this.slowUntil.set(targetSid, this.elapsed + SLOW_SECONDS);
        this.reviveProgress.delete(targetSid);
      } else {
        this.reviveProgress.set(targetSid, prog);
      }
    }

    // Bleed off progress for anyone no longer being actively revived; publish the in-progress flag.
    for (const [targetSid, prog] of this.reviveProgress) {
      if (revivedThisTick.has(targetSid)) continue;
      const decayed = prog - dt * REVIVE_DECAY;
      if (decayed <= 0) this.reviveProgress.delete(targetSid);
      else this.reviveProgress.set(targetSid, decayed);
    }
    this.state.players.forEach((p, sid) => {
      const flag = revivedThisTick.has(sid);
      if (p.beingRevived !== flag) p.beingRevived = flag;
    });
  }

  /**
   * A searcher who keeps a lit flashlight trained on Bigfoot (range + cone + line-of-sight) charges a
   * "dazzle": once sustained it blinds Bigfoot for a few seconds — its roar/grab are locked and its
   * client cuts the sight cone. A deterrent, not a stun-lock: it never frees an already-grabbed hunter.
   */
  private updateDazzle(dt: number, hunters: Array<{ sid: string; p: Player }>, bigfoots: Array<{ sid: string; p: Player }>) {
    for (const { sid: bfSid, p: bf } of bigfoots) {
      let aimed = false;
      for (const { p: h } of hunters) {
        if (h.status !== "active" || !h.flashlightOn) continue;
        const dx = bf.x - h.x;
        const dz = bf.z - h.z;
        const dist = Math.hypot(dx, dz);
        if (dist > DAZZLE_RANGE || dist < 1e-3) continue;
        // Beam forward from the hunter's yaw (matches the sim's -sin/-cos convention).
        const dot = (dx / dist) * -Math.sin(h.ry) + (dz / dist) * -Math.cos(h.ry);
        if (dot < DAZZLE_AIM_COS) continue; // Bigfoot isn't centred in the beam
        if (lineBlocked(this.world.colliders, h, bf)) continue; // a tree/rock blocks the light
        aimed = true;
        break;
      }

      const fill = aimed
        ? Math.min(DAZZLE_FILL_SECONDS, (this.dazzleFill.get(bfSid) ?? 0) + dt)
        : Math.max(0, (this.dazzleFill.get(bfSid) ?? 0) - dt * DAZZLE_DECAY);
      this.dazzleFill.set(bfSid, fill);
      // Sustained aim keeps refreshing the dazzle window, so it lingers DAZZLE_SECONDS after the beam leaves.
      if (fill >= DAZZLE_FILL_SECONDS) this.dazzledUntil.set(bfSid, this.elapsed + DAZZLE_SECONDS);

      const dazzled = this.elapsed < (this.dazzledUntil.get(bfSid) ?? 0);
      if (bf.dazzled !== dazzled) bf.dazzled = dazzled;
    }
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

  /**
   * Accrue footage while a hunter records Bigfoot in-frame and within range. The client's `inView` is
   * only a cheap early-out / HUD hint — the server independently recomputes visibility (range + aim cone
   * from the replicated yaw + line-of-sight), so a modified client can't bank footage through walls or
   * while facing away. This mirrors `updateDazzle`'s server-authoritative beam check.
   */
  private updateFilming(dt: number, hunters: Array<{ sid: string; p: Player }>, bigfoots: Array<{ sid: string; p: Player }>) {
    for (const { sid, p } of hunters) {
      if (p.status !== "active") continue;
      const flag = this.filmFlags.get(sid);
      const gaining = !!flag?.recording && bigfoots.some(({ p: b }) => this.canFilm(p, b));

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

  /** Server-authoritative visibility for filming (range + aim cone + line-of-sight); see antiCheat.filmVisible. */
  private canFilm(h: Player, bf: Player): boolean {
    return filmVisible(this.world.colliders, h.x, h.z, h.ry, bf.x, bf.z, FILM_RANGE, FILM_AIM_COS);
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
