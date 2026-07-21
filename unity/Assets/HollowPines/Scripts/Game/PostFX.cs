// The web build's post-processing look, rebuilt on URP Volumes: bloom + vignette + film grain
// + ACES tonemapping (the client's EffectComposer pass in core/Game.ts / config.POST). Also owns
// the per-ROLE exposure: each client renders its own scene, so Bigfoot's brighter night vision is
// purely local and never leaks to searcher screens — same trick as the web build.
// Built entirely in code (WorldBuilder bootstraps it); values tuned by eye, like everything else.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HollowPines.Game
{
    public class PostFX : MonoBehaviour
    {
        public static PostFX Instance { get; private set; }

        private ColorAdjustments _colorAdjustments;
        private Vignette _vignette;
        private Bloom _bloom;
        // Two independent reasons to lift exposure. Tracked separately and composed in ApplyExposure
        // so neither can clobber the other (the title screen ends exactly when a role is assigned).
        private bool _bigfootVision;
        private bool _titleMode;

        /// <summary>Attach the global volume + enable post on the main camera. Safe to call once from WorldBuilder.</summary>
        public static void Ensure(GameObject host)
        {
            if (Instance != null) return;
            Instance = host.AddComponent<PostFX>();
        }

        private void Awake()
        {
            Instance = this;

            var profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _bloom = profile.Add<Bloom>();
            _bloom.intensity.Override(0.9f);
            _bloom.threshold.Override(0.85f);
            _bloom.scatter.Override(0.65f);

            _vignette = profile.Add<Vignette>();
            _vignette.intensity.Override(0.33f);
            _vignette.smoothness.Override(0.55f);

            var grain = profile.Add<FilmGrain>();
            grain.type.Override(FilmGrainLookup.Medium1);
            grain.intensity.Override(0.28f);
            grain.response.Override(0.75f);

            var tone = profile.Add<Tonemapping>();
            tone.mode.Override(TonemappingMode.ACES);

            _colorAdjustments = profile.Add<ColorAdjustments>();
            _colorAdjustments.postExposure.Override(0f);
            _colorAdjustments.saturation.Override(-6f); // slightly desaturated dusk palette

            var vol = gameObject.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.profile = profile;

            var cam = Camera.main;
            if (cam != null)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
            }
        }

        /// <summary>
        /// Bigfoot sees the night brighter (predator eyes) — a local exposure lift, exactly like the
        /// web build's per-role exposure. Called from HPPlayer when the local role changes.
        /// </summary>
        public void SetBigfootVision(bool bigfoot)
        {
            _bigfootVision = bigfoot;
            ApplyExposure();
        }

        /// <summary>
        /// Title-screen look: lift the exposure and pull the vignette back so the menu backdrop reads
        /// as a lit scene rather than a dark field. Restored the moment a match connects.
        /// </summary>
        public void SetTitleBrightness(bool on)
        {
            _titleMode = on;
            if (_vignette != null) _vignette.intensity.Override(on ? 0.18f : 0.33f);
            ApplyExposure();
        }

        /// <summary>
        /// DEV — toggle bloom at runtime (the F3 overlay drives this). Bloom is full-screen and
        /// multi-pass, the single most expensive effect we run, so it is the first thing to switch
        /// off when hunting a frame-rate problem. Toggling the OVERRIDE rather than the effect's
        /// active flag keeps the tuned values intact for when it goes back on.
        /// </summary>
        public void SetBloomEnabled(bool on)
        {
            if (_bloom != null) _bloom.active = on;
        }

        public bool BloomEnabled => _bloom == null || _bloom.active;

        private void ApplyExposure()
        {
            if (_colorAdjustments == null) return;
            float ev = 0f;
            if (_titleMode) ev += 1.15f;      // menu backdrop
            if (_bigfootVision) ev += 0.9f;   // predator eyes
            _colorAdjustments.postExposure.Override(ev);
        }
    }
}
