import * as THREE from "three";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import { WORLD, DUSK } from "../config";
import { mulberry32, makeValueNoise } from "../util/rng";

type Collider = { x: number; z: number; r: number };

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

const c = (hex: string) => new THREE.Color(hex);

/** dusk(0) -> nightfall -> deep night -> witching -> dawn(1) */
const SKY_STOPS: SkyStop[] = [
  { t: 0.0, top: c("#2a2740"), bottom: c("#c87b53"), fog: c("#5a4a55"), fogD: 0.012, hemi: 0.6, sun: 1.6, sunCol: c("#ff9d5c") },
  { t: 0.16, top: c("#171a30"), bottom: c("#3a3550"), fog: c("#2a2b3d"), fogD: 0.018, hemi: 0.32, sun: 0.5, sunCol: c("#6a6cff") },
  { t: 0.5, top: c("#070912"), bottom: c("#0d1430"), fog: c("#0a0f1c"), fogD: 0.03, hemi: 0.16, sun: 0.18, sunCol: c("#5566cc") },
  { t: 0.85, top: c("#0a0a16"), bottom: c("#10182e"), fog: c("#0a0e1a"), fogD: 0.032, hemi: 0.14, sun: 0.16, sunCol: c("#5566cc") },
  { t: 1.0, top: c("#243a52"), bottom: c("#9fd0c9"), fog: c("#7fa6a0"), fogD: 0.014, hemi: 0.55, sun: 1.2, sunCol: c("#bfe0d0") },
];

/**
 * Builds the stylized low-poly forest: smooth-shaded terrain, instanced conifers,
 * a gradient sky that runs from dusk to dawn, fog, and a base-camp campfire.
 */
export class Environment {
  readonly scene: THREE.Scene;
  readonly treeColliders: Collider[] = [];

  private noise = makeValueNoise(WORLD.seed);
  private campfire?: THREE.PointLight;
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
  }

  /** Terrain height at world (x,z). Players/props sample this to sit on the ground. */
  getHeight(x: number, z: number): number {
    const nx = x * 0.0065;
    const nz = z * 0.0065;
    let h = 0;
    let amp = 1;
    let freq = 1;
    let norm = 0;
    for (let o = 0; o < 4; o++) {
      h += this.noise(nx * freq, nz * freq) * amp;
      norm += amp;
      amp *= 0.5;
      freq *= 2;
    }
    h = (h / norm) * WORLD.hillHeight;
    const d = Math.sqrt(x * x + z * z);
    const flat = THREE.MathUtils.smoothstep(d, WORLD.baseCampRadius, WORLD.baseCampRadius + 12);
    return h * flat;
  }

  /** Push an (x,z) point out of any tree trunk it overlaps. Returns the resolved point. */
  resolveCollision(x: number, z: number, radius: number): { x: number; z: number } {
    let nx = x;
    let nz = z;
    for (const t of this.treeColliders) {
      const min = t.r + radius;
      const dx = nx - t.x;
      const dz = nz - t.z;
      const d2 = dx * dx + dz * dz;
      if (d2 < min * min && d2 > 1e-6) {
        const d = Math.sqrt(d2);
        const push = min - d;
        nx += (dx / d) * push;
        nz += (dz / d) * push;
      }
    }
    return { x: nx, z: nz };
  }

  /** True if a tree trunk blocks the straight (XZ) line between two points. */
  lineBlockedByTrees(a: THREE.Vector3, b: THREE.Vector3): boolean {
    const dx = b.x - a.x;
    const dz = b.z - a.z;
    const len2 = dx * dx + dz * dz || 1e-6;
    for (const t of this.treeColliders) {
      let s = ((t.x - a.x) * dx + (t.z - a.z) * dz) / len2;
      s = Math.max(0, Math.min(1, s));
      const cx = a.x + dx * s;
      const cz = a.z + dz * s;
      const ddx = t.x - cx;
      const ddz = t.z - cz;
      if (ddx * ddx + ddz * ddz < t.r * t.r) return true;
    }
    return false;
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
  }

  private buildTerrain() {
    const geo = new THREE.PlaneGeometry(WORLD.size, WORLD.size, WORLD.segments, WORLD.segments);
    geo.rotateX(-Math.PI / 2);
    const pos = geo.attributes.position as THREE.BufferAttribute;
    for (let i = 0; i < pos.count; i++) {
      pos.setY(i, this.getHeight(pos.getX(i), pos.getZ(i)));
    }
    geo.computeVertexNormals(); // smooth normals => rounded, not blocky
    const mat = new THREE.MeshStandardMaterial({ color: DUSK.ground, roughness: 1, metalness: 0 });
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
    const trunkMat = new THREE.MeshStandardMaterial({ color: DUSK.trunk, roughness: 1 });
    const folMat = new THREE.MeshStandardMaterial({ color: DUSK.foliage, roughness: 1 });
    const n = WORLD.treeCount;
    const trunks = new THREE.InstancedMesh(trunk, trunkMat, n);
    const crowns = new THREE.InstancedMesh(foliage, folMat, n);

    const rand = mulberry32(WORLD.seed ^ 0x9e3779b9);
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const half = WORLD.size / 2 - 6;
    let placed = 0;
    for (let i = 0; i < n; i++) {
      const x = (rand() * 2 - 1) * half;
      const z = (rand() * 2 - 1) * half;
      if (Math.sqrt(x * x + z * z) < WORLD.baseCampRadius + 4) continue; // keep clearing open
      const s = 0.7 + rand() * 0.9;
      q.setFromAxisAngle(up, rand() * Math.PI * 2);
      m.compose(new THREE.Vector3(x, this.getHeight(x, z), z), q, new THREE.Vector3(s, s, s));
      trunks.setMatrixAt(placed, m);
      crowns.setMatrixAt(placed, m);
      this.treeColliders.push({ x, z, r: 0.45 * s });
      placed++;
    }
    trunks.count = placed;
    crowns.count = placed;
    trunks.instanceMatrix.needsUpdate = true;
    crowns.instanceMatrix.needsUpdate = true;
    this.scene.add(trunks, crowns);
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

  /** Per-frame ambience (campfire flicker). */
  update(t: number) {
    if (this.campfire) {
      this.campfire.intensity = 60 + Math.sin(t * 13) * 8 + Math.sin(t * 23.7) * 5;
    }
  }
}
