// The camp fire — built as a real object instead of the traffic cone it was.
//
// WHAT IT REPLACED. One `MeshUtil.Cone(0.6f, 1.1f, 8)` tinted orange, plus a point light at a
// constant 3.5 intensity. No motion of any kind. Every other object in camp had been through a
// materials pass; this had not, and it is the worst possible thing to leave static because a fire is
// defined by movement — it is the one object a player has a lifetime of reference for, so a still
// one reads as broken rather than as stylised.
//
// It also carries more weight than any other prop in the game. Camp is where proof becomes
// permanent, the fire is the warm centre of it, and on a dusk-to-dawn map it is the light you steer
// home by. UNITY_NOTES [legibility] argues silhouettes matter most at night; a fire is the inverse
// case, where the LIGHT is the thing being read, from far outside the range at which you can resolve
// any shape at all.
//
// HOW IT IS BUILT, and why each piece is there:
//   LOGS       four charred limbs in a lean-to cone. Gives the flame something to come OUT of; a
//              flame with no fuel under it is a floating effect.
//   EMBER BED  hashed glowing coals at the base, brightest at the centre. This is what still reads
//              once the flames are small, and it is what lights the ring of stones from beneath.
//   FLAME      an emissive particle system, additive, world-space, with size-over-lifetime so each
//              tongue tapers as it rises. Particles rather than an animated mesh because a flame's
//              silhouette is stochastic, and a vertex-animated cone reads as a wobbling cone.
//   SPARKS     rare, fast, long-lived embers that ride the smoke column. Cheap, and they are what
//              makes the fire feel HOT rather than merely bright.
//   SMOKE      a slow dark column, lit by nothing, that fades out. Reads best against the moon.
//   SCORCH     a dark disc under everything, so the fire sits IN the ground rather than on it.
//   FLICKER    the light itself, driven below by summed sines rather than by Random.
//
// PERF: three particle systems with small budgets, and they are shut off entirely on low detail
// (HPQuality) — see [perf], which is explicit that this build is tuned for an integrated GPU. The
// flicker is a handful of sin() per frame on one light.
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>
    /// Drives the campfire light and ember glow. Built by <see cref="WorldBuilder"/>; needs no
    /// networking, because a fire is the same for everyone and nothing about it is authoritative.
    /// </summary>
    public class Campfire : MonoBehaviour
    {
        private Light _light;
        private Material _emberMat;
        private float _baseIntensity;
        private Color _emberBase;
        private float _phase;

        /// <summary>
        /// Flicker is SUMMED SINES at incommensurable rates, not Random.value.
        ///
        /// Random flicker is the obvious implementation and it looks wrong: white noise changes by a
        /// large amount every single frame, which reads as a failing electric bulb — a strobe. Real
        /// firelight wanders, because it is a slow convection cycle with faster turbulence riding on
        /// it. Three sines whose periods share no common multiple never repeat audibly to the eye and
        /// give exactly that wander. It is also frame-rate independent, which Random is not: a random
        /// flicker visibly changes character between 30 and 144 fps.
        /// </summary>
        private static float Flicker(float t) =>
            0.62f + 0.20f * Mathf.Sin(t * 2.31f)
                  + 0.11f * Mathf.Sin(t * 5.77f + 1.7f)
                  + 0.07f * Mathf.Sin(t * 11.13f + 0.4f);

        public void Init(Light l, Material emberMat, float baseIntensity)
        {
            _light = l;
            _emberMat = emberMat;
            _baseIntensity = baseIntensity;
            _emberBase = emberMat != null ? emberMat.GetColor("_EmissionColor") : Color.black;
            // Per-fire offset so two fires (camp, and any future one) never pulse in lockstep.
            _phase = Mathf.Repeat(transform.position.x * 0.37f + transform.position.z * 0.19f, 10f);
        }

        private void Update()
        {
            if (_light == null) return;
            float t = Time.time + _phase;
            float f = Flicker(t);

            _light.intensity = _baseIntensity * f;
            // Hotter flares run slightly yellower, cooler troughs redder. Colour temperature moving
            // WITH brightness is most of what separates firelight from a dimmer switch.
            _light.color = Color.Lerp(new Color(1f, 0.42f, 0.16f), new Color(1f, 0.72f, 0.34f),
                                      Mathf.InverseLerp(0.35f, 1.0f, f));

            // The coals breathe on a slower cycle than the flame — they are thermal mass, so they lag.
            if (_emberMat != null)
                _emberMat.SetColor("_EmissionColor", _emberBase * (0.75f + 0.45f * Mathf.Sin(t * 0.9f) * 0.5f + 0.22f * f));
        }
    }
}
