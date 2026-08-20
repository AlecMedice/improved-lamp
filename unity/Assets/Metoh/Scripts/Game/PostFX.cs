// The web build's post-processing look, rebuilt on URP Volumes: bloom + vignette + film grain
// + ACES tonemapping (the client's EffectComposer pass in core/Game.ts / config.POST). Also owns
// the per-ROLE exposure: each client renders its own scene, so Yeti's brighter night vision is
// purely local and never leaks to searcher screens — same trick as the web build.
// Built entirely in code (WorldBuilder bootstraps it); values tuned by eye, like everything else.
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Metoh.Game
{
    public class PostFX : MonoBehaviour
    {
        public static PostFX Instance { get; private set; }

        private ColorAdjustments _colorAdjustments;
        private Vignette _vignette;
        private Bloom _bloom;
        // Two independent reasons to lift exposure. Tracked separately and composed in ApplyExposure
        // so neither can clobber the other (the title screen ends exactly when a role is assigned).
        private bool _yetiVision;
        private bool _titleMode;
        private bool _nightVision; // searcher glassing the forest through the tower binoculars

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
            _colorAdjustments.postExposure.Override(BaseExposure);
            // Saturation pulled back toward neutral (was -6). A near-monochrome palette was fighting
            // the one thing the world has to distinguish its surfaces with: the basin is blue, the
            // trail is warm, the tents and prayer flags are the only saturated things out there, and
            // desaturating the frame was quietly spending all of that.
            _colorAdjustments.saturation.Override(BaseSaturation);
            // Contrast up from 12. With the terrain now spanning bare rock to open snowpack there is
            // finally a real range in the image to expand; at 12 the new dark end was being lifted
            // back toward the same mid-grey everything already sat in.
            _colorAdjustments.contrast.Override(18f);

            // SPLIT TONING — cool shadows, warm highlights. This is the cheapest realism win in the
            // whole pass and it is pure grading, not lighting: real snow at night is lit by two
            // sources of very different colour, a blue-white sky and whatever warm light people
            // brought with them, and the eye reads that opposition as depth. Tinting a single
            // ambient colour cannot produce it, because the split has to happen across luminance.
            var split = profile.Add<SplitToning>();
            split.shadows.Override(MeshUtil.Rgb(0x2c4a72));    // moonlit blue in the darks
            split.highlights.Override(MeshUtil.Rgb(0xffd9a8)); // lamp/torch warmth in the lights
            split.balance.Override(-12f);                       // bias toward the shadow tint

            // A whisper of lens character. Both are deliberately near the bottom of their useful
            // range: this is a found-footage horror game, not a lens simulator, and either of these
            // pushed hard is instantly cheaper-looking than having none at all.
            var chroma = profile.Add<ChromaticAberration>();
            chroma.intensity.Override(0.08f);

            // Physically-shaped white balance — pull the whole frame slightly cold. Snow scenes that
            // are graded neutral read as overcast daylight no matter how dark you make them.
            var wb = profile.Add<WhiteBalance>();
            wb.temperature.Override(-14f);
            wb.tint.Override(4f);

            var vol = gameObject.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.priority = 10f;
            vol.profile = profile;

            ApplyCameraSettings();
        }

        /// <summary>
        /// Camera-side render settings: post-processing on, and ANTI-ALIASING.
        ///
        /// **The game shipped with no anti-aliasing of any kind.** MSAA is switched off in `HPQuality`
        /// for cost (it is a real cost on integrated graphics and that call is right), and no
        /// post-process AA was ever put in its place — so since the legibility pass took render scale
        /// to native 1.0, every edge in the frame has been a raw hard step.
        ///
        /// That hurts this scene more than most, for two reasons. This build is nearly all *thin*
        /// geometry — ladder rungs, tower cross-bracing, icicles, the conifer crowns' jagged silhouette,
        /// the skybox ridgeline — and thin geometry is exactly what aliases worst: a one-pixel-wide
        /// feature either hits a pixel centre or it does not, so it crawls and shimmers as you turn.
        /// And [legibility] established that at night, fogged, the SILHOUETTE is very nearly all the
        /// information reaching the player, which means the aliased edge is not a blemish on the image,
        /// it is sitting directly on top of the one cue the art is being read through.
        ///
        /// SMAA rather than FXAA on the tier that can afford it: FXAA is a luma-contrast blur and it
        /// softens the fine normal-map grain [materials] is built on, which is the same detail render
        /// scale 0.7 was already found to be dissolving. SMAA reconstructs edges from a shape pattern
        /// instead and leaves interior texture alone. It costs well under a millisecond, which is a
        /// fraction of what the bloom pass above already spends.
        ///
        /// Re-applied rather than set once, because the quality tier can move at runtime (the pause
        /// menu slider) and because `Camera.main` is grabbed here at bootstrap — see `HPQuality`.
        /// </summary>
        public static void ApplyCameraSettings()
        {
            var cam = Camera.main;
            if (cam == null) return;
            // HDR is AND-ed, not inherited: URP computes `isHdrEnabled` as the pipeline asset's
            // supportsHDR AND this camera's allowHDR. HPQuality asserts the asset half and explains at
            // length why — but a camera with allowHDR off defeats that assertion completely and in
            // exactly the same silent way, guts bloom, ACES roll-off, split toning and the snowpack
            // glitter, and errors nowhere. Asserting both halves is what actually closes it.
            cam.allowHDR = true;
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            if (HPQuality.HighDetail)
            {
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
            }
            else
            {
                // The cheap tier is already upscaling from a lower render scale, so its edges are soft
                // to begin with and SMAA has less to find. FXAA is close enough to free to keep on.
                data.antialiasing = AntialiasingMode.FastApproximateAntialiasing;
            }
        }

        /// <summary>
        /// Yeti sees the night brighter (predator eyes) — a local exposure lift, exactly like the
        /// web build's per-role exposure. Called from HPPlayer when the local role changes.
        /// </summary>
        public void SetYetiVision(bool yeti)
        {
            _yetiVision = yeti;
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

        /// <summary>
        /// Binocular night vision (searcher on the lookout). A hard exposure lift plus a green cast and
        /// a squeeze of saturation — the classic image-intensifier look — so the top of the tower
        /// genuinely reveals the treeline you can't otherwise see. Local only, like Yeti's vision.
        /// </summary>
        public void SetNightVision(bool on)
        {
            _nightVision = on;
            ApplyExposure();
        }

        /// <summary>
        /// Baseline exposure for gameplay, in stops.
        ///
        /// The scene was lit and graded to sit right at the bottom of the display range, which reads
        /// as "I can't see anything" rather than as dread — the difference being that dread needs you
        /// to *almost* make something out. A third of a stop is small enough that nothing becomes
        /// safe and large enough to lift the darks off the floor, where all the shadow detail the
        /// soft-shadow and AO work produces was previously being clipped to black and thrown away.
        /// </summary>
        private const float BaseExposure = 0.35f;

        /// <summary>Gameplay saturation. Read by both Awake and ApplyExposure — see the note there.</summary>
        private const float BaseSaturation = -2f;

        private void ApplyExposure()
        {
            if (_colorAdjustments == null) return;
            float ev = BaseExposure;
            if (_titleMode) ev += 1.15f;      // menu backdrop
            if (_yetiVision) ev += 0.9f;   // predator eyes
            if (_nightVision) ev += 2.0f;     // image intensifier — a big lift, it's the whole point
            _colorAdjustments.postExposure.Override(ev);
            // Green phosphor cast + desaturation while glassing; neutral otherwise.
            _colorAdjustments.colorFilter.Override(_nightVision ? new Color(0.55f, 1f, 0.6f) : Color.white);
            // BaseSaturation, not a second literal. This line used to restore -6 — the exact value
            // Awake had just deliberately moved away from — so the tuned grade survived only until the
            // first SetYetiVision/SetTitleBrightness call, i.e. never in an actual match. One constant.
            _colorAdjustments.saturation.Override(_nightVision ? -55f : BaseSaturation);
        }
    }
}
