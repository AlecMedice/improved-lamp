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

        /// <summary>
        /// Re-apply the sky/fog/light palette right now, ignoring the per-frame early-out.
        ///
        /// This used to only clear `_lastTod` and leave the actual re-apply to whoever called
        /// SetTimeOfDay next — which meant that every caller (TitleMode, FogMul, a reseed) was
        /// silently depending on GameManager's Update to run before anything was on screen. It
        /// usually did, so this looked fine; when it didn't, the change simply never landed and the
        /// world stayed lit for whatever state it was built in. A method whose whole job is
        /// "re-apply now" should not be a request that something else does it later.
        /// </summary>
        public void InvalidatePalette()
        {
            _lastTod = -1f;
            SetTimeOfDay(_appliedTod, _appliedNight);
        }

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
        /// How far the trodden channel sinks below the terrain, and how high the shoulders heap.
        ///
        /// VISUAL ONLY, and deliberately small. Terrain height lives in the parity-locked sim
        /// (Terrain.GetHeight) and that is what players actually stand on, so any channel deep enough
        /// to notice would read as a mismatch between where your feet are and what you can see. A few
        /// centimetres is all the relief a raking torch beam needs — the goal is an edge that catches
        /// light, not a trench.
        /// </summary>
        private const float TrailSinkDepth = 0.05f;
        private const float TrailBermHeight = 0.11f;

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
            // EVERYTHING IN THIS METHOD RUNS BEFORE THE FIRST FRAME IS PRESENTED, so its total is
            // exactly how long the window sits on a stale image before the title screen appears —
            // and on the first run after a script recompile it is at its worst, because a domain
            // reload throws away every static cache (ProcTex's normal maps, the audio cues) and the
            // shader variants have to compile on first use as well. Timed and logged per stage
            // because "startup is slow" is unactionable and "the bake is 4 of the 6 seconds" is not.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Instance = this;
            EnsureWorld();
            long tWorld = clock.ElapsedMilliseconds;
            Build();
            long tBuild = clock.ElapsedMilliseconds;
            // NOT BuildNavMesh(). Everything in this Awake runs before the first frame is ever
            // presented, so every millisecond spent here is a millisecond the window sits on a stale
            // image with no title screen on it — and a runtime bake over ~2,400 tree meshes is the
            // single largest item on that list. Nothing needs it until a CPU bot exists, which
            // requires a host, which requires somebody to click a button on the title screen that
            // cannot be drawn yet. So it is deferred to EnsureNavMesh.
            PostFX.Ensure(gameObject);
            // Before anything connects, so the TITLE cinematic flies through the same weather the match
            // does — the systems follow Camera.main, not a player.
            Weather.Ensure(gameObject);
            long tPost = clock.ElapsedMilliseconds;
            HPAudio.Ensure(gameObject); // synthesizes every cue + starts the wind/tarn beds
            long tAudio = clock.ElapsedMilliseconds;
            HPDebug.Ensure(gameObject); // F3 diagnostics overlay (costs nothing while hidden)

            // Title mode is set by TitleMenu on its first Update, which is after this — so the very
            // first palette the world gets is the gameplay one, and the menu's brighter backdrop
            // arrives a frame later. Applying it here as well would mean guessing at whether a menu
            // is coming; TitleMenu.SetTitleLighting re-applies immediately when it decides.
            SetTimeOfDay(0f);

            Debug.Log($"[boot] world {tWorld} ms, geometry {tBuild - tWorld} ms, post+weather {tPost - tBuild} ms, " +
                      $"audio {tAudio - tPost} ms, total {clock.ElapsedMilliseconds} ms before the first frame" +
                      " (navmesh bake deferred to the first CPU bot).");
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
        /// <summary>
        /// Set whenever the geometry changes, cleared by a bake. The bake is on-demand now, so
        /// something has to remember that the surface is stale.
        /// </summary>
        private bool _navDirty = true;

        /// <summary>
        /// Bake the bot navigation surface if the world has changed since the last bake.
        ///
        /// Called by the host immediately before it spawns CPU players — the only thing in the game
        /// that reads a NavMesh. Deliberately NOT called on plain clients: they were baking a surface
        /// only the host would ever query, paying the whole cost for nothing.
        ///
        /// It is safe to be late. Both bots plan with <c>NavMesh.CalculatePath</c> and refine wander
        /// goals with <c>SamplePosition</c>, and both fall back to beeline steering when the query
        /// fails, so the worst case of baking a moment after a bot exists is a few seconds of coarser
        /// routing — never walking through geometry, which the shared sim's collision owns.
        /// </summary>
        public static void EnsureNavMesh()
        {
            if (Instance == null || !Instance._navDirty) return;
            Instance.BuildNavMesh();
        }

        private void BuildNavMesh()
        {
            _navDirty = false;
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
            BootReport.Reset(); // the rebuild re-tests every shader lookup; don't carry stale news
            Build();
            // The world moved, so any baked surface is stale. Marked, not re-baked: a reseed lands on
            // every client, including the four that will never run a bot, and the next bot to wake up
            // asks for the bake itself. (The seed is rolled once per hosting session, before any bot
            // exists, so "a reseed while bots are already running" is not a case that can arise today.
            // If per-match reseeding ever ships, this is the line that has to grow a re-bake.)
            _navDirty = true;
            InvalidatePalette();      // _lastTod would otherwise early-out and leave the new moon unlit
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
        /// second lever in the [perf] perf order, after bloom.</summary>
        public void SetPropLightsEnabled(bool on)
        {
            foreach (var l in _propLights) if (l != null) l.enabled = on;
        }

        public int PropLightCount => _propLights.Count;
        public int UndergrowthMeshCount => _undergrowthRenderers.Count;

        // Shared assets for the repeated props, rebuilt per world.
        //
        // BuildMarkerMast and AddBox used to mint a fresh Material AND a fresh Mesh for every single
        // instance. Masts alone are ~13 per trail plus one per crevasse, each carrying a pole and six
        // flags — call it 450 materials and 450 meshes, on top of the 192 the per-chunk forest tinting
        // already spends. That is several times the "~200 per rebuild" ReleaseWorldMaterials was
        // written against, and the meshes are not swept at all. Every one of them is identical bar the
        // colour, so they cache: keyed per build, cleared in Build(), which keeps a reseed honest and
        // leaves the materials on renderers where the sweep can still find them.
        private readonly Dictionary<(Color, Color?, float), Material> _boxMats =
            new Dictionary<(Color, Color?, float), Material>();
        private readonly Dictionary<Vector3, Mesh> _boxMeshes = new Dictionary<Vector3, Mesh>();
        private readonly Dictionary<float, Mesh> _mastMeshes = new Dictionary<float, Mesh>();
        private Material _mastMat;
        private Mesh _mastFlagMesh;
        private Material[] _mastFlagMats;

        private void Build()
        {
            _undergrowthRenderers.Clear();
            _propLights.Clear();
            _boxMats.Clear();
            _boxMeshes.Clear();
            _mastMeshes.Clear();
            _mastMat = null;
            _mastFlagMesh = null;
            _mastFlagMats = null;
            BuildTerrain();
            BuildForest();
            BuildUndergrowth();
            BuildTrails();
            BuildLogs();
            BuildLake();
            BuildBasecamp();
            BuildWreck();
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
            // Render resolution. Collision samples the ANALYTIC height (Terrain.GetHeight), never
            // this mesh, so the two agree only as far as the mesh can resolve the function — and that
            // gap is not cosmetic. It is how far you sink into the ground.
            //
            // **THIS IS WHY YOU WALKED UNDER THE SNOW COMING BACK INTO CAMP.** A triangle is a flat
            // chord across a curved surface. Over a CONVEX crest the chord sits below the true
            // surface and your feet float a little, which nobody notices. Over a CONCAVE dip the
            // chord sits ABOVE it — and since your feet are clamped to the true surface, the rendered
            // snow closes over your boots.
            //
            // The pathological case is not the hills. It is `Terrain.MakeTerrain`'s base-camp
            // flattening: a smoothstep that ramps the whole terrain height to zero across the 12 m
            // annulus from BaseCampRadius (16 m) out to 28 m. That is the sharpest curvature anywhere
            // in the world, it is a ring centred exactly on the RV, and at 192 segments a quad spanned
            // 4.17 m — three quads to resolve the entire ramp. Measured against the sim: **0.52 m of
            // sink at the camp ring on the shipping seed** (1.07 m on seed 999), against 0.10 m worst
            // anywhere on the open map. Walking off a hill toward the camper is exactly the path that
            // crosses it, which is how this was reported.
            //
            // Chord error falls with the SQUARE of the spacing, so the fix is resolution — and it has
            // to be resolution rather than a gentler ramp, because the sim is parity-locked
            // ([parity-lock]) and the flattening curve cannot be softened from this side. 512 puts a
            // vertex every 1.56 m: camp-ring sink drops to 0.097 m and the open map to 0.027 m, i.e.
            // from "wading" to "boots slightly buried", which is what standing on snow should look like.
            //
            // Cost is a one-off at build/reseed and it is small: 263k verts in ONE mesh (12 MB, one
            // draw call — nothing to render), and the analytic sweep measures 34 ms against 9 ms at
            // 192. The normal/tangent recalculation below is the larger half; it is still a one-off,
            // and [startup] logs the stage, so a regression here shows up as a number rather than as a
            // mystery. Do not lower this to buy startup time without re-measuring the sink.
            int segs = 512;
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
                BootReport.MissingShader("Metoh/Snowpack",
                    "the ground is flat snow with no rock blend, no deep-snow basin tint and no wind scour");
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
            // Kept so the sway bake knows how tall each tree actually is — the weight ramp has to be
            // normalised per tree or a stunted spire sways as if it were a full-height one.
            var crownH = new float[TreeVariants];
            var crownHStunted = new float[TreeVariants];
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
                crownH[v] = h;
                crownHStunted[v] = h * 0.66f;
            }

            int cells = ForestGrid * ForestGrid;
            var trunkC = NewCombineBuckets(cells);
            var crownDarkC = NewCombineBuckets(cells);
            var crownLightC = NewCombineBuckets(cells);
            // Per-instance sway metadata, kept exactly parallel to the combine buckets above:
            // (baseY, 1/treeHeight, phase). BakeSwayData walks the two lists together after the merge.
            var trunkS = NewSwayBuckets(cells);
            var crownDarkS = NewSwayBuckets(cells);
            var crownLightS = NewSwayBuckets(cells);
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
                // the tree stream stays in lockstep with WorldData.BuildColliders (UNITY_NOTES
                // [rng-lockstep]). The treeIndex % 2 alternation was never an RNG draw either, which is why it
                // is safe to key off it here.
                bool aboveSnowline = y >= SnowlineHeight;
                var crownBucket = (aboveSnowline || treeIndex % 2 != 0) ? crownLightC : crownDarkC;
                // Deal a shape. Mixing in the cell index as well as the tree index stops neighbouring
                // trees (which are adjacent in the stream) from marching through the variants in the
                // same order and forming a visible repeat down a slope.
                int variant = (treeIndex * 7 + cell * 3) % TreeVariants;
                Mesh crown = aboveSnowline ? crownsStunted[variant] : crowns[variant];
                crownBucket[cell].Add(CI(crown, pos + Vector3.up * (1.5f * (float)s), rotQ, scale));

                // Sway metadata for BOTH pieces of this tree. Phase is hashed from the tree index, not
                // drawn — this loop is the RNG-lockstep loop, and one extra rand() here would move
                // every tree after it ([rng-lockstep]).
                float treeH = (1.5f + (aboveSnowline ? crownHStunted[variant] : crownH[variant])) * (float)s;
                var meta = new Vector3(y, 1f / Mathf.Max(0.5f, treeH), MeshUtil.Hash01(treeIndex * 8191 + 17));
                trunkS[cell].Add(meta);
                (aboveSnowline || treeIndex % 2 != 0 ? crownLightS : crownDarkS)[cell].Add(meta);
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
                // Trunks sway far less than crowns — the weight ramp already handles most of that
                // (a trunk's vertices are all near its own base), but halving the strength keeps a
                // 40 cm bole from visibly bending, which is the thing that reads as rubber.
                NewCombinedGo($"Trunks{c}", trunkC[c], MeshUtil.Sway(
                    TintByCell(TrunkCol, c, 0.10f), 0.16f, ProcTex.BarkNormal, 0.9f, 1.5f, 0.10f),
                    trunkS[c]);
                NewCombinedGo($"CrownsDark{c}", crownDarkC[c], MeshUtil.Sway(
                    TintByCell(CrownDark, c, 0.12f), 0.20f, ProcTex.BarkNormal, 0.45f, 2.5f, 0.26f),
                    crownDarkS[c]);
                NewCombinedGo($"CrownsLight{c}", crownLightC[c], MeshUtil.Sway(
                    TintByCell(CrownLight, c, 0.08f), 0.38f, ProcTex.SnowNormal, 0.7f, 2.5f, 0.20f),
                    crownLightS[c]);
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
            // A cloth sheet, not a box. See MeshUtil.FlagSheet and Shaders/Flag.shader — these used to
            // be solid UnitCubes of flat colour, which is a brick nailed to a stick.
            Mesh flag = MeshUtil.FlagSheet(8, 2);

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
                        // Each flag is yawed a little off its neighbour so the five do not stack into
                        // one flat plane — a bundle of lung-ta on a pole never hangs square.
                        var fq = rotQ * Quaternion.Euler(0f, f * 17f - 34f, 0f);
                        // The sheet flies OUT from the pole (hoist at x=0), so no centring offset: the
                        // flag starts where the cube used to be centred and streams away from it.
                        flagC[f][cell].Add(CI(flag, fpos, fq, new Vector3(0.30f, 0.16f, 1f) * (float)s));
                    }
                }
                else if (kind < (y >= ScreeBiasHeight ? 0.35 : 0.75)) driftC[cell].Add(CI(drift, pos - Vector3.up * 0.06f, rotQ, scale));
                else screeC[cell].Add(CI(scree, pos - Vector3.up * 0.05f, rotQ, scale));
            }

            var driftMat = MeshUtil.Surface(DriftCol, 0.44f, ProcTex.SnowNormal, 0.8f, 1.2f);
            var screeMat = MeshUtil.Surface(ScreeCol, 0.10f, ProcTex.RockNormal, 1.0f, 1.6f);
            var poleMat = MeshUtil.Surface(MeshUtil.Rgb(0x6b5b47), 0.14f, ProcTex.BarkNormal, 0.8f, 1.2f);
            var flagMats = new Material[FlagCols.Length];
            for (int f = 0; f < FlagCols.Length; f++) flagMats[f] = FlagMaterial(FlagCols[f], 0.16f);

            for (int c = 0; c < cells; c++)
            {
                TrackUndergrowth(NewCombinedGo($"Drifts{c}", driftC[c], driftMat));
                TrackUndergrowth(NewCombinedGo($"Scree{c}", screeC[c], screeMat));
                TrackUndergrowth(NewCombinedGo($"FlagPoles{c}", poleC[c], poleMat));
                for (int f = 0; f < FlagCols.Length; f++)
                    TrackUndergrowth(NewCombinedGo($"Flags{f}_{c}", flagC[f][c], flagMats[f]));
            }
        }

        /// <summary>
        /// A wind-driven flag material (Shaders/Flag.shader).
        ///
        /// Falls back to a flat lit colour if the shader is missing, rather than to magenta — a flag
        /// that has stopped moving is a small loss, and a field of magenta rectangles is not. Same
        /// rule as the sky and the snowpack, and BootReport says so out loud rather than letting the
        /// world quietly look wrong ([feedback]).
        /// </summary>
        private static Material FlagMaterial(int hex, float emission)
        {
            var c = MeshUtil.Rgb(hex);
            var shader = Shader.Find("Metoh/Flag");
            if (shader == null)
            {
                BootReport.MissingShader("Metoh/Flag", "prayer and marker flags hang dead still");
                return MeshUtil.Lit(c);
            }
            var m = new Material(shader);
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Emission", emission);
            return m;
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
            // PACKED snow, not powder. Until this pass the trail shared SnowNormal with the open
            // snowpack and only differed in albedo — identical micro-relief, so it caught the moon and
            // the torch exactly like the drift beside it, and the eye reads lighting response long
            // before it reads colour. That is why it looked like a painted stripe. ProcTex.PackedSnow-
            // Normal is boot dishes and scuff instead of crystal grain, and the smoothness drop on top
            // gives a second cue that survives at grazing angles where albedo contrast washes out.
            var mat = MeshUtil.Surface(TrailCol, 0.16f, ProcTex.PackedSnowNormal, 0.9f, 1f / 3f);
            // Loose snow shoulders, pushed aside by weeks of boots. Powder again, and slightly
            // brighter than the trail, so the berm catches a rim of light along both edges — that rim
            // is what reads as a trodden channel rather than a decal laid on flat ground.
            var bermMat = MeshUtil.Surface(DriftCol, 0.34f, ProcTex.SnowNormal, 0.85f, 1f / 3f,
                                           ProcTex.SnowDetailNormal, 7f);
            // Local, NOT a field: Build() runs again on every reseed, and a counter that survived the
            // rebuild would hand each session a different trail-colour assignment for the same world.
            int pathIndex = 0;
            foreach (var path in World.Paths)
            {
                var verts = new List<Vector3>();
                var tris = new List<int>();   // submesh 0 — the trodden channel
                var berms = new List<int>();  // submesh 1 — the loose shoulders
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

                    // FIVE columns per station, not two. The trail is now a trodden CHANNEL:
                    //
                    //   outer berm | inner berm | centre | inner berm | outer berm
                    //     +berm         0          -sink       0          +berm
                    //
                    // The old ribbon was two verts at a flat +0.04, i.e. a flat plane hovering above
                    // the snow with a hard polygon edge — which is the definition of a decal, and no
                    // amount of texture work fixes a silhouette that reads as a sticker. Sinking the
                    // centre and raising a lip either side gives real geometry for the moon and the
                    // torch to rake across, and the berm crest is what draws the eye along the route.
                    //
                    // This is VISUAL ONLY. Terrain height lives in the parity-locked sim
                    // (Terrain.GetHeight), and players walk on that, so the channel must stay shallow
                    // enough that nobody reads a mismatch between where they stand and what they see —
                    // hence centimetres, not a trench. Changing the real heightfield would be a sim
                    // change and would need both sims plus a golden regen ([parity-lock]).
                    for (int col = 0; col < 5; col++)
                    {
                        float side = col - 2f;                 // -2..2
                        float lateral = w * (Mathf.Abs(side) > 1.5f ? 1.18f : Mathf.Abs(side) * 0.62f) * Mathf.Sign(side);
                        float dy = Mathf.Abs(side) > 1.5f ? TrailBermHeight       // outer: heaped lip
                                 : Mathf.Abs(side) > 0.5f ? TrailBermHeight * 0.35f
                                 : -TrailSinkDepth;                                // centre: trodden down
                        double px = path.Pts[i].X + nx * lateral;
                        double pz = path.Pts[i].Z + nz * lateral;
                        verts.Add(new Vector3((float)px, (float)World.GetHeight(px, pz) + dy, (float)pz));
                    }
                }
                for (int i = 0; i + 1 < path.Pts.Count; i++)
                {
                    // Four quads per span now that each station has five columns, stitched across.
                    //
                    // Winding still matters and the rule is unchanged: with `b` one column further
                    // across than `a`, (a,b,c) is the order whose normal points up. Get it backwards
                    // and the ribbon is lit from underneath and backface-culled from above — an
                    // invisible trail, which is the bug this comment was left here to prevent.
                    int row = i * 5, next = (i + 1) * 5;
                    for (int col = 0; col < 4; col++)
                    {
                        int a = row + col, b = a + 1;
                        int c = next + col, d = c + 1;
                        // Outer quads are the berms and get powder; the middle two are the trodden
                        // channel and get packed snow. Two submeshes on one mesh rather than two
                        // meshes, because they share every vertex along the seam — splitting them
                        // into separate meshes would duplicate that edge and let the two materials
                        // pull apart under RecalculateNormals, showing as a crease down each side.
                        var into = (col == 0 || col == 3) ? berms : tris;
                        into.Add(a); into.Add(b); into.Add(c);
                        into.Add(b); into.Add(d); into.Add(c);
                    }
                }
                var mesh = new Mesh();
                mesh.SetVertices(verts);
                // World-space UVs again — a trail is a ribbon of packed snow lying on snow, so its
                // grain has to line up with the ground it sits a few centimetres above.
                var tuv = new Vector2[verts.Count];
                for (int i = 0; i < verts.Count; i++) tuv[i] = new Vector2(verts[i].x, verts[i].z);
                mesh.SetUVs(0, tuv);
                mesh.subMeshCount = 2;
                mesh.SetTriangles(tris, 0);
                mesh.SetTriangles(berms, 1);
                // Normals BEFORE tangents, and both are required: the channel now has real relief, and
                // a normal-mapped mesh with no tangents renders flat (see CLAUDE.md on hand-built
                // meshes having to supply their own).
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                var trailGo = NewMeshGo("Trail", mesh, mat);
                trailGo.GetComponent<MeshRenderer>().sharedMaterials = new[] { mat, bermMat };
                BuildTrailDetail(path);
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
        /// lockstep (UNITY_NOTES [rng-lockstep]).
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

            // --- cladding ---------------------------------------------------------
            //
            // **THIS IS THE "MADE IN THE 80s" FIX.** The hut was a normal-mapped box, and a normal map
            // cannot save a box: it varies how a surface catches light, but the surface is still four
            // flat planes meeting at hard right angles with nothing on them. At night that reads as an
            // untextured primitive, because the only thing a torch beam has to find is a flat wall.
            //
            // Real boards are the fix, and the reason is geometric rather than decorative: each one
            // stands a couple of centimetres proud of its neighbours, so a raking torch throws a hard
            // vertical shadow off every seam. Sixty of those turn one flat plane into a corrugated
            // surface whose lighting changes as you walk past it. That is the difference between a
            // wall and a rectangle, and it is invisible in a screenshot lit from the front.
            //
            // ONE welded mesh and ONE renderer for the whole building (MeshUtil.MeshGroup), so the
            // cost is a few hundred triangles, not sixty draw calls.
            var plankMat = MeshUtil.Surface(MeshUtil.Rgb(0x7d6d55), 0.12f, ProcTex.BarkNormal, 1.0f, 0.55f);
            var clad = NewMeshGo("Cladding", HutCladding(), plankMat);
            clad.transform.SetParent(root.transform, false);

            // --- drifted snow at the footings --------------------------------------
            // A building whose walls meet flat ground in a clean line reads as PLACED on the world
            // rather than standing in it — the same "hovering" tell SSAO went in to fix on props, and
            // AO alone cannot fix it here because the gap is real geometry, not just shading. Snow
            // banks against anything that has stood through a night, and this camp has stood through
            // many. Hashed lumps, so the drift is uneven the way wind-piled snow is.
            var footSnow = MeshUtil.Surface(MeshUtil.Rgb(0xe4ecf2), 0.34f, ProcTex.SnowNormal, 0.85f, 1.1f,
                                            ProcTex.SnowDetailNormal, 6f);
            for (int i = 0; i < 9; i++)
            {
                float t = (i + 0.5f) / 9f;
                float h = MeshUtil.Hash01(i * 43 + 5);
                for (int side = -1; side <= 1; side += 2)
                {
                    var bank = NewMeshGo("Footing", MeshUtil.Blob(0.62f + h * 0.22f, 0.30f + h * 0.14f, 0.52f,
                                                                  5, 8, 700 + i * 3 + side, 0.26f), footSnow);
                    bank.transform.SetParent(root.transform, false);
                    bank.transform.localPosition = new Vector3(Mathf.Lerp(-3.5f, 3.5f, t), 0.05f,
                                                               side * (1.28f + h * 0.14f));
                }
            }

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
            // A frame around it. A glowing rectangle flush with the wall reads as a decal; a reveal
            // and a sill say the wall has thickness, which is most of what makes a box read as built.
            AddBox(root, "WinFrameT", new Vector3(1.6f, 2.30f, 1.18f), new Vector3(1.9f, 0.14f, 0.10f), MeshUtil.Rgb(0x4a3f31));
            AddBox(root, "WinFrameB", new Vector3(1.6f, 1.50f, 1.18f), new Vector3(1.9f, 0.16f, 0.16f), MeshUtil.Rgb(0x4a3f31));
            AddBox(root, "WinMullion", new Vector3(1.6f, 1.9f, 1.19f), new Vector3(0.08f, 0.70f, 0.06f), MeshUtil.Rgb(0x3c3428));

            // --- door -------------------------------------------------------------
            // The hut had no way in. That is the kind of thing the eye notices without naming it:
            // a shelter you cannot enter is a prop, and camp is the one structure players approach
            // deliberately and repeatedly.
            AddBox(root, "DoorFrame", new Vector3(-1.7f, 1.05f, 1.17f), new Vector3(1.12f, 2.10f, 0.12f), MeshUtil.Rgb(0x4a3f31));
            AddBox(root, "Door", new Vector3(-1.7f, 1.00f, 1.22f), new Vector3(0.92f, 1.95f, 0.08f), MeshUtil.Rgb(0x6b5a44));
            AddBox(root, "DoorBrace", new Vector3(-1.7f, 1.55f, 1.27f), new Vector3(0.92f, 0.10f, 0.04f), MeshUtil.Rgb(0x4a3f31));
            AddBox(root, "Step", new Vector3(-1.7f, 0.10f, 1.55f), new Vector3(1.3f, 0.20f, 0.7f), MeshUtil.Rgb(0x5f513f));

            // --- stovepipe --------------------------------------------------------
            // Somebody is keeping warm in there. The thin vertical is also the only slim element on an
            // otherwise blocky silhouette, and it is what stops the roofline reading as a solid slab.
            var pipeMat = MeshUtil.Surface(MeshUtil.Rgb(0x2b2b2e), 0.34f, ProcTex.MetalNormal, 0.9f, 2.5f);
            var pipe = NewMeshGo("Stovepipe", MeshUtil.TaperedCylinder(0.13f, 0.115f, 1.9f, 8), pipeMat);
            pipe.transform.SetParent(root.transform, false);
            pipe.transform.localPosition = new Vector3(-2.3f, 3.1f, 0f);
            var cowl = NewMeshGo("PipeCowl", MeshUtil.TaperedCylinder(0.20f, 0.16f, 0.16f, 8), pipeMat);
            cowl.transform.SetParent(root.transform, false);
            cowl.transform.localPosition = new Vector3(-2.3f, 5.02f, 0f);
            if (HPQuality.HighDetail) BuildChimneySmoke(root.transform, new Vector3(-2.3f, 5.15f, 0f));

            // --- snow load + icicles ------------------------------------------------
            // It snows here every night (Weather runs during the title cinematic too), so bare timber
            // eaves are a continuity error as much as a shading one.
            var snowMat = MeshUtil.Surface(MeshUtil.Rgb(0xeef4f8), 0.30f, ProcTex.SnowNormal, 0.8f, 1.2f,
                                           ProcTex.SnowDetailNormal, 6f);
            for (int side = -1; side <= 1; side += 2)
            {
                var cap = NewMeshGo("RoofSnow", MeshUtil.MetricBox(new Vector3(7.15f, 0.10f, 1.98f)), snowMat);
                cap.transform.SetParent(root.transform, false);
                cap.transform.localPosition = new Vector3(0f, 3.19f, side * 0.755f);
                cap.transform.localRotation = Quaternion.Euler(side * 34f, 0f, 0f);
            }
            // Icicles along both eaves. Hashed length/offset per index, never an RNG draw
            // ([rng-lockstep]) — and they hang from the eave line, which is where meltwater runs.
            var iceMat = MeshUtil.Surface(MeshUtil.Rgb(0xd8ecf5), 0.72f, ProcTex.IceNormal, 0.6f, 3f);
            for (int i = 0; i < 14; i++)
            {
                float t = i / 13f;
                float side = (i % 2 == 0) ? 1f : -1f;
                float len = 0.16f + MeshUtil.Hash01(i * 37 + 9) * 0.30f;
                var ice = NewMeshGo("Icicle", MeshUtil.TaperedCylinder(0.035f, 0.004f, len, 5), iceMat);
                ice.transform.SetParent(root.transform, false);
                ice.transform.localPosition =
                    new Vector3(Mathf.Lerp(-3.4f, 3.4f, t) + MeshUtil.Hash01(i * 13) * 0.2f, 2.62f, side * 1.62f);
                ice.transform.localRotation = Quaternion.Euler(180f, 0f, 0f); // TaperedCylinder grows +Y
            }

            // Two A-frame tents in expedition orange, pitched clear of the hut's collider box.
            for (int i = -1; i <= 1; i += 2)
            {
                var tent = new GameObject("Tent");
                tent.transform.parent = root.transform;
                tent.transform.localPosition = new Vector3(i * 4.9f, 0f, i * 1.4f);
                tent.transform.localRotation = Quaternion.Euler(0f, i * 24f, 0f);
                // A real ridge tent, not Cone(r, h, 4) — that is a four-sided PYRAMID, and a pyramid
                // has a point where a tent has a ridge. See MeshUtil.RidgeTent for why the sag matters.
                var canvas = MeshUtil.Surface(MeshUtil.Rgb(0xc7563c), 0.22f, ProcTex.FabricNormal, 0.7f, 3f);
                var mf = tent.AddComponent<MeshFilter>();
                mf.sharedMesh = MeshUtil.RidgeTent(1.35f, 1.75f, 1.9f,
                    HPQuality.HighDetail ? 5 : 3, HPQuality.HighDetail ? 8 : 4);
                tent.AddComponent<MeshRenderer>().sharedMaterial = canvas;

                // Guy lines to pegs. Four thin poles for the cost of nothing, and they are what stop
                // the tent reading as an object resting ON the snow rather than pitched INTO it.
                var cord = MeshUtil.Lit(MeshUtil.Rgb(0x2e2a24));
                for (int g = 0; g < 4; g++)
                {
                    float sx = (g & 1) == 0 ? -1f : 1f;
                    float sz = g < 2 ? -1f : 1f;
                    var guy = NewMeshGo("Guy", MeshUtil.TaperedCylinder(0.018f, 0.014f, 1.5f, 3), cord);
                    guy.transform.SetParent(tent.transform, false);
                    guy.transform.localPosition = new Vector3(0f, 1.72f, sz * 1.85f);
                    guy.transform.localRotation = Quaternion.Euler(sz * 46f, 0f, sx * 34f);
                }
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
        /// The evidence duffel's canvas, welded into one mesh and centred on the bag's middle.
        ///
        /// A duffel is a CYLINDER with flat circular ends, not an ellipsoid — that shape is the whole
        /// reason the thing reads as a bag rather than as a boulder, and it is what the old two-blob
        /// version could not make. Lathed along +Y and laid on its side by the caller's frame: the
        /// profile below runs end to end, staying near-constant through the middle (a stuffed bag is
        /// straight-sided) and closing sharply at the ends into a flat face.
        /// </summary>
        private static Mesh DuffelBag()
        {
            var g = new MeshUtil.MeshGroup();
            // Profile in (along, radius). Slight belly: a full bag bulges in the middle and sags.
            var prof = new[]
            {
                new Vector2(-0.72f, 0.185f),
                new Vector2(-0.66f, 0.275f),   // end face closes fast — this is the flat circular end
                new Vector2(-0.40f, 0.310f),
                new Vector2(-0.10f, 0.325f),   // belly
                new Vector2( 0.22f, 0.318f),
                new Vector2( 0.52f, 0.295f),
                new Vector2( 0.66f, 0.265f),
                new Vector2( 0.72f, 0.175f),
            };
            // Rotate the lathe onto its side (+Y becomes +X) and squash it slightly flat, because a
            // bag resting on the ground spreads under its own weight rather than staying round.
            g.Add(MeshUtil.Lathe(prof, 14, 4021, 0.045f, 1f, 0.92f),
                  Vector3.zero, new Vector3(0f, 0f, -90f), new Vector3(1f, 1f, 0.86f));
            // Contents pushing out through the canvas. Hashed, and the single cheapest thing that
            // separates "a full bag" from "an inflatable".
            for (int i = 0; i < 4; i++)
            {
                float h = MeshUtil.Hash01(4100 + i * 31);
                g.Add(MeshUtil.Blob(0.15f + h * 0.06f, 0.11f + h * 0.05f, 0.13f, 5, 8, 4110 + i, 0.20f),
                      new Vector3(Mathf.Lerp(-0.45f, 0.45f, (i + 0.5f) / 4f),
                                  0.14f + h * 0.06f,
                                  (MeshUtil.Hash01(4200 + i * 17) - 0.5f) * 0.22f));
            }
            return g.Build();
        }

        /// <summary>
        /// The duffel's webbing — zip line, two carry handles, shoulder strap — welded into one mesh
        /// in the same frame as <see cref="DuffelBag"/>.
        /// </summary>
        private static Mesh DuffelWebbing()
        {
            var g = new MeshUtil.MeshGroup();
            // Zip, running the length along the top. Proud of the canvas so it casts its own line.
            g.Add(MeshUtil.MetricBox(new Vector3(1.24f, 0.030f, 0.055f)), new Vector3(0f, 0.300f, 0f));
            // Two carry handles: a post either side and a bar over the top, the loop you'd actually
            // grab. Placed inboard, where a duffel's handles are stitched.
            foreach (float hx in new[] { -0.26f, 0.26f })
            {
                foreach (float sz in new[] { -1f, 1f })
                    g.Add(MeshUtil.MetricBox(new Vector3(0.055f, 0.16f, 0.028f)),
                          new Vector3(hx, 0.315f, sz * 0.115f));
                g.Add(MeshUtil.MetricBox(new Vector3(0.055f, 0.028f, 0.25f)), new Vector3(hx, 0.395f, 0f));
            }
            // Shoulder strap, slung over the near side and hanging off the end.
            g.Add(MeshUtil.MetricBox(new Vector3(1.06f, 0.026f, 0.075f)),
                  new Vector3(-0.05f, 0.205f, 0.255f), new Vector3(0f, 0f, 7f));
            g.Add(MeshUtil.MetricBox(new Vector3(0.075f, 0.30f, 0.026f)),
                  new Vector3(-0.62f, 0.06f, 0.20f), new Vector3(18f, 0f, 0f));
            return g.Build();
        }

        /// <summary>
        /// Ribs and rivet lines for the snowcat's hull, welded into one mesh in the hull's frame.
        /// Sized against the Body box (3.5 x 1.25 x 2.0 at y 0.85) — move that and these float.
        /// </summary>
        private static Mesh HullPlating()
        {
            var g = new MeshUtil.MeshGroup();
            const float halfZ = 1.0f, bodyY = 0.85f;

            // Vertical ribs down both flanks.
            for (int i = 0; i < 6; i++)
            {
                float x = Mathf.Lerp(-1.55f, 1.55f, i / 5f);
                for (int side = -1; side <= 1; side += 2)
                    g.Add(MeshUtil.MetricBox(new Vector3(0.10f, 1.20f, 0.05f)),
                          new Vector3(x, bodyY, side * (halfZ + 0.025f)));
            }
            // A horizontal belt rail tying them, and a lip along the top edge.
            for (int side = -1; side <= 1; side += 2)
            {
                g.Add(MeshUtil.MetricBox(new Vector3(3.52f, 0.11f, 0.07f)),
                      new Vector3(0f, bodyY + 0.30f, side * (halfZ + 0.035f)));
                g.Add(MeshUtil.MetricBox(new Vector3(3.54f, 0.09f, 0.16f)),
                      new Vector3(0f, bodyY + 0.62f, side * (halfZ - 0.02f)));
                // Rivets along the belt. Tiny, and entirely a lighting effect — each one is a specular
                // dot that travels as a torch sweeps past, which is what a riveted panel does.
                for (int r = 0; r < 12; r++)
                {
                    float x = Mathf.Lerp(-1.68f, 1.68f, r / 11f);
                    g.Add(MeshUtil.Blob(0.030f, 0.030f, 0.022f, 4, 5, 811 + r, 0f),
                          new Vector3(x, bodyY + 0.30f, side * (halfZ + 0.075f)));
                }
            }
            return g.Build();
        }

        /// <summary>
        /// Board-by-board cladding for the basecamp hut, welded into one mesh in the hut's local
        /// frame. See the call site for why a normal-mapped box was never going to be enough.
        ///
        /// The hut body is 6.6 x 2.5 x 2.3 centred at y 1.5, and that box is a REAL COLLIDER in the
        /// parity-locked sim — so the boards stand only 3 cm proud. That is deliberately tiny: it is
        /// enough to throw a shadow line off every seam under a raking torch, which is the entire
        /// point, and far too little to be felt as a collision mismatch.
        ///
        /// Board depth and width are hashed per index ([rng-lockstep], never an RNG draw), so the wall
        /// has the slight unevenness of sawn timber rather than the machined regularity that would
        /// read as a texture. Corner posts cap the vertical edges, which is where a box most obviously
        /// looks like a box.
        /// </summary>
        private static Mesh HutCladding()
        {
            var g = new MeshUtil.MeshGroup();
            const float halfX = 3.30f, halfZ = 1.15f;
            const float yLo = 0.30f, yHi = 2.72f;             // inside the body, under the eaves
            const float boardH = yHi - yLo, midY = (yLo + yHi) * 0.5f;
            const float pitch = 0.285f, gap = 0.028f;

            // Long walls (front and back): boards run across X.
            int nx = Mathf.RoundToInt(6.6f / pitch);
            for (int i = 0; i < nx; i++)
            {
                float x = -halfX + (i + 0.5f) * (6.6f / nx);
                float w = (6.6f / nx) - gap;
                for (int side = -1; side <= 1; side += 2)
                {
                    float proud = 0.018f + MeshUtil.Hash01(i * 91 + side * 7) * 0.014f;
                    g.Add(MeshUtil.MetricBox(new Vector3(w, boardH, proud)),
                          new Vector3(x, midY, side * (halfZ + proud * 0.5f)));
                }
            }

            // End walls: boards run across Z.
            int nz = Mathf.RoundToInt(2.3f / pitch);
            for (int i = 0; i < nz; i++)
            {
                float z = -halfZ + (i + 0.5f) * (2.3f / nz);
                float w = (2.3f / nz) - gap;
                for (int side = -1; side <= 1; side += 2)
                {
                    float proud = 0.018f + MeshUtil.Hash01(i * 57 + side * 11 + 3) * 0.014f;
                    g.Add(MeshUtil.MetricBox(new Vector3(proud, boardH, w)),
                          new Vector3(side * (halfX + proud * 0.5f), midY, z));
                }
            }

            // Corner posts, and a top and bottom rail tying the boards. The rails are what stop the
            // cladding reading as loose planks leaning on a wall — real board-and-batten is framed.
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    g.Add(MeshUtil.MetricBox(new Vector3(0.16f, boardH + 0.10f, 0.16f)),
                          new Vector3(sx * (halfX + 0.02f), midY, sz * (halfZ + 0.02f)));
            foreach (float sz in new[] { -1f, 1f })
                foreach (float y in new[] { yLo + 0.09f, yHi - 0.09f })
                    g.Add(MeshUtil.MetricBox(new Vector3(6.68f, 0.13f, 0.05f)),
                          new Vector3(0f, y, sz * (halfZ + 0.045f)));

            return g.Build();
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
        /// <summary>
        /// Scatter along a trail: frozen bootprints, sled ruts, and grit worn through where the pack
        /// is thinnest.
        ///
        /// WHY GEOMETRY AND NOT A TEXTURE. The channel and its berms give the route a cross-section,
        /// but along its LENGTH it is still perfectly uniform, and uniformity at that scale is what
        /// reads as "a mesh someone extruded". Real trail is episodic — a patch of scuffed prints,
        /// then bare grit where a rock sits proud, then a smooth stretch. These are a handful of tiny
        /// meshes per path and they are what stop the eye sliding along it.
        ///
        /// Everything here is hashed from the point index, never drawn from an RNG stream
        /// ([rng-lockstep]): the trail must look identical on every machine, and the forest placement
        /// stream runs nowhere near this but the habit is the point.
        /// </summary>
        private void BuildTrailDetail(ForestPath path)
        {
            if (!HPQuality.HighDetail) return; // pure decoration, first thing to drop ([perf])

            var printMat = MeshUtil.Surface(MeshUtil.Rgb(0x7f8c98), 0.13f, ProcTex.PackedSnowNormal, 1.1f, 1.4f);
            var gritMat = MeshUtil.Surface(MeshUtil.Rgb(0x574f49), 0.09f, ProcTex.RockNormal, 1.0f, 2.2f);
            var rutMat = MeshUtil.Surface(MeshUtil.Rgb(0x8894a1), 0.20f, ProcTex.PackedSnowNormal, 1.0f, 1.0f);

            for (int i = 1; i + 1 < path.Pts.Count; i++)
            {
                Vec2 p = path.Pts[i];
                Vec2 nxt = path.Pts[i + 1];
                float dx = (float)(nxt.X - p.X), dz = (float)(nxt.Z - p.Z);
                float len = Mathf.Sqrt(dx * dx + dz * dz);
                if (len < 1e-3f) continue;
                float ux = dx / len, uz = dz / len;      // along
                float nx = -uz, nz = ux;                 // across
                float w = (float)path.HalfWidth;
                int h = i * 7919;

                // BOOTPRINTS — a cluster of shallow ovals pressed into the channel, staggered either
                // side of the centreline like an actual gait rather than dotted down the middle.
                if (MeshUtil.Hash01(h) < 0.55f)
                {
                    int n = 3 + (int)(MeshUtil.Hash01(h + 1) * 4f);
                    for (int k = 0; k < n; k++)
                    {
                        float t = (k + 0.5f) / n;
                        float lateral = ((k % 2 == 0) ? -1f : 1f) * w * 0.22f;
                        double px = p.X + ux * (len * t) + nx * lateral;
                        double pz = p.Z + uz * (len * t) + nz * lateral;
                        var pr = NewMeshGo("Bootprint", MeshUtil.EllipseDisc(0.15f, 0.09f, 7), printMat);
                        pr.transform.position = new Vector3((float)px,
                            (float)World.GetHeight(px, pz) - TrailSinkDepth + 0.012f, (float)pz);
                        pr.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(ux, uz) * Mathf.Rad2Deg, 0f);
                    }
                }

                // GRIT — where boots have worn the pack through to the scree underneath. Only on the
                // centreline, because that is where the wear is.
                if (MeshUtil.Hash01(h + 31) < 0.3f)
                {
                    double px = p.X + ux * (len * 0.5f);
                    double pz = p.Z + uz * (len * 0.5f);
                    var g = NewMeshGo("Grit", MeshUtil.EllipseDisc(0.34f + MeshUtil.Hash01(h + 5) * 0.3f, 0.22f, 9), gritMat);
                    g.transform.position = new Vector3((float)px,
                        (float)World.GetHeight(px, pz) - TrailSinkDepth + 0.016f, (float)pz);
                    g.transform.rotation = Quaternion.Euler(0f, MeshUtil.Hash01(h + 9) * 360f, 0f);
                }

                // RUTS — a pair of parallel sled grooves running the length of a span. Rarer, and they
                // are the one detail that implies the expedition hauled something heavy up here.
                if (MeshUtil.Hash01(h + 77) < 0.22f)
                {
                    for (int s = -1; s <= 1; s += 2)
                    {
                        double px = p.X + ux * (len * 0.5f) + nx * w * 0.3f * s;
                        double pz = p.Z + uz * (len * 0.5f) + nz * w * 0.3f * s;
                        var r = NewMeshGo("Rut", MeshUtil.EllipseDisc(0.07f, len * 0.42f, 6), rutMat);
                        r.transform.position = new Vector3((float)px,
                            (float)World.GetHeight(px, pz) - TrailSinkDepth + 0.010f, (float)pz);
                        r.transform.rotation = Quaternion.Euler(0f, Mathf.Atan2(ux, uz) * Mathf.Rad2Deg, 0f);
                    }
                }
            }
        }

        /// <summary>
        /// Cold air spilling out of a crevasse and pooling at the threshold.
        ///
        /// Alpha-blended and very faint. The point is not to be noticed as an effect — it is to be the
        /// one thing MOVING at the mouth, because a completely static opening reads as a painted
        /// backdrop the moment you walk toward it, however good its geometry is.
        /// </summary>
        private void BuildCrevasseMist(Transform parent)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f); // alpha — mist occludes, it does not glow
            mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = 3000;
            mat.SetTexture("_BaseMap", ProcTex.SoftDot);
            mat.mainTexture = ProcTex.SoftDot;

            var go = new GameObject("CrevasseMist");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0f, 0.35f, 1.6f);
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(5f, 10f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.18f, 0.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.4f, 3.0f);
            main.gravityModifier = 0.008f; // cold air SINKS — the opposite of the smoke systems
            main.maxParticles = 34;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.72f, 0.82f, 0.90f, 0.13f));

            var emission = ps.emission;
            emission.rateOverTime = 3.2f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(4.4f, 0.5f, 1.2f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(new Color(0.78f, 0.87f, 0.94f), 0f),
                        new GradientColorKey(new Color(0.66f, 0.76f, 0.86f), 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(0f, 1f) });
            col.color = new ParticleSystem.MinMaxGradient(g);

            // Creeps outward and downhill, away from the mouth.
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.Local;
            vel.z = new ParticleSystem.MinMaxCurve(0.15f, 0.55f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.22f);
            noise.frequency = 0.22f;
            noise.quality = ParticleSystemNoiseQuality.Low;

            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
        }

        /// <summary>A thin, slow column from the stovepipe. Same material story as the campfire smoke:
        /// alpha-blended, never additive, or the column glows.</summary>
        private void BuildChimneySmoke(Transform parent, Vector3 localPos)
        {
            var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_ZWrite", 0f);
            mat.renderQueue = 3000;
            mat.SetTexture("_BaseMap", ProcTex.SoftDot);
            mat.mainTexture = ProcTex.SoftDot;

            var go = new GameObject("ChimneySmoke");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var ps = go.AddComponent<ParticleSystem>();
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.5f, 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.5f);
            main.gravityModifier = -0.015f;
            main.maxParticles = 45;
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.56f, 0.58f, 0.22f));

            var emission = ps.emission;
            emission.rateOverTime = 5f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 6f;
            shape.radius = 0.10f;

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, SmokeGrowCurve());

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(SmokeGradient());

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-1.2f, -0.35f); // same wind the snow uses
            vel.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.45f);

            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
        }

        /// <summary>
        /// The wreck: a half-buried tracked snowcat at the edge of camp, nose-down and long dead.
        ///
        /// WHY IT IS HERE. Camp was all timber, canvas and snow — three soft, matte, similar materials,
        /// so nothing in it caught light differently from anything else. A steel hull with cracked
        /// glass is the one hard, dented, semi-metallic surface on the map, and it is what the new
        /// MetalNormal exists for: a torch sweeping across dented panel reads completely unlike a
        /// torch sweeping across a plank wall. It also answers a question the camp never did — how did
        /// five people and their gear get up a Himalayan valley — and answers it with something that
        /// visibly failed, which is the right note for this game.
        ///
        /// PLACEMENT IS DERIVED, NOT DRAWN. Its position comes from the seeded RV transform by fixed
        /// offsets, so it consumes no random numbers and cannot perturb the forest stream
        /// ([rng-lockstep]). Its collider is appended in WorldData.BuildColliders AFTER the tree loop
        /// for exactly the same reason.
        /// </summary>
        private void BuildWreck()
        {
            Vector3 at = WreckPosition(out float yawDeg);
            var root = new GameObject("Wreck");
            root.transform.parent = transform;
            root.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yawDeg, 0f));

            // Nose-down and canted: a vehicle that stopped where it broke, not one that parked.
            var hull = new GameObject("Hull");
            hull.transform.SetParent(root.transform, false);
            hull.transform.localRotation = Quaternion.Euler(-13f, 0f, 6f);

            var steel = MeshUtil.Surface(MeshUtil.Rgb(0x5c4a3a), 0.34f, ProcTex.MetalNormal, 1.0f, 0.55f, metallic: 0.55f);
            var rust = MeshUtil.Surface(MeshUtil.Rgb(0x6d4426), 0.14f, ProcTex.MetalNormal, 1.1f, 0.8f, metallic: 0.25f);
            var glass = MeshUtil.Surface(MeshUtil.Rgb(0x2b3a40), 0.88f, ProcTex.IceNormal, 0.5f, 1.4f, metallic: 0.2f);
            var track = MeshUtil.Surface(MeshUtil.Rgb(0x23201e), 0.10f, ProcTex.MetalNormal, 1.2f, 1.6f);

            AddBoxTo(hull, "Body", new Vector3(0f, 0.85f, 0f), new Vector3(3.5f, 1.25f, 2.0f), steel);
            AddBoxTo(hull, "Cab", new Vector3(0.85f, 1.85f, 0f), new Vector3(1.7f, 1.0f, 1.85f), steel);
            AddBoxTo(hull, "Windscreen", new Vector3(1.62f, 1.90f, 0f), new Vector3(0.10f, 0.72f, 1.55f), glass);
            AddBoxTo(hull, "SideGlass", new Vector3(0.85f, 1.95f, 0.93f), new Vector3(1.35f, 0.55f, 0.06f), glass);
            AddBoxTo(hull, "Bonnet", new Vector3(-1.35f, 1.35f, 0f), new Vector3(1.1f, 0.35f, 1.8f), rust);

            // Panel structure: ribs and rivet lines down both flanks, welded into one mesh.
            //
            // Same argument as the hut's cladding, and it applies harder to steel. MetalNormal gives
            // the surface dents and scratches, but the SHAPE is still three flat planes meeting at
            // right angles — and a vehicle is the one object a player has a lifetime of reference for,
            // so "box with a metal texture" is obvious in a way a box with a rock texture is not. Ribs
            // catch a torch as a row of hard highlights; rivets stipple the space between them. This
            // is what makes it read as fabricated rather than extruded.
            var plate = NewMeshGo("Plating", HullPlating(), rust);
            plate.transform.SetParent(hull.transform, false);

            // Tracks. Half-sunk, so only the top run shows — which is what sells "buried" without
            // needing to deform the terrain under it.
            for (int s = -1; s <= 1; s += 2)
            {
                AddBoxTo(hull, "Track", new Vector3(0f, 0.28f, s * 1.05f), new Vector3(3.7f, 0.55f, 0.42f), track);
                for (int w = 0; w < 4; w++)
                {
                    var road = NewMeshGo("Roller", MeshUtil.TaperedCylinder(0.26f, 0.26f, 0.30f, 8), rust);
                    road.transform.SetParent(hull.transform, false);
                    road.transform.localPosition = new Vector3(-1.35f + w * 0.9f, 0.30f, s * 1.05f);
                    road.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                }
            }

            // A sprung hatch and a bent exhaust: the two silhouette breaks that stop it reading as a
            // stack of boxes at the distance you actually see it from.
            var hatch = NewMeshGo("Hatch", MeshUtil.MetricBox(new Vector3(0.9f, 0.06f, 0.8f)), rust);
            hatch.transform.SetParent(hull.transform, false);
            hatch.transform.localPosition = new Vector3(0.2f, 2.38f, -0.35f);
            hatch.transform.localRotation = Quaternion.Euler(0f, 12f, -58f);

            var stack = NewMeshGo("Exhaust", MeshUtil.Limb(0.07f, 0.055f, 0.9f, 5, 6, 733, 0.05f), rust);
            stack.transform.SetParent(hull.transform, false);
            stack.transform.localPosition = new Vector3(-0.6f, 1.5f, -0.78f);
            stack.transform.localRotation = Quaternion.Euler(18f, 0f, 26f);

            // Drifted snow banking against the windward flank — the thing that actually says "this has
            // been here since before you arrived".
            var snow = MeshUtil.Surface(MeshUtil.Rgb(0xe6eef4), 0.32f, ProcTex.SnowNormal, 0.8f, 1.1f,
                                        ProcTex.SnowDetailNormal, 6f);
            for (int i = 0; i < 5; i++)
            {
                float t = i / 4f;
                var bank = NewMeshGo("Drift", MeshUtil.Blob(1.05f, 0.34f, 0.7f, 5, 9, 640 + i, 0.24f), snow);
                bank.transform.SetParent(root.transform, false);
                bank.transform.localPosition = new Vector3(Mathf.Lerp(-1.9f, 1.9f, t), 0.12f, -1.25f - MeshUtil.Hash01(i * 19) * 0.3f);
            }
        }

        /// <summary>
        /// Where the wreck sits, derived from the seeded basecamp transform. Shared with
        /// <c>WorldData.BuildColliders</c>, which must place its collider on the same spot — the two
        /// are kept in step by both being pure functions of <c>WorldData.Rv</c> and these constants.
        /// </summary>
        internal static Vector3 WreckPosition(out float yawDeg)
        {
            double ry = WorldData.Rv.Ry;
            double ox = System.Math.Cos(ry) * WorldData.WreckAlong - System.Math.Sin(ry) * WorldData.WreckAcross;
            double oz = -System.Math.Sin(ry) * WorldData.WreckAlong - System.Math.Cos(ry) * WorldData.WreckAcross;
            double x = WorldData.Rv.X + ox, z = WorldData.Rv.Z + oz;
            yawDeg = (float)(ry * Mathf.Rad2Deg + WorldData.WreckYawOffsetDeg);
            return new Vector3((float)x, (float)World.GetHeight(x, z), (float)z);
        }

        /// <summary>AddBox with an explicit material, for props that are not sawn timber.</summary>
        private void AddBoxTo(GameObject parent, string name, Vector3 localPos, Vector3 size, Material mat)
        {
            var go = NewMeshGo(name, MeshUtil.MetricBox(size), mat);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
        }

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

            // The bag. Was a rotated CAPSULE, which is a perfect cylinder with perfect hemispheres on
            // the ends — the shape of a propane tank, not of a canvas holdall with things in it. This
            // is the object every searcher walks up to and stares at while the deposit hold runs, so
            // it gets looked at closer than almost anything else in the world.
            // THE BAG IS STILL THE EVIDENCE STORE. It is what the deposit hold runs against, it is what
            // every searcher walks up to and stares at while the bar fills, and it stays a duffel —
            // canvas, slumped, obviously carried up here by hand. What it stops being is two
            // ellipsoids: a duffel has flat circular ENDS, a zip running its length, webbing handles
            // and lumps where the contents push out, and every one of those is a hard edge that a
            // torch finds. Welded into one mesh, so a much better bag costs one renderer instead of two.
            var canvasMat = MeshUtil.Surface(MeshUtil.Rgb(0xb8552f), 0.28f, ProcTex.FabricNormal, 0.8f, 2.5f);
            var body = NewMeshGo("Bag", DuffelBag(), canvasMat);
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.32f, 0f);

            // Webbing: the zip line, two carry handles and the shoulder strap, welded together — one
            // more renderer for the whole harness.
            var webbing = MeshUtil.Surface(MeshUtil.Rgb(0x3a3026), 0.14f, ProcTex.FabricNormal, 0.6f, 6f);
            var harness = NewMeshGo("BagWebbing", DuffelWebbing(), webbing);
            harness.transform.SetParent(root.transform, false);
            harness.transform.localPosition = new Vector3(0f, 0.32f, 0f);


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
                // Built at its real proportions via Lathe's xScale/zScale rather than squashed by the
                // transform. NON-UNIFORM TRANSFORM SCALE ON NORMAL-MAPPED GEOMETRY IS A BUG: it skews
                // tangent space, so the ice normals were being sheared, and it stretches UV tiling
                // anisotropically so the grain ran at different densities across one surface. This was
                // the biggest mesh in every cave and it had a 13 : 7.2 : 11 squash on it. Same class of
                // problem as the AddBox UV stretch, and the brow below was worse at 6 : 1.
                var mound = NewMeshGo("Mound", MeshUtil.Rock(6.5f, 9, 14, caveIndex + 3, 2.0f, 1.7f), rock);
                mound.transform.SetParent(root.transform, false);
                mound.transform.localPosition = new Vector3(0f, 1.1f, -3.4f);

                // THE OPENING — now an actual recess, which it never was.
                //
                // It has been a convex mass twice: first a scaled sphere ("a black beachball parked
                // against the ice"), then an irregular Rock, which broke the outline but was still
                // CONVEX. That is the whole problem, and no amount of lumpiness fixes it: a convex
                // surface bulges toward the viewer, so it can only ever read as a dark rock. Head-on
                // with the throat glow behind it you could just about believe it; from any angle you
                // could not.
                //
                // MeshUtil.Throat builds inward-facing faces receding into unlit black — genuine
                // concavity. It reads as an opening from every angle, it frames the Yeti properly
                // when it emerges, and it gives the mist something to sit inside.
                var throat = NewMeshGo("Throat", MeshUtil.Throat(2.5f, 1.9f, 7.5f, 14, 7, caveIndex + 11), voidMat);
                throat.transform.SetParent(root.transform, false);
                throat.transform.localPosition = new Vector3(0f, 1.45f, 0.55f);
                throat.transform.localRotation = Quaternion.Euler(0f, 180f, 0f); // bore points into the hill

                // Overhanging brow above the opening — the strongest "this is an entrance" cue, and
                // formerly a rotated cube. A straight horizontal edge is the one line that never occurs
                // in broken ice, so it read as a lintel somebody had installed.
                // Proportions in the mesh, not the transform — this one was the worst offender at a
                // 6 : 1 squash, which sheared its normals hardest of anything in the scene.
                var brow = NewMeshGo("Brow", MeshUtil.Rock(1.95f, 7, 12, caveIndex + 23, 2.0f, 0.87f, 0.32f), darkRock);
                brow.transform.SetParent(root.transform, false);
                brow.transform.localPosition = new Vector3(0f, 3.4f, 1.4f);
                brow.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);

                // Icicles across the brow — the detail that says ICE rather than rock, and the one
                // piece of geometry here small enough to read as detail at the range you approach from.
                // Hashed off the cave index like everything else, never from an RNG stream ([rng-lockstep]).
                var ice = MeshUtil.Surface(MeshUtil.Rgb(0xbfe2f0), 0.72f, ProcTex.IceNormal, 0.8f, 0.9f);
                int teeth = HPQuality.HighDetail ? 9 : 5;
                for (int t = 0; t < teeth; t++)
                {
                    float h01 = MeshUtil.Hash01(caveIndex * 977 + t * 37);
                    float len = 0.55f + h01 * 1.5f;
                    var spike = NewMeshGo("Icicle", MeshUtil.Limb(0.02f, 0.11f, len, 5, 5, caveIndex * 50 + t, 0.12f, capScale: 0f), ice);
                    spike.transform.SetParent(root.transform, false);
                    spike.transform.localPosition = new Vector3(
                        Mathf.Lerp(-3.3f, 3.3f, (t + 0.5f) / teeth), 3.25f - len, 1.75f + h01 * 0.5f);
                }

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
                // --- the ice-cavern layer -----------------------------------------------------
                //
                // The three things that separate "a hole in a grey lump" from "a crevasse in a
                // glacier". Each is cheap; together they are most of the read.
                //
                // 1. TRANSLUCENT LIP. Real glacier ice carries light a short way into itself, so an
                //    edge lit from behind glows rather than going to silhouette. URP/Lit has no
                //    subsurface term, so this fakes it the way the rest of the project does — a thin
                //    emissive shell on the lip only, tinted the throat's cold blue. It is what makes
                //    the opening read as ICE rather than as painted rock.
                var lipIce = MeshUtil.Surface(MeshUtil.Rgb(0x9fd4ea), 0.80f, ProcTex.IceNormal, 0.7f, 0.8f,
                                              emission: MeshUtil.Rgb(0x2d5f7d), emissionIntensity: 0.55f);
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * Mathf.PI * 2f;
                    float rr = 0.55f + MeshUtil.Hash01(caveIndex * 311 + i * 17) * 0.5f;
                    var chunk = NewMeshGo("LipIce", MeshUtil.Rock(rr, 5, 8, caveIndex * 9 + i, 1.3f, 0.8f), lipIce);
                    chunk.transform.SetParent(root.transform, false);
                    chunk.transform.localPosition = new Vector3(Mathf.Cos(a) * 2.7f, 1.45f + Mathf.Sin(a) * 2.1f, 0.95f);
                }

                // 2. RIME creeping out across the ground from the threshold — frost the cold breath of
                //    the crevasse has laid down. It ties the mouth to the snow it sits in; without it
                //    the whole assembly reads as parked on the surface.
                var rime = MeshUtil.Surface(MeshUtil.Rgb(0xe9f6fb), 0.55f, ProcTex.IceNormal, 0.6f, 1.4f);
                for (int i = 0; i < 6; i++)
                {
                    float t = i / 5f;
                    var patch = NewMeshGo("Rime", MeshUtil.EllipseDisc(2.6f - t * 1.5f, 1.7f - t * 0.9f, 11), rime);
                    patch.transform.SetParent(root.transform, false);
                    float fz = 2.2f + t * 4.2f;
                    patch.transform.localPosition = new Vector3((MeshUtil.Hash01(caveIndex * 71 + i) - 0.5f) * 2.4f, 0.03f, fz);
                }

                // 3. ICICLE CURTAIN at varying depths INSIDE the bore, not just across the brow. Teeth
                //    receding into the dark are what give the throat a sense of scale — with nothing
                //    inside it, a black hole has no depth cue at all no matter how concave it is.
                for (int i = 0; i < (HPQuality.HighDetail ? 12 : 6); i++)
                {
                    float h01 = MeshUtil.Hash01(caveIndex * 613 + i * 41);
                    float depth = 0.4f + h01 * 3.2f;                       // how far back it hangs
                    float len = 0.4f + MeshUtil.Hash01(caveIndex * 97 + i) * 1.3f;
                    float across = (MeshUtil.Hash01(caveIndex * 53 + i * 7) - 0.5f) * 3.6f * (1f - depth * 0.2f);
                    var spike = NewMeshGo("ThroatIcicle",
                        MeshUtil.Limb(0.015f, 0.075f, len, 5, 5, caveIndex * 80 + i, 0.14f, capScale: 0f), ice);
                    spike.transform.SetParent(root.transform, false);
                    spike.transform.localPosition = new Vector3(across, 3.05f - len - depth * 0.25f, 0.5f - depth);
                }

                // 4. MIST pooling at the threshold. Cold air spills out of a crevasse and sits low, and
                //    a slow drift across the opening is the one moving thing here — which is what stops
                //    the mouth reading as a painted backdrop when you approach it.
                if (HPQuality.HighDetail) BuildCrevasseMist(root.transform);

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
            // Shared per build — see the cache fields above Build().
            if (_mastMat == null)
                _mastMat = MeshUtil.Surface(MeshUtil.Rgb(0x6b5b47), 0.14f, ProcTex.BarkNormal, 0.8f, 1.2f);
            if (!_mastMeshes.TryGetValue(height, out Mesh mastMesh))
            {
                mastMesh = MeshUtil.TaperedCylinder(0.075f, 0.05f, height, 5);
                _mastMeshes[height] = mastMesh;
            }
            // Cloth, and a few more segments than the trail flags get: a mast flag is 55 cm and four
            // times closer to the eye when you are standing under it, so its ripple has to resolve.
            if (_mastFlagMesh == null) _mastFlagMesh = MeshUtil.FlagSheet(10, 3);
            if (_mastFlagMats == null)
            {
                _mastFlagMats = new Material[FlagCols.Length];
                // Slightly brighter self-lift than the trail flags. A marker mast exists to be found
                // from across the valley, so it is the one flag allowed to insist a little.
                for (int i = 0; i < FlagCols.Length; i++) _mastFlagMats[i] = FlagMaterial(FlagCols[i], 0.30f);
            }

            var mast = NewMeshGo("MarkerMast", mastMesh, _mastMat);
            mast.transform.SetParent(parent, false);
            mast.transform.localPosition = localPos;

            // Flags up the top two-thirds, starting on this mast's identity colour so the dominant
            // colour at the top is the one that names it.
            for (int f = 0; f < 6; f++)
            {
                var flag = NewMeshGo("MastFlag", _mastFlagMesh, _mastFlagMats[(ident + f) % FlagCols.Length]);
                flag.transform.SetParent(parent, false);
                flag.transform.localPosition = localPos + Vector3.up * (height * (0.94f - f * 0.10f));
                flag.transform.localRotation = Quaternion.Euler(0f, f * 26f, 0f);
                // Z is 1: the sheet has no thickness to scale, and squashing it to 0.03 as the old box
                // needed would flatten the ripple the shader displaces along the normal.
                flag.transform.localScale = new Vector3(0.62f, 0.38f, 1f);
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
            // is touched (UNITY_NOTES [rng-lockstep]).
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
            // 9 segments, not 5. A 5-segment cylinder is a PENTAGON, and at the diameter of a 10 m
            // structural post that is plainly visible as a flat-sided stick — the same primitive tell
            // [legibility] catches on the trees.
            Mesh post = MeshUtil.TaperedCylinder(0.22f, 0.18f, 10f, 9);
            var legOffsets = new[] { new Vector2(-1.4f, -1.4f), new Vector2(1.4f, -1.4f), new Vector2(1.4f, 1.4f), new Vector2(-1.4f, 1.4f) };
            foreach (var off in legOffsets)
            {
                var leg = NewMeshGo("Leg", post, wood);
                leg.transform.parent = root.transform;
                leg.transform.localPosition = new Vector3(off.x, 0f, off.y);
            }

            // --- cross-bracing ----------------------------------------------------
            // The tower was four unbraced vertical posts holding a platform 10 m up, which is not a
            // thing that stands, and the eye knows it without being able to name it. Bracing is also
            // the single cheapest silhouette win here: it turns a bare rectangle into a lattice, and a
            // lattice is what a fire lookout READS as from a distance — which matters, because this is
            // a landmark players navigate by long before they can resolve any of its detail.
            var brace = MeshUtil.Surface(MeshUtil.Rgb(0x4e453c), 0.12f, ProcTex.BarkNormal, 0.9f, 1.4f);
            for (int side = 0; side < 4; side++)
            {
                Vector2 a = legOffsets[side], b = legOffsets[(side + 1) % 4];
                // Two tiers of X-bracing plus a horizontal girt between them.
                for (int tier = 0; tier < 2; tier++)
                {
                    float y0 = 0.7f + tier * 4.3f, y1 = y0 + 4.1f;
                    AddStrut(root, brace, new Vector3(a.x, y0, a.y), new Vector3(b.x, y1, b.y), 0.055f);
                    AddStrut(root, brace, new Vector3(b.x, y0, b.y), new Vector3(a.x, y1, a.y), 0.055f);
                }
                AddStrut(root, brace, new Vector3(a.x, 4.9f, a.y), new Vector3(b.x, 4.9f, b.y), 0.07f);
                AddStrut(root, brace, new Vector3(a.x, 0.55f, a.y), new Vector3(b.x, 0.55f, b.y), 0.07f);
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

            // --- the brazier ------------------------------------------------------
            // Was a bare Light component with NO MESH AT ALL — light pouring out of empty air above
            // the deck. An iron fire basket is both the honest answer and the right one for a
            // wilderness lookout: this valley has no power, so every other warm light in the game is
            // a flame or a lamp somebody carried up, and a floating glow was the one thing breaking
            // that rule. It reuses the campfire's flicker and particles wholesale (see Campfire.cs),
            // which is most of why this is cheap.
            BuildBrazier(root.transform, new Vector3(1.15f, TowerClimbH, 1.15f));
            BuildTowerViewer(root.transform, new Vector3(-1.0f, TowerClimbH, -1.0f));

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

        /// <summary>
        /// The ladder. Was, accurately, a pile of sticks.
        ///
        /// The old one was two `TaperedCylinder(..., 4)` rails and rungs of the same — and FOUR
        /// SEGMENTS IS A SQUARE. So they were not round rails at all, they were square posts, floating
        /// at the tower face with nothing joining them to it and nothing at the top to grab. Every
        /// piece was also the same diameter, which is the tell that reads as "sticks" rather than
        /// "ladder": on a real one the stiles are chunky and the rungs are slimmer, and that contrast
        /// is most of the recognition.
        ///
        /// What makes this one read as built: round stiles (9 segments) that TAPER, rungs let into
        /// them at a smaller diameter, standoff brackets bolting it to the tower at three heights, and
        /// a safety hoop over the last stretch. The hoop is doing double duty — it is what a real fire
        /// lookout has, and it visually terminates the climb instead of the ladder just stopping.
        /// </summary>
        private void BuildLadderMesh(GameObject root, float baseY, Vector2 towerXZ, Vector2 toCentre, float faceR)
        {
            var wood = MeshUtil.Surface(MeshUtil.Rgb(0x5a3f24), 0.12f, ProcTex.BarkNormal, 1.0f, 1.2f);
            var iron = MeshUtil.Surface(MeshUtil.Rgb(0x39332e), 0.30f, ProcTex.MetalNormal, 0.9f, 2.0f, metallic: 0.5f);
            float topLocalY = TowerClimbH; // ladder runs from ground to the platform surface
            Vector2 side = new Vector2(-toCentre.y, toCentre.x); // perpendicular, for the two stiles
            var localFace = new Vector3(toCentre.x * faceR, 0, toCentre.y * faceR); // relative to root
            const float halfW = 0.32f;

            // Two stiles — round, and thicker at the foot where the load is.
            foreach (float s in new[] { -halfW, halfW })
            {
                var rail = NewMeshGo("LadderStile", MeshUtil.TaperedCylinder(0.075f, 0.055f, topLocalY, 9), wood);
                rail.transform.parent = root.transform;
                rail.transform.localPosition = localFace + new Vector3(side.x * s, 0, side.y * s);
            }

            // Rungs — slimmer than the stiles, and inset so they read as let INTO them rather than
            // laid across the front.
            Mesh rung = MeshUtil.TaperedCylinder(0.036f, 0.036f, halfW * 2f - 0.03f, 8);
            var rungRot = Quaternion.LookRotation(new Vector3(toCentre.x, 0, toCentre.y)) * Quaternion.Euler(0, 90, 90);
            for (float h = 0.34f; h < topLocalY - 0.1f; h += 0.34f)
            {
                var r = NewMeshGo("Rung", rung, wood);
                r.transform.parent = root.transform;
                r.transform.localPosition = localFace + new Vector3(0, h, 0);
                r.transform.localRotation = rungRot;
            }

            // Standoff brackets. Without these the ladder hangs in space beside the tower; with them
            // it is bolted to it, which is the difference between a prop and a structure.
            foreach (float h in new[] { 1.6f, 5.0f, 8.4f })
            {
                for (int s = -1; s <= 1; s += 2)
                {
                    Vector3 outer = localFace + new Vector3(side.x * halfW * s, h, side.y * halfW * s);
                    Vector3 inner = new Vector3(toCentre.x * 1.35f, h, toCentre.y * 1.35f);
                    AddStrut(root, iron, outer, inner, 0.035f);
                }
            }

            // Safety hoop over the top stretch — five arcs behind the climber.
            for (int i = 0; i < 5; i++)
            {
                float h = topLocalY - 2.6f + i * 0.62f;
                var hoop = NewMeshGo("LadderHoop", MeshUtil.Torus(0.42f, 0.028f, 14, 5, 0), iron);
                hoop.transform.parent = root.transform;
                hoop.transform.localPosition = localFace + new Vector3(toCentre.x * 0.18f, h, toCentre.y * 0.18f);
                // Torus lies in XZ; stand it up and face it along the ladder.
                hoop.transform.localRotation = Quaternion.LookRotation(new Vector3(toCentre.x, 0, toCentre.y)) *
                                               Quaternion.Euler(90f, 0f, 0f);
            }
        }

        /// <summary>
        /// The mounted tower viewer — the coin-operated binoculars from a scenic overlook (owner's
        /// reference, 2026-08-14), without the coin slot doing anything.
        ///
        /// Binoculars were previously not an object at all: glassing was a pure ability, hold-B on the
        /// deck, with no model anywhere. This gives it the thing you can see from the ground and walk
        /// up to, while keeping it bolted to the tower — a pocketable pair would delete the tower's
        /// whole reason to exist, since glassing IS the reward for climbing.
        ///
        /// See <see cref="TowerViewer"/> for why the aiming animation needs no new networking.
        /// </summary>
        private void BuildTowerViewer(Transform parent, Vector3 localPos)
        {
            var painted = MeshUtil.Surface(MeshUtil.Rgb(0x2f4f45), 0.42f, ProcTex.MetalNormal, 0.8f, 1.8f, metallic: 0.45f);
            var iron = MeshUtil.Surface(MeshUtil.Rgb(0x39332e), 0.30f, ProcTex.MetalNormal, 0.9f, 2.2f, metallic: 0.55f);
            var glass = MeshUtil.Surface(MeshUtil.Rgb(0x1b2b33), 0.92f, ProcTex.IceNormal, 0.4f, 2f, metallic: 0.3f);

            var root = new GameObject("TowerViewer");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPos;

            // Cast base and column. Heavy and tapered — these things are bolted down because they get
            // leaned on, and that heft is most of what makes the silhouette recognisable.
            var plate = NewMeshGo("ViewerBase", MeshUtil.TaperedCylinder(0.30f, 0.24f, 0.07f, 12), iron);
            plate.transform.SetParent(root.transform, false);
            var column = NewMeshGo("ViewerColumn", MeshUtil.TaperedCylinder(0.115f, 0.085f, 1.16f, 10), painted);
            column.transform.SetParent(root.transform, false);
            column.transform.localPosition = new Vector3(0f, 0.07f, 0f);

            // The yoke yaws; the head pitches inside it. Two transforms, because a single one cannot
            // do both without the eyepieces rolling as it swings.
            var yoke = new GameObject("Yoke").transform;
            yoke.SetParent(root.transform, false);
            yoke.localPosition = new Vector3(0f, 1.23f, 0f);

            var collar = NewMeshGo("Collar", MeshUtil.TaperedCylinder(0.10f, 0.10f, 0.09f, 10), iron);
            collar.transform.SetParent(yoke, false);
            for (int s = -1; s <= 1; s += 2)
            {
                var arm = NewMeshGo("YokeArm", MeshUtil.MetricBox(new Vector3(0.05f, 0.26f, 0.07f)), painted);
                arm.transform.SetParent(yoke, false);
                arm.transform.localPosition = new Vector3(s * 0.20f, 0.17f, 0f);
            }

            var head = new GameObject("Head").transform;
            head.SetParent(yoke, false);
            head.localPosition = new Vector3(0f, 0.29f, 0f);

            // Twin barrels plus the housing between them — the shape that says "binoculars" instantly
            // even as a silhouette, which is how it will usually be seen.
            AddBoxTo(head.gameObject, "Housing", Vector3.zero, new Vector3(0.34f, 0.20f, 0.30f), painted);
            for (int s = -1; s <= 1; s += 2)
            {
                var barrel = NewMeshGo("Barrel", MeshUtil.TaperedCylinder(0.075f, 0.062f, 0.42f, 10), painted);
                barrel.transform.SetParent(head, false);
                barrel.transform.localPosition = new Vector3(s * 0.105f, 0f, 0.14f);
                barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                var lens = NewMeshGo("Lens", MeshUtil.TaperedCylinder(0.058f, 0.058f, 0.012f, 12), glass);
                lens.transform.SetParent(head, false);
                lens.transform.localPosition = new Vector3(s * 0.105f, 0f, 0.56f);
                lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

                var cup = NewMeshGo("Eyecup", MeshUtil.TaperedCylinder(0.045f, 0.055f, 0.05f, 10), iron);
                cup.transform.SetParent(head, false);
                cup.transform.localPosition = new Vector3(s * 0.105f, 0f, -0.19f);
                cup.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            // Handles either side, and the coin box — the two details that make it read as the
            // seaside-overlook object rather than as a generic scope. The box is decoration only:
            // nothing charges the player anything.
            for (int s = -1; s <= 1; s += 2)
            {
                var grip = NewMeshGo("Handle", MeshUtil.TaperedCylinder(0.022f, 0.022f, 0.20f, 8), iron);
                grip.transform.SetParent(head, false);
                grip.transform.localPosition = new Vector3(s * 0.21f, -0.06f, -0.05f);
                grip.transform.localRotation = Quaternion.Euler(0f, 0f, s * 22f);
            }
            AddBoxTo(root, "CoinBox", new Vector3(0.14f, 0.86f, 0f), new Vector3(0.12f, 0.20f, 0.10f), iron);

            root.AddComponent<TowerViewer>().Init(yoke, head);
        }

        /// <summary>
        /// A timber/iron strut between two local points. Used for the tower's cross-bracing and the
        /// ladder's standoffs — anywhere a member has to connect two places rather than stand up.
        ///
        /// The maths worth stating: TaperedCylinder builds along +Y, so the rotation is
        /// FromToRotation(up, delta) and the length is the delta's magnitude. Getting this wrong gives
        /// struts that are the right length pointing the wrong way, which looks like a bug in the
        /// layout rather than in the rotation.
        /// </summary>
        private void AddStrut(GameObject parent, Material mat, Vector3 from, Vector3 to, float radius)
        {
            Vector3 delta = to - from;
            float len = delta.magnitude;
            if (len < 1e-3f) return;
            var go = NewMeshGo("Strut", MeshUtil.TaperedCylinder(radius, radius, len, 7), mat);
            go.transform.parent = parent.transform;
            go.transform.localPosition = from;
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, delta / len);
        }

        /// <summary>
        /// The lookout's fire basket: an iron brazier on a stand, burning all night.
        ///
        /// Shares <see cref="Campfire"/> and <see cref="BuildFireParticles"/> with the camp fire, so
        /// it flickers on the same summed-sine model and needs no code of its own. Scaled down and
        /// dimmed — it is a beacon seen from the valley floor, not a hearth you warm your hands at.
        /// </summary>
        private void BuildBrazier(Transform parent, Vector3 localPos)
        {
            var iron = MeshUtil.Surface(MeshUtil.Rgb(0x332d28), 0.28f, ProcTex.MetalNormal, 1.0f, 2.2f, metallic: 0.55f);
            var root = new GameObject("Brazier");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = localPos;

            // Tripod stand.
            for (int i = 0; i < 3; i++)
            {
                float a = i / 3f * Mathf.PI * 2f;
                AddStrut(root, iron, new Vector3(Mathf.Cos(a) * 0.34f, 0f, Mathf.Sin(a) * 0.34f),
                                     new Vector3(Mathf.Cos(a) * 0.13f, 0.62f, Mathf.Sin(a) * 0.13f), 0.028f);
            }
            // The basket: a bowl plus a rim, so the fire sits INSIDE something.
            var bowl = NewMeshGo("Basket", MeshUtil.TaperedCylinder(0.16f, 0.42f, 0.34f, 12), iron);
            bowl.transform.SetParent(root.transform, false);
            bowl.transform.localPosition = new Vector3(0f, 0.60f, 0f);
            var rim = NewMeshGo("BasketRim", MeshUtil.Torus(0.42f, 0.035f, 14, 6, 0), iron);
            rim.transform.SetParent(root.transform, false);
            rim.transform.localPosition = new Vector3(0f, 0.94f, 0f);

            // Coals in the basket, on their own emissive material so Campfire can pulse them.
            var emberMat = MeshUtil.Emissive(MeshUtil.Rgb(0x3a1b0c), MeshUtil.Rgb(0xff6a1e), 2.4f);
            for (int i = 0; i < 6; i++)
            {
                float a = MeshUtil.Hash01(i * 61 + 3) * Mathf.PI * 2f;
                float r = Mathf.Sqrt(MeshUtil.Hash01(i * 23 + 5)) * 0.26f;
                var coal = NewMeshGo("Coal", MeshUtil.Rock(0.075f + MeshUtil.Hash01(i * 13) * 0.05f, 4, 6, i + 40), emberMat);
                coal.transform.SetParent(root.transform, false);
                coal.transform.localPosition = new Vector3(Mathf.Cos(a) * r, 0.86f, Mathf.Sin(a) * r);
            }

            var light = new GameObject("BrazierLight").AddComponent<Light>();
            light.transform.SetParent(root.transform, false);
            light.transform.localPosition = new Vector3(0f, 1.15f, 0f);
            light.type = LightType.Point;
            light.color = MeshUtil.Rgb(0xffb060);
            light.range = 34f;
            light.intensity = 2.4f;
            root.AddComponent<Campfire>().Init(light, emberMat, 2.4f);

            if (HPQuality.HighDetail) BuildFireParticles(root.transform, 0.62f, new Vector3(0f, 0.9f, 0f));
        }

        /// <summary>
        /// The camp fire. See <see cref="Campfire"/> for the full rationale — in short, this was one
        /// static emissive cone and a constant light, and a fire is defined by movement.
        /// </summary>
        private void BuildCamp()
        {
            float gy = (float)World.GetHeight(0, 0);
            var root = new GameObject("Campfire");
            root.transform.parent = transform;
            root.transform.position = new Vector3(0f, gy, 0f);

            // --- scorched ground -------------------------------------------------
            // Sits 1 cm proud and is nearly black: without it the stones and logs look placed ON the
            // snow, and a fire that has burned all night has very obviously melted down into it.
            var scorch = NewMeshGo("Scorch", MeshUtil.EllipseDisc(2.3f, 2.1f, 18),
                MeshUtil.Surface(MeshUtil.Rgb(0x241d18), 0.18f, ProcTex.RockNormal, 0.5f, 0.6f));
            scorch.transform.SetParent(root.transform, false);
            scorch.transform.localPosition = new Vector3(0f, 0.01f, 0f);

            // --- ring of stones ---------------------------------------------------
            // Hashed radius/offset per stone rather than a perfect circle: a ring somebody actually
            // built is uneven. Index-hashed, never an RNG draw ([rng-lockstep]).
            var rock = MeshUtil.Surface(MeshUtil.Rgb(0x3a3a3a), 0.10f, ProcTex.RockNormal, 1.1f, 1.5f);
            for (int i = 0; i < 9; i++)
            {
                float a = i / 9f * Mathf.PI * 2f + MeshUtil.Hash01(i * 31) * 0.22f;
                float r = 1.15f + MeshUtil.Hash01(i * 17 + 5) * 0.28f;
                Boulder(rock, Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0.19 + MeshUtil.Hash01(i * 7 + 3) * 0.10);
            }

            // --- fuel -------------------------------------------------------------
            // Four charred limbs leaning into a cone. The flame needs something to come out of; a
            // flame with no fuel under it reads as a floating effect rather than as a fire.
            var charred = MeshUtil.Surface(MeshUtil.Rgb(0x2a221c), 0.06f, ProcTex.BarkNormal, 1.0f, 1.2f);
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.PI * 2f + 0.5f;
                var log = NewMeshGo("Log", MeshUtil.Limb(0.085f, 0.055f, 1.25f, 5, 7, 900 + i * 13, 0.05f), charred);
                log.transform.SetParent(root.transform, false);
                log.transform.localPosition = new Vector3(Mathf.Cos(a) * 0.42f, 0.06f, Mathf.Sin(a) * 0.42f);
                // Lean the tops INWARD. Limb builds along +Y, so the Z-tilt tips the top toward local
                // +X and the yaw then aims that tilt. Unity's Euler(0,θ,0) maps +X to (cosθ, 0, -sinθ),
                // and the top has to travel toward the centre — i.e. along (-cos a, -sin a) — which
                // solves to θ = 180° - a. Yawing by -a instead (the intuitive guess) points every log
                // outward and builds a fountain rather than a fire.
                log.transform.localRotation = Quaternion.Euler(0f, 180f - a * Mathf.Rad2Deg, 0f) *
                                              Quaternion.Euler(0f, 0f, 34f + MeshUtil.Hash01(i * 41) * 8f);
            }
            // One fallen log across the ring — asymmetry, and it breaks the tidy cone silhouette.
            var spent = NewMeshGo("SpentLog", MeshUtil.Limb(0.10f, 0.07f, 1.6f, 6, 5, 977, 0.06f), charred);
            spent.transform.SetParent(root.transform, false);
            spent.transform.localPosition = new Vector3(0.35f, 0.10f, -0.7f);
            spent.transform.localRotation = Quaternion.Euler(0f, 28f, 96f);

            // --- ember bed ---------------------------------------------------------
            // What still reads once the flames drop, and what lights the stones from beneath. One
            // shared material so Campfire can pulse every coal with a single SetColor.
            var emberMat = MeshUtil.Emissive(MeshUtil.Rgb(0x3a1b0c), MeshUtil.Rgb(0xff6a1e), 2.6f);
            for (int i = 0; i < 11; i++)
            {
                float a = MeshUtil.Hash01(i * 53 + 1) * Mathf.PI * 2f;
                float r = Mathf.Sqrt(MeshUtil.Hash01(i * 29 + 7)) * 0.62f; // sqrt: even area, not centre-clumped
                var coal = NewMeshGo("Coal", MeshUtil.Rock(0.10f + MeshUtil.Hash01(i * 11) * 0.07f, 4, 6, i), emberMat);
                coal.transform.SetParent(root.transform, false);
                coal.transform.localPosition = new Vector3(Mathf.Cos(a) * r, 0.05f, Mathf.Sin(a) * r);
            }

            // --- light -------------------------------------------------------------
            var fire = new GameObject("FireLight").AddComponent<Light>();
            fire.transform.SetParent(root.transform, false);
            fire.transform.localPosition = new Vector3(0f, 1.0f, 0f);
            fire.type = LightType.Point;
            fire.color = MeshUtil.Rgb(0xff7a3a);
            fire.range = 40f;
            fire.intensity = 3.5f;
            fire.shadows = HPQuality.HighDetail ? LightShadows.Soft : LightShadows.None;

            root.AddComponent<Campfire>().Init(fire, emberMat, 3.5f);

            // --- flame, sparks, smoke ------------------------------------------------
            // Skipped entirely on low detail: three emitters is exactly the kind of steady cost [perf]
            // says to keep off the integrated-GPU path, and the ember bed plus the flicker still sells
            // a fire without them.
            if (HPQuality.HighDetail) BuildFireParticles(root.transform);
        }

        /// <summary>
        /// Flame, sparks and smoke. Particles rather than an animated mesh because a flame's outline
        /// is stochastic — a vertex-animated cone just reads as a wobbling cone.
        /// </summary>
        /// <param name="scale">Overall size of the fire. The brazier reuses this at 0.62 — everything
        /// that has a length unit scales, and everything that is a RATE does not, because a smaller
        /// fire has smaller tongues at the same frequency, not fewer slower ones.</param>
        /// <param name="offset">Local origin, so a basket fire starts at the rim and not at the feet.</param>
        private void BuildFireParticles(Transform parent, float scale = 1f, Vector3 offset = default)
        {
            // Additive and unlit: fire EMITS, so it must not be shaded by the scene, and overlapping
            // tongues have to accumulate rather than occlude each other.
            //
            // Built through MeshUtil.ParticleMaterial, which is the ONLY thing that actually puts a
            // URP material into a transparent blend at runtime. This used to set `_Surface`/`_Blend`
            // and the render queue by hand and nothing else, which leaves the real blend state opaque
            // — see that method for the full explanation. Both this and the smoke below rendered as
            // hard opaque quads because of it.
            var flameMat = MeshUtil.ParticleMaterial(ProcTex.SoftDot, additive: true);
            // Short fade: flame tongues are small and sit right on the logs, so a long one would eat
            // the base of the fire — which is the brightest, most important part of it.
            MeshUtil.SetSoftParticles(flameMat, softFar: 0.35f, camNear: 0.15f, camFar: 0.45f);

            // FLAME ------------------------------------------------------------------
            var flameGo = new GameObject("Flame");
            flameGo.transform.SetParent(parent, false);
            flameGo.transform.localPosition = offset + new Vector3(0f, 0.12f * scale, 0f);
            var flame = flameGo.AddComponent<ParticleSystem>();
            var fmain = flame.main;
            fmain.simulationSpace = ParticleSystemSimulationSpace.World;
            fmain.startLifetime = new ParticleSystem.MinMaxCurve(0.36f, 0.72f);
            fmain.startSpeed = new ParticleSystem.MinMaxCurve(1.1f * scale, 2.3f * scale);
            fmain.startSize = new ParticleSystem.MinMaxCurve(0.22f * scale, 0.46f * scale);
            fmain.gravityModifier = -0.05f;      // hot gas rises: negative gravity, not upward velocity
            fmain.maxParticles = 140;
            fmain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.55f, 0.16f, 0.85f), new Color(1f, 0.80f, 0.34f, 0.85f));

            var femit = flame.emission;
            femit.rateOverTime = 55f;

            var fshape = flame.shape;
            fshape.shapeType = ParticleSystemShapeType.Cone;
            fshape.angle = 14f;
            fshape.radius = 0.30f * scale;

            // Tapering as it rises is what makes tongues instead of a column of dots.
            var fsize = flame.sizeOverLifetime;
            fsize.enabled = true;
            fsize.size = new ParticleSystem.MinMaxCurve(1f, SizeCurve());

            // Orange at the base to dark red at the tip, fading out — a flame does not just vanish,
            // it cools through the reds first.
            var fcol = flame.colorOverLifetime;
            fcol.enabled = true;
            fcol.color = new ParticleSystem.MinMaxGradient(FlameGradient());

            // Turbulence. Without it every tongue rises on a clean parabola and the whole thing reads
            // as a fountain.
            var fnoise = flame.noise;
            fnoise.enabled = true;
            fnoise.strength = new ParticleSystem.MinMaxCurve(0.55f);
            fnoise.frequency = 1.4f;
            fnoise.quality = ParticleSystemNoiseQuality.Medium;
            flameGo.GetComponent<ParticleSystemRenderer>().sharedMaterial = flameMat;

            // SPARKS ------------------------------------------------------------------
            var sparkGo = new GameObject("Sparks");
            sparkGo.transform.SetParent(parent, false);
            sparkGo.transform.localPosition = offset + new Vector3(0f, 0.35f * scale, 0f);
            var spark = sparkGo.AddComponent<ParticleSystem>();
            var smain = spark.main;
            smain.simulationSpace = ParticleSystemSimulationSpace.World;
            smain.startLifetime = new ParticleSystem.MinMaxCurve(1.3f, 3.0f);
            smain.startSpeed = new ParticleSystem.MinMaxCurve(1.8f * scale, 3.6f * scale);
            smain.startSize = new ParticleSystem.MinMaxCurve(0.020f * scale, 0.055f * scale);
            smain.gravityModifier = -0.14f;
            smain.maxParticles = 90;
            smain.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.72f, 0.30f, 1f), new Color(1f, 0.94f, 0.66f, 1f));

            var semit = spark.emission;
            semit.rateOverTime = 11f;

            var sshape = spark.shape;
            sshape.shapeType = ParticleSystemShapeType.Cone;
            sshape.angle = 20f;
            sshape.radius = 0.22f * scale;

            // Sparks wander much harder than flame — they are small enough for the air to throw around.
            var snoise = spark.noise;
            snoise.enabled = true;
            snoise.strength = new ParticleSystem.MinMaxCurve(1.5f);
            snoise.frequency = 0.7f;
            snoise.quality = ParticleSystemNoiseQuality.Low;

            var scol = spark.colorOverLifetime;
            scol.enabled = true;
            scol.color = new ParticleSystem.MinMaxGradient(SparkGradient());
            sparkGo.GetComponent<ParticleSystemRenderer>().sharedMaterial = flameMat;

            // SMOKE -------------------------------------------------------------------
            // Alpha-blended, NOT additive: smoke occludes. Sharing the additive material would make
            // the column glow, which is the single most common way a fire effect goes wrong.
            //
            // **This is the one the owner actually saw** — "the fire smoke is boxes of black". With
            // the blend state left opaque, every smoke quad drew its dark start colour as a flat
            // square with the soft-dot alpha ignored entirely, and with ZWrite still on they occluded
            // each other into a stack of hard-edged boxes.
            var smokeMat = MeshUtil.ParticleMaterial(ProcTex.SoftDot, additive: false);
            // A long fade, because a smoke puff is metres across: the fade has to be wider than the
            // intersection it is hiding or the hard line just moves rather than going away.
            MeshUtil.SetSoftParticles(smokeMat, softFar: 2.2f, camNear: 0.4f, camFar: 1.4f);

            var smokeGo = new GameObject("Smoke");
            smokeGo.transform.SetParent(parent, false);
            smokeGo.transform.localPosition = offset + new Vector3(0f, 0.9f * scale, 0f);
            var smoke = smokeGo.AddComponent<ParticleSystem>();
            var kmain = smoke.main;
            kmain.simulationSpace = ParticleSystemSimulationSpace.World;
            kmain.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6.5f);
            kmain.startSpeed = new ParticleSystem.MinMaxCurve(0.7f * scale, 1.4f * scale);
            kmain.startSize = new ParticleSystem.MinMaxCurve(0.5f * scale, 1.0f * scale);
            kmain.gravityModifier = -0.02f;
            kmain.maxParticles = 70;
            kmain.startColor = new ParticleSystem.MinMaxGradient(new Color(0.14f, 0.13f, 0.13f, 0.30f));

            var kemit = smoke.emission;
            kemit.rateOverTime = 9f;

            var kshape = smoke.shape;
            kshape.shapeType = ParticleSystemShapeType.Cone;
            kshape.angle = 10f;
            kshape.radius = 0.28f * scale;

            // Grows as it rises and dissipates — smoke expands as it cools and mixes.
            var ksize = smoke.sizeOverLifetime;
            ksize.enabled = true;
            ksize.size = new ParticleSystem.MinMaxCurve(1f, SmokeGrowCurve());

            var kcol = smoke.colorOverLifetime;
            kcol.enabled = true;
            kcol.color = new ParticleSystem.MinMaxGradient(SmokeGradient());

            // Drift downwind, matching the direction Weather blows the snow.
            var kvel = smoke.velocityOverLifetime;
            kvel.enabled = true;
            kvel.space = ParticleSystemSimulationSpace.World;
            kvel.x = new ParticleSystem.MinMaxCurve(-0.9f, -0.2f);
            kvel.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.4f);

            var knoise = smoke.noise;
            knoise.enabled = true;
            knoise.strength = new ParticleSystem.MinMaxCurve(0.35f);
            knoise.frequency = 0.35f;
            knoise.quality = ParticleSystemNoiseQuality.Low;
            smokeGo.GetComponent<ParticleSystemRenderer>().sharedMaterial = smokeMat;
        }

        private static AnimationCurve SizeCurve()
        {
            // Puff out fast, then taper to a point: the shape of a tongue of flame.
            var c = new AnimationCurve();
            c.AddKey(0f, 0.55f);
            c.AddKey(0.25f, 1f);
            c.AddKey(1f, 0.10f);
            return c;
        }

        private static AnimationCurve SmokeGrowCurve()
        {
            var c = new AnimationCurve();
            c.AddKey(0f, 0.45f);
            c.AddKey(1f, 1.9f);
            return c;
        }

        private static Gradient FlameGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.00f, 0.80f, 0.40f), 0.00f),
                    new GradientColorKey(new Color(1.00f, 0.46f, 0.12f), 0.45f),
                    new GradientColorKey(new Color(0.55f, 0.11f, 0.03f), 1.00f),
                },
                new[]
                {
                    new GradientAlphaKey(0.00f, 0.00f),
                    new GradientAlphaKey(1.00f, 0.16f),
                    new GradientAlphaKey(0.00f, 1.00f),
                });
            return g;
        }

        private static Gradient SparkGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.00f, 0.90f, 0.60f), 0.00f),
                    new GradientColorKey(new Color(1.00f, 0.42f, 0.10f), 1.00f),
                },
                new[]
                {
                    new GradientAlphaKey(1.00f, 0.00f),
                    new GradientAlphaKey(0.85f, 0.55f),
                    new GradientAlphaKey(0.00f, 1.00f),
                });
            return g;
        }

        private static Gradient SmokeGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    // Warm right at the base, where the flame is still lighting it, then plain grey.
                    new GradientColorKey(new Color(0.42f, 0.30f, 0.22f), 0.00f),
                    new GradientColorKey(new Color(0.17f, 0.17f, 0.18f), 0.35f),
                    new GradientColorKey(new Color(0.22f, 0.23f, 0.25f), 1.00f),
                },
                new[]
                {
                    new GradientAlphaKey(0.00f, 0.00f),
                    new GradientAlphaKey(0.55f, 0.20f),
                    new GradientAlphaKey(0.00f, 1.00f),
                });
            return g;
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
        /// the x axis to match the sim's handedness (see [handedness]), and its compass labels put **W at
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
            // The old sky material has to go by HAND. ReleaseWorldMaterials deliberately EXCLUDES
            // _skyMat (it must outlive the sweep, which runs before the rebuild), and it could not
            // find it anyway — the skybox hangs off RenderSettings, not off a Renderer. So every
            // reseed simply orphaned one: a leak inside the very code written to stop leaks.
            if (_skyMat != null) { Destroy(_skyMat); _skyMat = null; }

            var shader = Shader.Find("Metoh/NightSky");
            if (shader == null)
            {
                // Don't fail silently into a black void — this is exactly the "menu button that does
                // nothing" failure mode from [feedback]. Keep the old flat fill and say why.
                BootReport.MissingShader("Metoh/NightSky",
                    "the sky is a flat colour with no moon, no stars and no horizon ridgeline");
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
        /// tree/collider lockstep (UNITY_NOTES [rng-lockstep]), and being a pure function of the index means
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

        private static List<Vector3>[] NewSwayBuckets(int n)
        {
            var b = new List<Vector3>[n];
            for (int i = 0; i < n; i++) b[i] = new List<Vector3>();
            return b;
        }

        private GameObject NewCombinedGo(string name, List<CombineInstance> combines, Material mat,
                                         List<Vector3> swayMeta = null)
        {
            // Chunking leaves empty buckets (a grid cell that is all lake, or all camp clearing).
            // An empty combine yields a zero-vertex mesh and a renderer that costs culling work for
            // nothing, so skip them outright.
            if (combines.Count == 0) return null;
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.CombineMeshes(combines.ToArray(), true, true);
            if (swayMeta != null) BakeSwayData(mesh, combines, swayMeta);
            mesh.RecalculateBounds();
            return NewMeshGo(name, mesh, mat);
        }

        /// <summary>
        /// Write per-vertex sway data into UV2 of a merged forest chunk. Without this the sway shader
        /// has nothing to drive it and the forest renders rigid.
        ///
        /// THE PROBLEM THIS SOLVES. After CombineMeshes a chunk is one mesh of ~40 trees, and a vertex
        /// in it knows only its own position — not which tree it belongs to, nor how far up that tree
        /// it sits. Both are needed: sway has to be zero at each trunk's OWN base (a chunk-space height
        /// would leave uphill trees rigid and make downhill ones thrash, because they stand on sloped
        /// ground at different altitudes), and each tree needs its own phase or the whole stand leans
        /// in unison, which reads as the ground tilting.
        ///
        /// Both are recoverable here because the combine list is walked in the same order it was
        /// built: instance i contributed exactly `combines[i].mesh.vertexCount` vertices, in order, and
        /// `swayMeta[i]` is that tree's (baseY, 1/height, phase). So the spans line up by construction.
        ///
        /// The vertices are already in world space — CombineMeshes baked each instance's TRS in — so
        /// the height comparison is a straight subtraction and needs no transform.
        /// </summary>
        private static void BakeSwayData(Mesh mesh, List<CombineInstance> combines, List<Vector3> swayMeta)
        {
            var verts = mesh.vertices;
            var uv2 = new Vector2[verts.Length];
            int v = 0;
            int n = Mathf.Min(combines.Count, swayMeta.Count);
            for (int i = 0; i < n; i++)
            {
                Mesh src = combines[i].mesh;
                if (src == null) continue;
                int count = src.vertexCount;
                Vector3 meta = swayMeta[i]; // (baseY, 1/height, phase)
                for (int k = 0; k < count && v < verts.Length; k++, v++)
                {
                    float weight = Mathf.Clamp01((verts[v].y - meta.x) * meta.y);
                    uv2[v] = new Vector2(weight, meta.z);
                }
            }
            // Any tail left over (a defensive case — the two lists are built in lockstep) stays at
            // zero weight, i.e. rigid, which is the safe failure: a still tree, never a flying one.
            mesh.SetUVs(1, uv2);
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
            // Built from MetricBox rather than a scaled primitive cube — see MeshUtil.MetricBox for
            // the UV-stretching bug that fixes. It was visible on every structure in the game: the
            // hut's plank grain ran about 3x wider on the long walls than on the ends, from one
            // material, because a primitive cube gives every face 0..1 UVs regardless of its size.
            // Both cached per build: the four tower rails are one mesh and one material between them,
            // not eight objects. See the cache fields above Build().
            if (!_boxMeshes.TryGetValue(size, out Mesh mesh))
            {
                mesh = MeshUtil.MetricBox(size);
                _boxMeshes[size] = mesh;
            }
            var key = (color, emissive, glow);
            if (!_boxMats.TryGetValue(key, out Material mat))
            {
                // Everything AddBox builds is a made object — hut planks, crates, the tower platform
                // and rails — so they all get sawn-timber grain. A lit window is the exception:
                // emission is the whole point of it and surface detail would only fight the glow.
                //
                // Tiling is REPEATS PER METRE, since MetricBox's UVs are in metres: 0.42 gives a plank
                // roughly 2.4 m long, the same on every face of every box whatever its proportions.
                mat = emissive.HasValue
                    ? MeshUtil.Emissive(color, emissive.Value, glow)
                    : MeshUtil.Surface(color, 0.13f, ProcTex.BarkNormal, 0.85f, 0.42f);
                _boxMats[key] = mat;
            }

            var go = NewMeshGo(name, mesh, mat);
            go.transform.parent = parent.transform;
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one; // the size lives in the mesh now, not the transform
        }
    }
}
