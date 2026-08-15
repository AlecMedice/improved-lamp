// The lookout's mounted binoculars — the coin-operated tower viewer you find at scenic overlooks,
// minus the coin.
//
// WHY MOUNTED RATHER THAN CARRIED. The owner's reference solves a design problem the carried-item
// versions did not. Glassing is meant to be the REWARD FOR CLIMBING (see GAME_DESIGN: it is the whole
// point of the tower), so a pair of binoculars you can pocket and take down the ladder quietly
// deletes the tower's reason to exist. A pedestal viewer is an object you can see from the ground,
// walk up to, and use — everything the owner asked for — while staying bolted to the one place it is
// supposed to work.
//
// THE ANIMATION ADDS NO NETWORKING, and that constraint shaped the whole design. `HPPlayer._glassing`
// is a plain local bool, not a SyncVar, so a remote machine cannot know anybody is glassing. Rather
// than add one — CLAUDE.md is explicit that the animation layer reads only already-replicated state
// and adds no SyncVar and no RPC — the head aims using state that is ALREADY replicated: a player's
// position (are they on the deck, near the pedestal?) and their yaw. So every machine independently
// arrives at the same aim from data it already has, and there is nothing to desync. The cost is that
// the viewer tracks a nearby player whether or not they are actively holding the key, which reads
// perfectly well: it is a heavy thing on a stiff mount that somebody is leaning against.
using UnityEngine;

namespace Metoh.Game
{
    /// <summary>
    /// Aims the viewer head at whatever the nearest player on the lookout deck is looking at, and
    /// parks it when nobody is up there. Built by <see cref="WorldBuilder.BuildTowerViewer"/>.
    /// </summary>
    public class TowerViewer : MonoBehaviour
    {
        /// <summary>How close a player must be to be "at" the viewer.</summary>
        private const float UseRange = 1.9f;
        /// <summary>Degrees per second the head swings. Deliberately slow — it is cast iron on a
        /// stiff mount, and a head that snapped to a player's mouse would read as weightless.</summary>
        private const float SlewSpeed = 90f;
        /// <summary>Resting pitch when unattended: tipped slightly down over the valley.</summary>
        private const float IdlePitch = 8f;

        private Transform _yoke;   // yaws
        private Transform _head;   // pitches
        private float _yaw, _pitch = IdlePitch;

        public void Init(Transform yoke, Transform head)
        {
            _yoke = yoke;
            _head = head;
        }

        private void Update()
        {
            if (_yoke == null || _head == null) return;

            HPPlayer user = NearestUser(out float _);
            float wantYaw = _yaw, wantPitch = IdlePitch;

            if (user != null)
            {
                // The sim's forward is (-sin yaw, -cos yaw); Unity's Y-rotation is the same angle in
                // degrees, so the head can take the player's replicated yaw directly. Reconstructing it
                // from a look vector instead would introduce a second convention to keep in step with
                // the first — see [handedness] for how that goes.
                wantYaw = user.transform.eulerAngles.y;
                // Pitch is not replicated (only body yaw is), so the head holds a fixed downward
                // cant while in use rather than inventing a value. A viewer that tracked vertical
                // aim would need a new SyncVar, which is exactly what this class exists to avoid.
                wantPitch = 2f;
            }

            _yaw = Mathf.MoveTowardsAngle(_yaw, wantYaw, SlewSpeed * Time.deltaTime);
            _pitch = Mathf.MoveTowards(_pitch, wantPitch, SlewSpeed * 0.4f * Time.deltaTime);

            _yoke.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            _head.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        /// <summary>Nearest living searcher standing on the deck within reach of the pedestal.</summary>
        private HPPlayer NearestUser(out float dist)
        {
            HPPlayer best = null;
            float bestD2 = UseRange * UseRange;
            Vector3 here = transform.position;
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti) continue;
                if (p.Status.Value != HPPlayer.StatusActive) continue;
                Vector3 d = p.transform.position - here;
                // Height check as well as radius: someone directly below on the ground is not using it.
                if (Mathf.Abs(d.y) > 2.2f) continue;
                float d2 = d.x * d.x + d.z * d.z;
                if (d2 < bestD2) { bestD2 = d2; best = p; }
            }
            dist = best != null ? Mathf.Sqrt(bestD2) : float.MaxValue;
            return best;
        }
    }
}
