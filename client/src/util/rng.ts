/**
 * Deterministic RNG helpers. Moved to shared/sim so the client and the authoritative
 * server generate the same forest; re-exported here so existing imports keep working.
 */
export { mulberry32, makeValueNoise } from "../../../shared/sim";
