import * as THREE from "three";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import { RoundedBoxGeometry } from "three/examples/jsm/geometries/RoundedBoxGeometry.js";
import { WORLD, DUSK, CAVES } from "../config";
import { mulberry32 } from "../util/rng";
import {
  makeTerrain, buildColliders, FALLEN_LOGS, LOG_TABLE, LAKE, RV as RV_CFG,
  resolveCollision as simResolveCollision, logOverlap as simLogOverlap,
  lakeDepth as simLakeDepth, lineBlocked as simLineBlocked,
  type Collider, type FallenLog, type World,
} from "../../../shared/sim";

/** A keyframe in the dusk -> night -> dawn cycle. */
type SkyStop = {
  t: number;
  top: THREE.Color;
  bottom: THREE.Color;
  fog: THREE.Color;
  fogD: number;
  hemi: number;
  sun: number;
  sunCol: THREE.Color;
};

const col = (hex: string) => new THREE.Color(hex);

// Forest LOD: trees within TREE_NEAR keep the detailed 3-cone crown; farther trees use a single-cone
// impostor; past TREE_CULL the fog has swallowed them, so they're dropped entirely. The partition is
// only recomputed once the camera has moved TREE_LOD_STEP metres (cheap, a few times a second).
const TREE_NEAR = 120;
const TREE_CULL = 340;
const TREE_LOD_STEP = 15;

/** dusk(0) -> nightfall -> deep night -> witching -> dawn(1) */
const SKY_STOPS: SkyStop[] = [
  { t: 0.0, top: col("#2a2740"), bottom: col("#c87b53"), fog: col("#5a4a55"), fogD: 0.012, hemi: 0.6, sun: 1.6, sunCol: col("#ff9d5c") },
  { t: 0.16, top: col("#171a30"), bottom: col("#3a3550"), fog: col("#2a2b3d"), fogD: 0.018, hemi: 0.32, sun: 0.5, sunCol: col("#6a6cff") },
  { t: 0.5, top: col("#070912"), bottom: col("#0d1430"), fog: col("#0a0f1c"), fogD: 0.03, hemi: 0.16, sun: 0.18, sunCol: col("#5566cc") },
  { t: 0.85, top: col("#0a0a16"), bottom: col("#10182e"), fog: col("#0a0e1a"), fogD: 0.032, hemi: 0.14, sun: 0.16, sunCol: col("#5566cc") },
  { t: 1.0, top: col("#243a52"), bottom: col("#9fd0c9"), fog: col("#7fa6a0"), fogD: 0.014, hemi: 0.55, sun: 1.2, sunCol: col("#bfe0d0") },
];

/**
 * Builds the stylized low-poly forest: smooth-shaded terrain, instanced conifers,
 * a base camp (campfire + RV), the cave system, a dusk->dawn sky, and fog.
 */
export class Environment {
  readonly scene: THREE.Scene;
  /**
   * Static circle colliders (trees, the RV, cave boulders, tower) used for movement + LOS.
   * Built from the shared sim so the client and the authoritative server share one collider set.
   */
  readonly colliders: Collider[] = buildColliders(WORLD.seed, CAVES);

  private height = makeTerrain(WORLD.seed); // shared terrain sampler
  private creekPoints: THREE.Vector3[] = []; // sampled centre-line, for proximity audio
  private fallenLogs: FallenLog[] = FALLEN_LOGS; // shared (collision); meshes drawn from LOG_TABLE
  // Lake: an ellipse SW of camp, fed visually by the creek (shared so the sim agrees).
  private readonly lake = LAKE;
  private campfire?: THREE.PointLight;
  private caveLights: THREE.PointLight[] = []; // cave glows; only the nearest few stay lit (perf)
  // Forest LOD state: one transform per placed tree, split live across trunk / detailed / impostor meshes.
  private treeMats: THREE.Matrix4[] = [];
  private treePos: { x: number; z: number }[] = [];
  private trunkMesh!: THREE.InstancedMesh;
  private crownHi!: THREE.InstancedMesh; // detailed 3-cone crown (near)
  private crownLo!: THREE.InstancedMesh; // single-cone impostor (far)
  private lastLodX = Infinity;
  private lastLodZ = Infinity;
  private skyMat!: THREE.ShaderMaterial;
  private hemi!: THREE.HemisphereLight;
  private sun!: THREE.DirectionalLight;
  private fog!: THREE.FogExp2;

  constructor(scene: THREE.Scene) {
    this.scene = scene;
    this.buildSky();
    this.buildLights();
    this.buildTerrain();
    this.buildForest();
    this.buildBaseCamp();
    this.buildRV(RV_CFG.x, RV_CFG.z, RV_CFG.ry);
    this.buildCaves();
    this.buildCreek();
    this.buildTrailhead();
    this.buildLookoutTower(220, -230);
    this.buildLake();
    this.buildFallenTrees();
    this.buildBushes();
    this.buildRocks();
  }

  /** Terrain height at world (x,z). Players/props sample this to sit on the ground. */
  getHeight(x: number, z: number): number {
    return this.height(x, z);
  }

  /**
   * The sim world (terrain + colliders + logs) backing the shared movement step. Reuses
   * THIS Environment's own collider/log arrays so the local player and the rendered world
   * share one set. Built lazily and cached.
   */
  private _simWorld?: World;
  get simWorld(): World {
    return (this._simWorld ??= {
      seed: WORLD.seed,
      caves: CAVES,
      getHeight: this.height,
      colliders: this.colliders,
      climbables: this.colliders.filter((c) => c.climbH !== undefined),
      fallenLogs: this.fallenLogs,
    });
  }

  /** Push an (x,z) point out of any solid collider it overlaps. Returns the resolved point. */
  resolveCollision(x: number, z: number, radius: number): { x: number; z: number } {
    return simResolveCollision(this.colliders, x, z, radius);
  }

  /** True if a solid collider blocks the straight (XZ) line between two points. */
  lineBlocked(a: THREE.Vector3, b: THREE.Vector3): boolean {
    return simLineBlocked(this.colliders, a, b);
  }

  /** Horizontal distance from (x,z) to the nearest point on the creek centre-line. */
  distanceToCreek(x: number, z: number): number {
    let best = Infinity;
    for (const p of this.creekPoints) {
      const dx = p.x - x;
      const dz = p.z - z;
      const d2 = dx * dx + dz * dz;
      if (d2 < best) best = d2;
    }
    return Math.sqrt(best);
  }

  /**
   * Capsule overlap against all fallen logs, 0 = clear, 1 = fully inside.
   * Used to apply the hunter slow-down penalty (Bigfoot ignores logs).
   */
  logOverlap(x: number, z: number, playerRadius: number): number {
    return simLogOverlap(this.fallenLogs, x, z, playerRadius);
  }

  /**
   * How deep into the lake the point is, 0 = outside, 1 = dead centre.
   * Used to scale the wading speed penalty.
   */
  lakeDepth(x: number, z: number): number {
    return simLakeDepth(x, z);
  }

  /** Drive the sky/fog/lights from match time (0 = dusk .. 1 = dawn). */
  setTimeOfDay(t: number) {
    t = THREE.MathUtils.clamp(t, 0, 1);
    let a = SKY_STOPS[0];
    let b = SKY_STOPS[SKY_STOPS.length - 1];
    for (let i = 0; i < SKY_STOPS.length - 1; i++) {
      if (t >= SKY_STOPS[i].t && t <= SKY_STOPS[i + 1].t) {
        a = SKY_STOPS[i];
        b = SKY_STOPS[i + 1];
        break;
      }
    }
    const k = a === b ? 0 : (t - a.t) / (b.t - a.t);
    (this.skyMat.uniforms.top.value as THREE.Color).lerpColors(a.top, b.top, k);
    (this.skyMat.uniforms.bottom.value as THREE.Color).lerpColors(a.bottom, b.bottom, k);
    this.fog.color.lerpColors(a.fog, b.fog, k);
    this.fog.density = THREE.MathUtils.lerp(a.fogD, b.fogD, k);
    this.hemi.intensity = THREE.MathUtils.lerp(a.hemi, b.hemi, k);
    this.sun.intensity = THREE.MathUtils.lerp(a.sun, b.sun, k);
    this.sun.color.lerpColors(a.sunCol, b.sunCol, k);
  }

  private buildSky() {
    const geo = new THREE.SphereGeometry(WORLD.size, 32, 16);
    this.skyMat = new THREE.ShaderMaterial({
      side: THREE.BackSide,
      depthWrite: false,
      uniforms: { top: { value: DUSK.skyTop.clone() }, bottom: { value: DUSK.skyBottom.clone() } },
      vertexShader: `
        varying vec3 vPos;
        void main() { vPos = position; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }
      `,
      fragmentShader: `
        varying vec3 vPos;
        uniform vec3 top; uniform vec3 bottom;
        void main() {
          float h = clamp(normalize(vPos).y * 0.5 + 0.5, 0.0, 1.0);
          gl_FragColor = vec4(mix(bottom, top, pow(h, 0.7)), 1.0);
        }
      `,
    });
    this.scene.add(new THREE.Mesh(geo, this.skyMat));
    this.fog = new THREE.FogExp2(DUSK.fog.getHex(), 0.012);
    this.scene.fog = this.fog;
  }

  private buildLights() {
    this.hemi = new THREE.HemisphereLight(DUSK.ambientSky.getHex(), DUSK.ambientGround.getHex(), 0.6);
    this.scene.add(this.hemi);
    this.sun = new THREE.DirectionalLight(DUSK.sun.getHex(), 1.6);
    this.sun.position.set(-80, 28, -60); // low, setting sun
    this.scene.add(this.sun);
    // Faint, constant cool moonlight from high overhead. It's swamped by the warm sun at dusk/dawn,
    // but at deep night (sun ~0.16) it puts a cold rim on treetops and avatars so silhouettes read
    // against the dark — without lifting the overall gloom.
    const moon = new THREE.DirectionalLight(0x9fb6ff, 0.22);
    moon.position.set(60, 120, 40);
    this.scene.add(moon);
  }

  private buildTerrain() {
    const geo = new THREE.PlaneGeometry(WORLD.size, WORLD.size, WORLD.segments, WORLD.segments);
    geo.rotateX(-Math.PI / 2);
    const pos = geo.attributes.position as THREE.BufferAttribute;
    const colorArr: number[] = [];

    for (let i = 0; i < pos.count; i++) {
      const x = pos.getX(i);
      const z = pos.getZ(i);
      const h = this.getHeight(x, z);
      pos.setY(i, h);

      // Base colour from height: damp low ground → forest floor → rocky ridgeline.
      let r: number, g: number, b: number;
      if (h < 0.8) {
        r = 0.19; g = 0.26; b = 0.14; // low/wet
      } else if (h < 5) {
        r = 0.22; g = 0.33; b = 0.17; // mid forest floor
      } else if (h < 9) {
        r = 0.26; g = 0.36; b = 0.20; // upper slope
      } else {
        r = 0.32; g = 0.38; b = 0.24; // rocky ridge
      }

      // Camp clearing → brighter grass.
      const d = Math.sqrt(x * x + z * z);
      const clearK = 1 - THREE.MathUtils.smoothstep(d, 0, WORLD.baseCampRadius + 10);
      r = THREE.MathUtils.lerp(r, 0.38, clearK * 0.55);
      g = THREE.MathUtils.lerp(g, 0.52, clearK * 0.55);
      b = THREE.MathUtils.lerp(b, 0.22, clearK * 0.55);

      // Lake shore → grey-blue mud.
      const ldx = (x - this.lake.x) / (this.lake.rx * 1.25);
      const ldz = (z - this.lake.z) / (this.lake.rz * 1.25);
      const lakeK = Math.max(0, 1 - Math.sqrt(ldx * ldx + ldz * ldz));
      r = THREE.MathUtils.lerp(r, 0.17, lakeK * 0.7);
      g = THREE.MathUtils.lerp(g, 0.23, lakeK * 0.7);
      b = THREE.MathUtils.lerp(b, 0.30, lakeK * 0.7);

      colorArr.push(r, g, b);
    }

    geo.setAttribute("color", new THREE.BufferAttribute(new Float32Array(colorArr), 3));
    geo.computeVertexNormals();
    const mat = new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 1, metalness: 0 });
    this.scene.add(new THREE.Mesh(geo, mat));
  }

  /** A single conifer = tapered trunk + three stacked smooth cones, split by material. */
  private treeGeometry() {
    const trunk = new THREE.CylinderGeometry(0.22, 0.4, 3, 7);
    trunk.translate(0, 1.5, 0);
    const c1 = new THREE.ConeGeometry(2.0, 3.2, 8);
    c1.translate(0, 4.0, 0);
    const c2 = new THREE.ConeGeometry(1.5, 2.6, 8);
    c2.translate(0, 5.8, 0);
    const c3 = new THREE.ConeGeometry(1.0, 2.0, 8);
    c3.translate(0, 7.4, 0);
    const foliage = mergeGeometries([c1, c2, c3])!;
    trunk.computeVertexNormals();
    foliage.computeVertexNormals();
    return { trunk, foliage };
  }

  private buildForest() {
    const { trunk, foliage } = this.treeGeometry();
    // Impostor crown: a single cone matching the detailed crown's silhouette (base ~2.4, apex ~8.4).
    const impostor = new THREE.ConeGeometry(1.9, 6.2, 6);
    impostor.translate(0, 5.4, 0);
    impostor.computeVertexNormals();

    const trunkMat = new THREE.MeshStandardMaterial({ color: DUSK.trunk, roughness: 1 });
    const folMat = new THREE.MeshStandardMaterial({ color: DUSK.foliage, roughness: 1 });

    // Gather every tree transform (same seed/sequence/skip rules as before, so the shared tree
    // colliders still line up 1:1 with the rendered trunks).
    const rand = mulberry32(WORLD.seed ^ 0x9e3779b9);
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const half = WORLD.size / 2 - 6;
    for (let i = 0; i < WORLD.treeCount; i++) {
      const x = (rand() * 2 - 1) * half;
      const z = (rand() * 2 - 1) * half;
      if (Math.sqrt(x * x + z * z) < WORLD.baseCampRadius + 4) continue; // keep clearing open
      if (this.nearCave(x, z, 7)) continue; // keep cave mouths clear
      const s = 0.7 + rand() * 0.9;
      q.setFromAxisAngle(up, rand() * Math.PI * 2);
      const m = new THREE.Matrix4().compose(new THREE.Vector3(x, this.getHeight(x, z), z), q, new THREE.Vector3(s, s, s));
      this.treeMats.push(m);
      this.treePos.push({ x, z });
    }

    const cap = this.treeMats.length;
    this.trunkMesh = new THREE.InstancedMesh(trunk, trunkMat, cap);
    this.crownHi = new THREE.InstancedMesh(foliage, folMat, cap);
    this.crownLo = new THREE.InstancedMesh(impostor, folMat, cap);
    // The forest spans the whole map, so per-mesh frustum culling never fires; skip it (and the
    // stale-bounding-sphere hazard from a changing instance count).
    for (const mesh of [this.trunkMesh, this.crownHi, this.crownLo]) mesh.frustumCulled = false;
    this.scene.add(this.trunkMesh, this.crownHi, this.crownLo);
    this.updateForestLOD(0, 0, true); // initial partition (camera starts near camp)
  }

  /**
   * Split the forest by distance from the camera: detailed crowns near, single-cone impostors in the
   * mid-field, nothing past the fog line. Only recomputed once the camera has moved TREE_LOD_STEP.
   */
  updateForestLOD(camX: number, camZ: number, force = false) {
    if (!force && (camX - this.lastLodX) ** 2 + (camZ - this.lastLodZ) ** 2 < TREE_LOD_STEP ** 2) return;
    this.lastLodX = camX;
    this.lastLodZ = camZ;
    const near2 = TREE_NEAR * TREE_NEAR;
    const cull2 = TREE_CULL * TREE_CULL;
    let tr = 0, hi = 0, lo = 0;
    for (let i = 0; i < this.treeMats.length; i++) {
      const p = this.treePos[i];
      const d2 = (p.x - camX) ** 2 + (p.z - camZ) ** 2;
      if (d2 >= cull2) continue; // swallowed by fog — draw nothing
      const m = this.treeMats[i];
      this.trunkMesh.setMatrixAt(tr++, m);
      if (d2 < near2) this.crownHi.setMatrixAt(hi++, m);
      else this.crownLo.setMatrixAt(lo++, m);
    }
    this.trunkMesh.count = tr;
    this.crownHi.count = hi;
    this.crownLo.count = lo;
    this.trunkMesh.instanceMatrix.needsUpdate = true;
    this.crownHi.instanceMatrix.needsUpdate = true;
    this.crownLo.instanceMatrix.needsUpdate = true;
  }

  /** Trees currently drawn (any LOD) — for the perf readout. */
  get visibleTrees(): number {
    return this.trunkMesh?.count ?? 0;
  }

  private nearCave(x: number, z: number, r: number): boolean {
    return CAVES.some((c) => (c.x - x) ** 2 + (c.z - z) ** 2 < r * r);
  }

  private buildBaseCamp() {
    const y = this.getHeight(0, 0);
    const ring = new THREE.Mesh(
      new THREE.CylinderGeometry(1.1, 1.3, 0.3, 12),
      new THREE.MeshStandardMaterial({ color: 0x3a3a3a, roughness: 1 })
    );
    ring.position.set(0, y + 0.15, 0);

    const flames = new THREE.Mesh(
      new THREE.ConeGeometry(0.6, 1.4, 8),
      new THREE.MeshStandardMaterial({ color: 0xff7a2a, emissive: 0xff5a1e, emissiveIntensity: 2, roughness: 1 })
    );
    flames.position.set(0, y + 0.9, 0);

    this.campfire = new THREE.PointLight(0xff7a3a, 60, 45, 2);
    this.campfire.position.set(0, y + 1.5, 0);
    this.scene.add(ring, flames, this.campfire);
  }

  /** Low-poly research RV parked beside the campfire (rounded forms, lit windows). */
  private buildRV(x: number, z: number, ry: number) {
    const g = new THREE.Group();
    const cream = new THREE.MeshStandardMaterial({ color: 0xd9d3c2, roughness: 0.8 });
    const trim = new THREE.MeshStandardMaterial({ color: 0x7a8a6a, roughness: 0.9 });
    const dark = new THREE.MeshStandardMaterial({ color: 0x222428, roughness: 0.9 });
    const win = new THREE.MeshStandardMaterial({ color: 0xffd98a, emissive: 0xffb24d, emissiveIntensity: 1.4, roughness: 0.6 });

    const body = new THREE.Mesh(new RoundedBoxGeometry(6.4, 2.6, 2.6, 4, 0.45), cream);
    body.position.y = 2.0;
    const cab = new THREE.Mesh(new RoundedBoxGeometry(1.8, 1.9, 2.4, 3, 0.4), cream);
    cab.position.set(3.7, 1.5, 0);
    const windshield = new THREE.Mesh(new RoundedBoxGeometry(0.3, 1.0, 2.0, 2, 0.12), win);
    windshield.position.set(4.55, 1.7, 0);
    const stripe = new THREE.Mesh(new RoundedBoxGeometry(6.5, 0.45, 2.66, 2, 0.12), trim);
    stripe.position.y = 1.45;
    g.add(body, cab, windshield, stripe);

    for (const sz of [1.33, -1.33]) {
      const sw = new THREE.Mesh(new RoundedBoxGeometry(2.6, 0.9, 0.18, 2, 0.09), win);
      sw.position.set(-0.4, 2.2, sz);
      g.add(sw);
    }

    const wheelGeo = new THREE.CylinderGeometry(0.55, 0.55, 0.4, 16);
    for (const [wx, wz] of [[2.4, 1.4], [2.4, -1.4], [-2.4, 1.4], [-2.4, -1.4]] as const) {
      const w = new THREE.Mesh(wheelGeo, dark);
      w.rotation.x = Math.PI / 2;
      w.position.set(wx, 0.55, wz);
      g.add(w);
    }

    const lamp = new THREE.PointLight(0xffb866, 14, 16, 2); // warm interior glow
    lamp.position.set(0, 2.2, 0);
    g.add(lamp);

    g.position.set(x, this.getHeight(x, z), z);
    g.rotation.y = ry;
    this.scene.add(g);
    // RV body colliders are produced by the shared buildColliders().
  }

  /** Rocky cave entrances — Bigfoot's lairs and the nodes of its fast-travel network. */
  private buildCaves() {
    const rock = new THREE.MeshStandardMaterial({ color: 0x6a6a73, roughness: 1 });

    const boulder = (bx: number, by: number, bz: number, r: number, sy: number) => {
      const m = new THREE.Mesh(new THREE.IcosahedronGeometry(r, 1), rock);
      m.position.set(bx, by + r * 0.35, bz);
      m.scale.set(1, sy, 1);
      m.rotation.y = Math.random() * Math.PI;
      return m;
    };

    for (const cave of CAVES) {
      const y = this.getHeight(cave.x, cave.z);
      const dl = Math.hypot(cave.x, cave.z) || 1;
      const dx = -cave.x / dl; // toward map centre (the open side)
      const dz = -cave.z / dl;
      const px = -dz; // perpendicular (sides of the mouth)
      const pz = dx;

      const g = new THREE.Group();
      // Horseshoe of boulders with the opening facing the map centre.
      g.add(boulder(cave.x - dx * 3, y, cave.z - dz * 3, 2.0, 1.1));
      g.add(boulder(cave.x + px * 3.0, y, cave.z + pz * 3.0, 1.7, 1.2));
      g.add(boulder(cave.x - px * 3.0, y, cave.z - pz * 3.0, 1.7, 1.2));

      // No explicit mouth sphere — the boulder horseshoe + interior glow is enough.

      const glow = new THREE.PointLight(0x4a6ab0, 4, 12, 2); // faint depth inside the dark
      glow.position.set(cave.x - dx * 1.5, y + 1.4, cave.z - dz * 1.5);
      g.add(glow);
      this.caveLights.push(glow);
      this.scene.add(g);
      // Side + back colliders (mouth stays walkable) are produced by the shared buildColliders().
    }
  }

  /**
   * Bake a top-down terrain image for the map overlay (call once, reuse forever).
   * Draws: forest base, camp clearing, lake, creek, fallen logs.
   * Cave markers and player dots are drawn live on top by MapView.
   */
  generateMapCanvas(S: number, half: number): HTMLCanvasElement {
    const canvas = document.createElement("canvas");
    canvas.width = S;
    canvas.height = S;
    const ctx = canvas.getContext("2d")!;
    const w2c = (wx: number, wz: number) => ({
      x: ((wx + half) / (half * 2)) * S,
      y: ((wz + half) / (half * 2)) * S,
    });
    const sc = S / (half * 2); // world units → pixels

    // Base: dark forest floor
    ctx.fillStyle = "#243020";
    ctx.fillRect(0, 0, S, S);

    // Camp clearing — soft lighter grass disc
    const camp = w2c(0, 0);
    const campR = WORLD.baseCampRadius * sc;
    const campGrad = ctx.createRadialGradient(camp.x, camp.y, 0, camp.x, camp.y, campR * 2.4);
    campGrad.addColorStop(0, "rgba(70,100,48,0.95)");
    campGrad.addColorStop(1, "rgba(70,100,48,0)");
    ctx.fillStyle = campGrad;
    ctx.beginPath();
    ctx.arc(camp.x, camp.y, campR * 2.4, 0, Math.PI * 2);
    ctx.fill();

    // Lake — blue ellipse
    const lk = w2c(this.lake.x, this.lake.z);
    const lrx = this.lake.rx * sc;
    const lrz = this.lake.rz * sc;
    ctx.save();
    ctx.translate(lk.x, lk.y);
    ctx.scale(lrx, lrz);
    ctx.beginPath();
    ctx.arc(0, 0, 1, 0, Math.PI * 2);
    ctx.fillStyle = "#2a5a7a";
    ctx.fill();
    ctx.restore();

    // Creek — thin blue-grey polyline
    if (this.creekPoints.length > 0) {
      ctx.beginPath();
      this.creekPoints.forEach((p, i) => {
        const cp = w2c(p.x, p.z);
        if (i === 0) ctx.moveTo(cp.x, cp.y);
        else ctx.lineTo(cp.x, cp.y);
      });
      ctx.strokeStyle = "#4a8a9a";
      ctx.lineWidth = 3;
      ctx.stroke();
    }

    // Fallen logs — short dark-brown segments
    ctx.strokeStyle = "#6a4a2c";
    ctx.lineWidth = 2;
    for (const log of this.fallenLogs) {
      const c = w2c(log.cx, log.cz);
      const hw = log.halfLen * sc;
      ctx.beginPath();
      ctx.moveTo(c.x - log.ax * hw, c.y - log.az * hw);
      ctx.lineTo(c.x + log.ax * hw, c.y + log.az * hw);
      ctx.stroke();
    }

    return canvas;
  }

  /**
   * Winding stream that runs SW→NE, passing south of the camp.
   * Built as a ribbon along a CatmullRom spline, each vertex dropped onto the terrain.
   */
  private buildCreek() {
    const waypoints = [
      new THREE.Vector3(-380, 0, -200),
      new THREE.Vector3(-220, 0, -140),
      new THREE.Vector3(-80, 0, -55),
      new THREE.Vector3(60, 0, -35),
      new THREE.Vector3(180, 0, 40),
      new THREE.Vector3(320, 0, 20),
      new THREE.Vector3(390, 0, -80),
    ];
    // +0.10 keeps the ribbon above the terrain mesh so it's always visible.
    waypoints.forEach((p) => { p.y = this.getHeight(p.x, p.z) + 0.10; });

    const curve = new THREE.CatmullRomCurve3(waypoints);
    const N = 140;
    const halfW = 4.0;
    const positions: number[] = [];
    const normals: number[] = [];
    const indices: number[] = [];

    for (let i = 0; i <= N; i++) {
      const t = i / N;
      const pos = curve.getPoint(t);
      this.creekPoints.push(pos.clone()); // centre-line sample for proximity audio
      const tan = curve.getTangent(t);
      // Perpendicular in the XZ plane — keeps the ribbon flat on the ground.
      const right = new THREE.Vector3(tan.z, 0, -tan.x).normalize();

      for (const side of [-1, 1]) {
        const v = pos.clone().addScaledVector(right, side * halfW);
        v.y = this.getHeight(v.x, v.z) + 0.10;
        positions.push(v.x, v.y, v.z);
        normals.push(0, 1, 0);
      }
      if (i < N) {
        const base = i * 2;
        indices.push(base, base + 2, base + 1, base + 1, base + 2, base + 3);
      }
    }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(new Float32Array(positions), 3));
    geo.setAttribute("normal", new THREE.BufferAttribute(new Float32Array(normals), 3));
    geo.setIndex(indices);

    const mat = new THREE.MeshStandardMaterial({
      color: 0x1a7aaa,
      emissive: 0x0a4a7a,
      emissiveIntensity: 0.9,
      roughness: 0.0,
      metalness: 0.7,
    });
    this.scene.add(new THREE.Mesh(geo, mat));
  }

  /** Simple wooden sign post at the south edge of the camp clearing — trailhead marker. */
  private buildTrailhead() {
    const x = -2;
    const z = -(WORLD.baseCampRadius + 5);
    const y = this.getHeight(x, z);
    const g = new THREE.Group();
    const wood = new THREE.MeshStandardMaterial({ color: 0x7a5a3a, roughness: 1 });
    const dark = new THREE.MeshStandardMaterial({ color: 0x4a3020, roughness: 1 });

    const post = new THREE.Mesh(new THREE.CylinderGeometry(0.1, 0.14, 3.5, 6), wood);
    post.position.y = 1.75;

    const bar = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 2.4, 5), dark);
    bar.rotation.z = Math.PI / 2;
    bar.position.y = 3.2;

    const plaque = new THREE.Mesh(new THREE.BoxGeometry(2.0, 0.72, 0.1), wood);
    plaque.position.set(0, 3.2, 0.08);

    // Lantern: warm point light so it's a visible beacon at night.
    const lamp = new THREE.PointLight(0xffa040, 8, 14, 2);
    lamp.position.set(0, 3.6, 0.3);

    g.add(post, bar, plaque, lamp);
    g.position.set(x, y, z);
    g.rotation.y = Math.PI; // faces into the forest
    this.scene.add(g);
  }

  /** Tall wooden fire-lookout tower — visible nav landmark in the SE quadrant. */
  private buildLookoutTower(x: number, z: number) {
    const y = this.getHeight(x, z);
    const g = new THREE.Group();
    const wood = new THREE.MeshStandardMaterial({ color: 0x6a4a2c, roughness: 1 });
    const plank = new THREE.MeshStandardMaterial({ color: 0x7a5a3a, roughness: 0.9 });

    // Four corner posts (10 m tall).
    const postGeo = new THREE.CylinderGeometry(0.18, 0.22, 10, 5);
    for (const [px, pz] of [[-1.8, -1.8], [1.8, -1.8], [-1.8, 1.8], [1.8, 1.8]] as const) {
      const p = new THREE.Mesh(postGeo, wood);
      p.position.set(px, 5, pz);
      g.add(p);
    }

    // Horizontal braces at 3 m and 7 m.
    for (const h of [3, 7]) {
      const hx = new THREE.Mesh(new THREE.BoxGeometry(4.0, 0.14, 0.14), wood);
      hx.position.set(0, h, -1.8);
      const hx2 = hx.clone();
      hx2.position.set(0, h, 1.8);
      const hz = new THREE.Mesh(new THREE.BoxGeometry(0.14, 0.14, 4.0), wood);
      hz.position.set(-1.8, h, 0);
      const hz2 = hz.clone();
      hz2.position.set(1.8, h, 0);
      g.add(hx, hx2, hz, hz2);
    }

    // Platform floor.
    const floor = new THREE.Mesh(new THREE.BoxGeometry(4.2, 0.22, 4.2), plank);
    floor.position.y = 10.1;
    g.add(floor);

    // Railings along all four sides.
    const railMat = wood;
    for (const [rx, rz, rw, rd] of [
      [0, -2.0, 4.2, 0.14],
      [0, 2.0, 4.2, 0.14],
      [-2.0, 0, 0.14, 4.2],
      [2.0, 0, 0.14, 4.2],
    ] as const) {
      const rail = new THREE.Mesh(new THREE.BoxGeometry(rw, 0.8, rd), railMat);
      rail.position.set(rx, 10.6, rz);
      g.add(rail);
    }

    // Lantern at the top for night visibility.
    const lamp = new THREE.PointLight(0xffb060, 10, 30, 2);
    lamp.position.set(0, 11.2, 0);
    g.add(lamp);

    g.position.set(x, y, z);
    this.scene.add(g);
    // Tower collider is produced by the shared buildColliders().
  }

  /** Still lake fed by the creek — ellipse water plane with a faint blue-green glow. */
  private buildLake() {
    const { x, z, rx, rz } = this.lake;
    const y = this.getHeight(x, z) - 0.25; // sit slightly below surrounding terrain

    // Ellipse: start with a circle, scale X and Z to get the ellipse shape.
    const geo = new THREE.CircleGeometry(1, 40);
    geo.rotateX(-Math.PI / 2);
    const pos = geo.attributes.position as THREE.BufferAttribute;
    for (let i = 0; i < pos.count; i++) {
      pos.setX(i, pos.getX(i) * rx);
      pos.setZ(i, pos.getZ(i) * rz);
    }
    pos.needsUpdate = true;
    geo.computeVertexNormals();

    const mat = new THREE.MeshStandardMaterial({
      color: 0x2a5a6a,
      emissive: 0x0a2a3a,
      emissiveIntensity: 0.5,
      roughness: 0.05,
      metalness: 0.6,
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set(x, y, z);
    this.scene.add(mesh);

    // Subtle ambient light over the surface — visible reflection at night.
    const glow = new THREE.PointLight(0x2a6a8a, 6, 70, 2);
    glow.position.set(x, y + 2, z);
    this.scene.add(glow);
  }

  /** Scatter fallen logs as asymmetric obstacles across the map (table shared with the sim). */
  private buildFallenTrees() {
    for (const [cx, cz, angle, len] of LOG_TABLE) this.buildFallenTree(cx, cz, angle, len);
  }

  private buildFallenTree(cx: number, cz: number, angle: number, len: number) {
    const y = this.getHeight(cx, cz);
    const trunkR = 0.38;

    // CylinderGeometry stands upright along Y; rotateZ(PI/2) lays it along X;
    // mesh.rotation.y = angle then points it in the desired direction.
    const geo = new THREE.CylinderGeometry(trunkR * 0.85, trunkR, len, 7);
    geo.rotateZ(Math.PI / 2);
    const mat = new THREE.MeshStandardMaterial({ color: 0x5a4030, roughness: 1 });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.set(cx, y + trunkR * 0.7, cz);
    mesh.rotation.y = angle;
    this.scene.add(mesh);
    // Collision data for this log lives in the shared FALLEN_LOGS table.
  }

  /**
   * Scatter low-poly bush clusters across the map (two green variants, InstancedMesh).
   * Avoided near camp, caves, and the lake.
   */
  private buildBushes() {
    const rand = mulberry32(WORLD.seed ^ 0xf1e2d3c4);
    const N = 300;
    const geo = new THREE.IcosahedronGeometry(1, 0); // faceted — deliberately rough
    geo.computeVertexNormals();
    const matDark = new THREE.MeshStandardMaterial({ color: 0x3a6028, roughness: 1 });
    const matLight = new THREE.MeshStandardMaterial({ color: 0x4a7835, roughness: 1 });
    const meshDark = new THREE.InstancedMesh(geo, matDark, Math.ceil(N * 0.6));
    const meshLight = new THREE.InstancedMesh(geo, matLight, Math.floor(N * 0.4));

    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const half = WORLD.size / 2 - 10;
    let dIdx = 0;
    let lIdx = 0;

    for (let attempt = 0; dIdx + lIdx < N && attempt < N * 6; attempt++) {
      const x = (rand() * 2 - 1) * half;
      const z = (rand() * 2 - 1) * half;
      if (Math.sqrt(x * x + z * z) < WORLD.baseCampRadius + 6) continue;
      if (this.nearCave(x, z, 9)) continue;
      if (this.lakeDepth(x, z) > 0.08) continue;

      const y = this.getHeight(x, z);
      const sx = 0.4 + rand() * 0.75;
      const sy = sx * (0.55 + rand() * 0.5);
      const sz = 0.4 + rand() * 0.75;
      q.setFromAxisAngle(up, rand() * Math.PI * 2);
      m.compose(new THREE.Vector3(x, y + sy * 0.55, z), q, new THREE.Vector3(sx, sy, sz));

      const useDark = rand() < 0.6;
      if (useDark && dIdx < meshDark.count) {
        meshDark.setMatrixAt(dIdx++, m);
      } else if (lIdx < meshLight.count) {
        meshLight.setMatrixAt(lIdx++, m);
      } else if (dIdx < meshDark.count) {
        meshDark.setMatrixAt(dIdx++, m);
      }
    }

    meshDark.count = dIdx;
    meshLight.count = lIdx;
    meshDark.instanceMatrix.needsUpdate = true;
    meshLight.instanceMatrix.needsUpdate = true;
    this.scene.add(meshDark, meshLight);
  }

  /** Scatter stones along the lake shore and creek crossing points. */
  private buildRocks() {
    const rand = mulberry32(WORLD.seed ^ 0xa2b3c4d5);
    const N = 90;
    const geo = new THREE.IcosahedronGeometry(1, 0);
    geo.computeVertexNormals();
    const mat = new THREE.MeshStandardMaterial({ color: 0x585860, roughness: 1 });
    const mesh = new THREE.InstancedMesh(geo, mat, N);

    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    let placed = 0;

    // Lake shore — ring of stones just outside the water ellipse.
    for (let attempt = 0; placed < Math.floor(N * 0.55) && attempt < 200; attempt++) {
      const angle = rand() * Math.PI * 2;
      const rr = 0.88 + rand() * 0.22;
      const sx = this.lake.x + Math.cos(angle) * this.lake.rx * rr;
      const sz = this.lake.z + Math.sin(angle) * this.lake.rz * rr;
      if (this.lakeDepth(sx, sz) > 0.15) continue;
      const y = this.getHeight(sx, sz);
      const s = 0.25 + rand() * 0.75;
      q.setFromAxisAngle(up, rand() * Math.PI * 2);
      m.compose(new THREE.Vector3(sx, y + s * 0.25, sz), q, new THREE.Vector3(s, s * 0.55, s));
      mesh.setMatrixAt(placed++, m);
    }

    // Creek crossings — clusters of small stones near the water.
    for (const [cx, cz] of [[-80, -55], [60, -35], [180, 40]] as const) {
      for (let i = 0; i < 8 && placed < N; i++) {
        const rx = cx + (rand() - 0.5) * 14;
        const rz = cz + (rand() - 0.5) * 10;
        const y = this.getHeight(rx, rz);
        const s = 0.15 + rand() * 0.55;
        q.setFromAxisAngle(up, rand() * Math.PI * 2);
        m.compose(new THREE.Vector3(rx, y + s * 0.2, rz), q, new THREE.Vector3(s, s * 0.6, s));
        mesh.setMatrixAt(placed++, m);
      }
    }

    mesh.count = placed;
    mesh.instanceMatrix.needsUpdate = true;
    this.scene.add(mesh);
  }

  /** Per-frame ambience (campfire flicker). */
  update(t: number) {
    if (this.campfire) {
      this.campfire.intensity = 60 + Math.sin(t * 13) * 8 + Math.sin(t * 23.7) * 5;
    }
  }

  /**
   * Trim the forward-render light budget: keep only the `maxOn` cave glows nearest (x,z) lit; an
   * invisible light is skipped by the renderer entirely. You're never near more than one cave, and
   * the glow's range is 12 m, so far cave lights only cost fill-rate for nothing.
   */
  updateLightBudget(x: number, z: number, maxOn: number) {
    if (this.caveLights.length <= maxOn) return;
    const byDist = this.caveLights
      .map((l) => ({ l, d: (l.position.x - x) ** 2 + (l.position.z - z) ** 2 }))
      .sort((a, b) => a.d - b.d);
    byDist.forEach((e, i) => { e.l.visible = i < maxOn; });
  }

  /** Number of cave glow lights currently lit (for the perf readout). */
  get litCaveLights(): number {
    return this.caveLights.reduce((n, l) => n + (l.visible ? 1 : 0), 0);
  }
}
