import { mulberry32 } from "./rng";
import { WORLD, PATH_GEN } from "./constants";

/** A trail through the forest: a polyline of points, walked outward from camp. */
export type ForestPath = {
  /** Centreline points, camp end first. */
  pts: { x: number; z: number }[];
  /** Half-width of the cleared corridor (m). Trees inside it are skipped. */
  halfWidth: number;
};

/**
 * Old logging trails radiating out of the base camp.
 *
 * These are real terrain features, not decoration: `buildColliders` skips trees inside the
 * corridor, so a path is a genuinely tree-free lane — fast to run, but with long sightlines
 * that make you easy to film and easy to spot. Taking the trail is a speed-for-exposure trade,
 * which is the whole reason they exist.
 *
 * Seed-derived, so every client and the host lay down the identical network with nothing
 * replicated. Each trail starts at the edge of the camp clearing and wanders outward with a
 * bounded heading jitter until it leaves the map, so it reads as a meander rather than a spoke.
 */
export function generatePaths(seed: number): ForestPath[] {
  const rand = mulberry32(seed ^ PATH_GEN.seedXor);
  const out: ForestPath[] = [];
  const half = WORLD.size / 2;

  for (let i = 0; i < PATH_GEN.count; i++) {
    // Fan the trailheads around camp so two never leave in the same direction, then jitter
    // inside that slice — evenly spaced spokes would read as man-made radial symmetry.
    const slice = (Math.PI * 2) / PATH_GEN.count;
    let heading = i * slice + rand() * slice;
    let x = Math.cos(heading) * WORLD.baseCampRadius;
    let z = Math.sin(heading) * WORLD.baseCampRadius;

    const pts = [{ x, z }];
    for (let step = 0; step < PATH_GEN.maxSteps; step++) {
      heading += (rand() * 2 - 1) * PATH_GEN.jitter;
      x += Math.cos(heading) * PATH_GEN.stepLength;
      z += Math.sin(heading) * PATH_GEN.stepLength;
      pts.push({ x, z });
      if (Math.abs(x) > half || Math.abs(z) > half) break; // ran off the map — done
    }

    out.push({ pts, halfWidth: PATH_GEN.minHalfWidth + rand() * PATH_GEN.halfWidthSpan });
  }
  return out;
}

/**
 * How far inside a trail corridor (x,z) sits: 0 = off-trail, 1 = dead centre.
 * `margin` widens the test past the corridor itself — the tree loop uses it to keep trunks
 * from crowding the edge of a lane that is supposed to feel open.
 */
export function pathDepth(paths: readonly ForestPath[], x: number, z: number, margin = 0): number {
  let best = 0;
  for (const path of paths) {
    const w = path.halfWidth + margin;
    for (let i = 1; i < path.pts.length; i++) {
      const a = path.pts[i - 1];
      const b = path.pts[i];
      const dx = b.x - a.x;
      const dz = b.z - a.z;
      const len2 = dx * dx + dz * dz || 1e-6;
      let t = ((x - a.x) * dx + (z - a.z) * dz) / len2;
      t = t < 0 ? 0 : t > 1 ? 1 : t;
      const cx = a.x + dx * t;
      const cz = a.z + dz * t;
      const d = Math.sqrt((x - cx) ** 2 + (z - cz) ** 2);
      if (d < w) {
        const depth = 1 - d / w;
        if (depth > best) best = depth;
      }
    }
  }
  return best;
}
