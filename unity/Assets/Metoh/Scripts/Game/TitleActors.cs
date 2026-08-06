// Staged, animated bodies in the title cinematic.
//
// The title card used to be three camera moves over an empty valley. That was the right call when the
// only body in the game was a capsule — putting one on screen would have advertised the weakest thing
// in the build. Now that there are jointed animated figures, the menu backdrop is the first and
// longest look anybody gets at them, and an empty valley is leaving the whole pass off the poster.
//
// These are NOT players. They are three loose avatars driven by a synthetic AvatarInput, built when
// the menu appears and destroyed the moment a connection comes up. Nothing here touches FishNet,
// GameManager or the sim — a title actor cannot desync a match because it does not exist to one.
//
// The staging is written against the shot list in TitleMenu.OrbitCamp and only makes sense next to
// it: searchers around the fire in the camp shot, the Yeti crossing the corridor in the trail shot,
// nobody at all in the high push-back, where a figure would be four pixels tall.
using System.Collections.Generic;
using Metoh.Sim;
using UnityEngine;

namespace Metoh.Game
{
    public static class TitleActors
    {
        private const int SearcherCount = 3;

        private static Transform _root;
        private static Avatar _yeti;
        private static Transform _yetiHolder;
        private static readonly List<Avatar> _searchers = new List<Avatar>();
        private static readonly List<Transform> _searcherHolders = new List<Transform>();
        private static Material _fur, _eye, _gear;
        private static readonly List<Material> _cloth = new List<Material>();
        private static TorchBeam _beam;

        /// <summary>Build the cast when the menu comes up; tear it down the instant we connect.</summary>
        public static void SetActive(bool on)
        {
            if (on == (_root != null)) return;
            if (on) Build(); else Teardown();
        }

        // ------------------------------------------------------------------ build

        private static void Build()
        {
            var world = WorldBuilder.EnsureWorld();
            if (world == null) return;

            _root = new GameObject("TitleActors").transform;

            _fur = MeshUtil.Surface(MeshUtil.Rgb(0x2a2018), 0.08f, ProcTex.FurNormal, 1.15f, 2.2f);
            _eye = MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffcc55), 3.5f);
            _gear = MeshUtil.Surface(MeshUtil.Rgb(0x3a3630), 0.16f, ProcTex.FabricNormal, 0.7f, 3.5f);

            _yetiHolder = new GameObject("Yeti").transform;
            _yetiHolder.SetParent(_root, false);
            _yeti = Avatar.BuildYeti(_yetiHolder, _fur, _eye, 1337);
            Weather.AttachBreath(_yeti.HeadAnchor, true);

            // Three of the five specialty colours, so the team reads as a team of individuals rather
            // than as a colour swatch. Same palette HPPlayer deals from.
            int[] colors = { 0x8ac28a, 0xc2b27a, 0x7a9ac2 };
            for (int i = 0; i < SearcherCount; i++)
            {
                var holder = new GameObject("Searcher" + i).transform;
                holder.SetParent(_root, false);

                var cloth = MeshUtil.Surface(MeshUtil.Rgb(colors[i]), 0.24f, ProcTex.FabricNormal, 0.8f, 3f);
                _cloth.Add(cloth);

                var a = Avatar.BuildSearcher(holder, cloth, _gear, 400 + i * 97);
                Weather.AttachBreath(a.HeadAnchor, false);
                _searchers.Add(a);
                _searcherHolders.Add(holder);

                // One of them has their torch lit. It is the only moving light in the shot and the only
                // place the new beam gets shown off before you are holding one yourself.
                if (i == 0)
                {
                    var lightGo = new GameObject("Torch");
                    lightGo.transform.SetParent(a.TorchAnchor, false);
                    lightGo.transform.localPosition = new Vector3(0f, -0.06f, 0.06f);
                    lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    var l = lightGo.AddComponent<Light>();
                    l.type = LightType.Spot;
                    l.range = 55f;
                    l.spotAngle = 62f;
                    l.innerSpotAngle = 38f;
                    l.intensity = 14f;
                    l.color = MeshUtil.Rgb(0xffe9c4);
                    l.shadows = LightShadows.None; // a menu backdrop does not need a shadow map
                    _beam = TorchBeam.Build(lightGo.transform, l.range, l.spotAngle);
                    _beam?.SetOn(true);
                }
            }

            // Park everyone at camp until the first Tick stages them, so nobody spawns visible at the
            // world origin for a frame.
            PlaceAtCamp(world);
        }

        private static void Teardown()
        {
            _beam?.Dispose();
            _beam = null;
            _yeti?.Dispose();
            _yeti = null;
            foreach (var a in _searchers) a?.Dispose();
            _searchers.Clear();
            _searcherHolders.Clear();

            // Materials are native objects the GC does not collect — the leak the realism pass swept
            // out of the world builder. A player who backs out to the menu and reconnects repeatedly
            // would otherwise accumulate a set every time.
            if (_fur != null) Object.Destroy(_fur);
            if (_eye != null) Object.Destroy(_eye);
            if (_gear != null) Object.Destroy(_gear);
            foreach (var m in _cloth) if (m != null) Object.Destroy(m);
            _cloth.Clear();
            _fur = _eye = _gear = null;

            if (_root != null) Object.Destroy(_root.gameObject);
            _root = null;
            _yetiHolder = null;
        }

        // ------------------------------------------------------------------ staging

        /// <summary>
        /// Drive the cast for the current shot. Called from OrbitCamp, which has already worked out
        /// which shot is running and how far through it we are — recomputing that here would be a
        /// second copy of the shot list able to drift out of step with the camera it is staged for.
        /// </summary>
        public static void Tick(int shot, float k, float dt)
        {
            if (_root == null) return;
            var world = WorldBuilder.World;
            if (world == null) return;

            switch (shot)
            {
                case 0: StageCamp(world, k, dt); break;
                case 1: StageTrail(world, k, dt); break;
                default: HideAll(); break;   // the high push-back: everyone is sub-pixel from up there
            }
        }

        /// <summary>Shot 1 — the team stood around the fire, idling. The Yeti is not in this shot.</summary>
        private static void StageCamp(GameWorld world, float k, float dt)
        {
            SetYetiVisible(false);
            for (int i = 0; i < _searchers.Count; i++)
            {
                _searchers[i].SetVisible(true);
                // Spread around the fire, angled inward. Not evenly spaced: three figures at exactly
                // 120 degrees reads as a diagram.
                float ang = 0.6f + i * 2.1f + Mathf.Sin(Time.time * 0.05f + i) * 0.04f;
                float rad = 3.1f + i * 0.55f;
                var p = new Vector3(Mathf.Sin(ang) * rad, 0f, Mathf.Cos(ang) * rad);
                p.y = (float)world.GetHeight(p.x, p.z);
                _searcherHolders[i].position = p;
                // Face the fire at the origin, with a slow idle sway so nobody is a statue.
                float face = Mathf.Atan2(-p.x, -p.z) * Mathf.Rad2Deg + Mathf.Sin(Time.time * 0.35f + i * 2f) * 7f;
                _searcherHolders[i].rotation = Quaternion.Euler(0f, face, 0f);

                var inp = new AvatarInput { Speed = 0f, Status = HPPlayer.StatusActive };
                _searchers[i].Tick(in inp, dt);
            }
        }

        /// <summary>
        /// Shot 2 — the Yeti crosses the trail corridor ahead of the drifting camera.
        ///
        /// It is only on screen for the middle of the shot. A creature that walks on at the cut and off
        /// at the next one reads as a looping asset; one that crosses a gap in the trees while the
        /// camera happens to be pointed there reads as something you nearly missed, which is the entire
        /// tone of the game.
        /// </summary>
        private static void StageTrail(GameWorld world, float k, float dt)
        {
            HideSearchers();

            const float enter = 0.22f, exit = 0.78f;
            if (k < enter || k > exit) { SetYetiVisible(false); return; }
            SetYetiVisible(true);

            var pts = world.Paths.Count > 0 ? world.Paths[0].Pts : null;
            if (pts == null || pts.Count < 4) { SetYetiVisible(false); return; }

            // Sit ahead of where the camera has reached, so the crossing happens in the distance the
            // shot is looking down rather than on top of the lens.
            float camF = Mathf.Lerp(1f, Mathf.Min(5f, pts.Count - 2), k);
            int i = Mathf.Clamp(Mathf.RoundToInt(camF) + 2, 1, pts.Count - 2);
            var here = pts[i];
            var next = pts[Mathf.Min(i + 1, pts.Count - 1)];

            // Cross perpendicular to the trail's direction of travel.
            var fwd = new Vector2((float)(next.X - here.X), (float)(next.Z - here.Z));
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector2.up;
            fwd.Normalize();
            var side = new Vector2(-fwd.y, fwd.x);

            float cross = Mathf.InverseLerp(enter, exit, k);
            float lateral = Mathf.Lerp(11f, -11f, cross);
            var xz = new Vector2((float)here.X, (float)here.Z) + side * lateral;

            var p = new Vector3(xz.x, 0f, xz.y);
            p.y = (float)world.GetHeight(p.x, p.z);
            _yetiHolder.position = p;
            _yetiHolder.rotation = Quaternion.LookRotation(new Vector3(-side.x, 0f, -side.y));

            // Speed handed to the animation layer must match the speed it is actually being moved at,
            // or the feet skate — the same rule the networked bodies follow, just with the motion
            // authored here instead of measured.
            float shotSeconds = 11f * (exit - enter);
            var inp = new AvatarInput
            {
                Speed = 22f / shotSeconds,
                Status = HPPlayer.StatusActive,
            };
            _yeti.Tick(in inp, dt);
        }

        private static void PlaceAtCamp(GameWorld world)
        {
            for (int i = 0; i < _searcherHolders.Count; i++)
            {
                var p = new Vector3(Mathf.Sin(i * 2.1f) * 3.4f, 0f, Mathf.Cos(i * 2.1f) * 3.4f);
                p.y = (float)world.GetHeight(p.x, p.z);
                _searcherHolders[i].position = p;
            }
            SetYetiVisible(false);
        }

        private static void SetYetiVisible(bool on) => _yeti?.SetVisible(on);

        private static void HideSearchers()
        {
            foreach (var a in _searchers) a.SetVisible(false);
        }

        private static void HideAll()
        {
            SetYetiVisible(false);
            HideSearchers();
        }
    }
}
