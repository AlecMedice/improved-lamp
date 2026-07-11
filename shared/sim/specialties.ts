/**
 * Searcher character specialties — shared, pure data (no Three.js, no decorators), imported by both the
 * client and the server. See `docs/CHARACTER_FUNC_DEV.md` for the design and the per-value rationale.
 *
 * ENABLING LAYER (current): this module provides identity (ids + names), the tunables table, and the
 * random deal. The gameplay MODIFIERS in `SPECIALTIES` below are defined here but NOT yet wired into
 * gameplay — later steps read them in filming/revive/etc. Mara ("analysis") is intentionally
 * identity-only until non-film evidence exists.
 */

export type SpecialtyId = "analysis" | "photo" | "tracking" | "sound" | "endurance";

/** All five ids, in a stable order (the shuffle in `dealSpecialties` randomises the actual deal). */
export const SPECIALTY_IDS: readonly SpecialtyId[] = ["analysis", "photo", "tracking", "sound", "endurance"];

export function isSpecialtyId(x: unknown): x is SpecialtyId {
  return typeof x === "string" && (SPECIALTY_IDS as readonly string[]).includes(x);
}

/** Display name per specialty (HUD, nametag, dusk briefing). */
export const CHARACTER_NAME: Record<SpecialtyId, string> = {
  analysis: "Dr. Mara Okonkwo",
  photo: "Eli Vance",
  tracking: "Wren Castellano",
  sound: "Theo Park",
  endurance: "Sam Reyes",
};

/**
 * Tunables per specialty (Standard tier — see `docs/CHARACTER_FUNC_DEV.md` §3/§7). Numbers are
 * annotated with the baseline they modify. NOT YET APPLIED in gameplay (enabling layer is identity-only).
 */
export const SPECIALTIES = {
  // 🔬 Mara — identity-only for now: no gameplay modifier (needs a non-film evidence system to attach to).
  analysis: {},

  // 📷 Eli — reach + the flash.
  photo: {
    filmRangeMul: 1.25, // FILM_RANGE 38 -> 47.5m (server); client FILM.range 35 -> 43.75m
    flash: {
      range: 22, // short reach (< dazzle's 40)
      aimCos: Math.cos(0.5), // ~29° cone — an aimed shot
      dazzleSeconds: 3, // reuse DAZZLE_SECONDS: locks Bigfoot's roar/grab + cuts its sight
      revealSeconds: 5, // Bigfoot sees Eli's position marked for this long
      chargesPerNight: 1, // one flash per night, refills at nightfall
    },
  },

  // 🥾 Wren — the trail specialist.
  tracking: {
    clueWindowMul: 1.5, // MAP.clueWindow 15 -> 22.5s
    evidenceSightMul: 2.0, // MAP.evidenceSight 18 -> 36m
    footstepVolumeMul: 0.5, // client audio: half-volume footsteps
    mark: { cooldownSec: 8, lifetimeSec: 50 }, // team-visible trail marker (~CLUE_LIFETIME 50)
  },

  // 🎙️ Theo — ears + a filming edge.
  sound: {
    hearRangeMul: 1.8, // MAP.hearRange 35 -> 63m
    roarDirPersistSec: 10, // roar-direction indicator lingers on his HUD
    filmProgressMul: 1.15, // banks film ~15% faster (server-owned)
  },

  // 🩹 Sam — keeps the team standing.
  endurance: {
    reviveSecondsMul: 0.6, // REVIVE_SECONDS 4 -> 2.4s
    staminaMax: 150, // (baseline 100) much deeper reserve; envelope clamps to this for Sam
    staminaDrainMul: 0.85, // sprint/leap/climb cost ~15% less
    batteryGift: { amount: 50, charges: 1 }, // hand a spare battery (hold-E); needs the hand-off action
  },
} as const;

/**
 * Typed accessors — read a specialty's tunable, or the baseline default for `""`/`analysis`/anything
 * without that field. Every gameplay call site reads through these so there's no optional-chaining
 * sprawl and no place to forget the default. (Nested objects like `flash`/`mark` are read directly.)
 */
const TABLE = SPECIALTIES as unknown as Record<string, Record<string, number | undefined>>;
function specNum(id: string, key: string, dflt: number): number {
  const v = TABLE[id]?.[key];
  return typeof v === "number" ? v : dflt;
}
export const reviveMul = (id: string) => specNum(id, "reviveSecondsMul", 1); // Sam: 0.6
export const staminaMax = (id: string) => specNum(id, "staminaMax", 100); // Sam: 150
export const staminaDrainMul = (id: string) => specNum(id, "staminaDrainMul", 1); // Sam: 0.85
export const filmProgressMul = (id: string) => specNum(id, "filmProgressMul", 1); // Theo: 1.15
export const filmRangeMul = (id: string) => specNum(id, "filmRangeMul", 1); // Eli: 1.25 (step 3)
export const clueWindowMul = (id: string) => specNum(id, "clueWindowMul", 1); // Wren: 1.5
export const evidenceSightMul = (id: string) => specNum(id, "evidenceSightMul", 1); // Wren: 2.0
export const hearRangeMul = (id: string) => specNum(id, "hearRangeMul", 1); // Theo: 1.8
export const roarDirPersistSec = (id: string) => specNum(id, "roarDirPersistSec", 0); // Theo: 10 (0 = no indicator)
export const footstepVolumeMul = (id: string) => specNum(id, "footstepVolumeMul", 1); // Wren: 0.5

/** Wren's team-visible trail marker rules (cooldown + lifetime, seconds). */
export const TRACKING_MARK = SPECIALTIES.tracking.mark;

/**
 * Deal a specialty to each searcher. Distinct where the pool allows (≤5 searchers ⇒ all distinct);
 * a forced id (debug `?devSpecialty`) is honoured and removed from the random pool so it can't collide.
 * Pure given `rand` (inject `Math.random` in the server, a seeded rng in tests).
 */
export function dealSpecialties(
  searcherSids: string[],
  forced: Record<string, SpecialtyId | undefined>,
  rand: () => number
): Record<string, SpecialtyId> {
  const pool: SpecialtyId[] = [...SPECIALTY_IDS];
  for (let i = pool.length - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    [pool[i], pool[j]] = [pool[j], pool[i]];
  }
  const out: Record<string, SpecialtyId> = {};
  // Honour forced picks first, consuming them from the pool so random deals don't duplicate them.
  for (const sid of searcherSids) {
    const f = forced[sid];
    if (f) {
      out[sid] = f;
      const k = pool.indexOf(f);
      if (k >= 0) pool.splice(k, 1);
    }
  }
  // Deal the rest from the shuffled pool (wraps to a random id only if we ever exceed five searchers).
  for (const sid of searcherSids) {
    if (out[sid]) continue;
    out[sid] = pool.length ? pool.shift()! : SPECIALTY_IDS[Math.floor(rand() * SPECIALTY_IDS.length)];
  }
  return out;
}
