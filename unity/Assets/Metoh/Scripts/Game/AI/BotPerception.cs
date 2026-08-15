// What a bot can actually sense — the layer that feeds BeliefMap.
//
// The rule this file enforces: a brain never reaches into HPPlayer.All to find its quarry. It asks
// here, and here answers only with what this particular body could have perceived from where it is
// standing. That is the difference between a creature and a homing missile, and it is the one
// invariant worth protecting as the brains grow — every future "just peek at the real position"
// shortcut is a step back toward the beeline.
//
// The Yeti's sensing was already honest before this rewrite (sight with line-of-sight, a longer
// range on a lit torch, hearing that scales with movement and goes silent for a crouch) and that
// model is preserved here rather than replaced. What was NOT honest was the fallback: with nothing
// perceived, the old prowl walked at NearestSearcherRaw() — the true position of the nearest
// searcher, through walls, from anywhere on the map. That was the beeline. It is gone; the belief
// map is what fills the gap, and it fills it with a guess instead of an answer.
using Metoh.Sim;
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>One thing a bot sensed this tick.</summary>
    public struct Contact
    {
        public HPPlayer Who;
        public Vector3 At;
        /// <summary>1 = saw it plainly; lower = heard it, or glimpsed it at the edge of range. Feeds
        /// straight into how hard the belief map collapses on it.</summary>
        public float Confidence;
        public bool Seen;   // as opposed to merely heard
    }

    /// <summary>
    /// Sensing for both roles. Instanced per bot because hearing needs per-target speed history.
    /// </summary>
    public sealed class BotPerception
    {
        // --- Yeti senses (carried over from the old YetiBot, which had these right) -----
        public const float YetiSightRange = 34f;
        public const float YetiTorchSightRange = 80f;
        public const float YetiHearSprint = 30f;
        public const float YetiHearWalk = 15f;

        // --- searcher senses -----------------------------------------------------------
        /// <summary>Shorter than the Yeti's on purpose: it owns the dark, they don't.</summary>
        public const float SearcherSpotRange = 30f;
        public const float SearcherSpotRangeLit = 55f;
        public const float RoarHeardRange = 90f;

        /// <summary>Below this (m/s) a body is standing still and makes no sound.</summary>
        private const float StillSpeed = 0.6f;

        private readonly System.Collections.Generic.Dictionary<HPPlayer, Vector3> _lastPos =
            new System.Collections.Generic.Dictionary<HPPlayer, Vector3>();
        private readonly System.Collections.Generic.Dictionary<HPPlayer, float> _speed =
            new System.Collections.Generic.Dictionary<HPPlayer, float>();

        /// <summary>
        /// Sample how fast everyone is moving, for the hearing model.
        ///
        /// MUST run before any early-out (pause, frozen, intermission) — it measures distance/dt, so
        /// skipping it while frozen freezes _lastPos but not dt, and the first frame after resuming
        /// divides a whole pause of travel by one frame. Every searcher then reads as sprinting and
        /// the bot "hears" the entire map at once. This bug was found once already in YetiBot; it is
        /// restated here because the ordering requirement moved with the code.
        /// </summary>
        public void SampleSpeeds(float dt)
        {
            if (dt <= 0f) return;
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null) continue;
                Vector3 now = p.transform.position;
                if (_lastPos.TryGetValue(p, out Vector3 prev))
                {
                    float inst = Mathf.Sqrt(Flat2(now, prev)) / dt;
                    _speed[p] = Mathf.Lerp(_speed.TryGetValue(p, out float s) ? s : inst, inst, 0.4f);
                }
                _lastPos[p] = now;
            }
        }

        public float SpeedOf(HPPlayer p) => _speed.TryGetValue(p, out float s) ? s : 0f;

        /// <summary>
        /// The strongest searcher this Yeti perceives, or Confidence 0 if none. Sight needs line of
        /// sight and stretches for a lit torch; hearing ignores line of sight but scales with how
        /// loudly they move and is silent for a crouching or stationary searcher.
        /// </summary>
        public Contact PerceiveSearcher(Vector3 pos, float hearMul = 1f)
        {
            var result = new Contact();
            float bestScore = 0f;
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti || p.Status.Value == HPPlayer.StatusIncap) continue;

                Vector3 at = p.transform.position;
                float dist = Mathf.Sqrt(Flat2(at, pos));
                bool seen = false, heard = false;

                float sight = p.FlashOn.Value ? YetiTorchSightRange : YetiSightRange;
                if (dist <= sight && !Blocked(pos, at)) seen = true;

                if (!p.Crouched.Value)
                {
                    float spd = SpeedOf(p);
                    if (spd > StillSpeed)
                    {
                        float t = Mathf.InverseLerp((float)Sim.Player.WalkSpeed, (float)Sim.Player.SprintSpeed, spd);
                        float hear = Mathf.Lerp(YetiHearWalk, YetiHearSprint, Mathf.Clamp01(t)) * hearMul;
                        if (dist <= hear) heard = true;
                    }
                }
                if (!seen && !heard) continue;

                // Closest first, then the two predator preferences that were already tuned and are
                // worth keeping: take the one holding proof (it fights their win condition, not just
                // their bodies), and prefer one already wading in a drift basin (deep snow does not
                // slow the Yeti, so that is the cheapest catch on the field).
                float score = 1000f - dist;
                if (p.CarriedTotal > 0) score += 25f;
                var world = WorldBuilder.World;
                if (world != null && Movement.DeepSnowDepth(world, at.x, at.z) > 0.35) score += 15f;

                if (score > bestScore)
                {
                    bestScore = score;
                    result.Who = p;
                    result.At = at;
                    result.Seen = seen;
                    // A sighting is trustworthy; hearing alone gives a bearing, not a position, so it
                    // collapses the belief field much less hard.
                    result.Confidence = seen ? 1f : 0.45f;
                }
            }
            return result;
        }

        /// <summary>
        /// The Yeti, if this searcher can actually see it. Range plus a hard line-of-sight test, with
        /// the range extended while our own torch is lit.
        ///
        /// Still no hearing term for searchers, and that remains deliberate: footsteps are a cue a
        /// human player interprets, and handing the bot an audio channel would quietly make it better
        /// at detection than the person it stands in for. (The plan doc flags revisiting this now that
        /// cues feed a decaying belief rather than a hard fix — it is a play-test question, not a code
        /// one, so it stays off until someone has actually felt both.)
        /// </summary>
        public Contact PerceiveYeti(Vector3 pos, bool ownTorchOn)
        {
            var result = new Contact();
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || !p.IsYeti || p.Status.Value == HPPlayer.StatusIncap) continue;
                Vector3 at = p.transform.position;
                float d = Mathf.Sqrt(Flat2(at, pos));
                float range = ownTorchOn ? SearcherSpotRangeLit : SearcherSpotRange;
                if (d > range || Blocked(pos, at)) continue;

                result.Who = p;
                result.At = at;
                result.Seen = true;
                // Confidence falls off at the edge of range — a shape at 54 m in fog is not the same
                // report as one at 8 m, and the belief map should not treat them alike.
                result.Confidence = Mathf.Lerp(1f, 0.6f, Mathf.Clamp01(d / Mathf.Max(1f, range)));
                return result;
            }
            return result;
        }

        public static bool Blocked(Vector3 a, Vector3 b)
        {
            var world = WorldBuilder.World;
            if (world == null) return false;
            return Metoh.Sim.Collision.LineBlocked(world.Colliders, new Vec2(a.x, a.z), new Vec2(b.x, b.z));
        }

        public static float Flat2(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
