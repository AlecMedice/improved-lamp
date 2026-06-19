import * as THREE from "three";
import { Client, Room } from "colyseus.js";
import { SERVER_URL } from "../config";
import { RemotePlayer } from "../entities/RemotePlayer";

type MovePayload = {
  x: number; y: number; z: number; ry: number;
  flashlightOn: boolean; battery: number; stamina: number;
};

/**
 * Thin Colyseus wrapper. Degrades gracefully: if the server is unreachable the
 * game keeps running as single-player (you just won't see other people).
 *
 * Note: state is typed `any` here so the client needn't import the server's
 * schema classes. Phase 2 can share a typed schema package.
 */
export class Network {
  private client: Client;
  private room?: Room;
  private remotes = new Map<string, RemotePlayer>();
  connected = false;
  onStatus: (msg: string) => void = () => {};
  onPhase: (phase: string, timeOfDay: number) => void = () => {};

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
      if (key === room.sessionId) return; // skip self
      const rp = new RemotePlayer(this.scene, player.role);
      this.remotes.set(key, rp);
      const apply = () => rp.setTarget(player.x, player.y, player.z, player.ry, player.flashlightOn);
      apply();
      player.onChange(apply);
    });

    state.players.onRemove((_player: any, key: string) => {
      this.remotes.get(key)?.dispose();
      this.remotes.delete(key);
    });

    room.onStateChange((s: any) => this.onPhase(s.phase, s.timeOfDay));
  }

  sendMove(p: MovePayload) {
    this.room?.send("move", p);
  }

  update(dt: number) {
    for (const rp of this.remotes.values()) rp.update(dt);
  }
}
