/**
 * Dependency-free math helpers shared by client and server sim. Formulas match
 * three.js MathUtils exactly so the extracted sim stays numerically identical to
 * the original Environment/LocalPlayer code (no visual or reconciliation drift).
 */

/** three.js MathUtils.clamp */
export function clamp(value: number, min: number, max: number): number {
  return Math.max(min, Math.min(max, value));
}

/** three.js MathUtils.lerp — note the (1-t)*x + t*y form, kept for float parity. */
export function lerp(x: number, y: number, t: number): number {
  return (1 - t) * x + t * y;
}

/** three.js MathUtils.smoothstep */
export function smoothstep(x: number, min: number, max: number): number {
  if (x <= min) return 0;
  if (x >= max) return 1;
  x = (x - min) / (max - min);
  return x * x * (3 - 2 * x);
}
