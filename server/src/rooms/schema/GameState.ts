import { Schema, MapSchema, ArraySchema, type } from "@colyseus/schema";

/** One connected player — a Searcher (hunter) or Bigfoot. */
export class Player extends Schema {
  @type("string") role = "searcher"; // "searcher" | "bigfoot"
  @type("string") name = "Searcher";

  // Transform (client-sent, server-clamped in v1; see ROADMAP Phase 2 for full authority).
  // y is the player's FEET height (terrain height), so avatars sit on the ground for everyone.
  @type("number") x = 0;
  @type("number") y = 0;
  @type("number") z = 0;
  @type("number") ry = 0; // yaw, radians

  @type("boolean") flashlightOn = false;
  @type("number") battery = 100;
  @type("number") stamina = 100;

  @type("string") status = "active"; // "active" | "caught" | "out"
  @type("boolean") filming = false; // hunter is currently recording
  @type("number") filmProgress = 0; // 0..1 of the current video clip
}

/**
 * A trace Bigfoot leaves behind — the hint framework hunters follow.
 * Spawned server-side as Bigfoot moves; expires after a lifetime so the trail goes cold.
 */
export class Clue extends Schema {
  @type("string") id = "";
  @type("string") ctype = "footprint"; // "footprint" | "branch"
  @type("number") x = 0;
  @type("number") z = 0;
  @type("number") ry = 0; // heading the track points along
}

/** A map marker a hunter drops to coordinate a stakeout. One active ping per hunter. */
export class Ping extends Schema {
  @type("string") id = "";
  @type("number") x = 0;
  @type("number") z = 0;
}

/** Authoritative match state, replicated to all clients. */
export class GameState extends Schema {
  @type({ map: Player }) players = new MapSchema<Player>();
  @type([Clue]) clues = new ArraySchema<Clue>();
  @type([Ping]) pings = new ArraySchema<Ping>();

  @type("string") phase = "dusk"; // dusk|nightfall|midnight|witching|dawn
  @type("number") timeOfDay = 0; // 0 (dusk) .. 1 (dawn)

  // Win condition: hunters must capture this many solid videos of Bigfoot.
  @type("number") videosRequired = 3;
  @type("number") videosCaptured = 0;

  @type("string") winner = ""; // "" | "hunters" | "bigfoot"
}
