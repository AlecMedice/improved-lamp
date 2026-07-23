// The CPU Bigfoot brain — the opponent in the offline single-player mode (and the fastest way to
// test solo). This is meant to be a LEGITIMATE opponent someone plays without internet, not just a
// dev prop, so it plays the actual stealth game rather than tracking you through walls.
//
// It is INTENT ONLY. It never moves a transform or resolves an ability itself; it decides a
// direction and a couple of booleans and hands them to HPPlayer.ServerBotDrive / ServerBotRoar /
// ServerBotGrab, which run the exact same shared sim and the same GameManager.Try* authority a
// human's input lands in. So the bot obeys identical collision, stamina, cooldowns and range — there
// is no separate "AI physics" to drift out of parity with the real game.
//
// Runs on the HOST only, added to a bot player by HPPlayer.ServerBecomeBot. Plain MonoBehaviour (no
// networking of its own) because everything it touches is already server-side.
//
// PERCEPTION is the point. A predator that always knows where you are isn't scary, it's unfair — and
// it deletes the whole stealth layer (crouch = silent, flashlight = a beacon, break line of sight to
// escape). So the bot SENSES: it sees you within a cone-free range only with clear line of sight,
// sees your lit torch from much farther, hears you by how loudly you move (a sprint carries; a crouch
// makes no sound at all), and then REMEMBERS your last position and searches it before giving up.
// That loop — spotted, chase, lost behind the trees, hunt the area, fade back to wandering — is what
// makes it read as a creature instead of a homing missile.
//
// All tuning here is first-guess and has NOT run in the editor. The named constants are the dials.
using FishNet;
using HollowPines.Sim;
using UnityEngine;
using UnityEngine.AI;

namespace HollowPines.Game
{
    [RequireComponent(typeof(HPPlayer))]
    public class BigfootBot : MonoBehaviour
    {
        // --- perception (first-guess; editor-tune) ---------------------------------
        /// <summary>Sees an unlit searcher this far, with clear line of sight (Bigfoot has night eyes).</summary>
        private const float SightRange = 34f;
        /// <summary>A lit flashlight is a beacon in the dark — seen this far, still needs line of sight.</summary>
        private const float TorchSightRange = 80f;
        /// <summary>Hears a sprinting searcher this far (through trees — hearing ignores line of sight).</summary>
        private const float HearSprint = 30f;
        /// <summary>Hears a walking searcher this far. A crouch-walker makes NO sound (design rule), so
        /// there is no crouch hearing term at all — crouching past the bot in the dark actually works.</summary>
        private const float HearWalk = 15f;
        /// <summary>Below this speed (m/s) a searcher is treated as standing still and makes no sound.</summary>
        private const float StillSpeed = 0.6f;
        /// <summary>Seconds the bot keeps hunting a last-known position after losing the searcher.</summary>
        private const float MemorySeconds = 7f;

        // --- prowl: how the predator closes on prey it can't yet see/hear (first-guess) ---
        private const float ProwlJitter = 30f;      // the prowl target is this coarse — an area, not a pixel
        private const float ProwlRepick = 5f;       // re-aim only this often, so it lags your real movement
        private const float ProwlSprintBeyond = 55f; // sprint to close from far, walk in so perception can catch you

        // --- movement (first-guess) ------------------------------------------------
        private const float LoseRange = 85f;   // drop a quarry once it's this far (hysteresis vs re-acquire)
        private const float SprintBeyond = 8f; // sprint when the quarry is farther than this, else close carefully
        private const float CornerReach = 1.5f;
        private const float RepathInterval = 0.4f;
        private const float WanderGoalSeconds = 8f;
        private const float WanderRadius = 120f;

        private HPPlayer _self;
        private float _dbgAt; // throttle for the [botAI] guard trace
        private readonly NavMeshPath _path = new NavMeshPath();
        private Vector3[] _corners = System.Array.Empty<Vector3>();
        private int _corner;
        private float _repathAt;

        // Perception memory.
        private HPPlayer _quarry;         // who we're hunting (may currently be out of sight)
        private Vector3 _lastKnown;       // where we last perceived them
        private Vector3 _prowlTarget;     // coarse "head toward prey" goal while not yet perceiving anyone
        private float _prowlUntil;        // re-aim the prowl target after this
        private float _awareUntil;        // hunt the last-known spot until this time, then give up
        private readonly System.Collections.Generic.Dictionary<HPPlayer, Vector3> _lastPos =
            new System.Collections.Generic.Dictionary<HPPlayer, Vector3>();
        private readonly System.Collections.Generic.Dictionary<HPPlayer, float> _speed =
            new System.Collections.Generic.Dictionary<HPPlayer, float>();

        // Wander.
        private float _wanderUntil;
        private Vector3 _wanderGoal;

        private void Awake() => _self = GetComponent<HPPlayer>();

        private void Update()
        {
            // DEV trace (throttled): says exactly which guard the brain dies at, since the F3 line
            // shows it never sets a state. Remove once the bot is confirmed hunting.
            bool log = Time.time >= _dbgAt;
            if (log) _dbgAt = Time.time + 1f;

            // Host-authoritative and match-only. Clients hold a remote copy of the bot and never think
            // for it; the lobby and results screens are inert.
            if (!InstanceFinder.IsServerStarted) { if (log) Debug.Log("[botAI] bail: not server-started"); return; }
            if (_self == null) { if (log) Debug.Log("[botAI] bail: _self null"); return; }
            if (!_self.IsBot) { if (log) Debug.Log("[botAI] bail: not flagged bot"); return; }
            var gm = GameManager.Instance;
            if (gm == null) { if (log) Debug.Log("[botAI] bail: no GameManager"); return; }
            if (gm.MatchPhase.Value != GameManager.PhasePlaying) { if (log) Debug.Log($"[botAI] bail: phase={gm.MatchPhase.Value}"); return; }
            if (gm.IntermissionActive) { if (log) Debug.Log("[botAI] bail: intermission"); return; }
            if (_self.Status.Value != HPPlayer.StatusActive) { if (log) Debug.Log($"[botAI] bail: status={_self.Status.Value}"); return; }
            if (log) Debug.Log("[botAI] running past all guards");

            float dt = Mathf.Min(Time.deltaTime, 0.1f);
            UpdateHeardSpeeds(dt);

            Vector3 pos = transform.position;
            HPPlayer seen = Perceive(pos);

            if (seen != null)
            {
                _quarry = seen;
                _lastKnown = seen.transform.position;
                _awareUntil = Time.time + MemorySeconds;
            }
            else if (_quarry != null && (Time.time >= _awareUntil || FarLost(pos)))
            {
                _quarry = null; // memory expired or they broke well clear — back to prowling
            }

            if (_quarry != null)
            {
                // HUNT — toward the quarry if currently perceived, else toward where we last sensed it.
                TryAbilities();
                Vector3 goal = seen != null ? seen.transform.position : _lastKnown;
                Repath(goal);
                float d = Mathf.Sqrt(Flat2(goal, pos));
                DbgState = seen != null ? "HUNT" : "SEARCH";
                SteerAlongPath(pos, goal, sprint: d > SprintBeyond);
            }
            else
            {
                // Not perceiving anyone right now — but a predator doesn't wait to be walked into. It
                // PROWLS toward the nearest searcher's rough area (its instinct/scent), closing the gap
                // until they fall inside real sight/hearing and the precise HUNT takes over. The target
                // is coarse and refreshed slowly (ProwlRepick) with positional jitter, so it lags your
                // actual movement — you can still shake it by breaking line of sight and repositioning,
                // but you can't just stand at camp forever and never be found.
                // Aggressive by default; the F3 overlay can switch this off to restore the ORIGINAL
                // passive behavior (wander only, engage solely if you walk into perception) for tests.
                HPPlayer prey = AggressiveProwl ? NearestSearcherRaw() : null;
                if (prey != null)
                {
                    if (Time.time >= _prowlUntil)
                    {
                        Vector2 j = Random.insideUnitCircle * ProwlJitter;
                        _prowlTarget = prey.transform.position + new Vector3(j.x, 0f, j.y);
                        _prowlUntil = Time.time + ProwlRepick;
                    }
                    Repath(_prowlTarget);
                    DbgState = "PROWL";
                    bool far = Flat2(pos, prey.transform.position) > ProwlSprintBeyond * ProwlSprintBeyond;
                    SteerAlongPath(pos, _prowlTarget, sprint: far);
                }
                else
                {
                    // No searchers at all (only happens if everyone's down/gone) — plain roam. The goal
                    // is a real distant point even without a NavMesh, so the bot never stands still.
                    if (Time.time >= _wanderUntil || Flat2(pos, _wanderGoal) < 9f)
                    {
                        _wanderGoal = RandomNavPoint(pos);
                        _wanderUntil = Time.time + WanderGoalSeconds;
                        Repath(_wanderGoal, force: true);
                    }
                    else Repath(_wanderGoal);
                    DbgState = "WANDER";
                    SteerAlongPath(pos, _wanderGoal, sprint: false);
                }
            }
        }

        /// <summary>Current AI state, surfaced to the F3 overlay for debugging ("HUNT"/"SEARCH"/"WANDER").</summary>
        public string DbgState { get; private set; } = "—";

        /// <summary>
        /// DEV toggle (F3): true = the predator PROWLS toward you when it can't see you (the shipping
        /// behavior). False = the ORIGINAL passive brain — it only wanders and engages solely when you
        /// walk into its sight/hearing. Static so it applies to every bot and survives a reseed.
        /// </summary>
        public static bool AggressiveProwl = true;

        // --- perception ------------------------------------------------------------

        /// <summary>Track each searcher's speed so hearing can scale with how loudly they move.</summary>
        private void UpdateHeardSpeeds(float dt)
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot) continue;
                Vector3 now = p.transform.position;
                if (_lastPos.TryGetValue(p, out Vector3 prev) && dt > 0f)
                {
                    float inst = Mathf.Sqrt(Flat2(now, prev)) / dt;
                    // Smooth a little so a single stutter frame doesn't read as silence.
                    _speed[p] = Mathf.Lerp(_speed.TryGetValue(p, out float s) ? s : inst, inst, 0.4f);
                }
                _lastPos[p] = now;
            }
        }

        /// <summary>
        /// The strongest searcher the bot currently perceives, or null. Sight needs line of sight and
        /// is far longer when the target's torch is lit; hearing ignores line of sight but scales with
        /// movement and is silent for a crouching or still searcher.
        /// </summary>
        private HPPlayer Perceive(Vector3 pos)
        {
            HPPlayer best = null;
            float bestScore = 0f;
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot || p.Status.Value == HPPlayer.StatusIncap) continue;
                float dist = Mathf.Sqrt(Flat2(p.transform.position, pos));

                bool sensed = false;

                // Sight — line of sight required either way; a lit torch stretches the range.
                float sight = p.FlashOn.Value ? TorchSightRange : SightRange;
                if (dist <= sight && !Blocked(pos, p.transform.position)) sensed = true;

                // Hearing — no line-of-sight requirement, but crouch/standing still is silent.
                if (!sensed && !p.Crouched.Value)
                {
                    float spd = _speed.TryGetValue(p, out float s) ? s : 0f;
                    if (spd > StillSpeed)
                    {
                        // Interpolate the audible range between a walk and a sprint by speed.
                        float t = Mathf.InverseLerp((float)Sim.Player.WalkSpeed, (float)Sim.Player.SprintSpeed, spd);
                        float hear = Mathf.Lerp(HearWalk, HearSprint, Mathf.Clamp01(t));
                        if (dist <= hear) sensed = true;
                    }
                }

                if (!sensed) continue;
                // Prefer the closest — score is just inverse distance.
                float score = 1000f - dist;
                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private bool Blocked(Vector3 a, Vector3 b)
        {
            var world = WorldBuilder.World;
            if (world == null) return false;
            return HollowPines.Sim.Collision.LineBlocked(world.Colliders, new Vec2(a.x, a.z), new Vec2(b.x, b.z));
        }

        private bool FarLost(Vector3 pos) => _quarry != null && Flat2(_quarry.transform.position, pos) > LoseRange * LoseRange;

        /// <summary>
        /// Nearest living searcher, ignoring line of sight and range — the predator's coarse instinct
        /// for where prey is. Used ONLY to aim the prowl (a slow, jittered heading), never to attack:
        /// abilities still require the precise, LOS-gated <see cref="Perceive"/>. So this makes the bot
        /// close on you; it does not let it hit you through walls.
        /// </summary>
        private HPPlayer NearestSearcherRaw()
        {
            HPPlayer best = null;
            float bestD2 = float.MaxValue;
            Vector3 pos = transform.position;
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot || p.Status.Value == HPPlayer.StatusIncap) continue;
                float d2 = Flat2(p.transform.position, pos);
                if (d2 < bestD2) { bestD2 = d2; best = p; }
            }
            return best;
        }

        // --- abilities -------------------------------------------------------------

        /// <summary>Grab a frozen searcher in reach; otherwise roar if one is in the freeze radius and
        /// the roar is off cooldown. Both re-validate server-side, so this only decides WHEN to try.</summary>
        private void TryAbilities()
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot || p.Status.Value != HPPlayer.StatusFrozen) continue;
                if (Flat2(p.transform.position, transform.position) <= GameManager.GrabRadius * GameManager.GrabRadius)
                {
                    _self.ServerBotGrab();
                    return;
                }
            }

            if (_self.RoarReadyIn.Value > 0f) return;
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot || p.Status.Value != HPPlayer.StatusActive) continue;
                if (Flat2(p.transform.position, transform.position) <= GameManager.RoarRadius * GameManager.RoarRadius)
                {
                    _self.ServerBotRoar();
                    return;
                }
            }
        }

        // --- navigation ------------------------------------------------------------

        private void Repath(Vector3 goal, bool force = false)
        {
            if (!force && Time.time < _repathAt) return;
            _repathAt = Time.time + RepathInterval;
            if (NavMesh.CalculatePath(transform.position, goal, NavMesh.AllAreas, _path) && _path.corners.Length > 0)
            {
                _corners = _path.corners;
                _corner = _corners.Length > 1 ? 1 : 0; // corner 0 is our own position
            }
        }

        /// <summary>Face the next path corner and walk. If pathing yielded nothing (off-mesh, blocked),
        /// beeline the goal so the bot never locks up — the sim's collision handles the trees.</summary>
        private void SteerAlongPath(Vector3 pos, Vector3 fallbackGoal, bool sprint)
        {
            Vector3 target;
            if (_corners.Length > 0 && _corner < _corners.Length)
            {
                target = _corners[_corner];
                if (Flat2(target, pos) <= CornerReach * CornerReach && _corner < _corners.Length - 1) _corner++;
            }
            else target = fallbackGoal;

            _self.ServerBotFace(target.x - pos.x, target.z - pos.z);
            _self.ServerBotDrive(new MoveInput { W = true, Sprint = sprint, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
        }

        /// <summary>
        /// A wander goal a good distance off, in a random direction. The NavMesh only REFINES it (snap
        /// to the nearest walkable spot); it is never REQUIRED — the raw terrain point is returned if
        /// the mesh isn't there, so a failed bake degrades the bot to "walks the forest with the sim
        /// dodging trees" instead of "stands still". Never returns `around`, which was the standing bug.
        /// </summary>
        private Vector3 RandomNavPoint(Vector3 around)
        {
            var world = WorldBuilder.World;
            float half = (float)Sim.World.Size / 2f - 12f;

            for (int i = 0; i < 6; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float dist = Mathf.Lerp(35f, WanderRadius, Random.value); // always meaningfully far
                float x = Mathf.Clamp(around.x + Mathf.Cos(ang) * dist, -half, half);
                float z = Mathf.Clamp(around.z + Mathf.Sin(ang) * dist, -half, half);
                float y = world != null ? (float)world.GetHeight(x, z) : around.y;
                Vector3 probe = new Vector3(x, y, z);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 14f, NavMesh.AllAreas)) return hit.position;
                if (i == 5) return probe; // no navmesh — use the raw terrain point rather than give up
            }
            return around; // unreachable (the i==5 branch returns first), kept for the compiler
        }

        /// <summary>Squared XZ distance — height never matters for chase/range decisions.</summary>
        private static float Flat2(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
