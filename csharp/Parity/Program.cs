using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using HollowPines.Sim;

namespace HollowPines.Parity
{
    /// <summary>
    /// Determinism gate for the C# sim port. Two layers:
    ///   1. GOLDEN CROSS-CHECK — reconstructs the TS golden scenarios (scratchpad/gen-golden.ts) in C#
    ///      and asserts the results match csharp/parity/golden.json (dumped from the real TS sim).
    ///   2. MIRRORED PROPERTY TESTS — the same behavioural invariants the maintained vitest suite pins
    ///      (server/test/sim.*.test.ts, caves.test.ts, specialties.test.ts), re-expressed in C#.
    ///
    /// Integer / pure-arithmetic paths are checked EXACTLY; transcendental paths use a 1e-9 epsilon
    /// (JS vs .NET last-ULP; harmless — the host is authoritative). Run: dotnet run --project csharp/Parity
    /// </summary>
    internal static class Program
    {
        private const double Eps = 1e-9;
        private static int _failures;
        private static readonly StepModifiers Mods =
            new StepModifiers { SpeedMul = 1, BatteryDrainMul = 1, StaminaDrainMul = 1 };

        private static int Main()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "golden.json");
            if (!File.Exists(path)) { Console.Error.WriteLine($"golden.json not found at {path}"); return 2; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var g = doc.RootElement;
            uint seed = (uint)g.GetProperty("seed").GetInt64();

            Console.WriteLine("== golden cross-check ==");
            RngStream(g, seed);
            NoiseSamples(g, seed);
            TerrainSamples(g, seed);
            CavesCheck(g, seed);
            CaveHelpers(g, seed);
            PathsCheck(g, seed);
            CollidersCheck(g, seed);
            WorldCheck(g, seed);
            HunterTrajectory(g, seed);
            BigfootTrajectory(g, seed);
            SpecialtiesGolden(g);

            Console.WriteLine("\n== mirrored property tests (from server/test/*.ts) ==");
            DeterminismTests(seed);
            StaminaGateTest(seed);
            BatteryTest(seed);
            StaminaCeilingTests(seed);
            LeapTests(seed);
            EdgeNaNTest(seed);
            CaveLayoutTests(seed);
            SpecialtyDealTests();

            Console.WriteLine(_failures == 0
                ? "\nPARITY OK — C# sim matches the TypeScript golden fixture and the vitest invariants."
                : $"\nPARITY FAILED — {_failures} mismatch(es).");
            return _failures == 0 ? 0 : 1;
        }

        // ---------- golden cross-check ----------

        private static void RngStream(JsonElement g, uint seed)
        {
            var rand = Rng.Mulberry32(seed);
            var arr = g.GetProperty("rngStream");
            for (int i = 0; i < arr.GetArrayLength(); i++) ExactD($"rng[{i}]", rand(), arr[i].GetDouble());
        }

        private static void NoiseSamples(JsonElement g, uint seed)
        {
            var noise = Rng.MakeValueNoise(seed);
            foreach (var s in g.GetProperty("noiseSamples").EnumerateArray())
                Near($"noise({s.GetProperty("x").GetDouble()},{s.GetProperty("y").GetDouble()})",
                    noise(s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble()), s.GetProperty("v").GetDouble());
        }

        private static void TerrainSamples(JsonElement g, uint seed)
        {
            var height = Terrain.MakeTerrain(seed);
            foreach (var s in g.GetProperty("terrainSamples").EnumerateArray())
                Near($"terrain({s.GetProperty("x").GetDouble()},{s.GetProperty("z").GetDouble()})",
                    height(s.GetProperty("x").GetDouble(), s.GetProperty("z").GetDouble()), s.GetProperty("h").GetDouble());
        }

        private static void CavesCheck(JsonElement g, uint seed)
        {
            var caves = Caves.GenerateCaves(seed);
            var expected = g.GetProperty("caves");
            ExactI("cave count", caves.Count, expected.GetArrayLength());
            for (int i = 0; i < caves.Count; i++)
            {
                ExactD($"cave[{i}].x", caves[i].X, expected[i].GetProperty("x").GetDouble());
                ExactD($"cave[{i}].z", caves[i].Z, expected[i].GetProperty("z").GetDouble());
            }
        }

        private static void CaveHelpers(JsonElement g, uint seed)
        {
            var caves = Caves.GenerateCaves(seed);
            var emerge = g.GetProperty("caveEmerge");
            for (int i = 0; i < caves.Count; i++)
            {
                var e = Caves.CaveEmergePoint(caves[i]);
                Near($"emerge[{i}].x", e.X, emerge[i].GetProperty("x").GetDouble());
                Near($"emerge[{i}].z", e.Z, emerge[i].GetProperty("z").GetDouble());
                Near($"emerge[{i}].yaw", e.Yaw, emerge[i].GetProperty("yaw").GetDouble());
            }
            int p = 0;
            foreach (var probe in g.GetProperty("nearestProbes").EnumerateArray())
                ExactI($"nearestCaveIndex probe[{p++}]",
                    Caves.NearestCaveIndex(caves, probe.GetProperty("x").GetDouble(), probe.GetProperty("z").GetDouble()),
                    probe.GetProperty("i").GetInt32());
        }

        private static void PathsCheck(JsonElement g, uint seed)
        {
            var paths = Paths.GeneratePaths(seed);
            var ps = g.GetProperty("pathSummary");
            ExactI("path count", paths.Count, ps.GetProperty("count").GetInt32());

            var shapes = ps.GetProperty("shapes");
            for (int i = 0; i < paths.Count && i < shapes.GetArrayLength(); i++)
            {
                var s = shapes[i];
                // Point COUNT is exact-integer: it depends on where the meander crosses the map edge,
                // so an off-by-one here means the heading walk diverged, not that a float drifted.
                ExactI($"path[{i}].pts", paths[i].Pts.Count, s.GetProperty("n").GetInt32());
                Near($"path[{i}].halfWidth", paths[i].HalfWidth, s.GetProperty("halfWidth").GetDouble());
                Near($"path[{i}].first.x", paths[i].Pts[0].X, s.GetProperty("first").GetProperty("x").GetDouble());
                Near($"path[{i}].first.z", paths[i].Pts[0].Z, s.GetProperty("first").GetProperty("z").GetDouble());
                var last = paths[i].Pts[paths[i].Pts.Count - 1];
                Near($"path[{i}].last.x", last.X, s.GetProperty("last").GetProperty("x").GetDouble());
                Near($"path[{i}].last.z", last.Z, s.GetProperty("last").GetProperty("z").GetDouble());
            }

            foreach (var p in ps.GetProperty("depthProbes").EnumerateArray())
            {
                double x = p.GetProperty("x").GetDouble();
                double z = p.GetProperty("z").GetDouble();
                Near($"pathDepth({x},{z})", Paths.PathDepth(paths, x, z), p.GetProperty("d").GetDouble());
                Near($"pathDepth({x},{z},margin)", Paths.PathDepth(paths, x, z, 1.2), p.GetProperty("dm").GetDouble());
            }
        }

        private static void CollidersCheck(JsonElement g, uint seed)
        {
            var caves = Caves.GenerateCaves(seed);
            var paths = Paths.GeneratePaths(seed);
            var colliders = WorldData.BuildColliders(seed, caves, paths);
            var cs = g.GetProperty("colliderSummary");
            ExactI("collider count", colliders.Count, cs.GetProperty("count").GetInt32());
            var first3 = cs.GetProperty("first3");
            for (int i = 0; i < 3; i++)
            {
                Near($"collider[{i}].x", colliders[i].X, first3[i].GetProperty("x").GetDouble());
                Near($"collider[{i}].z", colliders[i].Z, first3[i].GetProperty("z").GetDouble());
                Near($"collider[{i}].r", colliders[i].R, first3[i].GetProperty("r").GetDouble());
            }
            var last = cs.GetProperty("last");
            var lc = colliders[colliders.Count - 1];
            ExactD("lookout.x", lc.X, last.GetProperty("x").GetDouble());
            ExactD("lookout.climbH", lc.ClimbH ?? -1, last.GetProperty("climbH").GetDouble());
        }

        private static void WorldCheck(JsonElement g, uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var ws = g.GetProperty("worldSummary");
            ExactI("world colliderCount", world.Colliders.Count, ws.GetProperty("colliderCount").GetInt32());
            ExactI("world climbableCount", world.Climbables.Count, ws.GetProperty("climbableCount").GetInt32());
            ExactI("world fallenLogCount", world.FallenLogs.Count, ws.GetProperty("fallenLogCount").GetInt32());
            var fl = ws.GetProperty("firstLog");
            Near("firstLog.ax", world.FallenLogs[0].Ax, fl.GetProperty("ax").GetDouble());
            Near("firstLog.az", world.FallenLogs[0].Az, fl.GetProperty("az").GetDouble());
        }

        private static void HunterTrajectory(JsonElement g, uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            double gy = world.GetHeight(0, 0);
            var st = new PlayerSimState
            {
                X = 0, Z = 0, FeetY = gy, GroundY = gy, Vy = 0, Grounded = true,
                Yaw = 0, Stamina = 100, Exhausted = false, Battery = 100, CurEye = 1.7,
                FlashlightOn = true, IsBigfoot = false, EyeHeight = 1.7,
            };
            var input = new MoveInput { W = true, Yaw = 0.6, Sprint = true, Dt = 1.0 / 20.0 };
            var expected = g.GetProperty("hunterTrajectory");
            int ei = 0;
            for (int i = 0; i < 40; i++)
            {
                Movement.StepPlayer(st, input, world, Mods);
                if (i % 10 == 9)
                {
                    var e = expected[ei++];
                    Near($"hunter[{i}].x", st.X, e.GetProperty("x").GetDouble());
                    Near($"hunter[{i}].z", st.Z, e.GetProperty("z").GetDouble());
                    Near($"hunter[{i}].stamina", st.Stamina, e.GetProperty("stamina").GetDouble());
                    Near($"hunter[{i}].battery", st.Battery, e.GetProperty("battery").GetDouble());
                }
            }
        }

        private static void BigfootTrajectory(JsonElement g, uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            double gy = world.GetHeight(30, 30);
            var st = new PlayerSimState
            {
                X = 30, Z = 30, FeetY = gy, GroundY = gy, Vy = 0, Grounded = true,
                Yaw = 1.0, Stamina = 100, Exhausted = false, Battery = 100, CurEye = 2.4,
                FlashlightOn = false, IsBigfoot = true, EyeHeight = 2.4,
            };
            var expected = g.GetProperty("bigfootTrajectory");
            int ei = 0;
            for (int i = 0; i < 30; i++)
            {
                var input = new MoveInput { W = true, Yaw = 1.0, Leap = i == 0, Dt = 1.0 / 20.0 };
                Movement.StepPlayer(st, input, world, Mods);
                if (i % 6 == 5)
                {
                    var e = expected[ei++];
                    Near($"bigfoot[{i}].x", st.X, e.GetProperty("x").GetDouble());
                    Near($"bigfoot[{i}].feetY", st.FeetY, e.GetProperty("feetY").GetDouble());
                    Near($"bigfoot[{i}].vy", st.Vy, e.GetProperty("vy").GetDouble());
                    Near($"bigfoot[{i}].stamina", st.Stamina, e.GetProperty("stamina").GetDouble());
                }
            }
        }

        private static void SpecialtiesGolden(JsonElement g)
        {
            var s = g.GetProperty("specialties");
            var ids = s.GetProperty("specialtyIds");
            for (int i = 0; i < ids.GetArrayLength(); i++)
                CheckStr($"specialtyId[{i}]", Specialties.SpecialtyIds[i], ids[i].GetString());
            var names = s.GetProperty("characterNames");
            for (int i = 0; i < names.GetArrayLength(); i++)
                CheckStr($"characterName[{i}]", Specialties.CharacterName[Specialties.SpecialtyIds[i]], names[i].GetString());

            foreach (var probe in s.GetProperty("getterProbe").EnumerateArray())
            {
                string id = probe.GetProperty("id").GetString();
                Near($"reviveMul({id})", Specialties.ReviveMul(id), probe.GetProperty("reviveMul").GetDouble());
                Near($"staminaMax({id})", Specialties.StaminaMax(id), probe.GetProperty("staminaMax").GetDouble());
                Near($"staminaDrainMul({id})", Specialties.StaminaDrainMul(id), probe.GetProperty("staminaDrainMul").GetDouble());
                Near($"filmProgressMul({id})", Specialties.FilmProgressMul(id), probe.GetProperty("filmProgressMul").GetDouble());
                Near($"filmRangeMul({id})", Specialties.FilmRangeMul(id), probe.GetProperty("filmRangeMul").GetDouble());
                Near($"clueWindowMul({id})", Specialties.ClueWindowMul(id), probe.GetProperty("clueWindowMul").GetDouble());
                Near($"evidenceSightMul({id})", Specialties.EvidenceSightMul(id), probe.GetProperty("evidenceSightMul").GetDouble());
                Near($"hearRangeMul({id})", Specialties.HearRangeMul(id), probe.GetProperty("hearRangeMul").GetDouble());
                Near($"roarDirPersistSec({id})", Specialties.RoarDirPersistSec(id), probe.GetProperty("roarDirPersistSec").GetDouble());
                Near($"footstepVolumeMul({id})", Specialties.FootstepVolumeMul(id), probe.GetProperty("footstepVolumeMul").GetDouble());
            }

            // Deterministic deals reproduced with the same seeded rng (mulberry32(999)).
            var sids = new List<string>();
            foreach (var e in s.GetProperty("dealSids").EnumerateArray()) sids.Add(e.GetString());
            CheckDeal("dealPlain", Specialties.DealSpecialties(sids, null, Rng.Mulberry32(999)), s.GetProperty("dealPlain"));
            CheckDeal("dealForced",
                Specialties.DealSpecialties(sids, new Dictionary<string, string> { { "c", "photo" } }, Rng.Mulberry32(999)),
                s.GetProperty("dealForced"));
        }

        // ---------- mirrored property tests ----------

        private static PlayerSimState MakeState(GameWorld world, Action<PlayerSimState> over = null)
        {
            double gy = world.GetHeight(0, 0);
            var st = new PlayerSimState
            {
                X = 0, Z = 0, FeetY = gy, GroundY = gy, Vy = 0, Grounded = true, Yaw = 0,
                Stamina = 100, Exhausted = false, Battery = 100, CurEye = Player.EyeHeight,
                FlashlightOn = false, IsBigfoot = false, EyeHeight = Player.EyeHeight,
            };
            over?.Invoke(st);
            return st;
        }

        private static MoveInput MakeInput(Action<MoveInputBuilder> over = null)
        {
            var b = new MoveInputBuilder { Dt = 0.05 };
            over?.Invoke(b);
            return b.Build();
        }

        private sealed class MoveInputBuilder
        {
            public bool W, Sprint, Leap, Jump, Crouch, Vault, Climb;
            public double Yaw, Dt;
            public MoveInput Build() => new MoveInput
            { W = W, Sprint = Sprint, Leap = Leap, Jump = Jump, Crouch = Crouch, Vault = Vault, Climb = Climb, Yaw = Yaw, Dt = Dt };
        }

        private static void DeterminismTests(uint seed)
        {
            var a = GameWorld.MakeWorld(seed);
            var b = GameWorld.MakeWorld(seed);
            bool sameColliders = a.Colliders.Count == b.Colliders.Count;
            for (int i = 0; sameColliders && i < a.Colliders.Count; i++)
                sameColliders &= a.Colliders[i].X == b.Colliders[i].X && a.Colliders[i].Z == b.Colliders[i].Z && a.Colliders[i].R == b.Colliders[i].R;
            Check("same seed rebuilds identical colliders", sameColliders);
            foreach (var xz in new[] { (0.0, 0.0), (12.0, -34.0), (-120.0, 200.0), (399.0, -399.0) })
                Check($"same seed identical terrain ({xz.Item1},{xz.Item2})", a.GetHeight(xz.Item1, xz.Item2) == b.GetHeight(xz.Item1, xz.Item2));
            var c = GameWorld.MakeWorld(seed + 1);
            bool differs = a.Colliders.Count != c.Colliders.Count;
            for (int i = 0; !differs && i < a.Colliders.Count; i++)
                differs = a.Colliders[i].X != c.Colliders[i].X || a.Colliders[i].Z != c.Colliders[i].Z;
            Check("different seed produces a different world", differs);
            foreach (var xz in new[] { (400.0, 400.0), (-400.0, -400.0), (400.0, -400.0) })
                Check($"terrain finite at corner ({xz.Item1},{xz.Item2})", double.IsFinite(a.GetHeight(xz.Item1, xz.Item2)));
        }

        private static void StaminaGateTest(uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var st = MakeState(world);
            for (int i = 0; i < 200 && !st.Exhausted; i++)
                Movement.StepPlayer(st, MakeInput(b => { b.W = true; b.Sprint = true; }), world, Mods);
            Check("sprinting drains to exhaustion", st.Exhausted && st.Stamina == 0);

            bool regainedBelowThreshold = true;
            for (int i = 0; i < 200 && st.Exhausted; i++)
            {
                Movement.StepPlayer(st, MakeInput(), world, Mods);
                if (st.Exhausted && st.Stamina >= Player.StaminaRecover) regainedBelowThreshold = false;
            }
            Check("exhausted clears only at/after recovery threshold",
                !st.Exhausted && regainedBelowThreshold && st.Stamina >= Player.StaminaRecover);
        }

        private static void BatteryTest(uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var st = MakeState(world, s => s.FlashlightOn = true);
            Movement.StepPlayer(st, MakeInput(b => b.Dt = 1), world, Mods);
            Near("battery drains by drainPerSec over 1s", st.Battery, 100 - Player.BatteryDrainPerSec);
            for (int i = 0; i < 200 && st.FlashlightOn; i++) Movement.StepPlayer(st, MakeInput(b => b.Dt = 1), world, Mods);
            Check("battery empties and flashlight cuts out", st.Battery == 0 && !st.FlashlightOn);
        }

        private static void StaminaCeilingTests(uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var st = MakeState(world, s => s.Stamina = 99);
            for (int i = 0; i < 20; i++) Movement.StepPlayer(st, MakeInput(b => b.Dt = 1), world, Mods);
            Check("regen caps at 100 by default", st.Stamina == 100);

            var st2 = MakeState(world, s => s.Stamina = 99);
            var mods = new StepModifiers { SpeedMul = 1, BatteryDrainMul = 1, StaminaDrainMul = 1, StaminaMax = 150 };
            for (int i = 0; i < 20; i++) Movement.StepPlayer(st2, MakeInput(b => b.Dt = 1), world, mods);
            Check("regen climbs to a raised max (150)", st2.Stamina == 150);
        }

        private static void LeapTests(uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var st = MakeState(world, s => s.IsBigfoot = true);
            double groundY = st.GroundY;
            Movement.StepPlayer(st, MakeInput(b => b.Leap = true), world, Mods);
            Check("leap leaves the ground", !st.Grounded);
            Near("leap stamina = 100 - cost + regen*dt", st.Stamina, 100 - Player.LeapStaminaCost + Player.StaminaRegenPerSec * 0.05);
            Near("leap vy = leapSpeed - g*dt", st.Vy, Player.LeapSpeed - Player.Gravity * 0.05);

            double apex = st.FeetY;
            for (int i = 0; i < 200 && !st.Grounded; i++)
            {
                Movement.StepPlayer(st, MakeInput(), world, Mods);
                apex = System.Math.Max(apex, st.FeetY);
            }
            double analytic = (Player.LeapSpeed * Player.LeapSpeed) / (2 * Player.Gravity);
            Check("leap apex near analytic v^2/2g", (apex - groundY) > analytic - 0.3 && (apex - groundY) < analytic + 0.4);
            Check("leap comes back down", st.Grounded);

            var st2 = MakeState(world, s => { s.IsBigfoot = true; s.Stamina = Player.LeapStaminaCost - 1; });
            Movement.StepPlayer(st2, MakeInput(b => b.Leap = true), world, Mods);
            Check("won't leap without enough stamina", st2.Grounded);
        }

        private static void EdgeNaNTest(uint seed)
        {
            var world = GameWorld.MakeWorld(seed);
            var st = MakeState(world, s => { s.X = 399; s.Z = -399; });
            for (int i = 0; i < 50; i++)
            {
                int yaw = i;
                Movement.StepPlayer(st, MakeInput(b => { b.W = true; b.Sprint = true; b.Yaw = yaw; }), world, Mods);
            }
            Check("no NaN when hammered at the world edge",
                double.IsFinite(st.X) && double.IsFinite(st.Z) && double.IsFinite(st.FeetY)
                && System.Math.Abs(st.X) <= 400 && System.Math.Abs(st.Z) <= 400);
        }

        private static void CaveLayoutTests(uint seed)
        {
            var caves = Caves.GenerateCaves(seed);
            Check("cave count == configured", caves.Count == CaveGen.Count);
            bool ok = true;
            for (int i = 0; i < caves.Count; i++)
            {
                double r = SimMath.Hypot(caves[i].X, caves[i].Z);
                ok &= r >= CaveGen.MinRadius - 1 && r <= CaveGen.MinRadius + CaveGen.RadiusSpan + 1;
                for (int j = i + 1; j < caves.Count; j++)
                    ok &= SimMath.Hypot(caves[i].X - caves[j].X, caves[i].Z - caves[j].Z) >= CaveGen.MinSpacing;
            }
            Check("caves well-spaced on the outer ring", ok);
        }

        private static void SpecialtyDealTests()
        {
            var five = new List<string> { "a", "b", "c", "d", "e" };
            var deal = Specialties.DealSpecialties(five, null, Rng.Mulberry32(1));
            Check("five searchers get distinct specialties", DistinctCount(five, deal) == 5);

            var three = new List<string> { "a", "b", "c" };
            Check("three searchers still distinct", DistinctCount(three, Specialties.DealSpecialties(three, null, Rng.Mulberry32(2))) == 3);

            var forced = new Dictionary<string, string> { { "c", "photo" } };
            bool held = true;
            for (int i = 0; i < 50; i++)
            {
                var d = Specialties.DealSpecialties(five, forced, Rng.Mulberry32((uint)(100 + i)));
                held &= d["c"] == "photo";
                foreach (var sid in five) if (sid != "c") held &= d[sid] != "photo";
                held &= DistinctCount(five, d) == 5;
            }
            Check("forced pick honoured and never duplicated", held);

            Check("isSpecialtyId validates", Specialties.IsSpecialtyId("tracking") && !Specialties.IsSpecialtyId("bigfoot") && !Specialties.IsSpecialtyId(null));
        }

        private static int DistinctCount(List<string> sids, Dictionary<string, string> deal)
        {
            var set = new HashSet<string>();
            foreach (var s in sids) set.Add(deal[s]);
            return set.Count;
        }

        // ---------- assertions ----------

        private static void Near(string name, double got, double want)
        {
            double d = System.Math.Abs(got - want);
            if (d <= Eps) Console.WriteLine($"  ok   {name}");
            else { _failures++; Console.WriteLine($"  FAIL {name}: got {got:R}, want {want:R} (|d|={d:R})"); }
        }

        private static void ExactD(string name, double got, double want)
        {
            if (got == want) Console.WriteLine($"  ok   {name}");
            else { _failures++; Console.WriteLine($"  FAIL {name}: got {got:R}, want {want:R} (must be exact)"); }
        }

        private static void ExactI(string name, int got, int want)
        {
            if (got == want) Console.WriteLine($"  ok   {name}");
            else { _failures++; Console.WriteLine($"  FAIL {name}: got {got}, want {want} (must be exact)"); }
        }

        private static void CheckStr(string name, string got, string want)
        {
            if (got == want) Console.WriteLine($"  ok   {name}");
            else { _failures++; Console.WriteLine($"  FAIL {name}: got '{got}', want '{want}'"); }
        }

        private static void Check(string name, bool ok)
        {
            if (ok) Console.WriteLine($"  ok   {name}");
            else { _failures++; Console.WriteLine($"  FAIL {name}"); }
        }

        private static void CheckDeal(string name, Dictionary<string, string> got, JsonElement want)
        {
            foreach (var prop in want.EnumerateObject())
            {
                string k = prop.Name;
                string w = prop.Value.GetString();
                if (got.TryGetValue(k, out var gv) && gv == w) Console.WriteLine($"  ok   {name}[{k}]");
                else { _failures++; Console.WriteLine($"  FAIL {name}[{k}]: got '{(got.ContainsKey(k) ? got[k] : "∅")}', want '{w}'"); }
            }
        }
    }
}
