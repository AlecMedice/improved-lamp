import * as THREE from "three";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import { WORLD, DUSK } from "../config";
import { mulberry32, makeValueNoise } from "../util/rng";

/**
 * Builds the stylized low-poly forest: smooth-shaded terrain, instanced conifers,
 * a gradient dusk sky, fog, and a warm base-camp campfire.
 */
export class Environment {
  readonly scene: THREE.Scene;
  private noise = makeValueNoise(WORLD.seed);
  private campfire?: THREE.PointLight;

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
    // Flatten the base-camp clearing around the origin.
    const d = Math.sqrt(x * x + z * z);
    const flat = THREE.MathUtils.smoothstep(d, WORLD.baseCampRadius, WORLD.baseCampRadius + 12);
    return h * flat;
  }

  private buildSky() {
    const geo = new THREE.SphereGeometry(WORLD.size, 32, 16);
    const mat = new THREE.ShaderMaterial({
      side: THREE.BackSide,
      depthWrite: false,
      uniforms: { top: { value: DUSK.skyTop }, bottom: { value: DUSK.skyBottom } },
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
    this.scene.add(new THREE.Mesh(geo, mat));
    this.scene.fog = new THREE.FogExp2(DUSK.fog.getHex(), 0.012);
  }

  private buildLights() {
    const hemi = new THREE.HemisphereLight(DUSK.ambientSky.getHex(), DUSK.ambientGround.getHex(), 0.6);
    this.scene.add(hemi);
    const sun = new THREE.DirectionalLight(DUSK.sun.getHex(), 1.6);
    sun.position.set(-80, 28, -60); // low, setting sun
    this.scene.add(sun);
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
