// Builds the whole forest, in code, from the deterministic shared sim (Metoh.Sim) — the
// Unity equivalent of the web client's Environment.ts. Every client renders the identical world
// because everything derives from World.Seed; there are no scene-baked positions and no assets.
//
// "Gameplay functional" pass: primitive low-poly meshes (smooth normals), fog + a moon light +
// warm prop lights, and a day-night palette driven by the replicated timeOfDay. Pretty comes in R5.
using System.Collections.Generic;
using Metoh.Sim;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

namespace Metoh.Game
{
    public class WorldBuilder : MonoBehaviour
    {
        /// <summary>The shared deterministic world — colliders/terrain the players also step against.</summary>
        public static GameWorld World { get; private set; }

        public static WorldBuilder Instance { get; private set; }

        private Light _moon;

        /// <summary>The moon, for HPQuality — it re-asserts shadow quality after every world rebuild.</summary>
        public static Light MoonLight { get; private set; }
        private float _lastTod = -1f;
        private float _appliedTod;  // last time-of-day handed to SetTimeOfDay (survives a reseed)
        private int _appliedNight = 1;
        private int _lastNight = -1;

        /// <summary>
        /// Local fog-density multiplier. Yeti's trade-off (web Phase 3): brighter near vision
        /// (PostFX exposure) but a murkier distance — set to ~1.35 for the local Yeti.
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

        // Palette — the Himalayan re-theme. The web build's Environment.ts is NO LONGER the source
        // of these: it keeps its forest colours deliberately (the web visuals are abandoned), so the
        // two palettes have diverged on purpose and are not worth re-syncing.
        private static readonly Color TrunkCol = MeshUtil.Rgb(0x3b3129);
        private static readonly Color CrownDark = MeshUtil.Rgb(0x2c4437);  // blue-green conifer
        private static readonly Color CrownLight = MeshUtil.Rgb(0x8fa3ad); // snow-laden bough
        private static readonly Color RockCol = MeshUtil.Rgb(0x6b7078);    // granite
        private static readonly Color LogCol = MeshUtil.Rgb(0x4a3c30);
        private static readonly Color GroundCol = MeshUtil.Rgb(0xc9d6e2);  // moonlit snowpack
        private static readonly Color LakeCol = MeshUtil.Rgb(0x9fc4d8);    // frozen tarn
        private static readonly Color DriftCol = MeshUtil.Rgb(0xdde7ee);   // undergrowth: wind-piled snow
        private static readonly Color ScreeCol = MeshUtil.Rgb(0x565c66);   // undergrowth: shattered rock

        // --- the ground's value range ------------------------------------------------------------
        //
        // These four are picked TOGETHER, as a spread rather than as individual colours, because the
        // complaint that started this pass was "everything looks the same" and that is a statement
        // about range, not about hue. Before, ground/trail/drift all sat inside the top fifth of the
        // value scale and the whole world came out as one bright grey. Now: bare rock at ~0x4a anchors
        // the dark end, packed trail at ~0x94, basin drift at ~0xa8, open snowpack at ~0xc9. Roughly
        // 2.7 stops between the darkest and lightest ground in the game, which is what gives the eye
        // something to judge shape and distance by.
        //
        // Hue does work here too, and it is doing something specific: the basin is pushed BLUE (deep
        // snow, sky-lit, in its own shadow) while the trail is pushed WARM-neutral (packed, scuffed,
        // trodden). They are close in value, so hue is what keeps them apart — and telling those two
        // apart is a live gameplay question, since one of them slows you down and the other doesn't.

        /// <summary>Wind-stripped rock on the steep ground. The dark anchor the whole palette needs.</summary>
        private static readonly Color RockBareCol = MeshUtil.Rgb(0x4a4f57);

        /// <summary>
        /// The deep-snow basin floor (<c>Movement.DeepSnowDepth</c>'s zone). Deliberately blue and
        /// clearly darker than open snowpack — this is the ~⅓ of the map that halves your speed, and
        /// until this pass it was completely invisible. A routing choice you cannot see is an ambush,
        /// not a choice.
        /// </summary>
        private static readonly Color BasinCol = MeshUtil.Rgb(0xa8bccf);

        /// <summary>
        /// Packed trail snow. **Darker than <see cref="GroundCol"/>, and the comment that used to sit
        /// here claiming it "must stay clearly lighter" was wrong on both counts** — the value shipped
        /// was already darker, and darker is also what real trodden snow does (compacted, scuffed,
        /// less air in it to scatter light back). What the trail actually needs is CONTRAST against
        /// open snow, in either direction, because Commit 5 turned this network into the surface that
        /// is not knee-deep and a player has to be able to see where it ends. Pushed further down to
        /// make that separation unmistakable, and kept warm so it never reads as basin drift.
        /// </summary>
        private static readonly Color TrailCol = MeshUtil.Rgb(0x94a0ab);

        /// <summary>
        /// Terrain height above which trees read as snow-caked: all-light crowns, and the top cone
        /// dropped so they look stunted by altitude.
        ///
        /// NOT a fraction of <c>World.HillHeight</c>, which is the noise AMPLITUDE (14) rather than a
        /// reachable height — on the shipping seed the terrain actually spans about -10.2 to +7.8, so
        /// the "60% of HillHeight" rule this re-theme was drafted with sits above the highest ground
        /// on the map and would have snow-caked nothing at all. Measured against a 400x400 sample
        /// grid, 3.0 is roughly the 85th percentile: the ridges go white, the valleys stay green.
        /// </summary>
        private const float SnowlineHeight = 3.0f;

        /// <summary>
        /// How strongly snowpack bounces ambient light back up (the Trilight ground term). Well under
        /// 1 — the ground does not return everything the sky delivers — but high enough that the
        /// undersides of branches, ledges and figures never go dead, which is what actually sells a
        /// surface as snow rather than as white plastic.
        /// </summary>
        private const float AmbientBounce = 1.0f;

        /// <summary>
        /// The evidence duffel beside the RV — the only place proof becomes permanent, and the one
        /// thing in the forest Yeti cannot touch. Derived from the RV's seeded transform so the
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
            BuildNavMesh();
            PostFX.Ensure(gameObject);
            HPAudio.Ensure(gameObject); // synthesizes every cue + starts the wind/tarn beds
            HPDebug.Ensure(gameObject); // F3 diagnostics overlay (costs nothing while hidden)
            SetTimeOfDay(0f);
        }

        private NavMeshSurface _navSurface;

        /// <summary>
        /// Bake the CPU bot's navigation surface. Runtime, and rebuilt on every reseed — the forest is
        /// procedural, so a baked-in-the-editor NavMesh is impossible; there is nothing to bake until
        /// the world exists.
        ///
        /// The bake carves around the trees (they're render-mesh children here) but the UNDERGROWTH is
        /// hidden first: ~5,200 knee-high ferns would shred the mesh into confetti for no benefit,
        /// since they aren't solid to anyone. This reuses the same renderer list the F3 perf toggle
        /// uses. The bot's actual COLLISION is still the shared sim's analytic circles — this surface
        /// only decides the global route (around the lake, over the hills), so an imperfect bake
        /// degrades to "occasionally clips a route past a trunk the sim then slides it around", never
        /// to walking through solid geometry.
        /// </summary>
        private void BuildNavMesh()
        {
            if (_navSurface == null)
            {
                _navSurface = gameObject.AddComponent<NavMeshSurface>();
                _navSurface.collectObjects = CollectObjects.Children; // our world is all under this transform
                _navSurface.useGeometry = NavMeshCollectGeometry.RenderMeshes; // trees have no PhysX colliders
            }

            bool undergrowthWasOn = _undergrowthRenderers.Count == 0 || _undergrowthRenderers[0] == null
                || _undergrowthRenderers[0].enabled;
            SetUndergrowthVisible(false);
            _navSurface.BuildNavMesh();
            SetUndergrowthVisible(undergrowthWasOn);

            // Confirm the bake actually produced walkable ground — an empty NavMesh is the leading
            // suspect when the CPU Yeti stands still. Cheap, and only logged once per (re)build.
            var tri = UnityEngine.AI.NavMesh.CalculateTriangulation();
            Debug.Log($"[navmesh] baked: {tri.vertices.Length} verts, {tri.indices.Length / 3} tris" +
                      (tri.indices.Length == 0 ? "  <-- EMPTY: bot will fall back to beeline steering" : ""));
        }

        /// <summary>Tear the built geometry down and lay it out again from the current World.</summary>
        private void Rebuild()
        {
            // Every mesh, prop and light the builders make is parented to this transform, so the
            // children ARE the world. PostFX/HPAudio are components on this GameObject rather than
            // children, so the COMPONENTS survive — but that is not the same as their objects
            // surviving, and reading it that way cost a session of dead audio: HPAudio used to
            // parent its 22 sources here and this loop ate every one of them. Anything a surviving
            // component owns must live outside this transform (HPAudio keeps a scene-root of its own).
            ReleaseWorldMaterials(); // BEFORE the children go — it reads their renderers to find them
            for (int i = transform.childCount - 1; i >= 0; i--) Destroy(transform.GetChild(i).gameObject);
            Build();
            BuildNavMesh();           // the world moved — the CPU bot's pathing surface must follow
            InvalidatePalette();      // _lastTod would otherwise early-out and leave the new moon unlit
            SetTimeOfDay(_appliedTod, _appliedNight);
        }

        /// <summary>
        /// Destroy the materials this world created, before the objects holding them are destroyed.
        ///
        /// `new Material(...)` allocates a native object that Unity does NOT collect when the last
        /// renderer referencing it goes away — it has to be destroyed by hand. Every builder here
        /// makes fresh materials on every reseed, so each match was quietly leaking a full set. That
        /// was survivable while there were a couple of dozen; the per-chunk forest tinting turns it
        /// into roughly two hundred per rebuild, which is exactly the kind of slow bleed that shows
        /// up as "the fourth match runs worse than the first" and gets blamed on something else.
        ///
        /// Sweeping the renderers is deliberate over tracking every creation site: materials are
        /// assigned in a dozen builders and several go straight onto a renderer, so a registry would
        /// be one forgotten call away from being wrong. What is on the object graph IS the truth.
        /// The skybox is excluded — it outlives the world and is rebuilt on its own terms.
        /// </summary>
        private void ReleaseWorldMaterials()
        {
            var seen = new HashSet<Material>();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m != _skyMat) seen.Add(m);
            }
            foreach (var m in seen) Destroy(m);
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
            BuildBasecamp();
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
            // Render resolution (collision samples the analytic height, not this mesh, so this is
            // purely a look/cost trade and can move freely). Raised from 120: at 120 a quad spans
            // ~6.7 m, which is too coarse to hold a ridgeline — every crest came out as a soft blob
            // and the horizon silhouette was visibly faceted. 192 puts a vertex every ~4.2 m for
            // ~37k vertices in ONE mesh, which is nothing to draw; the real cost is the one-off
            // GetHeight sweep at build/reseed time, and that is analytic and cheap.
            int segs = 192;
            float size = (float)Sim.World.Size;
            float half = size / 2f;
            var verts = new Vector3[(segs + 1) * (segs + 1)];
            // World-space UVs in METRES, so the snow material's tiling is set in real units rather
            // than in "fractions of an 800 m plane". At 120 segments a quad is ~6.7 m across, which is
            // far too coarse to carry surface detail in geometry — the normal map is doing all of that
            // work, and it can only do it if the UVs are dense and uniform.
            var uvs = new Vector2[(segs + 1) * (segs + 1)];
            for (int zi = 0; zi <= segs; zi++)
            {
                for (int xi = 0; xi <= segs; xi++)
                {
                    float x = -half + size * xi / segs;
                    float z = -half + size * zi / segs;
                    verts[zi * (segs + 1) + xi] = new Vector3(x, (float)World.GetHeight(x, z), z);
                    uvs[zi * (segs + 1) + xi] = new Vector2(x, z);
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
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            NewMeshGo("Terrain", mesh, SnowMaterial());
        }

        /// <summary>
        /// The snowpack surface — <c>Metoh/Snowpack</c>, which blends snow against wind-stripped rock
        /// by SLOPE and tints the deep-snow basin.
        ///
        /// This replaces a single flat URP/Lit colour stretched over all 800 m. Two normal layers were
        /// never the problem; one albedo was. A normal map varies the LIGHTING of a surface, but the
        /// surface still has exactly one colour, so at any distance past a few metres the whole map
        /// averages back to the same grey and there is nothing left to read the land by. Slope-driven
        /// rock puts genuine dark values back on the ridges and gully walls, which is what snow
        /// country actually looks like and what makes terrain shape legible at range.
        ///
        /// The drift constants come straight from the sim rather than being retyped here, so the tint
        /// can never disagree with the slow it is advertising.
        /// </summary>
        private static Material SnowMaterial()
        {
            var shader = Shader.Find("Metoh/Snowpack");
            if (shader == null)
            {
                // Same rule as the sky: never fail silently into something that looks like a choice.
                Debug.LogWarning("[WorldBuilder] Metoh/Snowpack shader not found — falling back to flat " +
                                 "snow (no rock, no basin tint). Is Shaders/Snowpack.shader synced?");
                return MeshUtil.Surface(
                    GroundCol, smoothness: 0.42f,
                    normal: ProcTex.SnowNormal, normalScale: 0.75f,
                    tiling: 1f / 6f,
                    detailNormal: ProcTex.SnowDetailNormal, detailTiling: 1f / 0.7f);
            }

            var m = new Material(shader);
            m.SetColor("_BaseColor", GroundCol);
            m.SetColor("_RockColor", RockBareCol);
            m.SetColor("_DriftColor", BasinCol);
            m.SetTexture("_BumpMap", ProcTex.SnowNormal);
            m.SetTexture("_RockMap", ProcTex.RockNormal);
            m.SetTexture("_DetailMap", ProcTex.SnowDetailNormal);

            // Tilings are in METRES PER REPEAT (the terrain's UVs are world metres), so these read as
            // real sizes: a 6 m snow grain, a 4 m rock fracture, a 0.7 m grain underfoot.
            m.SetFloat("_SnowTiling", 6f);
            m.SetFloat("_RockTiling", 4f);
            m.SetFloat("_DetailTiling", 0.7f);
            m.SetFloat("_SnowNormalScale", 0.75f);
            m.SetFloat("_RockNormalScale", 1.1f);
            m.SetFloat("_DetailScale", 0.6f);
            m.SetFloat("_SnowSmoothness", 0.42f);
            m.SetFloat("_RockSmoothness", 0.12f);

            // Slope thresholds in GRADIENT units (rise/run). This terrain runs 0..~0.4, so 0.17→0.34
            // bares the top ~15% of slopes: ridge shoulders and gully walls go to rock, everything
            // walkable stays snow. Raise _SlopeStart if the map ends up feeling too rocky.
            m.SetFloat("_SlopeStart", 0.17f);
            m.SetFloat("_SlopeEnd", 0.34f);
            m.SetFloat("_SlopeJitter", 0.4f);

            // The deep-snow basin, straight from the sim's own numbers (Movement.DeepSnowDepth).
            m.SetFloat("_DriftHeight", (float)Sim.Player.DriftHeight);
            m.SetFloat("_DriftDepth", (float)Sim.Player.DriftDepth);
            m.SetFloat("_DriftStrength", 0.65f);

            m.SetFloat("_MacroTiling", 42f);
            m.SetFloat("_MacroStrength", 0.22f);
            return m;
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

        /// <summary>How many distinct crown shapes are dealt across the forest. See BuildForest.</summary>
        private const int TreeVariants = 4;

        private void BuildForest()
        {
            // MUST mirror WorldData.BuildColliders' rand() call order exactly so the rendered trees
            // sit precisely on their colliders (same skips, same draws, same seed). Note the skips
            // below are the same four, in the same order — a mismatch here silently desyncs the
            // visible trunks from the invisible colliders players actually collide with.
            var rand = Rng.Mulberry32(World.Seed ^ 0x9e3779b9u);
            double half = Sim.World.Size / 2 - 6;

            // --- the tree shapes -------------------------------------------------------------
            //
            // FOUR VARIANTS OF EACH, dealt by a hash of the tree index. A forest is not one tree
            // repeated, and at 2,400 copies the repetition is obvious even through fog — you start
            // recognising individual trees, which destroys any sense that the valley is a place
            // rather than a texture. Four is enough that the eye stops finding the pattern, and it
            // costs four meshes rather than four draw calls because they all combine into the same
            // per-chunk mesh anyway.
            //
            // Detail scales with the quality tier. Note the LOW tier is actually CHEAPER than the
            // three cones it replaces (77 tris vs 96), so the cheap path got faster and better at
            // once; the high tier spends 153 tris on a silhouette worth having.
            int cRings = HPQuality.HighDetail ? 9 : 6;
            int cSegs = HPQuality.HighDetail ? 9 : 7;
            int tSegs = HPQuality.HighDetail ? 8 : 6;

            Mesh trunk = MeshUtil.TaperedCylinder(0.4f, 0.22f, 3f, tSegs);
            var crowns = new Mesh[TreeVariants];
            var crownsStunted = new Mesh[TreeVariants];
            for (int v = 0; v < TreeVariants; v++)
            {
                // Height and width wander per variant so the stand has a mix of lean spires and
                // squatter, broader trees rather than one silhouette at four random scales.
                float h = 6.2f + (v % 2) * 1.1f - (v / 2) * 0.5f;
                float r = 2.15f - (v % 2) * 0.25f + (v / 2) * 0.18f;
                crowns[v] = MeshUtil.Conifer(h, r, cRings, cSegs, tiers: 4 + v % 2, variant: v);
                // Above the snowline: shorter and broader, weighed down and wind-stunted by altitude.
                crownsStunted[v] = MeshUtil.Conifer(h * 0.66f, r * 1.08f, Mathf.Max(cRings - 2, 3), cSegs,
                    tiers: 3, variant: v + 64);
            }

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
                // Snowline. Above it every crown is the pale snow-laden material and the shape is the
                // stunted variant, so high ground reads as wind-beaten and white; below, crowns
                // alternate as before. All of this is pure material/mesh CHOICE driven by the
                // already-computed height and index — no rand() call is added, moved or skipped, so
                // the tree stream stays in lockstep with WorldData.BuildColliders (UNITY_PORT_NOTES
                // §3c). The treeIndex % 2 alternation was never an RNG draw either, which is why it
                // is safe to key off it here.
                bool aboveSnowline = y >= SnowlineHeight;
                var crownBucket = (aboveSnowline || treeIndex % 2 != 0) ? crownLightC : crownDarkC;
                // Deal a shape. Mixing in the cell index as well as the tree index stops neighbouring
                // trees (which are adjacent in the stream) from marching through the variants in the
                // same order and forming a visible repeat down a slope.
                int variant = (treeIndex * 7 + cell * 3) % TreeVariants;
                Mesh crown = aboveSnowline ? crownsStunted[variant] : crowns[variant];
                crownBucket[cell].Add(CI(crown, pos + Vector3.up * (1.5f * (float)s), rotQ, scale));
                treeIndex++;
            }

            // A material PER CHUNK, each with its colour nudged a little off the palette.
            //
            // After flat shading, the loudest "this is a game" tell is uniformity: 2,400 trunks in
            // exactly one brown reads as instancing no matter how good the surface response is. Real
            // stands of trees vary by age, aspect and how much snow they caught. This costs nothing —
            // each chunk is already its own GameObject and its own draw, and the SRP batcher batches
            // by shader rather than by material, so 64 tinted variants batch the same as one.
            for (int c = 0; c < cells; c++)
            {
                NewCombinedGo($"Trunks{c}", trunkC[c], MeshUtil.Surface(
                    TintByCell(TrunkCol, c, 0.10f), 0.16f, ProcTex.BarkNormal, 0.9f, 1.5f));
                NewCombinedGo($"CrownsDark{c}", crownDarkC[c], MeshUtil.Surface(
                    TintByCell(CrownDark, c, 0.12f), 0.20f, ProcTex.BarkNormal, 0.45f, 2.5f));
                NewCombinedGo($"CrownsLight{c}", crownLightC[c], MeshUtil.Surface(
                    TintByCell(CrownLight, c, 0.08f), 0.38f, ProcTex.SnowNormal, 0.7f, 2.5f));
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
        /// Snow drifts, scree and prayer-flag poles — the layer that makes 2,400 trunks read as a
        /// mountainside rather than a mown field with poles in it.
        ///
        /// The flag poles are not decoration. In a palette this close to monochrome a player has
        /// almost nothing to navigate by, so ~3% of the clutter is a strung pole in saturated
        /// primaries: the only strong colour out in the open, and therefore the thing you steer by
        /// and call out to a teammate.
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

            Mesh drift = MeshUtil.Cone(0.95f, 0.42f, 7);                        // wind-piled snow mound
            Mesh scree = MeshUtil.TaperedCylinder(0.5f, 0.34f, 0.42f, 5);       // shattered rock
            Mesh pole = MeshUtil.TaperedCylinder(0.055f, 0.045f, 2.3f, 5);
            Mesh flag = MeshUtil.UnitCube();

            var driftC = NewCombineBuckets(cells);
            var screeC = NewCombineBuckets(cells);
            var poleC = NewCombineBuckets(cells);
            var flagC = new List<CombineInstance>[FlagCols.Length][];
            for (int f = 0; f < FlagCols.Length; f++) flagC[f] = NewCombineBuckets(cells);

            for (int i = 0; i < UndergrowthCount; i++)
            {
                double x = (rand() * 2 - 1) * half;
                double z = (rand() * 2 - 1) * half;
                double kind = rand();
                double s = 0.65 + rand() * 0.8;
                double rot = rand() * System.Math.PI * 2;

                // Keep the camp clearing, the water and the trails themselves clear. Trails get only
                // the tree margin, so clutter creeps to the edge of a lane without closing it.
                if (System.Math.Sqrt(x * x + z * z) < Sim.World.BaseCampRadius + 2) continue;
                if (InLake(x, z, 1)) continue;
                if (Paths.PathDepth(World.Paths, x, z) > 0) continue;

                float y = (float)World.GetHeight(x, z);
                var pos = new Vector3((float)x, y, (float)z);
                var rotQ = Quaternion.Euler(0f, (float)(rot * Mathf.Rad2Deg), 0f);
                var scale = Vector3.one * (float)s;
                int cell = CellOf(x, z);

                // Same five draws in the same order as before (x, z, kind, s, rot) — the stream is
                // private to undergrowth, but keeping the shape means a reseed puts clutter in the
                // same places as the forest build it is meant to dress.
                //
                // Altitude only BIASES the drift/scree split rather than hard-splitting it, so the
                // boundary reads as a gradient of exposure instead of a contour line. It is a pure
                // branch on an already-drawn number: no extra rand() call on either side.
                if (kind >= 1.0 - FlagShare)
                {
                    poleC[cell].Add(CI(pole, pos, rotQ, scale));
                    for (int f = 0; f < FlagCols.Length; f++)
                    {
                        float t = 0.45f + f * 0.14f; // strung up the top half of the pole
                        var fpos = pos + Vector3.up * (2.3f * (float)s * t);
                        flagC[f][cell].Add(CI(flag, fpos, rotQ, new Vector3(0.13f, 0.10f, 0.02f) * (float)s));
                    }
                }
                else if (kind < (y >= ScreeBiasHeight ? 0.35 : 0.75)) driftC[cell].Add(CI(drift, pos - Vector3.up * 0.06f, rotQ, scale));
                else screeC[cell].Add(CI(scree, pos - Vector3.up * 0.05f, rotQ, scale));
            }

            var driftMat = MeshUtil.Surface(DriftCol, 0.44f, ProcTex.SnowNormal, 0.8f, 1.2f);
            var screeMat = MeshUtil.Surface(ScreeCol, 0.10f, ProcTex.RockNormal, 1.0f, 1.6f);
            var poleMat = MeshUtil.Surface(MeshUtil.Rgb(0x6b5b47), 0.14f, ProcTex.BarkNormal, 0.8f, 1.2f);
            var flagMats = new Material[FlagCols.Length];
            for (int f = 0; f < FlagCols.Length; f++) flagMats[f] = MeshUtil.Lit(MeshUtil.Rgb(FlagCols[f]));

            for (int c = 0; c < cells; c++)
            {
                TrackUndergrowth(NewCombinedGo($"Drifts{c}", driftC[c], driftMat));
                TrackUndergrowth(NewCombinedGo($"Scree{c}", screeC[c], screeMat));
                TrackUndergrowth(NewCombinedGo($"FlagPoles{c}", poleC[c], poleMat));
                for (int f = 0; f < FlagCols.Length; f++)
                    TrackUndergrowth(NewCombinedGo($"Flags{f}_{c}", flagC[f][c], flagMats[f]));
            }
        }

        /// <summary>Lung-ta colours, in the traditional order (sky, air, fire, water, earth).</summary>
        private static readonly int[] FlagCols = { 0x2980b9, 0xecf0f1, 0xc0392b, 0x27ae60, 0xf1c40f };

        /// <summary>Share of undergrowth candidates that become a prayer-flag pole.</summary>
        private const double FlagShare = 0.03;

        /// <summary>Above this terrain height clutter skews to scree; below it, to snow drifts.</summary>
        private const float ScreeBiasHeight = 0.5f;

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
            // Packed and scuffed, so duller than the open snowpack it cuts through — the smoothness
            // difference is a second cue on top of the albedo one, and it survives at grazing angles
            // where albedo contrast washes out.
            var mat = MeshUtil.Surface(TrailCol, 0.22f, ProcTex.SnowNormal, 0.55f, 1f / 4f);
            // Local, NOT a field: Build() runs again on every reseed, and a counter that survived the
            // rebuild would hand each session a different trail-colour assignment for the same world.
            int pathIndex = 0;
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
                // World-space UVs again — a trail is a ribbon of packed snow lying on snow, so its
                // grain has to line up with the ground it sits a few centimetres above.
                var tuv = new Vector2[verts.Count];
                for (int i = 0; i < verts.Count; i++) tuv[i] = new Vector2(verts[i].x, verts[i].z);
                mesh.SetUVs(0, tuv);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                NewMeshGo("Trail", mesh, mat);
                BuildTrailMarkers(path, pathIndex++);
            }
        }

        /// <summary>
        /// Marker masts along a trail, every few waypoints.
        ///
        /// These do two jobs at once. They make the packed corridor findable from off it — the trail
        /// network is the only ground that isn't knee-deep, so being unable to locate it from 60 m
        /// away in the trees made an entire movement mechanic hard to actually use. And they give the
        /// open valley a grid of fixed reference points, which is the other half of the "everything
        /// looks the same" problem: the horizon range tells you which WAY you are facing, and these
        /// tell you where you are.
        ///
        /// Every mast on one trail carries the same identity colour, so a trail is followable by
        /// colour: you can tell you are still on the one you started on rather than a crossing path.
        /// </summary>
        private void BuildTrailMarkers(ForestPath path, int index)
        {
            // Every third waypoint. Paths step PathGen.StepLength (26 m), so that is a mast roughly
            // every 78 m — close enough to catch sight of the next one, far enough not to fence the
            // trail in with poles.
            for (int i = 2; i < path.Pts.Count; i += 3)
            {
                double px = path.Pts[i].X, pz = path.Pts[i].Z;
                // Set to one side of the lane so it never stands in the walking line.
                Vec2 prev = path.Pts[i - 1];
                double dx = px - prev.X, dz = pz - prev.Z;
                double len = System.Math.Sqrt(dx * dx + dz * dz);
                if (len < 1e-4) continue;
                double ox = -dz / len * (path.HalfWidth + 0.8);
                double oz = dx / len * (path.HalfWidth + 0.8);

                var at = new Vector3((float)(px + ox), (float)World.GetHeight(px + ox, pz + oz), (float)(pz + oz));
                var root = new GameObject("TrailMarker");
                root.transform.parent = transform;
                root.transform.position = at;
                BuildMarkerMast(root.transform, Vector3.zero, 5.2f, index % FlagCols.Length);
            }
        }

        // --- Props ---------------------------------------------------------------

        private void BuildLogs()
        {
            var mat = MeshUtil.Surface(LogCol, 0.14f, ProcTex.BarkNormal, 1.1f, 1.4f);
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

            // World-space UVs in metres, matching the terrain's convention so the two surfaces agree
            // on scale where the ice meets the shore.
            var uvs = new Vector2[verts.Length];
            for (int i = 0; i < verts.Length; i++) uvs[i] = new Vector2(verts[i].x, verts[i].z);

            var mesh = new Mesh();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            // Ice is the one genuinely smooth surface out here — high smoothness so the moon and any
            // flashlight streak across it, with pressure lines in the normal so it reads as a frozen
            // sheet under stress rather than a pane of glass. The emissive lift survives from the old
            // lake: it keeps the tarn findable at night, which is a gameplay job, not a look.
            NewMeshGo("Tarn", mesh, MeshUtil.Surface(
                LakeCol, smoothness: 0.78f,
                normal: ProcTex.IceNormal, normalScale: 0.55f, tiling: 0.12f,
                emission: MeshUtil.Rgb(0x1c4258), emissionIntensity: 0.9f));
            BuildPressureRidges(cx, cz, rx, rz);
        }

        /// <summary>
        /// Pressure ridges buckled up out of the tarn ice. Render-only garnish, but it earns its
        /// place: without it a frozen lake is a flat pale ellipse that reads as a hole in the
        /// terrain, and the ridges are what say "this is a surface" — which matters because the
        /// sim still slows you here (<c>Collision.LakeDepth</c>), now as slush and breaking crust
        /// rather than water.
        ///
        /// Its own RNG stream, like the undergrowth: nothing here may perturb the tree/collider
        /// lockstep (UNITY_PORT_NOTES 3c).
        /// </summary>
        private void BuildPressureRidges(float cx, float cz, float rx, float rz)
        {
            var rand = Rng.Mulberry32(World.Seed ^ 0x1ce_c01du);
            var mat = MeshUtil.Surface(MeshUtil.Rgb(0xbcd8e6), 0.62f, ProcTex.IceNormal, 0.7f, 0.8f);
            int count = 4 + (int)(rand() * 3); // 4..6
            for (int i = 0; i < count; i++)
            {
                double a = rand() * System.Math.PI * 2;
                double t = 0.25 + rand() * 0.55;           // keep them off the rim
                float x = cx + (float)(System.Math.Cos(a) * rx * t);
                float z = cz + (float)(System.Math.Sin(a) * rz * t);
                float len = 6f + (float)rand() * 14f;
                float yaw = (float)(rand() * 360.0);

                var go = new GameObject("PressureRidge");
                go.transform.parent = transform;
                go.transform.SetPositionAndRotation(
                    new Vector3(x, (float)World.GetHeight(x, z) + 0.10f, z),
                    Quaternion.Euler(0f, yaw, 0f));
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = MeshUtil.TaperedCylinder(0.55f, 0.12f, len, 3); // a low triangular spine
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                // Lay the spine along the ground rather than standing it on end.
                go.transform.Rotate(90f, 0f, 0f, Space.Self);
            }
        }

        /// <summary>Water height at a point: just over the ground, feathering to nothing at the rim.</summary>
        private static float SurfaceY(float x, float z, float t)
        {
            return (float)World.GetHeight(x, z) + Mathf.Lerp(0.16f, 0.02f, t);
        }

        /// <summary>
        /// The expedition basecamp — a plank hut and canvas tents where the RV used to be.
        ///
        /// Built on the SAME seeded transform (<c>WorldData.Rv</c>, whose sim-side field name is
        /// deliberately left alone) and the hut body deliberately fills the RV's old 6.6 x 2.5 x 2.3
        /// footprint, because that box is a real collider in the parity-locked sim. Change the body's
        /// size and players start colliding with a shape that isn't drawn.
        ///
        /// The lit window and porch lamp survive the re-theme unchanged: in a night game where the
        /// duffel is the only place proof becomes permanent, that warm glow is the one fixed beacon
        /// on the whole map, and the thing searchers navigate home by.
        /// </summary>
        private void BuildBasecamp()
        {
            var root = new GameObject("Basecamp");
            root.transform.SetPositionAndRotation(
                new Vector3((float)WorldData.Rv.X, (float)World.GetHeight(WorldData.Rv.X, WorldData.Rv.Z), (float)WorldData.Rv.Z),
                Quaternion.Euler(0f, (float)(WorldData.Rv.Ry * Mathf.Rad2Deg), 0f));
            root.transform.parent = transform;

            // Hut body — must stay within the collider box the sim owns.
            AddBox(root, "Hut", new Vector3(0, 1.5f, 0), new Vector3(6.6f, 2.5f, 2.3f), MeshUtil.Rgb(0x8a7a62));
            AddBox(root, "Sill", new Vector3(0, 1.0f, 0), new Vector3(6.65f, 0.35f, 2.32f), MeshUtil.Rgb(0x5f513f));

            // A PITCHED roof, replacing the flat slab that used to sit on top. Nobody builds a flat
            // roof where it snows, and more to the point the camp is the one silhouette every player
            // navigates home by — a plain rectangle reads as a placeholder from the moment you can
            // see it. Two canted slabs cost two boxes and give the whole basecamp a recognisable
            // outline against the sky. The roof overhangs the collider box, which is fine: the box is
            // what you bump into and the eaves are 2.9 m up, well over head height.
            for (int side = -1; side <= 1; side += 2)
            {
                var pitch = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(pitch.GetComponent<UnityEngine.Collider>());
                pitch.name = "Roof";
                pitch.transform.SetParent(root.transform, false);
                pitch.transform.localPosition = new Vector3(0f, 3.02f, side * 0.72f);
                pitch.transform.localRotation = Quaternion.Euler(side * 34f, 0f, 0f);
                pitch.transform.localScale = new Vector3(7.1f, 0.22f, 1.95f);
                pitch.GetComponent<MeshRenderer>().sharedMaterial =
                    MeshUtil.Surface(MeshUtil.Rgb(0xe8f0f5), 0.40f, ProcTex.SnowNormal, 0.7f, 1.6f);
            }
            AddBox(root, "RidgeBeam", new Vector3(0, 3.58f, 0), new Vector3(7.2f, 0.16f, 0.22f), MeshUtil.Rgb(0x5f513f));

            AddBox(root, "Window", new Vector3(1.6f, 1.9f, 0), new Vector3(1.6f, 0.7f, 2.34f), MeshUtil.Rgb(0xffd98a), emissive: MeshUtil.Rgb(0xffb24d), glow: 1.4f);

            // Two A-frame tents in expedition orange, pitched clear of the hut's collider box.
            for (int i = -1; i <= 1; i += 2)
            {
                var tent = new GameObject("Tent");
                tent.transform.parent = root.transform;
                tent.transform.localPosition = new Vector3(i * 4.9f, 0f, i * 1.4f);
                tent.transform.localRotation = Quaternion.Euler(0f, i * 24f, 0f);
                var mf = tent.AddComponent<MeshFilter>();
                mf.sharedMesh = MeshUtil.Cone(1.5f, 1.9f, 4); // 4 segments = a pitched A-frame
                tent.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Surface(MeshUtil.Rgb(0xc7563c), 0.22f, ProcTex.FabricNormal, 0.7f, 3f);
            }

            AddBox(root, "Crate1", new Vector3(-2.4f, 0.35f, 1.9f), new Vector3(0.9f, 0.7f, 0.9f), MeshUtil.Rgb(0x6f6250));
            AddBox(root, "Crate2", new Vector3(-1.4f, 0.28f, 2.1f), new Vector3(0.7f, 0.55f, 0.7f), MeshUtil.Rgb(0x7d6f5b));

            // The tallest mast in the valley, over the one place searchers have to get back to.
            // The camp's lamps are the brightest things on the map and still vanish into the fog by
            // ~120 m; a 10.5 m mast clears the treeline and is visible as a silhouette from anywhere
            // with a sightline, which is what "navigate home" actually needs.
            BuildMarkerMast(root.transform, new Vector3(-3.2f, 0f, 2.6f), 10.5f, 1);

            var lamp = new GameObject("PorchLamp").AddComponent<Light>();
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
        /// because Yeti's whole fast-travel network hangs off recognising these.
        /// </summary>
        /// <summary>
        /// The evidence duffel: a canvas haul bag on a tarp beside the basecamp, lit by its own lamp so it
        /// reads as a destination from across the clearing. Purely a landmark — the deposit rule is
        /// server-side (GameManager.TryDeposit) and Yeti can do nothing to it.
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
            tarp.AddComponent<MeshRenderer>().sharedMaterial = MeshUtil.Surface(MeshUtil.Rgb(0x3a4650), 0.25f, ProcTex.FabricNormal, 0.6f, 2f);

            // The bag: a rounded body with end caps and a strap.
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Object.Destroy(body.GetComponent<UnityEngine.Collider>());
            body.name = "Bag";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.34f, 0f);
            body.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            body.transform.localScale = new Vector3(0.62f, 0.72f, 0.62f);
            body.GetComponent<MeshRenderer>().sharedMaterial = MeshUtil.Surface(MeshUtil.Rgb(0xb8552f), 0.28f, ProcTex.FabricNormal, 0.8f, 2.5f);

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
            // Glacier ice, not granite: the network is now a system of crevasses cut into the
            // icefall. Only the materials and the display names change — the sim's Caves API, the
            // seeded positions and the whole fast-travel rule are untouched.
            var rock = MeshUtil.Surface(MeshUtil.Rgb(0x8fb6c9), 0.55f, ProcTex.IceNormal, 0.9f, 1.2f);
            var darkRock = MeshUtil.Surface(MeshUtil.Rgb(0x4a6a80), 0.48f, ProcTex.IceNormal, 1.0f, 1.4f);
            // Near-black, unlit-looking interior so the opening reads as depth rather than a surface.
            var voidMat = MeshUtil.Lit(MeshUtil.Rgb(0x06121c));

            int caveIndex = 0;
            foreach (var cave in World.Caves)
            {
                double dl = System.Math.Sqrt(cave.X * cave.X + cave.Z * cave.Z);
                if (dl == 0) dl = 1;
                double dx = -cave.X / dl, dz = -cave.Z / dl; // toward map centre = the way the mouth faces
                double px = -dz, pz = dx;                    // sideways across the mouth
                float baseY = (float)World.GetHeight(cave.X, cave.Z);
                var centre = new Vector3((float)cave.X, baseY, (float)cave.Z);
                var faceRot = Quaternion.LookRotation(new Vector3((float)dx, 0f, (float)dz), Vector3.up);

                var root = new GameObject("Crevasse");
                root.transform.parent = transform;
                root.transform.SetPositionAndRotation(centre, faceRot);

                // The icefall the crevasse is cut into — irregular now rather than a stretched sphere,
                // which is what it read as from any angle that showed its outline against the sky.
                var mound = NewMeshGo("Mound", MeshUtil.Rock(1f, 9, 14, caveIndex + 3), rock);
                mound.transform.SetParent(root.transform, false);
                mound.transform.localPosition = new Vector3(0f, 1.1f, -3.4f);
                mound.transform.localScale = new Vector3(13f, 7.2f, 11f);

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

                // --- this crevasse's own identity ---------------------------------------------
                //
                // Every mouth used to be built identically, which made the fast-travel network a set
                // of interchangeable grey lumps: you could recognise "a crevasse" instantly and never
                // tell WHICH one, so nobody could say where they were and the map was the only way to
                // know. A marker mast in one of five colours turns each into a place with a name —
                // "the red mouth" — which is what a landmark actually is. The colours are the lung-ta
                // five, so the world's existing visual language covers it rather than needing a new
                // one, and the mast is tall enough to clear the treeline and be picked out at range.
                int ident = caveIndex % FlagCols.Length;
                Color identCol = MeshUtil.Rgb(FlagCols[ident]);
                BuildMarkerMast(root.transform, new Vector3(0f, 0f, 4.6f), 7.5f, ident);

                // The throat's glow carries the same colour, so the identity still reads at night
                // when the mast is only a silhouette. Blended well toward the original cold blue —
                // this is a cue, not a disco.
                var glow = new GameObject("CrevasseGlow").AddComponent<Light>();
                glow.transform.parent = root.transform;
                glow.transform.localPosition = new Vector3(0f, 1.4f, 1.6f);
                glow.type = LightType.Point;
                glow.color = Color.Lerp(MeshUtil.Rgb(0x7fc0e8), identCol, 0.35f);
                glow.range = 16f;
                glow.intensity = 2.0f;
                caveIndex++;
            }
        }

        /// <summary>
        /// A tall marker mast strung with prayer flags — the game's navigation beacon.
        ///
        /// Deliberately THIN (7 cm), which is what makes it defensible as render-only geometry in a
        /// world where collision lives in the shared sim and this pole is invisible to it. The
        /// undergrowth rule is that anything tall enough to hide a player must be a real collider
        /// (see BuildUndergrowth); a mast is tall but you cannot hide behind a broom handle, and
        /// walking through one is a far smaller sin than having nothing on the map to steer by.
        ///
        /// <paramref name="ident"/> selects the flag colour that runs top-down from that index, so a
        /// mast reads as "the red one" from a distance and as a full lung-ta string up close.
        /// </summary>
        private void BuildMarkerMast(Transform parent, Vector3 localPos, float height, int ident)
        {
            var mast = NewMeshGo("MarkerMast", MeshUtil.TaperedCylinder(0.075f, 0.05f, height, 5),
                MeshUtil.Surface(MeshUtil.Rgb(0x6b5b47), 0.14f, ProcTex.BarkNormal, 0.8f, 1.2f));
            mast.transform.SetParent(parent, false);
            mast.transform.localPosition = localPos;

            // Flags up the top two-thirds, starting on this mast's identity colour so the dominant
            // colour at the top is the one that names it.
            for (int f = 0; f < 6; f++)
            {
                var col = MeshUtil.Rgb(FlagCols[(ident + f) % FlagCols.Length]);
                var flag = NewMeshGo("MastFlag", MeshUtil.UnitCube(), MeshUtil.Emissive(col, col, 0.35f));
                flag.transform.SetParent(parent, false);
                flag.transform.localPosition = localPos + Vector3.up * (height * (0.94f - f * 0.10f));
                flag.transform.localRotation = Quaternion.Euler(0f, f * 26f, 0f);
                flag.transform.localScale = new Vector3(0.55f, 0.36f, 0.03f);
            }
        }

        /// <summary>
        /// A boulder. Irregular now rather than a scaled sphere — see <see cref="MeshUtil.Rock"/> for
        /// why that mattered more than it sounds. Rotated off-axis too, because a flattened sphere
        /// sitting perfectly level is still recognisably a flattened sphere however lumpy its surface.
        /// </summary>
        private void Boulder(Material rock, double x, double z, double r)
        {
            // Hashed from the position, so a given world always gets the same rocks and no RNG stream
            // is touched (UNITY_PORT_NOTES §3c).
            int variant = Mathf.Abs((int)(x * 73.3 + z * 149.7)) % 8;
            var go = NewMeshGo("Boulder", MeshUtil.Rock((float)r, 7, 10, variant), rock);
            float y = (float)World.GetHeight(x, z);
            go.transform.position = new Vector3((float)x, y + (float)r * 0.55f, (float)z);
            go.transform.rotation = Quaternion.Euler(variant * 7f - 24f, variant * 43f, variant * 5f - 18f);
            // Kept squat: these read as half-buried, which is what a rock in snow looks like.
            go.transform.localScale = new Vector3(1.15f, 0.82f, 1.15f);
        }

        // --- the lookout tower + its ladder (searchers climb it; binoculars live up top) ------------
        //
        // The tower collider (WorldData) is climbable at ClimbH = 9.5, so the shared sim already holds
        // a player standing on top at base+9.5 (GroundHeightAt) and stops pushing them out of its
        // footprint up there — for ANY role, no parity change needed. The platform MESH is aligned to
        // that same 9.5 so a searcher's feet land on the boards, not inside them. All that was missing
        // was a way UP for a searcher (Yeti scales it; searchers can't), which the ladder provides
        // as a client-side climb (HPPlayer) — see LadderXZ / LadderTopY below.

        private const float TowerClimbH = 9.5f; // MUST equal WorldData.Lookout's collider ClimbH

        /// <summary>Ladder line in world XZ — the searcher pins to this while climbing.</summary>
        public static Vector2 LadderXZ { get; private set; }
        /// <summary>Ground height at the ladder foot.</summary>
        public static float LadderBottomY { get; private set; }
        /// <summary>Feet height at the top of the ladder = the platform surface (tower base + ClimbH).</summary>
        public static float LadderTopY { get; private set; }
        /// <summary>How close (XZ) to the ladder line a searcher must be to mount.</summary>
        public const float LadderReach = 1.8f;

        private void BuildTower()
        {
            var root = new GameObject("Lookout");
            float baseY = (float)World.GetHeight(WorldData.Lookout.X, WorldData.Lookout.Z);
            var towerXZ = new Vector2((float)WorldData.Lookout.X, (float)WorldData.Lookout.Z);
            root.transform.position = new Vector3(towerXZ.x, baseY, towerXZ.y);
            root.transform.parent = transform;
            var wood = MeshUtil.Surface(MeshUtil.Rgb(0x5a5148), 0.12f, ProcTex.BarkNormal, 1.0f, 1.2f); // grey-weathered timber
            Mesh post = MeshUtil.TaperedCylinder(0.22f, 0.18f, 10f, 5);
            foreach (var off in new[] { new Vector2(-1.4f, -1.4f), new Vector2(1.4f, -1.4f), new Vector2(-1.4f, 1.4f), new Vector2(1.4f, 1.4f) })
            {
                var leg = NewMeshGo("Leg", post, wood);
                leg.transform.parent = root.transform;
                leg.transform.localPosition = new Vector3(off.x, 0f, off.y);
            }
            // Platform TOP aligned to the sim's climb height, so feet stand ON the boards.
            AddBox(root, "Platform", new Vector3(0, TowerClimbH - 0.175f, 0), new Vector3(3.6f, 0.35f, 3.6f), MeshUtil.Rgb(0x7a5a3a));
            // A low railing so the top reads as a place you stand rather than a diving board (render only).
            foreach (var e in new[] { new Vector3(0, 0, 1.7f), new Vector3(0, 0, -1.7f), new Vector3(1.7f, 0, 0), new Vector3(-1.7f, 0, 0) })
            {
                bool alongX = Mathf.Abs(e.z) > 0.1f;
                AddBox(root, "Rail", new Vector3(e.x, TowerClimbH + 0.55f, e.z),
                    alongX ? new Vector3(3.6f, 0.1f, 0.1f) : new Vector3(0.1f, 0.1f, 3.6f), MeshUtil.Rgb(0x6a4a2c));
            }

            var lamp = new GameObject("TowerLamp").AddComponent<Light>();
            lamp.transform.parent = root.transform;
            lamp.transform.localPosition = new Vector3(0, TowerClimbH + 0.9f, 0);
            lamp.type = LightType.Point;
            lamp.color = MeshUtil.Rgb(0xffb060);
            lamp.range = 30f;
            lamp.intensity = 1.6f;

            // Ladder on the face toward map centre (the side searchers approach from). Its line sits
            // just outside the collider so the foot is on open ground; the rails+rungs are render-only.
            Vector2 toCentre = (-towerXZ).normalized;
            if (toCentre.sqrMagnitude < 0.01f) toCentre = Vector2.down;
            float faceR = (float)WorldData.Lookout.R + 0.25f;
            LadderXZ = towerXZ + toCentre * faceR;
            LadderBottomY = (float)World.GetHeight(LadderXZ.x, LadderXZ.y);
            LadderTopY = baseY + TowerClimbH;
            BuildLadderMesh(root, baseY, towerXZ, toCentre, faceR);
        }

        private void BuildLadderMesh(GameObject root, float baseY, Vector2 towerXZ, Vector2 toCentre, float faceR)
        {
            var wood = MeshUtil.Surface(MeshUtil.Rgb(0x5a3f24), 0.12f, ProcTex.BarkNormal, 1.0f, 1.2f);
            float topLocalY = TowerClimbH; // ladder runs from ground to the platform surface
            Vector2 side = new Vector2(-toCentre.y, toCentre.x); // perpendicular, for the two rails
            var localFace = new Vector3(toCentre.x * faceR, 0, toCentre.y * faceR); // relative to root

            // Two vertical rails.
            foreach (float s in new[] { -0.32f, 0.32f })
            {
                var rail = NewMeshGo("LadderRail", MeshUtil.TaperedCylinder(0.06f, 0.06f, topLocalY, 4), wood);
                rail.transform.parent = root.transform;
                rail.transform.localPosition = localFace + new Vector3(side.x * s, 0, side.y * s);
            }
            // Rungs every 0.5 m.
            Mesh rung = MeshUtil.TaperedCylinder(0.05f, 0.05f, 0.64f, 4);
            for (float h = 0.4f; h < topLocalY; h += 0.5f)
            {
                var r = NewMeshGo("Rung", rung, wood);
                r.transform.parent = root.transform;
                r.transform.localPosition = localFace + new Vector3(0, h, 0);
                // lay it flat, spanning the two rails
                r.transform.localRotation = Quaternion.LookRotation(new Vector3(toCentre.x, 0, toCentre.y)) * Quaternion.Euler(0, 90, 90);
            }
        }

        private void BuildCamp()
        {
            var rock = MeshUtil.Surface(MeshUtil.Rgb(0x3a3a3a), 0.10f, ProcTex.RockNormal, 1.1f, 1.5f);
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

        /// <summary>
        /// Small seeded wobble on the rise bearing (radians), so sessions aren't identical — real
        /// moonrise wanders along the horizon through the year. Deliberately kept to ±10°: it must
        /// never be large enough to disturb the east→west track, and it derives from the replicated
        /// world seed, so every player sees the moon in the same place.
        /// </summary>
        private float _moonRiseAz;

        /// <summary>
        /// The moon's three nights. It WANES and rides lower, but it is never gone — every night ends
        /// with the moon still in the sky (owner's call, 2026-07-20).
        ///
        /// That constraint shapes the whole model: the moon must never finish its rise→set arc inside
        /// a night, so `ArcStart + MoonArcRate` stays below 1 for all three. Escalation then comes
        /// from PHASE, ALTITUDE and BRIGHTNESS instead of from the moon leaving — night 3 is a low,
        /// half-lit moon at 0.24 against night 1's high full moon at 0.42.
        ///
        /// This is a difficulty dial, not decoration: moonlight is the only thing that lets searchers
        /// cross the forest without burning flashlight battery, and battery drain is *already*
        /// escalated per night. Taking the moon away entirely stacked a blackout on top of that;
        /// dimming it to ~57% is the same pressure without ever making the map unreadable.
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
            public float Light;     // directional intensity at the top of the arc
        }

        private static readonly MoonNight[] MoonNights =
        {
            // Light values raised ~30% in the legibility pass, keeping the same night-to-night RATIO
            // (0.55 / 0.45 / 0.33 is the old 0.42 / 0.34 / 0.24 scaled) so the escalation curve the
            // difficulty was tuned against is untouched — only the floor moved.
            new MoonNight { Phase = -0.90f, ArcStart = 0.00f, PeakElev = 68f, Light = 0.55f },
            new MoonNight { Phase = -0.35f, ArcStart = 0.12f, PeakElev = 60f, Light = 0.45f },
            new MoonNight { Phase = 0.05f, ArcStart = 0.24f, PeakElev = 52f, Light = 0.33f },
        };

        /// <summary>
        /// Arc fraction covered over one whole night — the moon's angular speed, identical every night
        /// (only the starting point differs, so no night's sky appears to run faster than another's).
        ///
        /// Held so that `ArcStart + MoonArcRate` &lt; 1 for every night: the moon must never reach the
        /// end of its arc while the night is still running, because no night is allowed to go
        /// moonless. Night 3 is the binding case at 0.24 + 0.66 = 0.90 — low and sinking by dawn,
        /// but still up.
        /// </summary>
        private const float MoonArcRate = 0.66f;

        /// <summary>Elevation floor during the traverse — the "shallow drift". Below roughly this the
        /// moon rakes the 14 m hills and throws stretched shadows that read as a bug.</summary>
        private const float MoonMinElev = 35f;
        /// <summary>
        /// Bearing of due EAST in this sim's azimuth convention, where
        /// `dir = (cos e · sin a, sin e, cos e · cos a)` gives a=0 → +Z, a=90° → +X.
        ///
        /// **Read the map's compass before trusting your instincts here.** `MapView.ToMap` MIRRORS
        /// the x axis to match the sim's handedness (see §2), and its compass labels put **W at
        /// world +X and E at world −X** — the opposite of the usual assumption. North is −Z. So east
        /// is a = 270°, and the moon runs 270° → 360°(south) → 450°(west). Get this backwards and the
        /// moon rises in the west, which is the classic internally-consistent-but-wrong bug.
        /// </summary>
        private const float MoonAzEastDeg = 270f;
        /// <summary>Degrees swept across a full arc: east → south → west, the northern-sky path.</summary>
        private const float MoonSweepDeg = 180f;
        /// <summary>How much a low moon dims versus one overhead — atmospheric extinction, roughly.
        /// This is the only altitude-driven dimming now; it never reaches zero.</summary>
        private const float MoonLowDim = 0.72f;

        /// <summary>
        /// Moon direction + its normalised ALTITUDE (0 at the ends of the arc, 1 overhead) for a
        /// night and clock. Altitude never reaches 0 during a night — see <see cref="MoonArcRate"/>.
        /// </summary>
        private void MoonAt(int night, float tod, out Vector3 dir, out float alt, out MoonNight cfg)
        {
            cfg = MoonNights[Mathf.Clamp(night - 1, 0, MoonNights.Length - 1)];

            // One shared angular rate; only the STARTING point differs per night.
            float q = Mathf.Clamp01(cfg.ArcStart + tod * MoonArcRate);

            // East at q=0, west at q=1, through the southern sky. Every night runs the same direction;
            // only how far along it starts differs, so the moon always tracks E→W for every player.
            float az = _moonRiseAz + Mathf.Deg2Rad * (MoonAzEastDeg + q * MoonSweepDeg);
            // sin() arc: lowest at both ends, peak mid-arc. Never dips below MoonMinElev, so the moon
            // can't rake the hills and throw stretched shadows across the whole map.
            alt = Mathf.Sin(q * Mathf.PI);
            float elev = Mathf.Deg2Rad * Mathf.Lerp(MoonMinElev, cfg.PeakElev, alt);

            dir = new Vector3(
                Mathf.Cos(elev) * Mathf.Sin(az),
                Mathf.Sin(elev),
                Mathf.Cos(elev) * Mathf.Cos(az)).normalized;
        }

        private void BuildLighting()
        {
            var rand = Rng.Mulberry32(World.Seed ^ 0x11007a11u);
            _moonRiseAz = Mathf.Deg2Rad * (float)((rand() * 2.0 - 1.0) * 10.0);

            var moonGo = new GameObject("Moon");
            moonGo.transform.parent = transform;
            // Point the light FROM the moon, so the shadows on the ground agree with the disc the
            // skybox draws. These were unrelated before — there was no disc to disagree with.
            moonGo.transform.rotation = Quaternion.LookRotation(-_moonDir, Vector3.up);
            _moon = moonGo.AddComponent<Light>();
            _moon.type = LightType.Directional;
            _moon.color = MeshUtil.Rgb(0xb4c6ff);
            _moon.intensity = 0.40f; // snowpack throws moonlight back; the forest floor ate it
            // Soft shadows now, on the quality tiers that can afford them (HPQuality decides). Hard
            // shadows were defensible over a dark fogged forest floor; over open snowpack they are
            // the single most obvious "this is a game" tell, because a real shadow on snow has a soft
            // penumbra and bounces light back up into itself.
            _moon.shadows = LightShadows.Soft;
            _moon.shadowStrength = 0.72f; // snow bounce fills shadows; a black shadow reads as a hole
            MoonLight = _moon;
            HPQuality.ApplyShadowQuality(); // the tier decides; a reseed must not silently reset it

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;

            // Trilight, not Flat. Flat ambient lights every surface identically from every direction,
            // which is precisely the look that flattens geometry into cardboard. Snow is lit almost
            // entirely by the sky dome and by bounce off itself, so a sky/equator/ground gradient —
            // cold from above, brighter from below than you would expect — is both cheaper than any
            // GI solution and much closer to how the real thing is lit. The three colours are driven
            // per-phase in SetTimeOfDay.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;

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
            var shader = Shader.Find("Metoh/NightSky");
            if (shader == null)
            {
                // Don't fail silently into a black void — this is exactly the "menu button that does
                // nothing" failure mode from §4. Keep the old flat fill and say why.
                Debug.LogWarning("[WorldBuilder] Metoh/NightSky shader not found — " +
                                 "falling back to a flat sky. Is Shaders/NightSky.shader imported?");
                _skyMat = null;
                return;
            }

            _skyMat = new Material(shader);

            // The horizon range is SEEDED FROM THE WORLD, which makes it a real landmark rather than
            // wallpaper: every client in a session sees the same peaks in the same compass directions
            // (the seed is replicated), so "regroup under the notch" means the same thing to all six
            // players — while a different session gets a different skyline and can't be navigated from
            // memory. Set here rather than per-frame because it only changes when the world does.
            _skyMat.SetFloat(SkyRidgeSeedId, (World.Seed % 100000u) / 1000f);

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
            // Re-themed for altitude: every key shifted blue, and fog density down ~10% because
            // snowpack bounces moonlight instead of swallowing it like forest floor, with stars
            // lifted at dusk/dawn since thin cold air holds far less haze than the valley did.
            // NightSky.shader needs no edit — it is driven entirely from these keys.
            //
            // AMBIENT AND MOON RAISED ~40% / ~25% in the legibility pass. This is a deliberate
            // difficulty change and not only a look: moonlight is the only thing that lets a searcher
            // cross the valley without burning battery, so this loosens the night a little. It was
            // still the right call — the previous levels were dark enough that the terrain read as a
            // uniform black-blue field, which meant every one of the contrast cues below (rock, basin,
            // trail) was invisible no matter how well separated in albedo it was. There is no point
            // owning a palette you cannot see. If night 3 now feels too survivable, take it back out
            // of MoonNights[2].Light rather than out of ambient: losing the moon is the escalation the
            // design already has, while flat ambient is what makes geometry read as cardboard.
            new SkyKey(0.00f, 0x354060, 0x3e4257, 0x6a5d78, 0.00675f, 0.38f, 0.18f), // dusk
            new SkyKey(0.25f, 0x121c33, 0x162336, 0x323e56, 0.00900f, 0.48f, 0.72f), // nightfall
            new SkyKey(0.60f, 0x0a1220, 0x0b1526, 0x232f45, 0.01125f, 0.53f, 1.00f), // deep night
            new SkyKey(0.88f, 0x121c33, 0x172438, 0x323e56, 0.00945f, 0.45f, 0.76f), // pre-dawn
            new SkyKey(1.00f, 0x445068, 0x4e5566, 0x736a84, 0.00720f, 0.33f, 0.18f), // dawn
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
            MoonAt(night, t, out _moonDir, out float moonAlt, out MoonNight moonCfg);

            // Palette `moon` is the shape across a night (dimmer at dusk/dawn); the night's own Light
            // scales that whole shape; altitude dims it modestly when it rides low. No term here can
            // reach zero — the moon is always up, so it is always lighting something.
            float lit = moon * (moonCfg.Light / MoonNights[0].Light) * Mathf.Lerp(MoonLowDim, 1f, moonAlt);
            // Trilight ambient. The GROUND term is the one that matters here and it is deliberately
            // the brightest of the three: standing on snowpack under a moon, a startling amount of
            // the light reaching your face has bounced UP off the ground. Lighting a snow scene with
            // sky-only ambient is what makes it look like grey plastic — the undersides of every
            // branch, ledge and figure go dead, which never happens over snow. The ground colour is
            // tinted toward the snowpack albedo so the bounce carries the right hue.
            RenderSettings.ambientLight = ambient;                       // sky term (Trilight reads this)
            RenderSettings.ambientSkyColor = ambient;
            RenderSettings.ambientEquatorColor = ambient * 1.15f;
            RenderSettings.ambientGroundColor = Color.Lerp(ambient, GroundCol, 0.45f) * AmbientBounce;

            if (_moon != null)
            {
                _moon.intensity = lit;
                _moon.transform.rotation = Quaternion.LookRotation(-_moonDir, Vector3.up);
                // Shadow quality is HPQuality's call (it knows the tier), not this per-frame path's.
                // This line used to hard-code LightShadows.Hard and silently undo anything set at
                // build time — a setting that is re-applied every frame can never be configured.
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
                // Moonwash: a bright full moon drowns the fainter stars, a low half-moon doesn't.
                // So night 3 trades moonlight for a visibly better sky — the escalation still has a
                // payoff even though the moon never actually leaves.
                // Moonwash: a bright full moon drowns the fainter stars, a low half-moon doesn't.
                // The 0.18..0.53 window tracks the raised moon levels — it used to be 0.14..0.42, and
                // leaving it there after lifting the moon would have pinned every night at full wash,
                // silently deleting night 3's darker-sky payoff.
                float moonWash = Mathf.InverseLerp(0.18f, 0.53f, lit);
                float stars = Mathf.Lerp(a.Stars, b.Stars, k) * Mathf.Lerp(1.40f, 1f, moonWash);
                _skyMat.SetFloat(SkyStarsId, stars * (TitleMode ? 1.25f : 1f));
                _skyMat.SetVector(SkyMoonDirId, _moonDir);
                _skyMat.SetFloat(SkyMoonPhaseId, moonCfg.Phase);
                _skyMat.SetFloat(SkyMoonBrightId, Mathf.Lerp(1.7f, 3.2f, moonWash));
                _skyMat.SetFloat(SkyMoonGlowId, 0.8f);

                // The distant range shares the palette, so it can never disagree with the air in
                // front of it. Rock tracks the sky colour (kept dark — it is a silhouette first and a
                // surface second); its snowfields track the moon, which is what makes the skyline
                // visibly brighter on night 1 than on night 3.
                _skyMat.SetColor(SkyRidgeColId, sky * 0.62f);
                _skyMat.SetColor(SkyRidgeSnowId,
                    Color.Lerp(sky, GroundCol, 0.60f) * Mathf.Lerp(0.55f, 1.05f, moonWash));
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
        private static readonly int SkyRidgeSeedId = Shader.PropertyToID("_RidgeSeed");
        private static readonly int SkyRidgeColId = Shader.PropertyToID("_RidgeColor");
        private static readonly int SkyRidgeSnowId = Shader.PropertyToID("_RidgeSnowColor");

        // --- helpers ----------------------------------------------------------------

        private static CombineInstance CI(Mesh mesh, Vector3 pos, Quaternion rot, Vector3 scale)
        {
            return new CombineInstance { mesh = mesh, transform = Matrix4x4.TRS(pos, rot, scale) };
        }

        /// <summary>
        /// Nudge a palette colour deterministically per forest chunk, so neighbouring stands differ.
        ///
        /// Uses its own hash of the cell index rather than any RNG stream — this must never touch the
        /// tree/collider lockstep (UNITY_PORT_NOTES §3c), and being a pure function of the index means
        /// it is stable across a rebuild without needing a stream at all. Value-only: hue is left
        /// alone so the palette still reads as one deliberate scheme rather than as noise.
        /// </summary>
        private static Color TintByCell(Color c, int cell, float amount)
        {
            uint h = (uint)cell * 2654435761u;
            h ^= h >> 15;
            float t = (h & 0xffff) / 65535f * 2f - 1f; // -1..1
            float k = 1f + t * amount;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
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
            // Everything AddBox builds is a made object — hut planks, crates, the tower platform and
            // rails — so they all get sawn-timber grain. A lit window is the exception: emission is
            // the whole point of it and surface detail would only fight the glow.
            go.GetComponent<MeshRenderer>().sharedMaterial = emissive.HasValue
                ? MeshUtil.Emissive(color, emissive.Value, glow)
                : MeshUtil.Surface(color, 0.13f, ProcTex.BarkNormal, 0.85f, 1.4f);
        }
    }
}
