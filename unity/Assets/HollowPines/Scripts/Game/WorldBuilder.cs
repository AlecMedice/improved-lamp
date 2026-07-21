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
        private float _appliedTod;  // last time-of-day handed to SetTimeOfDay (survives a reseed)
        private int _appliedNight = 1;
        private int _lastNight = -1;

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
        private static readonly Color FernCol = MeshUtil.Rgb(0x35521f);  // undergrowth, a shade under the crowns
        private static readonly Color BushCol = MeshUtil.Rgb(0x2b4a26);
        private static readonly Color TrailCol = MeshUtil.Rgb(0x51452f); // packed dirt, warmer than the ground

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

        /// <summary>
        /// Swap the whole forest to a different seed and rebuild the geometry.
        ///
        /// The host rolls a seed per hosting session and replicates it (GameManager.WorldSeed), so no
        /// two sessions share a forest — the caves in particular have to move, or a group that plays
        /// twice already knows every lair. Everything here derives from the seed, so a reseed is
        /// literally "throw the meshes away and run the builders again".
        ///
        /// Anything that CACHES world-derived data has to be invalidated with it. There are only two:
        /// the map's baked terrain background, and the palette's `_lastTod` early-out. Nothing holds a
        /// <see cref="GameWorld"/> reference across a reseed — <see cref="HPPlayer"/> and
        /// <see cref="GameManager"/> both read this static through a property for exactly that reason.
        /// </summary>
        public static void SetSeed(uint seed)
        {
            if (World != null && World.Seed == seed) return;
            World = GameWorld.MakeWorld(seed);
            MapView.InvalidateBackground();
            if (Instance != null) Instance.Rebuild();
        }

        private void Awake()
        {
            Instance = this;
            EnsureWorld();
            Build();
            PostFX.Ensure(gameObject);
            HPAudio.Ensure(gameObject); // synthesizes every cue + starts the wind/creek beds
            HPDebug.Ensure(gameObject); // F3 diagnostics overlay (costs nothing while hidden)
            SetTimeOfDay(0f);
        }

        /// <summary>Tear the built geometry down and lay it out again from the current World.</summary>
        private void Rebuild()
        {
            // Every mesh, prop and light the builders make is parented to this transform, so the
            // children ARE the world. PostFX/HPAudio are components on this GameObject rather than
            // children, so they survive — which matters: re-synthesizing the cues would cut the beds.
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            Build();
            InvalidatePalette();      // _lastTod would otherwise early-out and leave the new moon unlit
            SetTimeOfDay(_appliedTod, _appliedNight);
        }

        // --- Dev cost levers (the F3 overlay toggles these live) ------------------
        //
        // Collected during Build so the overlay never has to search the scene by name, and CLEARED
        // first because a reseed destroys and recreates every one of these objects — a stale list
        // here would be a fistful of MissingReferenceExceptions the next time you pressed a key.
        private readonly List<Renderer> _undergrowthRenderers = new List<Renderer>();
        private readonly List<Light> _propLights = new List<Light>();

        /// <summary>Undergrowth is ~5,200 scattered meshes; hiding it isolates its cost from the trees'.</summary>
        public void SetUndergrowthVisible(bool on)
        {
            foreach (var r in _undergrowthRenderers) if (r != null) r.enabled = on;
        }

        /// <summary>The warm prop lights (campfire, RV, cave glows). Realtime point lights are the
        /// second lever in the §7 perf order, after bloom.</summary>
        public void SetPropLightsEnabled(bool on)
        {
            foreach (var l in _propLights) if (l != null) l.enabled = on;
        }

        public int PropLightCount => _propLights.Count;
        public int UndergrowthMeshCount => _undergrowthRenderers.Count;

        private void Build()
        {
            _undergrowthRenderers.Clear();
            _propLights.Clear();
            BuildTerrain();
            BuildForest();
            BuildUndergrowth();
            BuildTrails();
            BuildLogs();
            BuildLake();
            BuildRv();
            BuildDuffel();
            BuildCaves();
            BuildTower();
            BuildCamp();
            BuildLighting();

            // Sweep up the prop lights rather than registering them at each creation site: they're
            // made by four different builders, and a scan can't be forgotten when a fifth is added.
            // The moon is Directional and stays out of it — killing that would black out the world.
            foreach (var l in GetComponentsInChildren<Light>(true))
                if (l.type != LightType.Directional) _propLights.Add(l);
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

        /// <summary>
        /// The forest is chunked into a <see cref="ForestGrid"/>×<see cref="ForestGrid"/> grid of
        /// combined meshes rather than three map-sized ones.
        ///
        /// Why: a single combined mesh has a map-sized bounding box, so Unity can never frustum-cull
        /// any of it — every trunk in the forest is submitted every frame no matter where you look.
        /// That was survivable at 700 trees and is not at 2,400. Per-cell meshes let the camera throw
        /// away everything behind it and everything past the fog, which is most of the map. The cost
        /// is more draw calls (cells × 3 materials), and draw calls are the cheap side of that trade.
        /// </summary>
        private const int ForestGrid = 8;

        private void BuildForest()
        {
            // MUST mirror WorldData.BuildColliders' rand() call order exactly so the rendered trees
            // sit precisely on their colliders (same skips, same draws, same seed). Note the skips
            // below are the same four, in the same order — a mismatch here silently desyncs the
            // visible trunks from the invisible colliders players actually collide with.
            var rand = Rng.Mulberry32(World.Seed ^ 0x9e3779b9u);
            double half = Sim.World.Size / 2 - 6;

            Mesh trunk = MeshUtil.TaperedCylinder(0.4f, 0.22f, 3f, 7);
            Mesh cone1 = MeshUtil.Cone(2.0f, 3.2f, 8);
            Mesh cone2 = MeshUtil.Cone(1.5f, 2.6f, 8);
            Mesh cone3 = MeshUtil.Cone(1.0f, 2.0f, 8);

            int cells = ForestGrid * ForestGrid;
            var trunkC = NewCombineBuckets(cells);
            var crownDarkC = NewCombineBuckets(cells);
            var crownLightC = NewCombineBuckets(cells);
            int treeIndex = 0;

            for (int i = 0; i < Sim.World.TreeCount; i++)
            {
                double x = (rand() * 2 - 1) * half;
                double z = (rand() * 2 - 1) * half;
                if (System.Math.Sqrt(x * x + z * z) < Sim.World.BaseCampRadius + 4) continue;
                if (NearCave(x, z, 7)) continue;
                if (InLake(x, z, 3)) continue;
                if (Paths.PathDepth(World.Paths, x, z, PathGen.TreeMargin) > 0) continue;
                double s = 0.7 + rand() * 0.9;
                double rot = rand() * System.Math.PI * 2; // same draw the collider builder discards

                float y = (float)World.GetHeight(x, z);
                var pos = new Vector3((float)x, y, (float)z);
                var rotQ = Quaternion.Euler(0f, (float)(rot * Mathf.Rad2Deg), 0f);
                var scale = Vector3.one * (float)s;

                int cell = CellOf(x, z);
                trunkC[cell].Add(CI(trunk, pos, rotQ, scale));
                var crowns = (treeIndex % 2 == 0) ? crownDarkC : crownLightC;
                crowns[cell].Add(CI(cone1, pos + Vector3.up * (2.2f * (float)s), rotQ, scale));
                crowns[cell].Add(CI(cone2, pos + Vector3.up * (4.0f * (float)s), rotQ, scale));
                crowns[cell].Add(CI(cone3, pos + Vector3.up * (5.6f * (float)s), rotQ, scale));
                treeIndex++;
            }

            var trunkMat = MeshUtil.Lit(TrunkCol);
            var darkMat = MeshUtil.Lit(CrownDark);
            var lightMat = MeshUtil.Lit(CrownLight);
            for (int c = 0; c < cells; c++)
            {
                NewCombinedGo($"Trunks{c}", trunkC[c], trunkMat);
                NewCombinedGo($"CrownsDark{c}", crownDarkC[c], darkMat);
                NewCombinedGo($"CrownsLight{c}", crownLightC[c], lightMat);
            }
        }

        private static List<CombineInstance>[] NewCombineBuckets(int n)
        {
            var buckets = new List<CombineInstance>[n];
            for (int i = 0; i < n; i++) buckets[i] = new List<CombineInstance>();
            return buckets;
        }

        /// <summary>Which forest chunk (x,z) falls in. Clamped, so the world edge can't index out.</summary>
        private static int CellOf(double x, double z)
        {
            double size = Sim.World.Size;
            int cx = Mathf.Clamp((int)((x + size / 2) / size * ForestGrid), 0, ForestGrid - 1);
            int cz = Mathf.Clamp((int)((z + size / 2) / size * ForestGrid), 0, ForestGrid - 1);
            return cz * ForestGrid + cx;
        }

        private static bool NearCave(double x, double z, double r)
        {
            foreach (var c in World.Caves)
                if ((c.X - x) * (c.X - x) + (c.Z - z) * (c.Z - z) < r * r) return true;
            return false;
        }

        /// <summary>Mirrors WorldData's private lake test — trees don't grow in open water.</summary>
        private static bool InLake(double x, double z, double margin)
        {
            double nx = (x - WorldData.Lake.X) / (WorldData.Lake.Rx + margin);
            double nz = (z - WorldData.Lake.Z) / (WorldData.Lake.Rz + margin);
            return nx * nx + nz * nz < 1;
        }

        // --- Undergrowth + trails -------------------------------------------------

        /// <summary>
        /// Ferns, bushes and mossy rocks — the layer that makes 2,400 trunks read as a *forest*
        /// floor rather than a mown field with poles in it.
        ///
        /// Deliberately RENDER-ONLY, and deliberately low (knee-to-waist). Undergrowth is not in the
        /// shared sim at all: it has no collider, so it never blocks a searcher, and it is short
        /// enough that it can't hide a standing player the line-of-sight check thinks is visible.
        /// Anything tall enough to break that promise belongs in the sim as a real collider, where
        /// both the host and every client agree on it.
        ///
        /// It draws from its own RNG stream (seed ^ a private xor), so adding or retuning clutter can
        /// never shift the tree stream and move a collider.
        /// </summary>
        private void BuildUndergrowth()
        {
            var rand = Rng.Mulberry32(World.Seed ^ 0x5eedb115u);
            double half = Sim.World.Size / 2 - 6;
            int cells = ForestGrid * ForestGrid;

            Mesh fern = MeshUtil.Cone(0.55f, 0.75f, 5);
            Mesh bush = MeshUtil.Cone(0.9f, 1.15f, 6);
            Mesh rock = MeshUtil.TaperedCylinder(0.5f, 0.34f, 0.42f, 5);

            var fernC = NewCombineBuckets(cells);
            var bushC = NewCombineBuckets(cells);
            var rockC = NewCombineBuckets(cells);

            for (int i = 0; i < UndergrowthCount; i++)
            {
                double x = (rand() * 2 - 1) * half;
                double z = (rand() * 2 - 1) * half;
                double kind = rand();
                double s = 0.65 + rand() * 0.8;
                double rot = rand() * System.Math.PI * 2;

                // Keep the camp clearing, the water and the trails themselves clear. Trails get only
                // the tree margin, so scrub creeps to the edge of a lane without closing it.
                if (System.Math.Sqrt(x * x + z * z) < Sim.World.BaseCampRadius + 2) continue;
                if (InLake(x, z, 1)) continue;
                if (Paths.PathDepth(World.Paths, x, z) > 0) continue;

                float y = (float)World.GetHeight(x, z);
                var pos = new Vector3((float)x, y, (float)z);
                var rotQ = Quaternion.Euler(0f, (float)(rot * Mathf.Rad2Deg), 0f);
                var scale = Vector3.one * (float)s;
                int cell = CellOf(x, z);

                if (kind < 0.5) fernC[cell].Add(CI(fern, pos, rotQ, scale));
                else if (kind < 0.85) bushC[cell].Add(CI(bush, pos, rotQ, scale));
                else rockC[cell].Add(CI(rock, pos - Vector3.up * 0.05f, rotQ, scale));
            }

            var fernMat = MeshUtil.Lit(FernCol);
            var bushMat = MeshUtil.Lit(BushCol);
            var rockMat = MeshUtil.Lit(RockCol);
            for (int c = 0; c < cells; c++)
            {
                TrackUndergrowth(NewCombinedGo($"Ferns{c}", fernC[c], fernMat));
                TrackUndergrowth(NewCombinedGo($"Bushes{c}", bushC[c], bushMat));
                TrackUndergrowth(NewCombinedGo($"Rocks{c}", rockC[c], rockMat));
            }
        }

        private void TrackUndergrowth(GameObject go)
        {
            if (go == null) return; // empty grid cell — NewCombinedGo skips those
            var r = go.GetComponent<Renderer>();
            if (r != null) _undergrowthRenderers.Add(r);
        }

        /// <summary>How many clutter candidates to scatter (rejections thin it, as with the trees).</summary>
        private const int UndergrowthCount = 5200;

        /// <summary>
        /// The logging trails as visible ground: a packed-dirt ribbon laid along each seeded polyline.
        ///
        /// The corridor is already real to the sim (no trees grow in it) — this is what makes it
        /// legible, so "follow the trail" is something a player can actually decide to do. Drawn as a
        /// quad strip that conforms to the terrain and floats a few centimetres over it, the same
        /// trick the lake sheet uses: the ground is analytic and can't be carved.
        /// </summary>
        private void BuildTrails()
        {
            var mat = MeshUtil.Lit(TrailCol);
            foreach (var path in World.Paths)
            {
                var verts = new List<Vector3>();
                var tris = new List<int>();
                for (int i = 0; i < path.Pts.Count; i++)
                {
                    // Segment direction, averaged at the joints so corners don't pinch.
                    Vec2 prev = path.Pts[Mathf.Max(i - 1, 0)];
                    Vec2 next = path.Pts[Mathf.Min(i + 1, path.Pts.Count - 1)];
                    float dx = (float)(next.X - prev.X);
                    float dz = (float)(next.Z - prev.Z);
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len < 1e-4f) { dx = 1f; dz = 0f; len = 1f; }
                    float nx = -dz / len, nz = dx / len; // left normal in XZ

                    // Narrow toward the far end so a trail fades out instead of stopping dead.
                    float taper = Mathf.Lerp(1f, 0.55f, i / (float)Mathf.Max(1, path.Pts.Count - 1));
                    float w = (float)path.HalfWidth * taper;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double px = path.Pts[i].X + nx * w * side;
                        double pz = path.Pts[i].Z + nz * w * side;
                        verts.Add(new Vector3((float)px, (float)World.GetHeight(px, pz) + 0.04f, (float)pz));
                    }
                }
                for (int i = 0; i + 1 < path.Pts.Count; i++)
                {
                    // Winding matters: vertex `a` is the RIGHT edge (the side loop runs -1 first), so
                    // (a,b,c) is the order whose normal points up. Get it backwards and the ribbon is
                    // lit from underneath and backface-culled from above — an invisible trail.
                    int a = i * 2, b = a + 1, c = a + 2, d = a + 3;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    tris.Add(b); tris.Add(d); tris.Add(c);
                }
                var mesh = new Mesh();
                mesh.SetVertices(verts);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                NewMeshGo("Trail", mesh, mat);
            }
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

        /// <summary>
        /// Where the moon sits right now, as a unit vector pointing FROM the world TOWARD the moon.
        /// Recomputed every palette update from the night number and the clock (see MoonAt).
        /// </summary>
        private Vector3 _moonDir = new Vector3(0.35f, 0.62f, -0.7f).normalized;

        /// <summary>Which compass direction the moon rises from this session (radians). Seeded.</summary>
        private float _moonRiseAz;

        /// <summary>
        /// The moon's three nights. It WANES and sets progressively earlier, so night 1 is lit from
        /// dusk to dawn and night 3 loses its moon before the night is half done.
        ///
        /// This is a difficulty dial, not decoration: moonlight is the only thing that lets searchers
        /// cross the forest without burning flashlight battery, and battery drain is *already*
        /// escalated per night. The light column is therefore pulled back gently (0.42 → 0.24 rather
        /// than to nothing) and a starlight floor keeps the map navigable after moonset — the dark
        /// stretch is meant to raise the cost of moving, not to stop play.
        ///
        /// Strictly, a waning moon rises later rather than setting earlier; we set it earlier so the
        /// darkness lands on the FINALE instead of the opening. Nobody who isn't tracking lunar
        /// mechanics will notice, and the dramatic shape is worth the small cheat.
        /// </summary>
        private struct MoonNight
        {
            public float Phase;     // shader convention: -1 full .. 0 half .. +1 new
            /// <summary>
            /// How far along its rise→set arc the moon ALREADY IS at dusk (0 = just rising,
            /// 1 = setting). This, not a per-night speed, is what makes later nights lose the moon
            /// sooner — every night moves at the same angular rate, so the sky never appears to run
            /// fast. Night 3 opens with the moon just past its peak and descending all night.
            /// </summary>
            public float ArcStart;
            public float PeakElev;  // degrees at the top of the arc
            public float Light;     // directional intensity while it's well up
            public float AzOffset;  // degrees added to the seeded rise direction, so nights differ
        }

        private static readonly MoonNight[] MoonNights =
        {
            new MoonNight { Phase = -0.90f, ArcStart = 0.00f, PeakElev = 68f, Light = 0.42f, AzOffset = 0f },
            new MoonNight { Phase = -0.35f, ArcStart = 0.26f, PeakElev = 60f, Light = 0.34f, AzOffset = 42f },
            new MoonNight { Phase = 0.05f, ArcStart = 0.53f, PeakElev = 52f, Light = 0.24f, AzOffset = 87f },
        };

        /// <summary>
        /// Arc fraction covered per unit of night — the moon's angular speed, identical every night.
        /// Tuned so night 1 (ArcStart 0) sets just before dawn: moonset tod = (1 - ArcStart) / rate,
        /// giving 0.95 / 0.70 / 0.45 for the three nights.
        /// </summary>
        private const float MoonArcRate = 1.053f;

        /// <summary>Elevation floor during the traverse — the "shallow drift". Below roughly this the
        /// moon rakes the 14 m hills and throws stretched shadows that read as a bug.</summary>
        private const float MoonMinElev = 35f;
        /// <summary>Degrees of azimuth the moon sweeps between rise and set.</summary>
        private const float MoonSweepDeg = 130f;
        /// <summary>Ambient the world keeps once the moon is down. Starlight, not pitch black.</summary>
        private const float MoonsetLightFloor = 0.07f;

        /// <summary>
        /// Moon direction + how far "up" it still is (1 = well up, 0 = set) for a night and clock.
        /// </summary>
        private void MoonAt(int night, float tod, out Vector3 dir, out float up, out MoonNight cfg)
        {
            cfg = MoonNights[Mathf.Clamp(night - 1, 0, MoonNights.Length - 1)];

            // One shared angular rate; only the STARTING point differs per night.
            float q = Mathf.Clamp01(cfg.ArcStart + tod * MoonArcRate);

            float az = _moonRiseAz + Mathf.Deg2Rad * (cfg.AzOffset + q * MoonSweepDeg);
            // sin() arc: floor at both ends, peak mid-arc. Never dips below MoonMinElev, so the moon
            // can't rake the hills and throw stretched shadows across the whole map.
            float elev = Mathf.Deg2Rad * Mathf.Lerp(MoonMinElev, cfg.PeakElev, Mathf.Sin(q * Mathf.PI));

            dir = new Vector3(
                Mathf.Cos(elev) * Mathf.Sin(az),
                Mathf.Sin(elev),
                Mathf.Cos(elev) * Mathf.Cos(az)).normalized;

            // Fade over the last stretch of the arc rather than snapping off at the horizon.
            up = 1f - Mathf.Clamp01(Mathf.InverseLerp(0.93f, 1f, q));
        }

        private void BuildLighting()
        {
            var rand = Rng.Mulberry32(World.Seed ^ 0x11007a11u);
            _moonRiseAz = (float)(rand() * System.Math.PI * 2.0);

            var moonGo = new GameObject("Moon");
            moonGo.transform.parent = transform;
            // Point the light FROM the moon, so the shadows on the ground agree with the disc the
            // skybox draws. These were unrelated before — there was no disc to disagree with.
            moonGo.transform.rotation = Quaternion.LookRotation(-_moonDir, Vector3.up);
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

            BuildSky();
        }

        /// <summary>
        /// The procedural skybox (Shaders/NightSky.shader). Replaces what used to be a FLAT SOLID
        /// COLOUR camera clear — there was no sky and no moon at all, only a directional light
        /// named "Moon". Colours, star brightness and the moon's position are driven per-frame from
        /// the same palette that drives the fog, so the sky and the haze can never disagree.
        /// </summary>
        private void BuildSky()
        {
            var shader = Shader.Find("HollowPines/NightSky");
            if (shader == null)
            {
                // Don't fail silently into a black void — this is exactly the "menu button that does
                // nothing" failure mode from §4. Keep the old flat fill and say why.
                Debug.LogWarning("[WorldBuilder] HollowPines/NightSky shader not found — " +
                                 "falling back to a flat sky. Is Shaders/NightSky.shader imported?");
                _skyMat = null;
                return;
            }

            _skyMat = new Material(shader);
            RenderSettings.skybox = _skyMat;
            var cam = Camera.main;
            if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
        }

        private Material _skyMat;

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
            /// <summary>How much of the star field shows — washed out at dusk/dawn, full at 3am.</summary>
            public float Stars;
            public SkyKey(float t, int sky, int fog, int amb, float dens, float moon, float stars)
            {
                T = t; Sky = MeshUtil.Rgb(sky); Fog = MeshUtil.Rgb(fog); Ambient = MeshUtil.Rgb(amb);
                FogDensity = dens; Moon = moon; Stars = stars;
            }
        }

        private static readonly SkyKey[] SkyKeys =
        {
            new SkyKey(0.00f, 0x3a3550, 0x453a4a, 0x40384a, 0.0075f, 0.30f, 0.05f), // dusk
            new SkyKey(0.25f, 0x141a2e, 0x18202e, 0x1c2434, 0.0100f, 0.38f, 0.65f), // nightfall
            new SkyKey(0.60f, 0x0a0e1c, 0x0c1220, 0x121a28, 0.0125f, 0.42f, 1.00f), // deep night
            new SkyKey(0.88f, 0x141a2e, 0x1a2030, 0x1c2434, 0.0105f, 0.36f, 0.70f), // pre-dawn
            new SkyKey(1.00f, 0x4a4258, 0x584a52, 0x4a4456, 0.0080f, 0.26f, 0.05f), // dawn
        };

        /// <summary>
        /// Blend the sky/fog/light palette for a 0..1 night progress. Called by GameManager every
        /// frame; <paramref name="night"/> is 1-based and selects the moon's phase/arc for the night.
        /// </summary>
        public void SetTimeOfDay(float t, int night = 1)
        {
            // Remembered so a reseed rebuild resumes the same palette and the same night, not dusk.
            _appliedTod = t;
            _appliedNight = night;
            // The night is part of the early-out: the moon's phase and arc change between nights even
            // when the clock reads the same, so comparing tod alone would freeze the sky at a rollover.
            if (Mathf.Abs(t - _lastTod) < 0.0005f && night == _lastNight) return;
            _lastTod = t;
            _lastNight = night;
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
            // --- the moon: phase + arc for THIS night, and where it is on the clock ---------
            MoonAt(night, t, out _moonDir, out float moonUp, out MoonNight moonCfg);

            // Palette `moon` is the shape across a night (dimmer at dusk/dawn); the night's own Light
            // scales that whole shape; `moonUp` then takes it down to starlight once it sets.
            float lit = moon * (moonCfg.Light / MoonNights[0].Light);
            RenderSettings.ambientLight = ambient * Mathf.Lerp(0.62f, 1f, moonUp);
            if (_moon != null)
            {
                _moon.intensity = Mathf.Lerp(MoonsetLightFloor, lit, moonUp);
                _moon.transform.rotation = Quaternion.LookRotation(-_moonDir, Vector3.up);
                // No shadow-caster once it's down — moonset shadows from a moon nobody can see is
                // the exact mismatch this whole change was meant to remove.
                _moon.shadows = moonUp > 0.05f ? LightShadows.Hard : LightShadows.None;
            }

            if (_skyMat != null)
            {
                // Horizon takes the palette's sky colour so it meets the fog seamlessly at the
                // treeline; the zenith is DARKER, which is the way a real night sky runs — brightest
                // low down, deepest overhead. Getting that inverted is the usual tell that a sky is
                // a lerped gradient rather than an observed one.
                _skyMat.SetColor(SkyHorizonId, sky);
                _skyMat.SetColor(SkyZenithId, sky * 0.42f);
                _skyMat.SetColor(SkyGroundId, sky * 0.30f);
                // Stars come OUT when the moon goes down — no moonwash. On night 3 that's the payoff
                // for losing the light: the sky gets better exactly as the forest gets worse.
                float stars = Mathf.Lerp(a.Stars, b.Stars, k) * Mathf.Lerp(1.45f, 1f, moonUp);
                _skyMat.SetFloat(SkyStarsId, stars * (TitleMode ? 1.25f : 1f));
                _skyMat.SetVector(SkyMoonDirId, _moonDir);
                _skyMat.SetFloat(SkyMoonPhaseId, moonCfg.Phase);
                _skyMat.SetFloat(SkyMoonBrightId, Mathf.Lerp(1.6f, 3.2f, Mathf.InverseLerp(0.24f, 0.44f, lit)) * moonUp);
                _skyMat.SetFloat(SkyMoonGlowId, 0.8f * moonUp);
            }
            else
            {
                var cam = Camera.main;
                if (cam != null)
                {
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = sky;
                }
            }
        }

        // Cached shader property ids — SetColor(string) hashes the name on every call, and this runs
        // every frame from GameManager's clock.
        private static readonly int SkyHorizonId = Shader.PropertyToID("_HorizonColor");
        private static readonly int SkyZenithId = Shader.PropertyToID("_ZenithColor");
        private static readonly int SkyGroundId = Shader.PropertyToID("_GroundColor");
        private static readonly int SkyStarsId = Shader.PropertyToID("_StarBrightness");
        private static readonly int SkyMoonDirId = Shader.PropertyToID("_MoonDir");
        private static readonly int SkyMoonBrightId = Shader.PropertyToID("_MoonBrightness");
        private static readonly int SkyMoonPhaseId = Shader.PropertyToID("_MoonPhase");
        private static readonly int SkyMoonGlowId = Shader.PropertyToID("_MoonGlow");

        // --- helpers ----------------------------------------------------------------

        private static CombineInstance CI(Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            return new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(pos, rot, scale) };
        }

        private GameObject NewCombinedGo(string name, List<CombineInstance> combines, Material mat)
        {
            // Chunking leaves empty buckets (a grid cell that is all lake, or all camp clearing).
            // An empty combine yields a zero-vertex mesh and a renderer that costs culling work for
            // nothing, so skip them outright.
            if (combines.Count == 0) return null;
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
