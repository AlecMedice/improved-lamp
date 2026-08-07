// The CPU Yeti brain — the opponent in the offline single-player mode (and the fastest way to
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
// TRACKING is how it finds you in the first place. The old brain closed the gap by walking at the
// nearest searcher's true position — omniscience, dressed up with jitter. It now follows SNOW PRINTS
// instead: the tracks searchers leave off-trail, which only the Yeti can see. That is the same
// information the fiction says it has, so the behaviour is honest, and it makes the deep-snow mechanic
// cut both ways — stay on the packed trails or in camp and you leave the bot genuinely nothing to
// follow. The omniscient prowl survives only as a last resort when no track exists at all, so a team
// that hides perfectly still isn't rewarded with a Yeti that wanders the far side of the map forever.
//
// The full behaviour set, in priority order:
//   DAZZLED  — blinded, abilities locked: break off and get out of the beam rather than stand in it
//   DRAG     — after a grab, haul the victim away from the duffel before dropping them
//   HUNT     — perceived right now: close and use roar/grab
//   SEARCH   — lost them: work the last-known position until memory expires
//   TRACK    — follow the freshest snow print, and take a crevasse if the trail is cold and far
//   PROWL    — no tracks anywhere: the old coarse instinct, as a floor
//   WANDER   — nobody left to hunt
//
// All tuning here is first-guess and has NOT run in the editor. The named constants are the dials.
using FishNet;
using Metoh.Sim;
using UnityEngine;
using UnityEngine.AI;

namespace Metoh.Game
{
    [RequireComponent(typeof(HPPlayer))]
    public class YetiBot : MonoBehaviour
    {
        // --- perception (first-guess; editor-tune) ---------------------------------
        /// <summary>Sees an unlit searcher this far, with clear line of sight (Yeti has night eyes).</summary>
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

        // --- tracking: following snow prints (first-guess) -------------------------
        /// <summary>Ignore prints older than this. Below the server's 35 s print lifetime on purpose —
        /// a track at the edge of expiry is a lead to nowhere, and chasing it looks like confusion.</summary>
        private const float TrackMaxAge = 22f;
        /// <summary>Re-choose which print to follow only this often, so it commits to a trail
        /// instead of twitching between two searchers' tracks every frame.</summary>
        private const float TrackRepick = 2.5f;
        /// <summary>Sprint toward a track farther than this; walk it in so perception can catch up.</summary>
        private const float TrackSprintBeyond = 30f;
        /// <summary>Consider a print reached at this radius — then immediately look for a fresher one.</summary>
        private const float TrackReach = 4f;

        // --- crevasse fast-travel (first-guess) ------------------------------------
        /// <summary>Only bother travelling if it saves at least this much ground.</summary>
        private const float TravelWorthwhile = 120f;
        /// <summary>Don't consider travelling more often than this (the server also has a cooldown).</summary>
        private const float TravelThinkInterval = 6f;

        // --- reactions (first-guess) -----------------------------------------------
        /// <summary>Seconds to retreat after being dazzled — long enough that the searcher's beam wins
        /// them real distance, short enough that it comes back.</summary>
        private const float DazzleBreakSeconds = 3.5f;
        // (How long a haul lasts is GameManager.CarrySeconds now — the server owns the carry timer,
        // and the bot's own DragSeconds/DragClearOfDuffel early-drop went with the toggle release.)

        // --- target preference (first-guess) ---------------------------------------
        /// <summary>Score bonus for a searcher carrying proof — worth ~25 m of extra walk.</summary>
        private const float CarrierBias = 25f;
        /// <summary>Score bonus for a searcher currently stuck in a drift basin.</summary>
        private const float BoggedBias = 15f;

        /// <summary>Spend the roar on a lone target only inside this range, where the grab should land.
        /// Comfortably under the ~25 m roar radius, so a single searcher at the fringe doesn't burn it.</summary>
        private const float RoarCommitRange = 14f;

        // --- movement (first-guess) ------------------------------------------------
        private const float LoseRange = 85f;   // drop a quarry once it's this far (hysteresis vs re-acquire)
        private const float SprintBeyond = 8f; // sprint when the quarry is farther than this, else close carefully
        private const float CornerReach = 1.5f;
        private const float RepathInterval = 0.4f;
        private const float WanderGoalSeconds = 8f;
        private const float WanderRadius = 120f;

        private HPPlayer _self;
        // MUST be built in Awake, never as a field initializer: NavMeshPath's constructor calls
        // InitializeNavMeshPath, which Unity forbids from a MonoBehaviour constructor. C# compiles
        // field initializers into the constructor in declaration order, so a throw here abandons
        // EVERY initializer below it (_corners, _lastPos, _speed) and leaves them null — the brain
        // then dies on the first dereference of Update(), every frame, having never thought once.
        private NavMeshPath _path;
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

        // Tracking / reactions.
        private ClueMarker _track;      // the print we're currently walking to
        private float _trackRepickAt;   // re-choose a print after this
        private float _travelThinkAt;   // next time we're allowed to consider a crevasse hop
        private float _breakOffUntil;   // retreating from a flashlight beam until this time
        private Vector3 _breakOffDir;   // the direction we bolted, held so the retreat is a line not a jitter

        private void Awake()
        {
            _self = GetComponent<HPPlayer>();
            _path = new NavMeshPath(); // see the field — this cannot be an initializer
            // The bake is on demand (WorldBuilder.EnsureNavMesh), and a bot waking up IS the demand —
            // nothing else in the game reads a NavMesh. Asking here rather than from the spawn site
            // means every path that creates a bot, now or later, gets a surface without knowing it
            // had to arrange one. Idempotent: the second bot's call is free.
            WorldBuilder.EnsureNavMesh();
        }

        private void Update()
        {
            // Host-authoritative and match-only. Clients hold a remote copy of the bot and never think
            // for it; the lobby and results screens are inert.
            //
            // Each guard reports itself through DbgState rather than through the Console. It used to
            // log a throttled line every second forever ("remove once the bot is confirmed hunting" —
            // it is), which in the editor means a stack-trace capture and a Console row every second
            // per bot, and there are five bots now. The overlay and the play-test log both read
            // DbgState, so nothing is lost. Literals only: LateUpdate compares by reference.
            if (!InstanceFinder.IsServerStarted) { DbgState = "off: not server"; return; }
            if (_self == null) return; // no DbgState to set — the brain has no player
            if (!_self.IsBot) { DbgState = "off: not a bot"; return; }
            var gm = GameManager.Instance;
            if (gm == null) { DbgState = "off: no manager"; return; }
            if (gm.MatchPhase.Value != GameManager.PhasePlaying) { DbgState = "off: not playing"; return; }
            if (gm.IntermissionActive) { DbgState = "off: intermission"; return; }
            if (_self.Status.Value != HPPlayer.StatusActive) { DbgState = "off: not active"; return; }
            float dt = Mathf.Min(Time.deltaTime, 0.1f);

            // The hearing sampler runs BEFORE the pause check, and this ordering is load-bearing. It
            // measures speed as (distance since last sample) / dt. Skipping it while paused freezes
            // _lastPos but not dt, so the first frame after unpausing divides a whole pause's worth of
            // travel by one frame — every searcher reads as sprinting and the bot "hears" the entire
            // map the instant you let it go. That defeats the point of pausing to watch it hunt.
            UpdateHeardSpeeds(dt);

            // DEV freeze (F3). After the guards and before any steering: perception and the state
            // machine below never run, so the bot holds position and holds its last state — which is
            // what makes it inspectable. Abilities stop with it, so a paused Yeti standing next to you
            // cannot grab you. It is still driven with a null input so the shared sim keeps ticking:
            // otherwise stamina neither drains nor recovers and it resumes as winded as it paused.
            if (Paused)
            {
                DbgState = "PAUSED";
                _self.ServerBotDrive(new MoveInput { W = false, Dt = dt });
                return;
            }

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

            // DRAG — a grab landed and we're hauling someone. Take them AWAY from the duffel: a body
            // dropped at camp is a two-second rescue, a body dropped in the dark costs the team a
            // search. Outranks everything below because the victim is already caught.
            //
            // WHEN the haul ends is not the bot's call any more. GameManager.CarrySeconds runs the
            // carry and drops the victim itself, so the brain only decides which way to walk while it
            // lasts — and the second ServerBotGrab that used to release is gone, because a second
            // grab is now refused rather than treated as a drop.
            if (IsDragging())
            {
                Vector3 away = pos - WorldBuilder.DuffelPosition();
                away.y = 0f;
                if (away.sqrMagnitude < 1f) away = transform.forward;
                DbgState = "DRAG";
                Vector3 dragGoal = pos + away.normalized * 40f;
                Repath(dragGoal);
                SteerAlongPath(pos, dragGoal, sprint: false); // dragging is a walk, not a sprint
                return;
            }

            // DAZZLED — a searcher is holding a beam on us. Roar and grab are locked server-side, so
            // standing here is pure loss: it hands them film of a stationary Yeti. Break line of sight
            // instead. This is what makes the flashlight feel like a weapon rather than a status icon.
            if (_self.Dazzled.Value && Time.time >= _breakOffUntil - DazzleBreakSeconds)
            {
                _breakOffUntil = Time.time + DazzleBreakSeconds;
                Vector3 from = seen != null ? seen.transform.position : _lastKnown;
                Vector3 away = pos - from;
                away.y = 0f;
                _breakOffDir = away.sqrMagnitude > 1f ? away.normalized : -transform.forward;
            }
            if (Time.time < _breakOffUntil)
            {
                DbgState = "DAZZLED";
                Vector3 goal = pos + _breakOffDir * 30f;
                Repath(goal);
                SteerAlongPath(pos, goal, sprint: true);
                return;
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
            // TRACK — handled inside FollowTrack (it steers and sets DbgState). Random mode skips it:
            // "random" has to mean it isn't seeking at all, and reading prints is seeking.
            else if (AiMode != Mode.Random && FollowTrack(pos))
            {
            }
            else
            {
                // Not perceiving anyone right now — but a predator doesn't wait to be walked into. It
                // PROWLS toward the nearest searcher's rough area (its instinct/scent), closing the gap
                // until they fall inside real sight/hearing and the precise HUNT takes over. The target
                // is coarse and refreshed slowly (ProwlRepick) with positional jitter, so it lags your
                // actual movement — you can still shake it by breaking line of sight and repositioning,
                // but you can't just stand at camp forever and never be found.
                // Only Mode.Hunt does this. Track and Random both drop to the wander below, and the
                // difference between them is whether FollowTrack above was allowed to run.
                HPPlayer prey = AiMode == Mode.Hunt ? NearestSearcherRaw() : null;
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

        /// <summary>
        /// Current AI state, surfaced to the F3 overlay for debugging. One of
        /// DRAG / DAZZLED / HUNT / SEARCH / TRACK / PROWL / WANDER — listed in the priority order the
        /// Update loop resolves them, so the overlay reads as "why is it doing that".
        /// </summary>
        public string DbgState { get; private set; } = "—";

        /// <summary>
        /// Write state TRANSITIONS to the play-test log. The overlay shows what the bot is doing right
        /// now; the log has to answer "what was it doing thirty seconds ago, when the tester says it
        /// got stuck" — and only the transitions carry that. HPLog.Change drops the repeats, so this
        /// costs one string compare a frame.
        /// </summary>
        private void LateUpdate()
        {
            if (_self == null) return;

            // Compare the cheap parts FIRST. Formatting the line unconditionally and letting
            // HPLog.Change discard the duplicate would allocate a string every frame, which on the
            // integrated GPU this is tuned for is exactly the kind of steady GC churn [perf] warns about.
            if (ReferenceEquals(DbgState, _loggedState) && AiMode == _loggedMode &&
                Paused == _loggedPaused && Mathf.Approximately(SpeedMul, _loggedSpeed)) return;
            _loggedState = DbgState;
            _loggedMode = AiMode;
            _loggedPaused = Paused;
            _loggedSpeed = SpeedMul;

            HPLog.Change("yeti.ai", "AI", $"{DbgState} (mode {AiMode}{(Paused ? ", PAUSED" : "")}" +
                                          $"{(SpeedMul < 0.999f ? $", {SpeedMul:0.00}x" : "")})");
        }

        private string _loggedState;
        private Mode _loggedMode = (Mode)(-1); // never a real mode, so the first frame always logs
        private bool _loggedPaused;
        private float _loggedSpeed = -1f;

        /// <summary>How the bot decides where to go when it has nothing perceived and no track.</summary>
        public enum Mode
        {
            /// <summary>Omniscient fallback: walk at a searcher's true position. This is the one that
            /// reads as "it beelines straight at me" — it always knows roughly where you are.</summary>
            Hunt = 0,
            /// <summary>Honest perception only — sight, hearing and snow prints. Break line of sight
            /// and stay on the packed trails and it genuinely loses you.</summary>
            Track = 1,
            /// <summary>Roam at random and never seek. Engages only if you walk into its senses.
            /// Useful for testing everything that is not the chase.</summary>
            Random = 2,
        }

        /// <summary>
        /// DEV (F3): which brain the CPU Yeti is running. Static so it applies to every bot and
        /// survives a reseed.
        ///
        /// Defaults to <see cref="Mode.Hunt"/> because with no fallback at all a team hiding
        /// motionless in camp is never found and the night just runs out — but Hunt is also why the
        /// bot can feel like it is homing on you through the forest, so <see cref="Mode.Track"/> is
        /// the one to play-test against. Track is arguably the better game.
        /// </summary>
        public static Mode AiMode = Mode.Hunt;

        /// <summary>DEV (F3): freeze the bot where it stands. It keeps sensing and its state machine
        /// keeps resolving — only the movement and the abilities stop, so you can walk up to it and
        /// read what it thinks it is doing.</summary>
        public static bool Paused;

        /// <summary>DEV (F3): scale the bot's movement speed. 0.5 makes a chase slow enough to watch
        /// and to out-walk deliberately, which is how you tell "it tracked me" from "it caught me".</summary>
        public static float SpeedMul = 1f;

        /// <summary>Back-compat for anything still asking the old yes/no question.</summary>
        public static bool AggressiveProwl => AiMode == Mode.Hunt;

        // --- tracking --------------------------------------------------------------

        /// <summary>
        /// Follow the freshest usable snow print. Returns false if there is no track worth walking to,
        /// which drops the caller through to the coarse prowl.
        ///
        /// This is the bot's honest answer to "where did they go" — the exact information the Yeti is
        /// designed to have and nobody else can see. It commits to one print for TrackRepick seconds
        /// rather than re-choosing every frame, because a predator that re-aims 60 times a second
        /// between two searchers' trails reads as indecision, not menace.
        /// </summary>
        private bool FollowTrack(Vector3 pos)
        {
            // Re-choose on a timer, when the current track expires/despawns, or once we've reached it.
            bool reached = _track != null && Flat2(_track.transform.position, pos) <= TrackReach * TrackReach;
            if (_track == null || reached || Time.time >= _trackRepickAt || TrackAge(_track) > TrackMaxAge)
            {
                _track = FreshestPrint(pos);
                _trackRepickAt = Time.time + TrackRepick;
            }
            if (_track == null) return false;

            Vector3 goal = _track.transform.position;
            float d = Mathf.Sqrt(Flat2(goal, pos));

            // If the trail is cold AND a long walk away, take the crevasse network instead of jogging
            // the width of the map. This is the Yeti's own fast-travel, through the same validated
            // authority a human uses — cooldown and "must be standing in a mouth" still apply, so it
            // can only do this when it has genuinely earned the reposition.
            if (d > TravelWorthwhile && Time.time >= _travelThinkAt)
            {
                _travelThinkAt = Time.time + TravelThinkInterval;
                TryCrevasseTravel(pos, goal, d);
            }

            DbgState = "TRACK";
            Repath(goal);
            SteerAlongPath(pos, goal, sprint: d > TrackSprintBeyond);
            return true;
        }

        /// <summary>Freshest snow print within TrackMaxAge, tie-broken toward the closer one.</summary>
        private ClueMarker FreshestPrint(Vector3 pos)
        {
            ClueMarker best = null;
            float bestScore = float.MinValue;
            foreach (var c in ClueMarker.All)
            {
                if (c == null || c.CType.Value != ClueMarker.TypeSnowPrint) continue;
                float age = TrackAge(c);
                if (age > TrackMaxAge) continue;
                // Freshness leads, distance is a real tie-breaker rather than a rounding error. The
                // weights are in comparable units on purpose: 10 per second of age against 0.15 per
                // metre means ~67 m of extra walk is worth one second of freshness. Weight distance
                // much lower and the bot will cross the whole 800 m map for a print a second newer,
                // abandoning a trail under its feet.
                float score = -age * 10f - Mathf.Sqrt(Flat2(c.transform.position, pos)) * 0.15f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        private static float TrackAge(ClueMarker c) => Time.time - c.Born;

        /// <summary>
        /// Hop to whichever crevasse leaves us closest to the trail, if that actually beats walking.
        /// Requires standing in a mouth (the server enforces it), so in practice this fires when the
        /// bot happens to pass one while the trail is far — which is exactly when a human would use it.
        /// </summary>
        private void TryCrevasseTravel(Vector3 pos, Vector3 goal, float walkDist)
        {
            if (_self.CaveReadyIn > 0f) return;
            var world = WorldBuilder.World;
            if (world == null || world.Caves == null) return;

            int here = Caves.NearestCaveIndex(world.Caves, pos.x, pos.z);
            if (here < 0) return; // not standing in a mouth — nothing to travel from

            int best = -1;
            float bestD = walkDist - TravelWorthwhile * 0.5f; // must beat walking by a clear margin
            for (int i = 0; i < world.Caves.Count; i++)
            {
                if (i == here) continue;
                var c = world.Caves[i];
                float dx = (float)c.X - goal.x, dz = (float)c.Z - goal.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best >= 0) _self.ServerBotCaveTravel(best);
        }

        // --- reactions -------------------------------------------------------------

        /// <summary>Are we currently hauling someone? (The victim carries our object id.)</summary>
        private bool IsDragging()
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsYeti) continue;
                if (p.GrabberObjectId.Value == _self.ObjectId) return true;
            }
            return false;
        }

        // --- perception ------------------------------------------------------------

        /// <summary>Track each searcher's speed so hearing can scale with how loudly they move.</summary>
        private void UpdateHeardSpeeds(float dt)
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsYeti) continue;
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
                if (p == null || p.IsYeti || p.Status.Value == HPPlayer.StatusIncap) continue;
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

                // Closest is the baseline, then two predator instincts on top.
                float score = 1000f - dist;

                // Go for the one holding proof. Carried evidence is worth nothing until it reaches the
                // duffel and it SPILLS on a grab, so taking the carrier is worth more than taking a
                // searcher with empty hands — this is the difference between the bot fighting the team
                // and the bot fighting their win condition.
                if (p.CarriedTotal > 0) score += CarrierBias;

                // Prefer prey that is already wading. Deep snow doesn't touch the Yeti, so a searcher
                // caught in a drift basin is the cheapest kill on the field, and cutting them off there
                // is the exact behaviour the mechanic exists to create.
                var world = WorldBuilder.World;
                if (world != null && Movement.DeepSnowDepth(world, p.transform.position.x, p.transform.position.z) > 0.35)
                    score += BoggedBias;

                if (score > bestScore) { bestScore = score; best = p; }
            }
            return best;
        }

        private bool Blocked(Vector3 a, Vector3 b)
        {
            var world = WorldBuilder.World;
            if (world == null) return false;
            return Metoh.Sim.Collision.LineBlocked(world.Colliders, new Vec2(a.x, a.z), new Vec2(b.x, b.z));
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
                if (p == null || p.IsYeti || p.Status.Value == HPPlayer.StatusIncap) continue;
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
                if (p == null || p.IsYeti || p.Status.Value != HPPlayer.StatusFrozen) continue;
                if (Flat2(p.transform.position, transform.position) <= GameManager.GrabRadius * GameManager.GrabRadius)
                {
                    _self.ServerBotGrab();
                    return;
                }
            }

            if (_self.RoarReadyIn.Value > 0f) return;

            // Roar is on a long cooldown, so spending it the instant one searcher clips the radius is
            // usually a waste — the freeze is an AoE and the follow-up grab can only take one person
            // at a time. Hold it unless the shot is actually worth taking: either it catches two or
            // more, or the single target is close enough that the grab is a near-certainty.
            int caught = 0;
            float nearest2 = float.MaxValue;
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsYeti || p.Status.Value != HPPlayer.StatusActive) continue;
                float d2 = Flat2(p.transform.position, transform.position);
                if (d2 > GameManager.RoarRadius * GameManager.RoarRadius) continue;
                caught++;
                if (d2 < nearest2) nearest2 = d2;
            }
            if (caught == 0) return;
            bool worthIt = caught >= 2 || nearest2 <= RoarCommitRange * RoarCommitRange;
            if (worthIt) _self.ServerBotRoar();
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
