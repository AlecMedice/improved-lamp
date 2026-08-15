// The CPU Yeti brain — the opponent in offline single-player, and the fastest way to test solo.
// Meant to be a LEGITIMATE opponent someone plays without internet, not a dev prop.
//
// REWRITTEN 2026-08-14 (docs/AI Rewrite.md). The sensing here was already honest and is KEPT: sight
// needs line of sight, a lit torch is a beacon visible much farther, hearing scales with how loudly
// you move and a crouch makes no sound at all. What was not honest was the FALLBACK. With nothing
// perceived, the old brain prowled toward NearestSearcherRaw() — the true position of the nearest
// searcher, through walls, from anywhere on the map — and since that was the default mode, "it
// beelines at me" was the normal experience. The old code knew: Mode.Hunt's own doc comment called
// it "omniscience, dressed up with jitter" and recommended play-testing Mode.Track instead.
//
// The fallback is now a BeliefMap. When the Yeti loses you it does not consult your position; it
// consults a probability field built from what it actually sensed, and that field DIFFUSES — the
// longer since it saw you, the wider the area it has to consider. So losing it is real, and being
// found again is the result of it working the odds rather than reading your transform.
//
// Behaviours, highest priority first:
//   DRAG      — hauling a victim away from the duffel (a body dropped in the dark costs a search)
//   DAZZLED   — blinded and ability-locked: get out of the beam instead of posing for the camera
//   HUNT      — perceived right now: close, and use roar/grab
//   STALK     — perceived, but far and unaware: approach along cover, torch-side, no sprint
//   AMBUSH    — high belief nearby but nothing seen: sit still on the likely approach and wait
//   GUARD     — a downed searcher is bait; hold off it and take whoever comes
//   SEARCH    — work the belief field, densest ground first
//   TRACK     — follow snow prints (the honest signal only the Yeti can read)
//   PATROL    — nothing at all: sweep where searchers are likely to be, not at random
//
// INTENT ONLY, and that must stay true: it never moves a transform or resolves an ability itself.
// It hands intent to HPPlayer.ServerBot*, which run the same shared sim and the same
// GameManager.Try* authority a human's input lands in — identical collision, stamina, cooldowns and
// ranges, with no parallel "AI physics" to drift out of parity. HOST ONLY.
using FishNet;
using Metoh.Sim;
using UnityEngine;
using UnityEngine.AI;

namespace Metoh.Game
{
    [RequireComponent(typeof(HPPlayer))]
    public class YetiBot : MonoBehaviour
    {
        // --- memory / commitment ---------------------------------------------------
        /// <summary>Seconds the bot keeps hunting a last-known position after losing the searcher.</summary>
        private const float MemorySeconds = 7f;
        private const float LoseRange = 85f;

        // --- tracking (snow prints) ------------------------------------------------
        private const float TrackMaxAge = 22f;
        private const float TrackRepick = 2.5f;
        private const float TrackSprintBeyond = 30f;
        private const float TrackReach = 4f;

        // --- crevasse fast-travel --------------------------------------------------
        private const float TravelWorthwhile = 120f;
        private const float TravelThinkInterval = 6f;

        // --- reactions -------------------------------------------------------------
        private const float DazzleBreakSeconds = 3.5f;

        /// <summary>Spend the roar on a lone target only inside this, where the grab should land.</summary>
        private const float RoarCommitRange = 14f;

        // --- predator behaviours ---------------------------------------------------
        /// <summary>Beyond this a perceived, unaware searcher is stalked rather than charged.</summary>
        private const float StalkBeyond = 26f;
        /// <summary>Belief peak above which sitting still and waiting beats walking around.</summary>
        private const float AmbushConfidence = 0.05f;
        /// <summary>How long an ambush holds before it gives up and searches again.</summary>
        private const float AmbushSeconds = 7f;
        /// <summary>Stand this far off a downed body when guarding it — close enough to punish a
        /// rescue, far enough that the rescuer commits before noticing.</summary>
        private const float GuardStandoff = 13f;

        // --- pressure governor -----------------------------------------------------
        /// <summary>
        /// After a grab the bot deliberately eases off for a while.
        ///
        /// Not mercy — pacing. A predator that converts every success straight into the next hunt
        /// removes the part of the night the game is actually about (the searching), and a team that
        /// never gets a quiet minute never gets to bank, so matches end in a shutout that teaches the
        /// player nothing. Backing off also makes the next approach frightening again, because dread
        /// needs a gap to grow in.
        /// </summary>
        private const float PressureBackoffSeconds = 18f;
        private const float PressureBackoffRange = 55f;

        // --- movement --------------------------------------------------------------
        private const float SprintBeyond = 8f;
        private const float CornerReach = 1.5f;
        private const float RepathInterval = 0.4f;
        private const float PatrolGoalSeconds = 8f;

        private HPPlayer _self;
        private BotPerception _senses;
        private BeliefMap _belief;
        private UtilityChooser _chooser;
        private BotRandom _rng;

        private NavMeshPath _path; // see the note below — never a field initializer
        private Vector3[] _corners = System.Array.Empty<Vector3>();
        private int _corner;
        private float _repathAt;

        // Sensing cache.
        private Contact _contact;
        private float _contactDist = float.MaxValue;
        private float _perceiveAt;
        private float _beliefAt;

        // Memory.
        private HPPlayer _quarry;
        private Vector3 _lastKnown;
        private float _awareUntil;
        private float _backoffUntil;

        private Vector3 _patrolGoal;
        private float _patrolUntil;
        private Vector3 _ambushSpot;
        private float _ambushUntil;

        private ClueMarker _track;
        private float _trackRepickAt;
        private float _travelThinkAt;
        private float _breakOffUntil;
        private Vector3 _breakOffDir;

        private void Awake()
        {
            _self = GetComponent<HPPlayer>();
            // MUST be built here, never as a field initializer: NavMeshPath's constructor calls
            // InitializeNavMeshPath, which Unity forbids from a MonoBehaviour constructor. C# compiles
            // field initializers into the constructor in declaration order, so a throw here abandons
            // EVERY initializer below it and leaves them null — the brain then dies on the first
            // dereference in Update(), every frame, having never thought once.
            _path = new NavMeshPath();
            _senses = new BotPerception();
            _belief = new BeliefMap();
            _chooser = new UtilityChooser();
            _rng = BotRandom.For(_self);
            // The bake is on demand and a bot waking up IS the demand — nothing else reads a NavMesh.
            // Idempotent, so the second bot's call is free.
            WorldBuilder.EnsureNavMesh();
        }

        private void Update()
        {
            // Each guard reports through DbgState rather than the Console: this used to log a throttled
            // line every second per bot forever, and in the editor that is a stack-trace capture and a
            // Console row per second, times five bots. Literals only — LateUpdate compares by reference.
            if (!InstanceFinder.IsServerStarted) { Force("off: not server"); return; }
            if (_self == null) return;
            if (!_self.IsBot) { Force("off: not a bot"); return; }
            var gm = GameManager.Instance;
            if (gm == null) { Force("off: no manager"); return; }
            if (gm.MatchPhase.Value != GameManager.PhasePlaying) { Force("off: not playing"); return; }
            if (gm.IntermissionActive) { Force("off: intermission"); return; }
            if (_self.Status.Value != HPPlayer.StatusActive) { Force("off: not active"); return; }

            float dt = Mathf.Min(Time.deltaTime, 0.1f);

            // BEFORE the pause check, and that ordering is load-bearing: this measures distance/dt, so
            // skipping it while paused freezes the sample but not dt, and the first frame after
            // unpausing divides a whole pause of travel by one frame. Every searcher then reads as
            // sprinting and the bot "hears" the entire map the instant you let it go.
            _senses.SampleSpeeds(dt);

            // DEV freeze (F3). After the guards, before any steering: perception and scoring never run,
            // so the bot holds position AND holds its last state, which is what makes it inspectable.
            // Still driven with a null input so the shared sim keeps ticking — otherwise stamina
            // neither drains nor recovers and it resumes as winded as it paused.
            if (Paused)
            {
                Force("PAUSED");
                _self.ServerBotDrive(new MoveInput { W = false, Dt = dt });
                return;
            }

            if (Time.time >= _perceiveAt)
            {
                _perceiveAt = Time.time + 1f / BotBrainTuning.PerceiveHz;
                Sense();
            }
            if (Time.time >= _beliefAt)
            {
                float bdt = 1f / BotBrainTuning.BeliefHz;
                _beliefAt = Time.time + bdt;
                TickBelief(bdt);
            }
            if (_chooser.ShouldDecide) Decide();

            // Acting runs EVERY frame even though deciding does not: steering has to be smooth, and a
            // bot that only updated its heading five times a second visibly stair-steps around corners.
            Act(transform.position);
        }

        // --- sensing ----------------------------------------------------------------

        private void Sense()
        {
            Vector3 pos = transform.position;
            _contact = _senses.PerceiveSearcher(pos);
            _contactDist = _contact.Who != null ? Mathf.Sqrt(BotPerception.Flat2(_contact.At, pos)) : float.MaxValue;

            if (_contact.Who != null)
            {
                _quarry = _contact.Who;
                _lastKnown = _contact.At;
                _awareUntil = Time.time + MemorySeconds;
                // A sighting is a hard fix; a sound is a bearing. Confidence carries that difference
                // into the field rather than flattening both into "I know where you are".
                if (_contact.Seen) _belief.Sighting(_contact.At);
                else _belief.Rumour(_contact.At, 22f, _contact.Confidence);
            }
            else if (_quarry != null && (Time.time >= _awareUntil || FarLost(pos)))
            {
                _quarry = null;
            }
        }

        private void TickBelief(float dt)
        {
            Vector3 pos = transform.position;
            _belief.RumourTrust = BotDifficulty.RumourTrust;
            _belief.SpreadMul = BotDifficulty.SpreadMul;

            // Negative information: we are here and see nobody, so they are probably not here. This is
            // what stops the bot re-walking ground it just cleared.
            if (_contact.Who == null) _belief.Cleared(pos, BotPerception.YetiSightRange * 0.9f);

            // Snow prints are the Yeti's honest signal and nobody else can read them, so they feed the
            // field directly rather than being a separate chase mode.
            ClueMarker print = FreshestPrint(pos);
            if (print != null)
            {
                float age = Time.time - print.Born;
                _belief.Rumour(print.transform.position, Mathf.Lerp(18f, 60f, Mathf.Clamp01(age / TrackMaxAge)),
                               Mathf.Lerp(0.6f, 0.2f, Mathf.Clamp01(age / TrackMaxAge)));
            }

            // Searchers must return to the duffel to score, so camp is permanently the likeliest place
            // to find one. This is a prior, not knowledge — it is true of the MAP, not of any player,
            // which is exactly the kind of inference a predator is entitled to make.
            //
            // Mode.Track opts out: that mode's contract is "sensed evidence only", and a standing hint
            // about camp is an inference the mode is explicitly there to exclude. Without this gate
            // Track would quietly drift toward camp forever and stop being the honest comparison it
            // exists to provide.
            if (AiMode == Mode.Hunt) _belief.Rumour(WorldBuilder.DuffelPosition(), 55f, 0.05f);

            _belief.Tick(dt, (float)Sim.Player.SprintSpeed);
        }

        // --- deciding ---------------------------------------------------------------

        private void Decide()
        {
            Vector3 pos = transform.position;
            _chooser.Begin();

            // Hard interrupts first — these are not decisions and must not be argued with by the
            // commitment rules, so they bypass scoring entirely.
            if (IsDragging()) { _chooser.Force("DRAG"); return; }

            if (_self.Dazzled.Value && Time.time >= _breakOffUntil - DazzleBreakSeconds)
            {
                _breakOffUntil = Time.time + DazzleBreakSeconds;
                Vector3 from = _contact.Who != null ? _contact.At : _lastKnown;
                Vector3 away = pos - from;
                away.y = 0f;
                _breakOffDir = away.sqrMagnitude > 1f ? away.normalized : -transform.forward;
            }
            if (Time.time < _breakOffUntil) { _chooser.Force("DAZZLED"); return; }

            bool easing = Time.time < _backoffUntil;

            // --- HUNT / STALK --------------------------------------------------------
            if (_contact.Who != null)
            {
                // Close and committed, or far and creeping. Splitting these is most of what makes it
                // read as a predator: charging from 40 m announces itself and the target simply walks
                // away, while closing quietly to 25 m first turns the same approach into an ambush.
                float near = Util.Closeness(_contactDist, 6f, StalkBeyond);
                if (!easing) _chooser.Consider("HUNT", 0.75f + 0.25f * near);
                _chooser.Consider("STALK", (_contactDist > StalkBeyond ? 0.7f : 0.2f) * (easing ? 0.5f : 1f));
            }

            // --- GUARD ---------------------------------------------------------------
            // A downed searcher is bait that the team has to come to. Standing off one is a trap the
            // rescue walks into, and it costs nothing to set.
            // GUARD and AMBUSH are the two behaviours that turn the Yeti from a chaser into a
            // predator, and both are Hunt-only: Track's contract is to react to sensed evidence and
            // nothing else, and lying in wait is a plan rather than a reaction.
            bool scheming = AiMode == Mode.Hunt && !easing;

            HPPlayer bait = NearestDowned(pos, out float baitDist);
            if (bait != null && baitDist < 90f && scheming)
                _chooser.Consider("GUARD", 0.65f * Util.Closeness(baitDist, 10f, 90f));

            // --- AMBUSH --------------------------------------------------------------
            // Strong belief, nothing seen: waiting beats walking. A moving predator is one the prey
            // hears coming; a still one in the right place is the reason the trail was worth reading.
            if (_contact.Who == null && _belief.Confidence > AmbushConfidence && scheming)
                _chooser.Consider("AMBUSH", 0.55f * Mathf.Clamp01(_belief.Confidence * 12f));

            // --- SEARCH / TRACK ------------------------------------------------------
            if (_contact.Who == null && _quarry != null && Time.time < _awareUntil)
                _chooser.Consider("SEARCH", 0.6f);

            if (AiMode != Mode.Random && FreshestPrint(pos) != null)
                _chooser.Consider("TRACK", 0.45f);

            // --- PATROL --------------------------------------------------------------
            // The floor. Not random roam: it walks the belief field's best unexplored ground, so even
            // with nothing sensed the Yeti moves like something working a territory.
            _chooser.Consider("PATROL", AiMode == Mode.Random ? 0.9f : 0.3f);

            _chooser.Resolve();
        }

        // --- acting ------------------------------------------------------------------

        private void Act(Vector3 pos)
        {
            switch (_chooser.Chosen)
            {
                case "DRAG":    DoDrag(pos); break;
                case "DAZZLED": DoDazzled(pos); break;
                case "HUNT":    DoHunt(pos); break;
                case "STALK":   DoStalk(pos); break;
                case "GUARD":   DoGuard(pos); break;
                case "AMBUSH":  DoAmbush(pos); break;
                case "SEARCH":  DoSearch(pos); break;
                case "TRACK":   DoTrack(pos); break;
                default:        DoPatrol(pos); break;
            }
        }

        private void DoDrag(Vector3 pos)
        {
            // Take them AWAY from the duffel: a body dropped at camp is a two-second rescue, one
            // dropped in the dark costs the team a search. WHEN the haul ends is the server's call
            // (GameManager.CarrySeconds), so the brain only picks a direction.
            Vector3 away = pos - WorldBuilder.DuffelPosition();
            away.y = 0f;
            if (away.sqrMagnitude < 1f) away = transform.forward;
            Vector3 goal = pos + away.normalized * 40f;
            Repath(goal);
            SteerAlongPath(pos, goal, sprint: false); // dragging is a walk
        }

        private void DoDazzled(Vector3 pos)
        {
            Vector3 goal = pos + _breakOffDir * 30f;
            Repath(goal);
            SteerAlongPath(pos, goal, sprint: true);
        }

        private void DoHunt(Vector3 pos)
        {
            TryAbilities();
            Vector3 goal = _contact.Who != null ? _contact.At : _lastKnown;
            Repath(goal);
            float d = Mathf.Sqrt(BotPerception.Flat2(goal, pos));
            SteerAlongPath(pos, goal, sprint: d > SprintBeyond);
        }

        /// <summary>
        /// Close on a target that has not reacted yet — quietly, and off the direct line.
        ///
        /// Approaching along the exact bearing is what makes a chase readable: the searcher sees a
        /// shape growing dead ahead and backs straight off. Coming in on a slight arc keeps the
        /// distance closing while giving the silhouette somewhere to hide, and not sprinting keeps it
        /// out of the hearing model that the searchers' own torch-lit sweep would otherwise catch.
        /// </summary>
        private void DoStalk(Vector3 pos)
        {
            TryAbilities();
            Vector3 target = _contact.Who != null ? _contact.At : _lastKnown;
            Vector3 toward = target - pos;
            toward.y = 0f;
            if (toward.sqrMagnitude < 1f) { DoHunt(pos); return; }

            // Swing wide by a per-bot-stable amount, so two Yetis (or one across two matches) don't
            // arc identically. Sign is fixed per bot rather than re-rolled, or the approach wobbles.
            float side = _stalkSide == 0f ? (_stalkSide = _rng.Value < 0.5f ? -1f : 1f) : _stalkSide;
            Vector3 perp = new Vector3(-toward.z, 0f, toward.x).normalized * side;
            float arc = Mathf.Clamp(toward.magnitude * 0.35f, 4f, 18f);
            Vector3 goal = target - toward.normalized * 6f + perp * arc;

            Repath(goal);
            SteerAlongPath(pos, goal, sprint: false); // walking is quiet; sprinting announces you
        }

        private float _stalkSide;

        private void DoGuard(Vector3 pos)
        {
            HPPlayer bait = NearestDowned(pos, out _);
            if (bait == null) { DoSearch(pos); return; }
            TryAbilities();

            // Hold a post off the body rather than standing on it — a rescuer who can see the Yeti
            // from range simply doesn't come, and then the bait is wasted.
            Vector3 body = bait.transform.position;
            Vector3 off = pos - body;
            off.y = 0f;
            if (off.sqrMagnitude < 1f) off = transform.forward;
            Vector3 post = body + off.normalized * GuardStandoff;

            if (BotPerception.Flat2(post, pos) > 16f) { Repath(post); SteerAlongPath(pos, post, sprint: false); }
            else
            {
                _self.ServerBotFace(body.x - pos.x, body.z - pos.z);
                _self.ServerBotDrive(new MoveInput { W = false, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
            }
        }

        private void DoAmbush(Vector3 pos)
        {
            if (Time.time >= _ambushUntil)
            {
                _ambushSpot = _belief.Best();
                _ambushUntil = Time.time + AmbushSeconds;
            }
            TryAbilities();

            // Walk to the likely ground, then STOP. Standing still is the whole behaviour: it makes no
            // sound, so the hearing model the searchers do not have cannot save them, and it puts the
            // Yeti where the odds say they will walk.
            if (BotPerception.Flat2(_ambushSpot, pos) > 64f)
            {
                Repath(_ambushSpot);
                SteerAlongPath(pos, _ambushSpot, sprint: false);
            }
            else
            {
                _self.ServerBotFace(_ambushSpot.x - pos.x, _ambushSpot.z - pos.z);
                _self.ServerBotDrive(new MoveInput { W = false, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
            }
        }

        private void DoSearch(Vector3 pos)
        {
            TryAbilities();
            Vector3 goal = _quarry != null ? _lastKnown : _belief.Best();
            Repath(goal);
            SteerAlongPath(pos, goal, sprint: BotPerception.Flat2(goal, pos) > SprintBeyond * SprintBeyond);
        }

        private void DoTrack(Vector3 pos)
        {
            bool reached = _track != null && BotPerception.Flat2(_track.transform.position, pos) <= TrackReach * TrackReach;
            if (_track == null || reached || Time.time >= _trackRepickAt || Time.time - _track.Born > TrackMaxAge)
            {
                _track = FreshestPrint(pos);
                _trackRepickAt = Time.time + TrackRepick;
            }
            if (_track == null) { DoPatrol(pos); return; }

            Vector3 goal = _track.transform.position;
            float d = Mathf.Sqrt(BotPerception.Flat2(goal, pos));

            // Cold trail a long way off: take the crevasse network rather than jogging the map. Goes
            // through the same validated authority a human uses — cooldown and "must be standing in a
            // mouth" still apply, so it only happens when the reposition was genuinely earned.
            if (d > TravelWorthwhile && Time.time >= _travelThinkAt)
            {
                _travelThinkAt = Time.time + TravelThinkInterval;
                TryCrevasseTravel(pos, goal, d);
            }

            Repath(goal);
            SteerAlongPath(pos, goal, sprint: d > TrackSprintBeyond);
        }

        private void DoPatrol(Vector3 pos)
        {
            if (Time.time >= _patrolUntil || BotPerception.Flat2(pos, _patrolGoal) < 81f)
            {
                // Mode.Random means "do not seek at all" — it exists to test everything that is not
                // the chase, so it must not consult belief. Everything else patrols the odds.
                _patrolGoal = AiMode == Mode.Random
                    ? RandomNavPoint(pos)
                    : _belief.BestSearchTarget(pos, null, _self.ObjectId);
                _patrolGoal += new Vector3(_rng.Signed * 8f, 0f, _rng.Signed * 8f);
                _patrolUntil = Time.time + PatrolGoalSeconds;
                Repath(_patrolGoal, force: true);
            }
            else Repath(_patrolGoal);
            SteerAlongPath(pos, _patrolGoal, sprint: false);
        }

        // --- abilities ---------------------------------------------------------------

        /// <summary>Grab a frozen searcher in reach; else roar if the shot is worth the cooldown.
        /// Both re-validate server-side, so this only decides WHEN to try.</summary>
        private void TryAbilities()
        {
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti || p.Status.Value != HPPlayer.StatusFrozen) continue;
                if (BotPerception.Flat2(p.transform.position, transform.position) <=
                    (float)(GameManager.GrabRadius * GameManager.GrabRadius))
                {
                    _self.ServerBotGrab();
                    // A landed grab is a success, and successes are what the pacing governor counts.
                    _backoffUntil = Time.time + PressureBackoffSeconds;
                    return;
                }
            }

            if (_self.RoarReadyIn.Value > 0f) return;

            // The roar is a long cooldown and an AoE whose follow-up grab can only take one person, so
            // spending it the instant somebody clips the radius is usually waste. Hold unless the shot
            // is actually worth taking: two or more caught, or one close enough that the grab lands.
            int caught = 0;
            float nearest2 = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti || p.Status.Value != HPPlayer.StatusActive) continue;
                float d2 = BotPerception.Flat2(p.transform.position, transform.position);
                if (d2 > (float)(GameManager.RoarRadius * GameManager.RoarRadius)) continue;
                caught++;
                if (d2 < nearest2) nearest2 = d2;
            }
            if (caught == 0) return;
            if (caught >= 2 || nearest2 <= RoarCommitRange * RoarCommitRange) _self.ServerBotRoar();
        }

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

        // --- lookups ------------------------------------------------------------------

        private bool IsDragging()
        {
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti) continue;
                if (p.GrabberObjectId.Value == _self.ObjectId) return true;
            }
            return false;
        }

        private HPPlayer NearestDowned(Vector3 pos, out float dist)
        {
            HPPlayer best = null;
            float bestD2 = float.MaxValue;
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p.IsYeti || p.Status.Value != HPPlayer.StatusIncap) continue;
                float d2 = BotPerception.Flat2(p.transform.position, pos);
                if (d2 < bestD2) { bestD2 = d2; best = p; }
            }
            dist = best != null ? Mathf.Sqrt(bestD2) : float.MaxValue;
            return best;
        }

        private ClueMarker FreshestPrint(Vector3 pos)
        {
            ClueMarker best = null;
            float bestScore = float.MinValue;
            var all = ClueMarker.All;
            for (int i = 0; i < all.Count; i++)
            {
                ClueMarker c = all[i];
                if (c == null || c.CType.Value != ClueMarker.TypeSnowPrint) continue;
                float age = Time.time - c.Born;
                if (age > TrackMaxAge) continue;
                // Freshness leads, distance is a real tie-breaker. Unlike the searchers' old clue
                // scoring, this one is FINE as-is: snow prints are the Yeti's designed signal and it is
                // meant to read the whole field of them. The searcher bug was that it applied the same
                // weighting to a signal it should only have been able to see from a few metres away.
                float score = -age * 10f - Mathf.Sqrt(BotPerception.Flat2(c.transform.position, pos)) * 0.15f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        private bool FarLost(Vector3 pos) =>
            _quarry != null && BotPerception.Flat2(_quarry.transform.position, pos) > LoseRange * LoseRange;

        // --- dev knobs -----------------------------------------------------------------

        public string DbgState => _chooser != null ? _chooser.Chosen : "—";
        public string DbgScores => _chooser != null ? _chooser.DebugTop(3) : "";
        public float DbgConfidence => _belief != null ? _belief.Confidence : 0f;
        public BeliefMap Belief => _belief;
        /// <summary>Where this bot's night actually went — dumped to the play log at match end.</summary>
        public string DbgHistogram => _chooser != null ? _chooser.Histogram() : "";

        private void Force(string s) => _chooser?.Force(s);

        /// <summary>How the bot behaves when it has nothing perceived.</summary>
        public enum Mode
        {
            /// <summary>Full predator: belief-driven patrol, ambush, stalking and guarding.</summary>
            Hunt = 0,
            /// <summary>Perception and snow prints only — no camp prior, no ambush. Break line of sight
            /// and stay on the packed trails and it genuinely has nothing.</summary>
            Track = 1,
            /// <summary>Roam and never seek. Engages only if you walk into its senses.</summary>
            Random = 2,
        }

        /// <summary>
        /// DEV (F3): which brain the CPU Yeti runs. Static so it applies to every bot and survives a
        /// reseed.
        ///
        /// Hunt is now safe as the default, which it was not before: it used to mean "walk at the
        /// nearest searcher's true position" and now means "work the belief field". The old warning
        /// that Hunt reads as homing no longer applies, because nothing in this brain reads a
        /// searcher's transform without having sensed them first.
        /// </summary>
        public static Mode AiMode = Mode.Hunt;

        /// <summary>DEV (F3): freeze where it stands. It keeps sensing and scoring — only movement and
        /// abilities stop, so you can walk up and read what it thinks it is doing.</summary>
        public static bool Paused;

        /// <summary>DEV (F3): scale movement speed. 0.5 makes a chase slow enough to watch and to
        /// out-walk deliberately, which is how you tell "it tracked me" from "it caught me".</summary>
        public static float SpeedMul = 1f;

        /// <summary>Back-compat for anything still asking the old yes/no question.</summary>
        public static bool AggressiveProwl => AiMode == Mode.Hunt;

        // --- navigation ----------------------------------------------------------------

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
                if (BotPerception.Flat2(target, pos) <= CornerReach * CornerReach && _corner < _corners.Length - 1)
                    _corner++;
            }
            else target = fallbackGoal;

            _self.ServerBotFace(target.x - pos.x, target.z - pos.z);
            _self.ServerBotDrive(new MoveInput { W = true, Sprint = sprint, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
        }

        /// <summary>
        /// A wander goal a good distance off. The NavMesh only REFINES it (snap to the nearest walkable
        /// spot); it is never REQUIRED — the raw terrain point is returned if the mesh isn't there, so a
        /// failed bake degrades to "walks the forest with the sim dodging trees" rather than to
        /// "stands still". Never returns `around`, which was the standing bug.
        /// </summary>
        private Vector3 RandomNavPoint(Vector3 around)
        {
            var world = WorldBuilder.World;
            float half = (float)Sim.World.Size / 2f - 12f;
            for (int i = 0; i < 6; i++)
            {
                float ang = _rng.Value * Mathf.PI * 2f;
                float dist = Mathf.Lerp(35f, 120f, _rng.Value);
                float x = Mathf.Clamp(around.x + Mathf.Cos(ang) * dist, -half, half);
                float z = Mathf.Clamp(around.z + Mathf.Sin(ang) * dist, -half, half);
                float y = world != null ? (float)world.GetHeight(x, z) : around.y;
                Vector3 probe = new Vector3(x, y, z);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 14f, NavMesh.AllAreas)) return hit.position;
                if (i == 5) return probe; // no navmesh — raw terrain point rather than give up
            }
            return around; // unreachable (the i==5 branch returns first), kept for the compiler
        }

        // --- play-test log ---------------------------------------------------------------

        private void LateUpdate()
        {
            if (_self == null || _chooser == null) return;
            // Cheap comparisons FIRST. Formatting unconditionally and letting HPLog.Change drop the
            // duplicate would allocate a string every frame, which on the integrated GPU this is tuned
            // for is exactly the steady GC churn [perf] warns about.
            if (ReferenceEquals(_chooser.Chosen, _loggedState) && AiMode == _loggedMode &&
                Paused == _loggedPaused && Mathf.Approximately(SpeedMul, _loggedSpeed)) return;
            _loggedState = _chooser.Chosen;
            _loggedMode = AiMode;
            _loggedPaused = Paused;
            _loggedSpeed = SpeedMul;

            HPLog.Change("yeti.ai", "AI", $"{_chooser.Chosen} (mode {AiMode}{(Paused ? ", PAUSED" : "")}" +
                                          $"{(SpeedMul < 0.999f ? $", {SpeedMul:0.00}x" : "")}) [{_chooser.DebugTop(3)}]");
        }

        private string _loggedState;
        private Mode _loggedMode = (Mode)(-1); // never a real mode, so the first frame always logs
        private bool _loggedPaused;
        private float _loggedSpeed = -1f;
    }
}
