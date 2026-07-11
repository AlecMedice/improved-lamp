import * as THREE from "three";
import { Client, Room } from "colyseus.js";
import { SERVER_URL } from "../config";
import { RemotePlayer } from "../entities/RemotePlayer";
import { ClueData } from "../world/ClueField";
import { AudioEngine } from "./AudioEngine";

type MovePayload = {
  x: number; y: number; z: number; ry: number;
  flashlightOn: boolean; battery: number; stamina: number;
  recording: boolean; inView: boolean;
  reviving: boolean; reviveTarget: string; // hunter reviving a downed teammate (held action)
};

export type SelfInfo = { status: string; filmProgress: number; role: string; slowed: boolean; dazzled: boolean; specialty: string; characterName: string };

/** Per-night escalation multipliers, server-authoritative (see GameState). */
export type EscalationInfo = {
  bigfootSpeedMul: number;
  batteryDrainMul: number;
  staminaDrainMul: number;
  roarCooldownSec: number;
};

/**
 * Thin Colyseus wrapper. Degrades gracefully: if the server is unreachable the
 * game keeps running as single-player (no other people, no clues, local clock).
 *
 * State is typed `any` here so the client needn't import the server's schema
 * classes; Phase 2 can share a typed schema package.
 */
export class Network {
  private client: Client;
  private room?: Room;
  private adopted?: Room; // an already-joined room handed over from the lobby
  private reconnectionToken?: string;
  private intentionalLeave = false;
  private remotes = new Map<string, RemotePlayer>();
  private bigfoot?: RemotePlayer;
  connected = false;

  onStatus: (msg: string) => void = () => {};
  onPhase: (phase: string, timeOfDay: number) => void = () => {};
  onNight: (night: number, total: number) => void = () => {};
  onFootage: (captured: number, required: number) => void = () => {};
  onSelf: (info: SelfInfo) => void = () => {};
  onEnd: (winner: string) => void = () => {};
  onClueAdd: (clue: ClueData) => void = () => {};
  onClueRemove: (id: string) => void = () => {};
  onPingAdd: (id: string, x: number, z: number) => void = () => {};
  onPingRemove: (id: string) => void = () => {};
  onReturnToLobby: () => void = () => {}; // host reset the match back to the lobby
  onRoar: (x: number, z: number) => void = () => {}; // another player's roar, at its world position
  onEscalation: (e: EscalationInfo) => void = () => {};

  private prevMatchPhase = "";

  constructor(private scene: THREE.Scene, private role: string, private name: string, room?: Room, private audio?: AudioEngine) {
    this.client = new Client(SERVER_URL);
    this.adopted = room;
  }

  async connect(): Promise<boolean> {
    // Lobby handed us a live room — reuse it instead of joining a new one.
    if (this.adopted) {
      this.room = this.adopted;
      this.connected = true;
      this.reconnectionToken = this.room.reconnectionToken;
      this.onStatus("online");
      this.bind(this.room);
      return true;
    }
    try {
      this.room = await this.client.joinOrCreate("forest", { role: this.role, name: this.name });
      this.connected = true;
      this.reconnectionToken = this.room.reconnectionToken;
      this.onStatus("online");
      this.bind(this.room);
      return true;
    } catch (e) {
      this.onStatus("offline · solo");
      console.warn("Could not reach the Hollow Pines server — running offline.", e);
      return false;
    }
  }

  /** Tear down avatars before a rebind (e.g. after reconnecting) to avoid duplicates. */
  private clearRemotes() {
    for (const rp of this.remotes.values()) rp.dispose();
    this.remotes.clear();
    this.bigfoot = undefined;
  }

  /** Lost the connection mid-match: retry with the saved token for ~20s (server holds the slot). */
  private async tryReconnect() {
    if (this.intentionalLeave || !this.reconnectionToken) return;
    for (let attempt = 0; attempt < 10; attempt++) {
      this.onStatus("reconnecting…");
      try {
        this.room = await this.client.reconnect(this.reconnectionToken);
        this.reconnectionToken = this.room.reconnectionToken;
        this.connected = true;
        this.onStatus("online");
        this.clearRemotes();
        this.bind(this.room);
        return;
      } catch {
        await new Promise((r) => setTimeout(r, 2000));
      }
    }
    this.onStatus("disconnected");
  }

  private bind(room: Room) {
    const state = room.state as any;

    state.players.onAdd((player: any, key: string) => {
      if (key === room.sessionId) {
        const applySelf = () =>
          this.onSelf({
            status: player.status,
            filmProgress: player.filmProgress,
            role: player.role,
            slowed: player.slowed,
            dazzled: !!player.dazzled,
            specialty: player.specialty ?? "",
            characterName: player.characterName ?? "",
          });
        applySelf();
        player.onChange(applySelf);
        return;
      }
      const rp = new RemotePlayer(this.scene, player.role, this.audio);
      this.remotes.set(key, rp);
      if (player.role === "bigfoot") this.bigfoot = rp;
      const apply = () => {
        rp.setTarget(player.x, player.y, player.z, player.ry, player.flashlightOn);
        rp.setFilming(player.filming);
        rp.setStatus(player.status);
        rp.setBeingRevived(!!player.beingRevived);
        rp.setSpecialty(player.specialty ?? "");
      };
      apply();
      player.onChange(apply);
    });

    state.players.onRemove((_player: any, key: string) => {
      const rp = this.remotes.get(key);
      if (rp && rp === this.bigfoot) this.bigfoot = undefined;
      rp?.dispose();
      this.remotes.delete(key);
    });

    state.clues.onAdd((c: any) => this.onClueAdd({ id: c.id, ctype: c.ctype, x: c.x, z: c.z, ry: c.ry }));
    state.clues.onRemove((c: any) => this.onClueRemove(c.id));

    state.pings.onAdd((p: any) => this.onPingAdd(p.id, p.x, p.z));
    state.pings.onRemove((p: any) => this.onPingRemove(p.id));

    // Diegetic roar broadcast — fired for everyone; skip our own (we play it locally).
    room.onMessage("roar", (m: any) => {
      if (m?.by === room.sessionId) return;
      this.onRoar(m.x, m.z);
    });

    room.onStateChange((s: any) => {
      this.onPhase(s.phase, s.timeOfDay);
      this.onNight(s.nightNumber, s.totalNights);
      this.onFootage(s.videosCaptured, s.videosRequired);
      this.onEscalation({
        bigfootSpeedMul: s.bigfootSpeedMul ?? 1,
        batteryDrainMul: s.batteryDrainMul ?? 1,
        staminaDrainMul: s.staminaDrainMul ?? 1,
        roarCooldownSec: s.roarCooldownSec ?? 25,
      });
      if (s.winner) this.onEnd(s.winner);
      // Host pressed "Return to lobby" after a result → everyone resets.
      if (this.prevMatchPhase === "results" && s.matchPhase === "lobby") this.onReturnToLobby();
      this.prevMatchPhase = s.matchPhase;
    });

    // Unexpected drop → try to reconnect within the server's grace window.
    room.onLeave((code: number) => {
      this.connected = false;
      if (!this.intentionalLeave && code !== 1000) this.tryReconnect();
    });
    room.onError((code: number, message?: string) =>
      console.warn(`Room error ${code}: ${message ?? ""}`)
    );
  }

  /** This player's authoritative position (used to follow Bigfoot's drag while incapacitated). */
  getSelfPosition(): { x: number; z: number } | null {
    const p = (this.room?.state as any)?.players?.get(this.room?.sessionId);
    return p ? { x: p.x, z: p.z } : null;
  }

  /** Nearest incapacitated teammate within `radius` of (x,z) — the local hunter's revive target. */
  getIncapTeammate(x: number, z: number, radius: number): { sid: string; x: number; z: number } | null {
    const players = (this.room?.state as any)?.players;
    if (!players) return null;
    const selfSid = this.room?.sessionId;
    let best: { sid: string; x: number; z: number } | null = null;
    let bestD = radius * radius;
    players.forEach((p: any, sid: string) => {
      if (sid === selfSid || p.role === "bigfoot" || p.status !== "incapacitated") return;
      const dx = p.x - x;
      const dz = p.z - z;
      const d = dx * dx + dz * dz;
      if (d <= bestD) {
        bestD = d;
        best = { sid, x: p.x, z: p.z };
      }
    });
    return best;
  }

  /** World position of the (remote) Bigfoot, or null if none / Bigfoot is local. */
  getBigfootPosition(): THREE.Vector3 | null {
    return this.bigfoot ? this.bigfoot.group.position.clone() : null;
  }

  /**
   * Bigfoot senses overlay: reveal each hunter's silhouette when within `range` of (ox,oz).
   * `on=false` clears them all. No-op unless the local player is Bigfoot (only Bigfoot calls it).
   */
  refreshSenses(on: boolean, ox: number, oz: number, range: number) {
    const r2 = range * range;
    for (const rp of this.remotes.values()) {
      if (rp.isBigfoot) continue;
      const inRange = range <= 0 || (rp.group.position.x - ox) ** 2 + (rp.group.position.z - oz) ** 2 <= r2;
      rp.setSensed(on && inRange);
    }
  }

  /** Flat (x,z) of remote searchers — teammates shown on the hunters' map. */
  getRemoteSearchers(): Array<{ x: number; z: number }> {
    const out: Array<{ x: number; z: number }> = [];
    for (const rp of this.remotes.values()) {
      if (!rp.isBigfoot) out.push({ x: rp.group.position.x, z: rp.group.position.z });
    }
    return out;
  }

  /** Live stakeout ping positions from shared state — shown on the hunters' map. */
  getPings(): Array<{ x: number; z: number }> {
    const out: Array<{ x: number; z: number }> = [];
    const pings = (this.room?.state as any)?.pings;
    if (pings) for (const p of pings) out.push({ x: p.x, z: p.z });
    return out;
  }

  /** Hunter drops a stakeout ping at a world (x,z). */
  sendPing(x: number, z: number) {
    this.room?.send("ping", { x, z });
  }

  /** Bigfoot abilities. */
  sendRoar() {
    this.room?.send("roar");
  }
  sendGrab() {
    this.room?.send("grab");
  }
  sendCharge() {
    this.room?.send("charge");
  }

  sendMove(p: MovePayload) {
    this.room?.send("move", p);
  }

  /** Bigfoot asks the server to fast-travel to cave `index` (server validates + is authoritative). */
  /** Debug only: hot-swap the local searcher's persona (server rejects unless ALLOW_DEV_ROLE). */
  sendDebugSetSpecialty(id: string) {
    this.room?.send("debugSetSpecialty", { id });
  }

  sendCaveTravel(index: number) {
    this.room?.send("caveTravel", { index });
  }

  /** Is this client the host (can return the match to the lobby)? */
  isHost(): boolean {
    const s = this.room?.state as any;
    return !!s && !!this.room && s.hostId === this.room.sessionId;
  }

  /** Host asks the server to reset back to the lobby after a result. */
  sendReturnToLobby() {
    this.room?.send("returnToLobby");
  }

  update(dt: number) {
    for (const rp of this.remotes.values()) rp.update(dt);
  }
}
