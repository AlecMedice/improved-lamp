import { Room, Client } from "@colyseus/core";
import { GameState, Player, EvidenceNode } from "./schema/GameState";

/** Length of one match (a compressed night), in seconds. */
const MATCH_SECONDS = 600;

/** timeOfDay threshold -> phase name. First threshold the time is *below* wins. */
const PHASES: Array<[number, string]> = [
  [0.15, "dusk"],
  [0.45, "nightfall"],
  [0.75, "midnight"],
  [0.95, "witching"],
  [Infinity, "dawn"],
];

const WORLD_HALF = 200;

export class ForestRoom extends Room<GameState> {
  maxClients = 6;
  private elapsed = 0;

  onCreate() {
    this.setState(new GameState());
    this.spawnEvidence(6);

    // Clients stream their transform here. v1: trust + clamp. (See ROADMAP Phase 2.)
    this.onMessage("move", (client, data: any) => {
      const p = this.state.players.get(client.sessionId);
      if (!p || p.status === "out" || !data) return;
      p.x = clamp(num(data.x, p.x), -WORLD_HALF, WORLD_HALF);
      p.y = clamp(num(data.y, p.y), -10, 60);
      p.z = clamp(num(data.z, p.z), -WORLD_HALF, WORLD_HALF);
      p.ry = num(data.ry, p.ry);
      if (typeof data.flashlightOn === "boolean") p.flashlightOn = data.flashlightOn;
      p.battery = clamp(num(data.battery, p.battery), 0, 100);
      p.stamina = clamp(num(data.stamina, p.stamina), 0, 100);
    });

    // Phase 4 hook: collecting / transmitting evidence will live here.
    this.onMessage("collect", (client, data: any) => {
      const node = this.state.evidence.find((e) => e.id === data?.id);
      if (node && !node.collectedBy && !node.destroyed) {
        node.collectedBy = client.sessionId;
      }
    });

    this.setSimulationInterval((dt) => this.update(dt), 1000 / 20);
    console.log("ForestRoom created.");
  }

  onJoin(client: Client, options: any) {
    const p = new Player();
    const hasBigfoot = [...this.state.players.values()].some((pl) => pl.role === "bigfoot");
    p.role = options?.role === "bigfoot" && !hasBigfoot ? "bigfoot" : "searcher";
    p.name = (options?.name as string) || (p.role === "bigfoot" ? "Bigfoot" : "Searcher");

    // Spawn searchers at the base-camp clearing; Bigfoot out in the trees.
    if (p.role === "bigfoot") {
      const a = Math.random() * Math.PI * 2;
      p.x = Math.cos(a) * 90;
      p.z = Math.sin(a) * 90;
    } else {
      p.x = (Math.random() - 0.5) * 8;
      p.z = 18 + (Math.random() - 0.5) * 4;
    }

    this.state.players.set(client.sessionId, p);
    console.log(`${client.sessionId} joined as ${p.role} (${this.clients.length}/${this.maxClients}).`);
  }

  onLeave(client: Client) {
    this.state.players.delete(client.sessionId);
  }

  private update(dtMs: number) {
    if (this.state.winner) return;
    const dt = dtMs / 1000;
    this.elapsed += dt;
    this.state.timeOfDay = Math.min(1, this.elapsed / MATCH_SECONDS);
    this.state.phase = phaseFor(this.state.timeOfDay);
    // Phase 4: evaluate win/loss (evidence transmitted, all downed, dawn reached).
  }

  private spawnEvidence(n: number) {
    const types = ["footprint", "fur", "photo", "audio", "nest", "claw", "scat"];
    for (let i = 0; i < n; i++) {
      const e = new EvidenceNode();
      e.id = "ev" + i;
      e.etype = types[i % types.length];
      const a = Math.random() * Math.PI * 2;
      const r = 30 + Math.random() * 120;
      e.x = Math.cos(a) * r;
      e.z = Math.sin(a) * r;
      this.state.evidence.push(e);
    }
  }
}

function phaseFor(t: number): string {
  for (const [thr, name] of PHASES) if (t < thr) return name;
  return "dawn";
}
function clamp(v: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, v));
}
function num(v: any, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) ? v : fallback;
}
