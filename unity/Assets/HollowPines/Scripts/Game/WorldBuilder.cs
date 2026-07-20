// Builds the whole forest, in code, from the deterministic shared sim (HollowPines.Sim) — the
// Unity equivalent of the web client's Environment.ts. Every client renders the identical world
// because everything derives from World.Seed; there are no scene-baked positions and no assets.
//
// "Gameplay functional" pass: primitive low-poly meshes (smooth normals), fog + a moon light +
// warm prop lights, and a day-night palette driven by the replicated timeOfDay. Pretty comes in R5.
using System.Collections.Generic;
using HollowPines.Sim;
using UnityEngine;

namespace HollowPines.Game
{
    public class WorldBuilder : MonoBehaviour
    {
        /// <summary>The shared deterministic world — colliders/terrain the players also step against.</summary>
        public static GameWorld World { get; private set; }

        public static WorldBuilder Instance { get; private set; }

        private Light _moon;
        private float _lastTod = -1f;

        /// <summary>
        /// Local fog-density multiplier. Bigfoot's trade-off (web Phase 3): brighter near vision
        /// (PostFX exposure) but a murkier distance — set to ~1.35 for the local Bigfoot.
        /// </summary>
        public static float FogMul = 1f;

        /// <summary>Force the next SetTimeOfDay to re-apply (after FogMul changes).</summary>
        public void InvalidatePalette() { _lastTod = -1f; }

        /// <summary>
        /// Title-screen mode: the menu backdrop is a showpiece, not a horror beat, so it gets lit
        /// well above gameplay dusk — brighter ambient and moon, thinner fog so the treeline reads.
        /// Applied inside SetTimeOfDay so it composes with the day-night palette instead of fighting
        /// GameManager, which drives the clock every frame regardless of connection state.
        /// </summary>
        public static bool TitleMode;
        private const float TitleAmbientBoost = 2.6f;
        private const float TitleMoonBoost = 3.2f;
        private const float TitleFogMul = 0.35f;
        private const float TitleSkyBoost = 1.7f;

        // Palette (hex values lifted from the web build's Environment.ts).
        private static readonly Color TrunkCol = MeshUtil.Rgb(0x4a3828);
        private static readonly Color CrownDark = MeshUtil.Rgb(0x3a6028);
        private static readonly Color CrownLight = MeshUtil.Rgb(0x4a7835);
        private static readonly Color RockCol = MeshUtil.Rgb(0x585860);
        private static readonly Color LogCol = MeshUtil.Rgb(0x5a4030);
        private static readonly Color GroundCol = MeshUtil.Rgb(0x2e4023);
        private static readonly Color LakeCol = MeshUtil.Rgb(0x2a5a6a);

        /// <summary>
        /// The evidence duffel beside the RV — the only place proof becomes permanent, and the one
        /// thing in the forest Bigfoot cannot touch. Derived from the RV's seeded transform so the
        /// renderer and the host agree without a second copy of the coordinates.
        /// </summary>
        public static Vector3 DuffelPosition()
        {
            EnsureWorld();
            double ry = WorldData.Rv.Ry;
            // 3.2 m off the RV's side, toward the campfire end.
            double ox = System.Math.Cos(ry) * 3.2 + System.Math.Sin(ry) * 1.4;
            double oz = -System.Math.Sin(ry) * 3.2 + System.Math.Cos(ry) * 1.4;
            double x = WorldData.Rv.X + ox, z = WorldData.Rv.Z + oz;
            return new Vector3((float)x, (float)World.GetHeight(x, z), (float)z);
        }

        public static GameWorld EnsureWorld()
        {
            if (World == null) World = GameWorld.MakeWorld(Sim.World.Seed);
            return World;
        }

        private void Awake()
        {
            Instance = this;
            EnsureWorld();
            BuildTerrain();
            BuildForest();
            BuildLogs();
            BuildLake();
            BuildRv();
            BuildDuffel();
            BuildCaves();
            BuildTower();
            BuildCamp();
            BuildLighting();
            PostFX.Ensure(gameObject);
            HPAudio.Ensure(gameObject); // synthesizes every cue + starts the wind/creek beds
            SetTimeOfDay(0f);
        }

        // --- Terrain -----------------------------------------------------------

        private void BuildTerrain()
        {
            int segs = 120; // render resolution (collision samples the analytic height, not this mesh)
            float size = (float)Sim.World.Size;
            float half = size / 2f;
            var verts = new Vector3[(segs + 1) * (segs + 1)];
            for (int zi = 0; zi <= segs; zi++)
            {
                for (int xi = 0; xi <= segs; xi++)
                {
                    float x = -half + size * xi / segs;
                    float z = -half + size * zi / segs;
                    verts[zi * (segs + 1) + xi] = new Vector3(x, (float)World.GetHeight(x, z), z);
                }
            }
            var tris = new int[segs * segs * 6];
            int t = 0;
            for (int zi = 0; zi < segs; zi++)
            {
                for (int xi = 0; xi < segs; xi++)
                {
                    int i0 = zi * (segs + 1) + xi;
                    int i1 = i0 + 1;
                    int i2 = i0 + segs + 1;
                    int i3 = i2 + 1;
                    tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                    tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
                }
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            NewMeshGo("Terrain", mesh, MeshUtil.Lit(GroundCol));
        }

        // --- Forest --------------------------------------------------------------

        private void BuildForest()
        {
            // MUST mirror WorldData.BuildColliders' rand() call order exactly so the rendered trees
            // sit precisely on their colliders (same skips, same draws, same seed).
            var rand = Rng.Mulberry32(Sim.World.Seed ^ 0x9e3779b9u);
            double half = Sim.World.Size / 2 - 6;

            Mesh trunk = MeshUtil.TaperedCylinder(0.4f, 0.22f, 3f, 7);
            Mesh cone1 = MeshUtil.Cone(2.0f, 3.2f, 8);
            Mesh cone2 = MeshUtil.Cone(1.5f, 2.6f, 8);
            Mesh cone3 = MeshUtil.Cone(1.0f, 2.0f, 8);

            var trunkC = new List<CombineInstance>();
            var crownDarkC = new List<CombineInstance>();
            var crownLightC = new List<CombineInstance>();
            int treeIndex = 0;

            for (int i = 0; i < Sim.World.TreeCount; i++)
            {
                double x = (rand() * 2 - 1) * half;
                double z = (rand() * 2 - 1) * half;
                if (System.Math.Sqrt(x * x + z * z) < Sim.World.BaseCampRadius + 4) continue;
                if (NearCave(x, z, 7)) continue;
                double s = 0.7 + rand() * 0.9;
                double rot = rand() * System.Math.PI * 2; // same draw the collider builder discards

                float y = (float)World.GetHeight(x, z);
                var pos = new Vector3((float)x, y, (float)z);
                var rotQ = Quaternion.Euler(0f, (float)(rot * Mathf.Rad2Deg), 0f);
                var scale = Vector3.one * (float)s;

                trunkC.Add(CI(trunk, pos, rotQ, scale));
                var crowns = (treeIndex % 2 == 0) ? crownDarkC : crownLightC;
                crowns.Add(CI(cone1, pos + Vector3.up * (2.2f * (float)s), rotQ, scale));
                crowns.Add(CI(cone2, pos + Vector3.up * (4.0f * (float)s), rotQ, scale));
                crowns.Add(CI(cone3, pos + Vector3.up * (5.6f * (float)s), rotQ, scale));
                treeIndex++;
            }

            NewCombinedGo("Trunks", trunkC, MeshUtil.Lit(TrunkCol));
            NewCombinedGo("CrownsDark", crownDarkC, MeshUtil.Lit(CrownDark));
            NewCombinedGo("CrownsLight", crownLightC, MeshUtil.Lit(CrownLight));
        }

        private static bool NearCave(double x, double z, double r)
        {
            foreach (var c in World.Caves)
                if ((c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z) < r * r) return true;
            return false;
        }

        // --- Props ---------------------------------------------------------------

        private void BuildLogs()
        {
            var mat = MeshUtil.Lit(LogCol);
            foreach (var log in World.FallenLogs)
            {
                float len = (float)(log.HalfLen * 2);
                var mesh = MeshUtil.TaperedCylinder((float)log.R, (float)log.R * 0.85f, len, 7);
                var go = NewMeshGo("Log", mesh, mat);
                var axis = new Vector3((float)log.Ax, 0f, (float)log.Az);
                go.transform.SetPositionAndRotation(
                    new Vector3((float)log.Cx, (float)World.GetHeight(log.Cx, log.Cz) + (float)log.R * 0.7f, (float)log.Cz)
                        - axis * (len / 2f),
                    Quaternion.FromToRotation(Vector3.up, axis));
            }
        }

        /// <summary>
        /// The lake, as a sheet that FOLLOWS THE TERRAIN instead of a flat disc.
        ///
        /// Why: the lake is 120 m x 90 m and `HillHeight` is 14 m, so a flat plane at the centre's
        /// height floated metres above every lower fold of ground — the map looked flooded to the
        /// horizon. Terrain can't be carved to fit it either: `Terrain.MakeTerrain` is the
        /// parity-locked shared sim, and players stand on its analytic height, so a visual-only
        /// basin would leave them walking on invisible ground above the water.
        ///
        /// Conforming solves both: the water covers exactly the ellipse the sim slows you in
        /// (`Collision.LakeDepth`), never rises above the land, and reads as the shallow, wadeable
        /// water the movement rules already describe — you slow down in it, you never swim.
        /// </summary>
        private void BuildLake()
        {
            const int rings = 12, segs = 44;
            float rx = (float)WorldData.Lake.Rx, rz = (float)WorldData.Lake.Rz;
            float cx = (float)WorldData.Lake.X, cz = (float)WorldData.Lake.Z;

            var verts = new Vector3[1 + rings * segs];
            verts[0] = new Vector3(cx, SurfaceY(cx, cz, 0f), cz);
            for (int r = 1; r <= rings; r++)
            {
                float t = r / (float)rings;
                for (int s = 0; s < segs; s++)
                {
                    float a = s / (float)segs * Mathf.PI * 2f;
                    float x = cx + Mathf.Cos(a) * rx * t;
                    float z = cz + Mathf.Sin(a) * rz * t;
                    verts[1 + (r - 1) * segs + s] = new Vector3(x, SurfaceY(x, z, t), z);
                }
            }

            var tris = new System.Collections.Generic.List<int>((rings * segs) * 6);
            for (int s = 0; s < segs; s++) // centre fan
            {
                int a = 1 + s, b = 1 + (s + 1) % segs;
                tris.Add(0); tris.Add(b); tris.Add(a);
            }
            for (int r = 1; r < rings; r++) // quad bands
            {
                int inner = 1 + (r - 1) * segs, outer = 1 + r * segs;
                for (int s = 0; s < segs; s++)
                {
                    int s2 = (s + 1) % segs;
                    tris.Add(inner + s); tris.Add(inner + s2); tris.Add(outer + s);
                    tris.Add(outer + s); tris.Add(inner + s2); tris.Add(outer + s2);
                }
            }

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            NewMeshGo("Lake", mesh, MeshUtil.Emissive(LakeCol, MeshUtil.Rgb(0x0a2a3a), 0.6f));
        }

        /// <summary>Water height at a point: just over the ground, feathering to nothing at the rim.</summary>
        private static float SurfaceY(float x, float z, float t)
        {
            return (float)World.GetHeight(x, z) + Mathf.Lerp(0.16f, 0.02f, t);
        }

        private void BuildRv()
        {
            var root = new GameObject("RV");
            root.transform.SetPositionAndRotation(
                new Vector3((float)WorldData.Rv.X, (float)World.GetHeight(WorldData.Rv.X, WorldData.Rv.Z), (float)WorldData.Rv.Z),
                Quaternion.Euler(0f, (float)(WorldData.Rv.Ry * Mathf.Rad2Deg), 0f));
            root.transform.parent = transform;

            AddBox(root, "Body", new Vector3(0, 1.5f, 0), new Vector3(6.6f, 2.5f, 2.3f), MeshUtil.Rgb(0xd9d3c2));
            AddBox(root, "Stripe", new Vector3(0, 1.0f, 0), new Vector3(6.65f, 0.4f, 2.32f), MeshUtil.Rgb(0x7a8a6a));
            AddBox(root, "Window", new Vector3(1.6f, 1.9f, 0), new Vector3(1.6f, 0.7f, 2.34f), MeshUtil.Rgb(0xffd98a), emissive: MeshUtil.Rgb(0xffb24d), glow: 1.4f);
            var lamp = new GameObject("RvLamp").AddComponent<Light>();
            lamp.transform.parent = root.transform;
            lamp.transform.localPosition = new Vector3(0, 2.2f, 1.6f);
            lamp.type = LightType.Point;
            lamp.color = MeshUtil.Rgb(0xffb866);
            lamp.range = 16f;
            lamp.intensity = 2.2f;
        }

        /// <summary>
        /// A cave mouth: a rock mound built into the hillside with a dark opening facing map centre,
        /// framed by an overhang and flanking boulders, with rubble at the threshold. Read as an
        /// ENTRANCE — the earlier three-boulders-in-a-row version read as scenery, which mattered
        /// because Bigfoot's whole fast-travel network hangs off recognising these.
        /// </summary>
        /// <summary>
        /// The evidence duffel: a canvas holdall on a tarp beside the RV, lit by its own lamp so it
        /// reads as a destination from across the clearing. Purely a landmark — the deposit rule is
        /// server-side (GameManager.TryDeposit) and Bigfoot can do nothing to it.
        /// </summary>
        private void BuildDuffel()
        {
            Vector3 at = DuffelPosition();
            var root = new GameObject("EvidenceDuffel");
            root.transform.parent = transform;
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, (float)(WorldData.Rv.Ry * Mathf.Rad2Deg) + 20f, 0f));

            // Ground tarp, so the spot reads as "put things here".
            var tarp = new GameObject("Tarp");
            tarp.transform.SetParent(root.transform, false);
            tarp.transform.localPosition = new Vector3(0f, 0.03f, 0f);
            tarp.AddComponent<MeshFilter>().sharedMesh = MeshUtil.EllipseDisc(1.5f, 1.2f, 14);
            tarp.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0x2f3a30));

            // The bag: a rounded body with end caps and a strap.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<UnityEngine.Collider>());
            body.name = "Bag";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.34f, 0f);
            body.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            body.transform.localScale = new Vector3(0.62f, 0.72f, 0.62f);
            body.GetComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0x6b5a3a));

            var strap = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(strap.GetComponent<UnityEngine.Collider>());
            strap.name = "Strap";
            strap.transform.SetParent(root.transform, false);
            strap.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            strap.transform.localScale = new Vector3(1.5f, 0.09f, 0.22f);
            strap.GetComponent<MeshRenderer>().sharedMaterial = MeshUtil.Lit(MeshUtil.Rgb(0x3a3026));

            // A warm work lamp over it — the "safe place" beacon.
            var lamp = new GameObject("DuffelLamp").AddComponent<Light>();
            lamp.transform.parent = root.transform;
            lamp.transform.localPosition = new Vector3(0f, 2.0f, 0f);
            lamp.type = LightType.Point;
            lamp.color = MeshUtil.Rgb(0xffd9a0);
            lamp.range = 12f;
            lamp.intensity = 2.0f;

            var glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(glow.GetComponent<UnityEngine.Collider>());
            glow.name = "LampBulb";
            glow.transform.SetParent(root.transform, false);
            glow.transform.localPosition = new Vector3(0f, 2.0f, 0f);
            glow.transform.localScale = Vector3.one * 0.16f;
            glow.GetComponent<MeshRenderer>().sharedMaterial =
                MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffd9a0), 3f);
        }

        private void BuildCaves()
        {
            var rock = MeshUtil.Lit(RockCol);
            var darkRock = MeshUtil.Lit(MeshUtil.Rgb(0x3c3c44));
            // Near-black, unlit-looking interior so the opening reads as depth rather than a surface.
            var voidMat = MeshUtil.Lit(MeshUtil.Rgb(0x05060a));

            foreach (var cave in World.Caves)
            {
                double dl = System.Math.Sqrt(cave.X * cave.X + cave.Z * cave.Z);
                if (dl == 0) dl = 1;
                double dx = -cave.X / dl, dz = -cave.Z / dl; // toward map centre = the way the mouth faces
                double px = -dz, pz = dx;                    // sideways across the mouth
                float baseY = (float)World.GetHeight(cave.X, cave.Z);
                var centre = new Vector3((float)cave.X, baseY, (float)cave.Z);
                var faceRot = Quaternion.LookRotation(new Vector3((float)dx, 0f, (float)dz), Vector3.up);

                var root = new GameObject("Cave");
                root.transform.parent = transform;
                root.transform.SetPositionAndRotation(centre, faceRot);

                // The hillside the cave is cut into — a squashed dome sitting BEHIND the opening.
                var mound = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(mound.GetComponent<UnityEngine.Collider>());
                mound.name = "Mound";
                mound.transform.SetParent(root.transform, false);
                mound.transform.localPosition = new Vector3(0f, 1.1f, -3.4f);
                mound.transform.localScale = new Vector3(13f, 7.2f, 11f);
                mound.GetComponent<MeshRenderer>().sharedMaterial = rock;

                // The opening: a dark recess set into the mound's front face.
                var mouth = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Object.Destroy(mouth.GetComponent<UnityEngine.Collider>());
                mouth.name = "Mouth";
                mouth.transform.SetParent(root.transform, false);
                mouth.transform.localPosition = new Vector3(0f, 1.5f, 0.7f);
                mouth.transform.localScale = new Vector3(4.6f, 3.9f, 4.2f);
                mouth.GetComponent<MeshRenderer>().sharedMaterial = voidMat;

                // Overhanging brow above the opening — the strongest "this is an entrance" cue.
                var brow = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(brow.GetComponent<UnityEngine.Collider>());
                brow.name = "Brow";
                brow.transform.SetParent(root.transform, false);
                brow.transform.localPosition = new Vector3(0f, 3.5f, 1.5f);
                brow.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);
                brow.transform.localScale = new Vector3(7.2f, 0.9f, 3.0f);
                brow.GetComponent<MeshRenderer>().sharedMaterial = darkRock;

                // Flanking pillars framing the opening, and rubble spilling out of it.
                Boulder(rock, cave.X + px * 3.1 + dx * 0.6, cave.Z + pz * 3.1 + dz * 0.6, 1.9);
                Boulder(rock, cave.X - px * 3.1 + dx * 0.6, cave.Z - pz * 3.1 + dz * 0.6, 1.9);
                Boulder(darkRock, cave.X + dx * 3.6 + px * 1.5, cave.Z + dz * 3.6 + pz * 1.5, 0.7);
                Boulder(darkRock, cave.X + dx * 4.2 - px * 1.1, cave.Z + dz * 4.2 - pz * 1.1, 0.5);
                Boulder(darkRock, cave.X + dx * 2.9 - px * 2.0, cave.Z + dz * 2.9 - pz * 2.0, 0.6);

                // Cold light bleeding out of the throat, so it's findable at night.
                var glow = new GameObject("CaveGlow").AddComponent<Light>();
                glow.transform.parent = root.transform;
                glow.transform.localPosition = new Vector3(0f, 1.4f, 1.6f);
                glow.type = LightType.Point;
                glow.color = MeshUtil.Rgb(0x4a6ab0);
                glow.range = 14f;
                glow.intensity = 1.8f;
            }
        }

        private void Boulder(Material rock, double x, double z, double r)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Boulder";
            Object.Destroy(go.GetComponent<UnityEngine.Collider>()); // sim owns collision, not PhysX
            go.GetComponent<MeshRenderer>().sharedMaterial = rock;
            go.transform.parent = transform;
            float y = (float)World.GetHeight(x, z);
            go.transform.position = new Vector3((float)x, y + (float)r * 0.75f, (float)z);
            go.transform.localScale = new Vector3((float)r * 2.2f, (float)r * 1.7f, (float)r * 2.2f);
        }

        private void BuildTower()
        {
            var root = new GameObject("Lookout");
            float baseY = (float)World.GetHeight(WorldData.Lookout.X, WorldData.Lookout.Z);
            root.transform.position = new Vector3((float)WorldData.Lookout.X, baseY, (float)WorldData.Lookout.Z);
            root.transform.parent = transform;
            var wood = MeshUtil.Lit(MeshUtil.Rgb(0x6a4a2c));
            Mesh post = MeshUtil.TaperedCylinder(0.22f, 0.18f, 10f, 5);
            foreach (var off in new[] { new Vector2(-1.4f, -1.4f), new Vector2(1.4f, -1.4f), new Vector2(-1.4f, 1.4f), new Vector2(1.4f, 1.4f) })
            {
                var leg = NewMeshGo("Leg", post, wood);
                leg.transform.parent = root.transform;
                leg.transform.localPosition = new Vector3(off.x, 0f, off.y);
            }
            AddBox(root, "Platform", new Vector3(0, 9.8f, 0), new Vector3(3.6f, 0.35f, 3.6f), MeshUtil.Rgb(0x7a5a3a));
            var lamp = new GameObject("TowerLamp").AddComponent<Light>();
            lamp.transform.parent = root.transform;
            lamp.transform.localPosition = new Vector3(0, 10.6f, 0);
            lamp.type = LightType.Point;
            lamp.color = MeshUtil.Rgb(0xffb060);
            lamp.range = 30f;
            lamp.intensity = 1.6f;
        }

        private void BuildCamp()
        {
            var rock = MeshUtil.Lit(MeshUtil.Rgb(0x3a3a3a));
            for (int i = 0; i < 7; i++)
            {
                float a = i / 7f * Mathf.PI * 2f;
                Boulder(rock, Mathf.Cos(a) * 1.2f, Mathf.Sin(a) * 1.2f, 0.22);
            }
            var ember = NewMeshGo("Embers", MeshUtil.Cone(0.6f, 1.1f, 8),
                MeshUtil.Emissive(MeshUtil.Rgb(0xff7a2a), MeshUtil.Rgb(0xff5a1e), 2f));
            ember.transform.position = new Vector3(0, (float)World.GetHeight(0, 0), 0);
            var fire = new GameObject("Campfire").AddComponent<Light>();
            fire.transform.parent = transform;
            fire.transform.position = new Vector3(0, (float)World.GetHeight(0, 0) + 1.2f, 0);
            fire.type = LightType.Point;
            fire.color = MeshUtil.Rgb(0xff7a3a);
            fire.range = 40f;
            fire.intensity = 3.5f;
        }

        private void BuildLighting()
        {
            var moonGo = new GameObject("Moon");
            moonGo.transform.parent = transform;
            moonGo.transform.rotation = Quaternion.Euler(52f, -28f, 0f);
            _moon = moonGo.AddComponent<Light>();
            _moon.type = LightType.Directional;
            _moon.color = MeshUtil.Rgb(0x9fb6ff);
            _moon.intensity = 0.35f;
            // Hard shadows: soft shadows cost real time on integrated GPUs and the forest is so
            // fogged and dark that the difference barely reads. Revisit in the R5 art pass.
            _moon.shadows = LightShadows.Hard;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        }

        // --- Day-night ------------------------------------------------------------

        [System.Serializable]
        private struct SkyKey
        {
            public float T;
            public Color Sky;
            public Color Fog;
            public Color Ambient;
            public float FogDensity;
            public float Moon;
            public SkyKey(float t, int sky, int fog, int amb, float dens, float moon)
            { T = t; Sky = MeshUtil.Rgb(sky); Fog = MeshUtil.Rgb(fog); Ambient = MeshUtil.Rgb(amb); FogDensity = dens; Moon = moon; }
        }

        private static readonly SkyKey[] SkyKeys =
        {
            new SkyKey(0.00f, 0x3a3550, 0x453a4a, 0x40384a, 0.0075f, 0.30f), // dusk
            new SkyKey(0.25f, 0x141a2e, 0x18202e, 0x1c2434, 0.0100f, 0.38f), // nightfall
            new SkyKey(0.60f, 0x0a0e1c, 0x0c1220, 0x121a28, 0.0125f, 0.42f), // deep night
            new SkyKey(0.88f, 0x141a2e, 0x1a2030, 0x1c2434, 0.0105f, 0.36f), // pre-dawn
            new SkyKey(1.00f, 0x4a4258, 0x584a52, 0x4a4456, 0.0080f, 0.26f), // dawn
        };

        /// <summary>Blend the sky/fog/light palette for a 0..1 night progress. Called by GameManager.</summary>
        public void SetTimeOfDay(float t)
        {
            if (Mathf.Abs(t - _lastTod) < 0.0005f) return;
            _lastTod = t;
            SkyKey a = SkyKeys[0], b = SkyKeys[SkyKeys.Length - 1];
            for (int i = 0; i < SkyKeys.Length - 1; i++)
            {
                if (t >= SkyKeys[i].T && t <= SkyKeys[i + 1].T) { a = SkyKeys[i]; b = SkyKeys[i + 1]; break; }
            }
            float k = Mathf.InverseLerp(a.T, b.T, t);
            Color sky = Color.Lerp(a.Sky, b.Sky, k);
            Color fog = Color.Lerp(a.Fog, b.Fog, k);
            Color ambient = Color.Lerp(a.Ambient, b.Ambient, k);
            float fogDensity = Mathf.Lerp(a.FogDensity, b.FogDensity, k) * FogMul;
            float moon = Mathf.Lerp(a.Moon, b.Moon, k);

            if (TitleMode)
            {
                sky *= TitleSkyBoost;
                fog *= TitleSkyBoost;
                ambient *= TitleAmbientBoost;
                fogDensity *= TitleFogMul;
                moon *= TitleMoonBoost;
            }

            RenderSettings.fogColor = fog;
            RenderSettings.fogDensity = fogDensity;
            RenderSettings.ambientLight = ambient;
            if (_moon != null) _moon.intensity = moon;
            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = sky;
            }
        }

        // --- helpers ----------------------------------------------------------------

        private static CombineInstance CI(Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            return new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(pos, rot, scale) };
        }

        private GameObject NewCombinedGo(string name, List<CombineInstance> combines, Material mat)
        {
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(combines.ToArray(), true, true);
            mesh.RecalculateBounds();
            return NewMeshGo(name, mesh, mat);
        }

        private GameObject NewMeshGo(string name, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.parent = transform;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        private void AddBox(GameObject parent, string name, Vector3 localPos, Vector3 size, Color color, Color? emissive = null, float glow = 1f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.Destroy(go.GetComponent<UnityEngine.Collider>());
            go.transform.parent = parent.transform;
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = size;
            go.GetComponent<MeshRenderer>().sharedMaterial =
                emissive.HasValue ? MeshUtil.Emissive(color, emissive.Value, glow) : MeshUtil.Lit(color);
        }
    }
}
