import { Schema, MapSchema, ArraySchema, type } from "@colyseus/schema";

/** One connected player — a Searcher or Bigfoot. */
export class Player extends Schema {
  @type("string") role = "searcher"; // "searcher" | "bigfoot"
  @type("string") name = "Searcher";

  // Transform (client-sent, server-clamped in v1; see ROADMAP Phase 2 for full authority)
  @type("number") x = 0;
  @type("number") y = 0;
  @type("number") z = 0;
  @type("number") ry = 0; // yaw, radians

  // Cosmetic / gameplay state mirrored to everyone
  @type("boolean") flashlightOn = false;
  @type("number") battery = 100;
  @type("number") stamina = 100;
  @type("string") status = "active"; // "active" | "downed" | "out"
}

/** A piece of evidence the searchers must collect and transmit. */
export class EvidenceNode extends Schema {
  @type("string") id = "";
  @type("string") etype = "footprint"; // footprint|fur|photo|audio|nest|claw|scat
  @type("number") x = 0;
  @type("number") z = 0;
  @type("string") collectedBy = ""; // sessionId or ""
  @type("boolean") transmitted = false;
  @type("boolean") destroyed = false;
}

/** Authoritative match state, replicated to all clients. */
export class GameState extends Schema {
  @type({ map: Player }) players = new MapSchema<Player>();
  @type([EvidenceNode]) evidence = new ArraySchema<EvidenceNode>();

  @type("string") phase = "dusk"; // dusk|nightfall|midnight|witching|dawn
  @type("number") timeOfDay = 0; // 0 (dusk) .. 1 (dawn)
  @type("number") evidenceRequired = 3;
  @type("number") evidenceTransmitted = 0;
  @type("string") winner = ""; // "" | "searchers" | "bigfoot"
}
