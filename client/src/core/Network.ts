import * as THREE from "three";
import { Client, Room } from "colyseus.js";
import { SERVER_URL } from "../config";
import { RemotePlayer } from "../entities/RemotePlayer";
import { ClueData } from "../world/ClueField";

type MovePayload = {
  x: number; y: number; z: number; ry: number;
  flashlightOn: boolean; battery: number; stamina: number;
  recording: boolean; inView: boolean;
};

export type SelfInfo = { status: string; filmProgress: number; role: string };

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
  private remotes = new Map<string, RemotePlayer>();
  private bigfoot?: RemotePlayer;
  connected = false;

  onStatus: (msg: string) => void = () => {};
  onPhase: (phase: string, timeOfDay: number) => void = () => {};
  onFootage: (captured: number, required: number) => void = () => {};
  onSelf: (info: SelfInfo) => void = () => {};
  onEnd: (winner: string) => void = () => {};
  onClueAdd: (clue: ClueData) => void = () => {};
  onClueRemove: (id: string) => void = () => {};

  constructor(private scene: THREE.Scene, private role: string, private name: string) {
    this.client = new Client(SERVER_URL);
  }

  async connect(): Promise<boolean> {
    try {
      this.room = await this.client.joinOrCreate("forest", { role: this.role, name: this.name });
      this.connected = true;
      this.onStatus("online");
      this.bind(this.room);
      return true;
    } catch (e) {
      this.onStatus("offline · solo");
      console.warn("Could not reach the Hollow Pines server — running offline.", e);
      return false;
    }
  }

  private bind(room: Room) {
    const state = room.state as any;

    state.players.onAdd((player: any, key: string) => {
      if (key === room.sessionId) {
        const applySelf = () =>
          this.onSelf({ status: player.status, filmProgress: player.filmProgress, role: player.role });
        applySelf();
        player.onChange(applySelf);
        return;
      }
      const rp = new RemotePlayer(this.scene, player.role);
      this.remotes.set(key, rp);
      if (player.role === "bigfoot") this.bigfoot = rp;
      const apply = () => {
        rp.setTarget(player.x, player.y, player.z, player.ry, player.flashlightOn);
        rp.setFilming(player.filming);
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

    room.onStateChange((s: any) => {
      this.onPhase(s.phase, s.timeOfDay);
      this.onFootage(s.videosCaptured, s.videosRequired);
      if (s.winner) this.onEnd(s.winner);
    });
  }

  /** World position of the (remote) Bigfoot, or null if none / Bigfoot is local. */
  getBigfootPosition(): THREE.Vector3 | null {
    return this.bigfoot ? this.bigfoot.group.position.clone() : null;
  }

  /** Flat (x,z) of remote searchers — teammates shown on the hunters' map. */
  getRemoteSearchers(): Array<{ x: number; z: number }> {
    const out: Array<{ x: number; z: number }> = [];
    for (const rp of this.remotes.values()) {
      if (!rp.isBigfoot) out.push({ x: rp.group.position.x, z: rp.group.position.z });
    }
    return out;
  }

  sendMove(p: MovePayload) {
    this.room?.send("move", p);
  }

  update(dt: number) {
    for (const rp of this.remotes.values()) rp.update(dt);
  }
}
