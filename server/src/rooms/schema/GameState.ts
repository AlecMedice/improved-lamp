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

  @type("string") status = "active"; // "active" | "frozen" | "incapacitated"
  @type("boolean") slowed = false; // 25% movement slow after recovering from incapacitation
  @type("boolean") beingRevived = false; // an active teammate is currently reviving this incapacitated hunter
  @type("boolean") dazzled = false; // Bigfoot only: a searcher's sustained flashlight is blinding it (roar/grab locked)
  @type("boolean") filming = false; // hunter is currently recording
  @type("number") filmProgress = 0; // 0..1 of the current video clip
  @type("boolean") connected = true; // false during a reconnection grace period
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

  // Match lifecycle.
  @type("string") matchPhase = "lobby"; // "lobby" | "playing" | "results"
  @type("string") hostId = ""; // sessionId of the host (first player); can start the match

  @type("string") phase = "dusk"; // dusk|nightfall|midnight|witching|dawn (within one night, 8pm->8am)
  @type("number") timeOfDay = 0; // 0 (8pm) .. 1 (8am) of the current night
  @type("number") nightNumber = 1; // current night, 1..totalNights
  @type("number") totalNights = 3; // Bigfoot wins by surviving this many nights

  // Win condition: hunters must capture this many solid videos of Bigfoot (team total).
  @type("number") videosRequired = 3;
  @type("number") videosCaptured = 0;

  // Per-night escalation, server-authoritative (set each tick from the ESCALATION table).
  // The server enforces the rest (freeze, clue lifetime); these are the multipliers the client
  // computes with (movement/drain are client-side in v1), replicated as the single source of truth.
  @type("number") bigfootSpeedMul = 1; // Bigfoot moves this much faster on later nights
  @type("number") batteryDrainMul = 1; // flashlight drains this much faster
  @type("number") staminaDrainMul = 1; // sprinting drains this much faster
  @type("number") roarCooldownSec = 25; // effective roar cooldown (for the Bigfoot client's UI gate)

  @type("string") winner = ""; // "" | "hunters" | "bigfoot"
}
