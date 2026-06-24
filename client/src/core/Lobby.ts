import { Client, Room } from "colyseus.js";
import { SERVER_URL } from "../config";

export type MatchHandoff = { room: Room; role: string; name: string };

/**
 * Pre-game waiting room. Joins the forest room with just a name, renders the connected
 * players, and lets the host start. Resolves with the live room + this client's assigned
 * role once the server flips `matchPhase` to "playing", so Game can adopt the same room.
 */
export class Lobby {
  private client = new Client(SERVER_URL);
  private overlay = byId("lobby-overlay");
  private list = byId("lobby-players");
  private startBtn = byId("lobby-start") as HTMLButtonElement;
  private status = byId("lobby-status");
  private handedOff = false;

  /** Join and show the lobby. Resolves when the host starts the match. */
  async join(name: string): Promise<MatchHandoff> {
    const devRole = new URLSearchParams(location.search).get("devRole") ?? undefined;
    const room = await this.client.joinOrCreate("forest", { name, devRole });
    this.overlay.style.display = "flex";
    this.startBtn.onclick = () => room.send("startMatch");

    return new Promise<MatchHandoff>((resolve) => {
      room.onStateChange((s: any) => {
        if (this.handedOff) return;
        if (s.matchPhase === "playing") {
          this.handedOff = true;
          const self = s.players.get(room.sessionId);
          this.overlay.style.display = "none";
          resolve({ room, role: self?.role ?? "searcher", name });
          return;
        }
        this.render(s, room.sessionId);
      });
    });
  }

  private render(s: any, selfId: string) {
    const isHost = s.hostId === selfId;
    const devRole = new URLSearchParams(location.search).get("devRole");
    this.list.innerHTML = "";
    s.players.forEach((p: any, sid: string) => {
      const li = document.createElement("div");
      li.className = "lobby-player";
      const crown = sid === s.hostId ? " 👑" : "";
      const you = sid === selfId ? " (you)" : "";
      const dev = sid === selfId && devRole ? ` [dev: ${devRole}]` : "";
      const off = p.connected ? "" : " — reconnecting…";
      li.textContent = `${p.name}${crown}${you}${dev}${off}`;
      this.list.appendChild(li);
    });

    const count = s.players.size;
    this.startBtn.style.display = isHost ? "block" : "none";
    this.status.textContent = isHost
      ? count >= 2
        ? `${count} in the lobby — press Start when everyone's in.`
        : "Waiting for players… you can start solo to test the world."
      : "Waiting for the host to start the match…";
  }
}

function byId(id: string): HTMLElement {
  const el = document.getElementById(id);
  if (!el) throw new Error(`lobby element #${id} missing`);
  return el;
}
