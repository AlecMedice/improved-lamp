// Falling snow, ground spindrift, breath vapour and motes in the torch beam.
//
// WHY. The project had ZERO particle systems. A Himalayan valley at night with perfectly still air is
// the single largest "this is a 3D scene, not a place" tell left after the legibility pass — weather
// is the cheapest atmosphere per frame available anywhere in a game, and snow specifically is the one
// effect that makes a snowfield read as WEATHER rather than as white ground.
//
// It also does real work beyond mood. Falling snow gives the fog a near field: without something
// visibly moving between the camera and the fog wall, distance is unreadable, and this build's whole
// horror geometry is about not being able to tell how far away something is. Breath gives away a
// hiding searcher. Motes are what make a torch beam legible as a beam.
//
// THE TITLE SCREEN GETS ALL OF IT. This is bootstrapped from WorldBuilder.Awake, which runs before
// anything connects, and the systems follow Camera.main rather than a player — so the title
// cinematic flies through the same weather the match does. A title card with still air behind it and
// snow in the game would advertise the exact seam this pass exists to close.
using UnityEngine;

namespace Metoh.Game
{
    public class Weather : MonoBehaviour
    {
        public static Weather Instance { get; private set; }

        private ParticleSystem _snow, _drift;
        private Material _particleMat;
        private Transform _follow;

        /// <summary>
        /// The volume snow is simulated in, as a half-extent around the camera.
        ///
        /// Small on purpose. Snow is only ever seen against the fog, and the fog closes at ~150 m, so
        /// simulating a 400 m box would spend the entire particle budget on flakes nobody can see.
        /// The box is re-centred on the camera every frame instead, which gives infinite snow for the
        /// cost of a 45 m one.
        /// </summary>
        private const float SnowBox = 45f;

        public static void Ensure(GameObject host)
        {
            if (Instance != null) return;
            Instance = host.AddComponent<Weather>();
        }

        private void Awake()
        {
            Instance = this;

            // One material shared by every system here — snow, drift, breath and motes are all a white
            // soft dot, and sharing lets the SRP batcher put them in one draw.
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                      ?? Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Sprites/Default");
            _particleMat = new Material(shader);
            _particleMat.SetTexture("_BaseMap", ProcTex.SoftDot);
            _particleMat.mainTexture = ProcTex.SoftDot;
            // Alpha-blended, not additive. Snow OCCLUDES what is behind it — that is precisely how it
            // adds depth to the fog. Additive snow glows and stops reading as matter.
            SetTransparent(_particleMat);

            _snow = BuildSnow();
            _drift = BuildDrift();
        }

        private void OnDestroy()
        {
            if (_particleMat != null) Destroy(_particleMat);
        }

        /// <summary>
        /// Re-centre the simulation volumes on whatever camera is live. LateUpdate so it runs after the
        /// title cinematic and after HPPlayer's own camera work — moving the emitter in Update would
        /// leave it one frame behind the view every frame, which shows up as snow thinning out on
        /// whichever side you are turning toward.
        /// </summary>
        private void LateUpdate()
        {
            if (_follow == null || !_follow.gameObject.activeInHierarchy)
            {
                var cam = Camera.main;
                if (cam == null) return;
                _follow = cam.transform;
            }

            Vector3 p = _follow.position;
            if (_snow != null)
                _snow.transform.position = new Vector3(p.x, p.y + SnowBox * 0.55f, p.z);
            if (_drift != null)
            {
                var world = WorldBuilder.World;
                float ground = world != null ? (float)world.GetHeight(p.x, p.z) : p.y;
                _drift.transform.position = new Vector3(p.x, ground + 0.25f, p.z);
            }
        }

        // ------------------------------------------------------------------ systems

        private ParticleSystem BuildSnow()
        {
            var ps = NewSystem("Snowfall");
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // or the flakes ride the camera
            main.startLifetime = new ParticleSystem.MinMaxCurve(9f, 16f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.1f, 2.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.10f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.55f));
            main.gravityModifier = 0.02f;
            main.maxParticles = HPQuality.HighDetail ? 2600 : 1100;

            var emission = ps.emission;
            emission.rateOverTime = HPQuality.HighDetail ? 320f : 140f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(SnowBox * 2f, 1f, SnowBox * 2f);

            // Flakes fall, but they do not fall STRAIGHT. A steady sideways drift plus per-particle
            // noise is the difference between snow and a screensaver: real flakes are light enough that
            // air movement dominates gravity, which is why snow appears to hang and swirl.
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-1.3f, -0.2f);
            vel.z = new ParticleSystem.MinMaxCurve(-0.5f, 0.6f);

            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.5f;
            noise.frequency = 0.22f;
            noise.quality = HPQuality.HighDetail ? ParticleSystemNoiseQuality.Medium : ParticleSystemNoiseQuality.Low;

            // Fade in and out rather than popping. A flake appearing at full opacity 45 m up is
            // invisible; one vanishing at full opacity right in front of you is not.
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeInOut(0.12f, 0.75f);

            ps.Play();
            return ps;
        }

        /// <summary>
        /// Snow already on the ground, picked up and blown along the surface. This is the effect that
        /// most says "high altitude, and the wind has not stopped in days" — and unlike falling snow it
        /// reads even when the player is looking down at their feet.
        /// </summary>
        private ParticleSystem BuildDrift()
        {
            var ps = NewSystem("Spindrift");
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 3.2f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3.5f, 8.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, 0.30f));
            main.gravityModifier = 0f;
            main.maxParticles = HPQuality.HighDetail ? 700 : 260;

            var emission = ps.emission;
            emission.rateOverTime = HPQuality.HighDetail ? 190f : 80f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(70f, 0.5f, 70f);

            // Blown along the same axis the falling snow drifts on, or the two read as two different
            // weathers happening at once.
            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(-7f, -2.5f);
            vel.y = new ParticleSystem.MinMaxCurve(0.05f, 0.8f);
            vel.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeInOut(0.2f, 0.6f);

            ps.Play();
            return ps;
        }

        /// <summary>
        /// Breath vapour for one player. Remotes only — the owner's head is inside their own camera, so
        /// their breath would sit on the lens and fog the game rather than the world.
        ///
        /// Gameplay, not just mood: a searcher holding still in the dark with their torch off is
        /// otherwise perfectly hidden, and now they are not quite. It is a small, fair tell — you have
        /// to already be looking at roughly the right patch of dark to notice it.
        /// </summary>
        public static ParticleSystem AttachBreath(Transform head, bool yeti)
        {
            if (Instance == null || head == null) return null;
            var ps = Instance.NewSystem("Breath");
            ps.transform.SetParent(head, false);
            ps.transform.localPosition = new Vector3(0f, yeti ? 0.02f : 0.02f, yeti ? 0.24f : 0.13f);

            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World; // a puff hangs where it was made
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.9f, 1.7f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(yeti ? 0.9f : 0.5f, yeti ? 1.8f : 1.1f);
            main.startSize = new ParticleSystem.MinMaxCurve(yeti ? 0.14f : 0.07f, yeti ? 0.30f : 0.15f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 1f, 1f, yeti ? 0.34f : 0.22f));
            main.maxParticles = 60;

            // Bursts, not a stream: breathing is periodic, and a continuous plume reads as a machine.
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[]
            {
                new ParticleSystem.Burst(0f, (short)(yeti ? 6 : 3)) { repeatInterval = yeti ? 2.6f : 3.4f, cycleCount = 0 },
            });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 16f;
            shape.radius = 0.02f;

            // A puff expands as it cools and disperses.
            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 2.4f));

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeInOut(0.1f, 0.45f);

            ps.Play();
            return ps;
        }

        /// <summary>
        /// Dust and ice crystals hanging in a torch beam. Parented to the beam object, so it turns on
        /// and off with the light for free and costs nothing while the torch is stowed.
        /// </summary>
        public static ParticleSystem AttachMotes(Transform beam, float length, float radius)
        {
            if (Instance == null || beam == null) return null;
            var ps = Instance.NewSystem("Motes");
            ps.transform.SetParent(beam, false);

            var main = ps.main;
            // LOCAL space here, unlike everything else: motes should sweep with the beam as it turns,
            // because what is being drawn is the air the beam is currently lighting, not particles that
            // happen to be there.
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.05f, 0.35f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.035f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.95f, 0.85f, 0.5f));
            main.maxParticles = HPQuality.HighDetail ? 140 : 60;

            var emission = ps.emission;
            emission.rateOverTime = HPQuality.HighDetail ? 55f : 24f;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = Mathf.Atan2(radius, length) * Mathf.Rad2Deg;
            shape.radius = 0.05f;
            shape.length = length;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = FadeInOut(0.15f, 0.6f);

            ps.Play();
            return ps;
        }

        // ------------------------------------------------------------------ plumbing

        private ParticleSystem NewSystem(string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop();

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = _particleMat;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.alignment = ParticleSystemRenderSpace.View;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortingFudge = 0f;
            return ps;
        }

        /// <summary>Alpha ramp: in over the first fraction, out over the last. Shared by every system.</summary>
        private static ParticleSystem.MinMaxGradient FadeInOut(float inEnd, float outStart)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, inEnd),
                    new GradientAlphaKey(1f, outStart),
                    new GradientAlphaKey(0f, 1f),
                });
            return new ParticleSystem.MinMaxGradient(g);
        }

        /// <summary>
        /// Put a URP material into alpha-blended transparent mode from code.
        ///
        /// URP does not derive its blend state from the shader alone — the _Surface/_Blend floats,
        /// the blend factors, ZWrite, the render queue AND the _SURFACE_TYPE_TRANSPARENT keyword all
        /// have to agree. Setting only some of them is the usual way a runtime-built transparent
        /// material comes out opaque, which for snow means every flake renders as a solid white square.
        /// </summary>
        private static void SetTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_AlphaClip", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.DisableKeyword("_ALPHATEST_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
    }
}
