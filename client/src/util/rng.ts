/**
 * Deterministic forest helpers — moved to the shared module so the server generates the
 * identical world. Re-exported here for backward compatibility with existing imports.
 */
export { mulberry32, makeValueNoise } from "@shared/rng";
