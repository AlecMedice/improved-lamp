// The CPU searcher brain — the team you hunt in PLAY AS YETI, and the other half of offline play.
//
// REWRITTEN 2026-08-14 (docs/AI Rewrite.md). What was here before was a fixed priority ladder over
// near-perfect information, and it produced the single behaviour the owner reported: four searchers
// walking in a line straight at the Yeti. Three separate leaks caused that, and all three are closed
// here — worth listing, because each is easy to reintroduce:
//
//   1. CLUE SCORING was `-age*10 - dist*0.15`, which weights one second of freshness against 66
//      metres of walking. Every bot therefore targeted the single newest clue in the world, and the
//      newest clue is wherever the Yeti is standing RIGHT NOW. That is not following a trail, it is
//      homing on a live position.
//   2. CLUE VISIBILITY had no distance gate at all — the old FreshestTrailClue scanned every marker
//      on the map, so a searcher 700 m away "saw" a footprint. A clue is now only usable within
//      MapView.EvidenceSight (specialty-scaled, and much shorter with the torch off) and in line of
//      sight, so the trail has to be FOUND before it can be followed. This is the change that makes
//      the torch a real sensor rather than a status icon.
//   3. THE LADDER STARVED ITSELF. Because the Yeti drips clues continuously, FreshestTrailClue
//      practically never returned null, so INVESTIGATE always fired and Explore() — the actual
//      searching — almost never ran at all. (UNITY_NOTES [searcher-bots] called EXPLORE the weakest
//      rung; the sharper truth was that it was nearly dead code.)
//
// WHAT REPLACED IT. The bot keeps a BeliefMap: a probability field for where the Yeti is, updated by
// sightings, by rumours (roars, call-outs, trails) and — most importantly — by NEGATIVE information,
// so ground it has just looked at stops attracting it. Action choice is by utility score rather than
// by rung, so "the Yeti is close, but only just, and my bag is nearly full" is a decision the bot can
// actually express. Coordination goes through TeamBlackboard: claims, not shared eyes.
//
// UNCHANGED, AND MUST STAY THAT WAY: this is INTENT ONLY. It never moves a transform or resolves an
// action itself; it picks a direction and some booleans and hands them to HPPlayer.ServerBot*, which
// run the same shared sim and the same GameManager authority a human's input lands in. A CPU searcher
// is bound by identical collision, stamina, battery, film range/cone/LOS, channel durations and
// cooldowns. HOST ONLY, plain MonoBehaviour, attached at runtime by HPPlayer.ServerBecomeBot.
using System.Collections.Generic;
using FishNet;
using Metoh.Sim;
using UnityEngine;
using UnityEngine.AI;

namespace Metoh.Game
{
    [RequireComponent(typeof(HPPlayer))]
    public class SearcherBot : MonoBehaviour
    {
        // --- fear / spacing --------------------------------------------------------
        /// <summary>Stop retreating once this far clear.</summary>
        private const float SafeRange = 45f;
        /// <summary>Hold roughly this while filming — inside film range, outside comfortable grab reach.</summary>
        private const float FilmStandoff = 22f;
        /// <summary>A roar is heard this far and seeds a belief bump.</summary>
        private const float RoarHeardRange = BotPerception.RoarHeardRange;
        /// <summary>Seconds a heard roar keeps steering decisions.</summary>
        private const float RoarMemory = 8f;

        // --- knowledge -------------------------------------------------------------
        /// <summary>How close a downed teammate must be to be noticed unprompted (walked up on).
        /// Beyond this the bot must have been TOLD — see <see cref="OnTeammateTaken"/>.</summary>
        private const float DownSpotRange = 35f;
        /// <summary>How long a "teammate taken" call-out stays actionable.</summary>
        private const float TakenMemory = 25f;
        /// <summary>Evidence sight with the torch OFF. Finding tracks in the dark barely works, which
        /// is the whole trade the torch exists to make.</summary>
        private const float UnlitSightFactor = 0.35f;

        // --- torch discipline ------------------------------------------------------
        private const float BatteryReserve = 25f;
        private const float TorchIdleOffSeconds = 4f;

        // --- work ------------------------------------------------------------------
        private const float WorkReach = 2.2f;
        private const float CollectSeekRange = 45f;
        private const float PileSeekRange = 120f;

        // --- movement --------------------------------------------------------------
        private const float CornerReach = 1.5f;
        private const float RepathInterval = 0.45f;
        private const float SprintBeyond = 18f;

        private HPPlayer _self;
        private BotProfile _profile;
        private BotRandom _rng;
        private BotPerception _senses;
        private BeliefMap _belief;
        private UtilityChooser _chooser;

        private NavMeshPath _path; // built in Awake, never as a field initializer (see YetiBot)
        private Vector3[] _corners = System.Array.Empty<Vector3>();
        private int _corner;
        private float _repathAt;

        // Sensing cache — refreshed at BotBrainTuning.PerceiveHz, read every frame.
        private Contact _yeti;
        private float _yetiDist = float.MaxValue;
        private float _perceiveAt;
        private float _beliefAt;

        // Memory.
        private Vector3 _lastRoarAt;
        private float _roarHeardAt = -999f;
        private HPPlayer _takenTeammate;
        private float _takenHeardAt = -999f;
        private float _torchWantedAt;
        private float _pingedAt = -999f;
        private float _markedAt = -999f;

        // Current targets, chosen in Decide() and steered toward in Act().
        private HPPlayer _reviveTarget;
        private ProofPile _pileTarget;
        private ClueMarker _collectTarget;
        private Vector3 _lead;
        private bool _hasLead;
        private Vector3 _sweepGoal;
        private float _sweepRepickAt;

        /// <summary>Current action, surfaced to the F3 overlay.</summary>
        public string DbgState => _chooser != null ? _chooser.Chosen : "—";
        /// <summary>Top scored actions with their numbers — the "why" behind DbgState.</summary>
        public string DbgScores => _chooser != null ? _chooser.DebugTop(3) : "";
        /// <summary>How sure this bot is about where the Yeti is, 0..1-ish. Overlay only.</summary>
        public float DbgConfidence => _belief != null ? _belief.Confidence : 0f;
        /// <summary>The belief field, for the F3 heatmap.</summary>
        public BeliefMap Belief => _belief;
        /// <summary>Where this bot's night actually went — dumped to the play log at match end.</summary>
        public string DbgHistogram => _chooser != null ? _chooser.Histogram() : "";

        private void Awake()
        {
            _self = GetComponent<HPPlayer>();
            _path = new NavMeshPath();
            _rng = BotRandom.For(_self);
            _senses = new BotPerception();
            _belief = new BeliefMap();
            _chooser = new UtilityChooser();
            WorldBuilder.EnsureNavMesh(); // on-demand bake; see YetiBot.Awake
        }

        private void OnEnable()
        {
            // The profile depends on the dealt specialty, which is set AFTER the brain is attached
            // (ServerBecomeBot runs from ServerBotPlace, which the match-start loop calls once roles
            // are settled). Re-read it here and again lazily in Decide, so a bot re-placed between
            // nights picks up any change rather than keeping night one's character.
            _profile = BotProfile.For(_self != null ? _self.Specialty.Value : "");
        }

        private void Update()
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (_self == null || !_self.IsBot || _self.IsYeti) return;

            float dt = Mathf.Min(Time.deltaTime, 0.1f);

            // BEFORE any early-out — see BotPerception.SampleSpeeds for why this ordering is
            // load-bearing rather than stylistic. It sat below the phase/intermission guard, which is
            // the bug that note describes; harmless HERE only because searchers have no hearing model
            // and never read SpeedOf, but the ordering is the contract, not the current caller.
            _senses.SampleSpeeds(dt);

            var gm = GameManager.Instance;
            if (gm == null || gm.MatchPhase.Value != GameManager.PhasePlaying || gm.IntermissionActive) return;

            // Frozen or downed: the sim refuses to move us anyway. Stop issuing intent so nothing
            // dangles in a channel the server would reject.
            if (_self.Status.Value != HPPlayer.StatusActive)
            {
                _chooser.Force(_self.Status.Value == HPPlayer.StatusIncap ? "DOWNED" : "FROZEN");
                ClearChannels();
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
            _yeti = _senses.PerceiveYeti(pos, _self.FlashOn.Value);
            _yetiDist = _yeti.Who != null ? Mathf.Sqrt(BotPerception.Flat2(_yeti.At, pos)) : float.MaxValue;

            if (_yeti.Who != null)
            {
                _belief.Sighting(_yeti.At);
                // Tell the team. This is a radio call — one position, one timestamp — and it is the
                // only thing about the Yeti that leaves this bot's head.
                TeamBlackboard.CallOutYeti(_yeti.At);
            }
        }

        private void TickBelief(float dt)
        {
            Vector3 pos = transform.position;
            _belief.RumourTrust = BotDifficulty.RumourTrust;
            _belief.SpreadMul = BotDifficulty.SpreadMul;

            // NEGATIVE INFORMATION. We are standing here with (usually) a torch and we do not see it,
            // so it is probably not here. Without this the bot re-walks ground it just cleared, which
            // was most of what "aimless" looked like.
            if (_yeti.Who == null)
            {
                float lookRadius = _self.FlashOn.Value ? BotPerception.SearcherSpotRangeLit
                                                       : BotPerception.SearcherSpotRange;
                _belief.Cleared(pos, lookRadius * 0.8f);
            }

            // Ground covered is shared — dividing a search is exactly what a party coordinates.
            TeamBlackboard.Coverage.MarkSwept(pos, BotBrainTuning.SweepRadius);

            // Rumours: a recent roar, a live team call-out, and any trail we can actually see.
            if (Time.time - _roarHeardAt < RoarMemory)
                _belief.Rumour(_lastRoarAt, (float)GameManager.RoarRadius, 0.5f);

            if (TeamBlackboard.CalloutFresh(10f) && _yeti.Who == null)
                _belief.Rumour(TeamBlackboard.LastYetiCallout, 35f, 0.35f);

            if (TryTrailLead(pos, out Vector3 lead, out float ageOfLead))
            {
                _lead = lead;
                _hasLead = true;
                // Older trail => vaguer guess. The radius growing with age is what turns a cold track
                // into a search area instead of a false certainty.
                float radius = Mathf.Lerp(20f, 70f, Mathf.Clamp01(ageOfLead / 15f));
                _belief.Rumour(lead, radius, 0.45f);
            }
            else if (Time.time - _roarHeardAt > RoarMemory) _hasLead = false;

            _belief.Tick(dt, (float)Sim.Player.WalkSpeed * 1.1f);
        }

        /// <summary>
        /// Where the trail suggests the Yeti WENT — not where the newest print is.
        ///
        /// This is the fix for the beeline. Two changes from the old FreshestTrailClue: only clues
        /// this searcher could actually see count (range + line of sight, and range collapses with the
        /// torch off), and rather than walking to the newest print the bot reads the trail's DIRECTION
        /// from an older print to a newer one and projects ahead of it by roughly how far the Yeti
        /// could have travelled since. Following a trail means predicting, not chasing breadcrumbs.
        /// </summary>
        private bool TryTrailLead(Vector3 pos, out Vector3 lead, out float age)
        {
            lead = default;
            age = 0f;

            string spec = _self.Specialty.Value;
            float sight = MapView.EvidenceSight * (float)Specialties.EvidenceSightMul(spec);
            if (!_self.FlashOn.Value) sight *= UnlitSightFactor;
            float window = MapView.ClueWindow * (float)Specialties.ClueWindowMul(spec);
            float sight2 = sight * sight;

            ClueMarker newest = null, older = null;
            float newestAge = float.MaxValue, olderAge = -1f;

            var all = ClueMarker.All;
            for (int i = 0; i < all.Count; i++)
            {
                ClueMarker c = all[i];
                if (c == null || !c.IsYetiTrail) continue;   // snow prints are the Yeti's to read
                float a = Time.time - c.Born;
                if (a > window) continue;
                Vector3 at = c.transform.position;
                if (BotPerception.Flat2(at, pos) > sight2) continue;         // must be near enough to see
                if (BotPerception.Blocked(pos, at)) continue;                // ...and actually visible

                if (a < newestAge) { newestAge = a; newest = c; }
                if (a > olderAge) { olderAge = a; older = c; }
            }
            if (newest == null) return false;

            Vector3 tip = newest.transform.position;
            age = newestAge;

            // With two visible prints of different ages we have a heading; project along it by how far
            // the Yeti could have walked since the newest one was laid. With only one, all we know is
            // the point itself.
            if (older != null && older != newest && olderAge - newestAge > 0.5f)
            {
                Vector3 heading = tip - older.transform.position;
                heading.y = 0f;
                if (heading.sqrMagnitude > 1f)
                {
                    float ahead = Mathf.Clamp(newestAge * (float)Sim.Player.WalkSpeed * 0.6f, 0f, 45f);
                    tip += heading.normalized * ahead;
                }
            }
            lead = tip;
            return true;
        }

        // --- deciding ---------------------------------------------------------------

        private void Decide()
        {
            Vector3 pos = transform.position;
            _chooser.Begin();

            // Cheap to re-read and it keeps a re-placed bot honest about who it is.
            if (!string.IsNullOrEmpty(_self.Specialty.Value)) _profile = BotProfile.For(_self.Specialty.Value);

            int carried = _self.CarriedTotal;
            var gm = GameManager.Instance;
            bool lateNight = gm != null && gm.TimeOfDay.Value > 0.85f;

            // --- FLEE ---------------------------------------------------------------
            // Fear scales continuously with how close the thing is relative to this character's nerve,
            // rather than snapping on at a threshold. Carrying proof makes it worse: a grab spills it.
            float fear = 0f;
            if (_yeti.Who != null)
                fear = Util.Sharpen(Util.Closeness(_yetiDist, _profile.PanicRange * 0.6f, _profile.PanicRange * 2.2f));
            bool roarOnTop = Time.time - _roarHeardAt < 2.5f &&
                             BotPerception.Flat2(_lastRoarAt, pos) <
                                 (float)(GameManager.RoarRadius * GameManager.RoarRadius);
            if (roarOnTop) fear = Mathf.Max(fear, 0.8f);
            if (carried > 0) fear *= 1.25f;
            _chooser.Consider("FLEE", fear * _profile.Flee);

            // --- FILM ---------------------------------------------------------------
            // The win condition. Beats fear at a comfortable standoff and loses to it up close, which
            // is exactly the trade the design wants a searcher to be making.
            if (_yeti.Who != null)
            {
                float filmRange = (float)(GameManager.FilmRange * Specialties.FilmRangeMul(_self.Specialty.Value));
                if (_yetiDist <= filmRange)
                {
                    float quality = Util.Closeness(_yetiDist, FilmStandoff, filmRange * 1.05f);
                    float safety = Util.Ramp(_yetiDist, _profile.PanicRange * 0.8f, _profile.PanicRange * 1.6f);
                    _chooser.Consider("FILM", (0.55f + 0.45f * quality) * safety * _profile.Film);
                }
            }

            // --- REVIVE / OVERWATCH --------------------------------------------------
            _reviveTarget = null;
            HPPlayer downed = FindKnownDowned(pos, out float downedDist);
            if (downed != null)
            {
                // Urgency from the real incap clock: a trip that arrives after they self-recover is
                // wasted, and one that arrives with seconds to spare is the most valuable thing on the
                // field. StatusEndsIn is already replicated, so this costs nothing to read.
                float remain = downed.StatusEndsIn.Value;
                float walk = downedDist / Mathf.Max(1f, (float)Sim.Player.SprintSpeed);
                bool arrivable = remain <= 0f || walk < remain - (float)(GameManager.ReviveSeconds *
                                     Specialties.ReviveMul(_self.Specialty.Value));

                // Is the Yeti thought to be sitting on the body? Reviving under the monster donates a
                // second victim — the old brain did exactly that.
                float dangerAtBody = _belief.ProbabilityAt(downed.transform.position.x, downed.transform.position.z);
                float guarded = Mathf.Clamp01(dangerAtBody * 60f);

                // NO CLAIM IS TAKEN HERE. Claims are asserted in Act(), when the bot is actually
                // doing the job — see DoRevive. Claiming during SCORING meant merely *considering* a
                // rescue reserved it for 3 s, so a bot that thought about REVIVE and then chose FLEE
                // still locked everyone else out of the body it had just run away from. "One job, one
                // owner" is only worth anything if the owner is the one doing it.
                _reviveTarget = downed;
                if (arrivable)
                {
                    float score = 0.85f * _profile.Revive
                                * Util.Closeness(downedDist, 5f, 140f)
                                * (1f - 0.55f * guarded);
                    if (downed.BeingRevived.Value) score *= 0.5f; // hands are already on them
                    _chooser.Consider("REVIVE", score);
                }
                // Overwatch is offered alongside, not instead: the useful second body is not another
                // pair of hands on the same torso — it is a torch pointed at the Yeti. A rescue with
                // one reviver and one dazzler is a play; four bodies in a heap is a free double grab.
                // Whoever loses the revive claim in Act() falls through to it.
                if (guarded > 0.25f && downedDist < 60f)
                    _chooser.Consider("OVERWATCH", 0.6f * guarded * Util.Closeness(downedDist, 8f, 70f));
            }

            // --- BANK ---------------------------------------------------------------
            if (carried > 0)
            {
                float fullness = Mathf.Clamp01(carried / (float)Mathf.Max(1, _profile.BankAt));
                float urgency = lateNight ? 1f : fullness;
                _chooser.Consider("BANK", 0.7f * urgency * _profile.Bank);
            }

            // --- RECOVER (spilled proof) --------------------------------------------
            // New behaviour. A grab spills the victim's bag into a ProofPile and, until now, nothing
            // ever went back for it — ServerBotRecoverPile was wired and never called, so every grab
            // was a permanent loss the team never even tried to answer.
            ProofPile pile = FindPile(pos, out float pileDist);
            _pileTarget = pile;
            if (pile != null)
            {
                // Claimed in DoRecover, not here — same reason as the revive claim above.
                float worth = Mathf.Clamp01(pile.Total / 3f);
                _chooser.Consider("RECOVER", (0.45f + 0.3f * worth) * _profile.Recover
                                             * Util.Closeness(pileDist, 10f, PileSeekRange));
            }

            // --- COLLECT ------------------------------------------------------------
            _collectTarget = FindCollectable(pos, out float clueDist);
            if (_collectTarget != null)
                _chooser.Consider("COLLECT", 0.5f * _profile.Collect * Util.Closeness(clueDist, 4f, CollectSeekRange));

            // --- INVESTIGATE ---------------------------------------------------------
            if (Time.time - _roarHeardAt < RoarMemory)
            {
                _lead = _lastRoarAt;
                _hasLead = true;
            }
            if (_hasLead && BotPerception.Flat2(_lead, pos) > WorkReach * WorkReach)
            {
                float d = Mathf.Sqrt(BotPerception.Flat2(_lead, pos));
                _chooser.Consider("INVESTIGATE", 0.45f * _profile.Investigate * Util.Closeness(d, 10f, 220f));
            }

            // --- SWEEP ---------------------------------------------------------------
            // The floor: always available, always beaten by anything concrete. This is the rung the
            // old ladder practically never reached, and it is where a search party actually lives.
            _chooser.Consider("SWEEP", 0.22f * _profile.Sweep);

            _chooser.Resolve();
        }

        // --- acting ------------------------------------------------------------------

        /// <summary>The action Act() last dispatched — drives the transition reset below.</summary>
        private string _actedAs;

        private void Act(Vector3 pos)
        {
            // EVERY ACTUATION IS STICKY, so leaving an action has to undo what entering it set.
            //
            // DoFilm holds `Crouched` while it lines up a shot and `Recording` for the whole take, and
            // nothing but DoFlee and ClearChannels ever put them back. So a bot that filmed and then
            // switched to SWEEP stayed crouched for the rest of the night — at half speed, and, because
            // crouching suppresses Movement.LeavesSnowPrints, no longer laying the tracks the Yeti's
            // entire hunt is built on. It also kept Recording, which blinks its REC bead at the Yeti
            // forever and re-films the instant the monster crosses the cone. The collect and revive
            // targets had the same shape, cleared only inside their own action's far branch.
            //
            // Resetting on the TRANSITION rather than per frame is what keeps this cheap and keeps the
            // do-methods free to assert what they need on the way in.
            if (!ReferenceEquals(_chooser.Chosen, _actedAs))
            {
                _actedAs = _chooser.Chosen;
                ClearChannels();
                _self.ServerBotSetCrouched(false);
            }

            switch (_chooser.Chosen)
            {
                case "FLEE":        DoFlee(pos); break;
                case "FILM":        DoFilm(pos); break;
                case "REVIVE":      DoRevive(pos); break;
                case "OVERWATCH":   DoOverwatch(pos); break;
                case "BANK":        DoBank(pos); break;
                case "RECOVER":     DoRecover(pos); break;
                case "COLLECT":     DoCollect(pos); break;
                case "INVESTIGATE": DoInvestigate(pos); break;
                default:            DoSweep(pos); break;
            }
        }

        private void DoFlee(Vector3 pos)
        {
            Vector3 from = _yeti.Who != null ? _yeti.At : _lastRoarAt;
            Vector3 away = pos - from;
            away.y = 0f;
            if (away.sqrMagnitude < 1f) away = -transform.forward;

            // Eli's flash is a panic button and it finally gets pressed: it dazzles the Yeti, which
            // locks its roar and grab, which is worth far more than three more metres of running.
            if (_profile.CanFlash && _yeti.Who != null && _yetiDist < 18f && _self.AbilityCharges.Value > 0)
            {
                Face(pos, _yeti.At);
                _self.ServerBotFlash();
            }

            Vector3 goal = pos + away.normalized * SafeRange;
            Vector3 duffel = WorldBuilder.DuffelPosition();
            if (Vector3.Dot((duffel - pos).normalized, away.normalized) > 0.2f) goal = duffel;

            SetTorch(true);
            _self.ServerBotSetRecording(false);
            _self.ServerBotSetCrouched(false);
            Steer(pos, goal, sprint: true);
        }

        private void DoFilm(Vector3 pos)
        {
            if (_yeti.Who == null) { DoSweep(pos); return; }
            SetTorch(true);
            Face(pos, _yeti.At);
            _self.ServerBotSetRecording(true);

            if (_yetiDist < FilmStandoff * 0.8f)
            {
                _self.ServerBotSetCrouched(false);
                Vector3 away = pos - _yeti.At;
                away.y = 0f;
                Steer(pos, pos + away.normalized * 12f, sprint: false);
            }
            else if (_yetiDist > FilmStandoff * 1.4f)
            {
                _self.ServerBotSetCrouched(false);
                Steer(pos, _yeti.At, sprint: false);
            }
            else
            {
                // Crouch while holding the shot: silent, leaves no tracks, and the usual cost (half
                // speed) is exactly zero while standing still. The one place stealth is free.
                _self.ServerBotSetCrouched(true);
                Halt();
            }
        }

        private void DoRevive(Vector3 pos)
        {
            if (_reviveTarget == null) { DoSweep(pos); return; }
            // Claim on COMMIT. Re-asserting your own claim always succeeds, so running this every
            // frame is what holds the job while the bot is on it and releases it a few seconds after
            // it stops — no explicit release, so getting grabbed mid-rescue frees the body for someone
            // else. Losing the race means another bot got there first: go be the torch instead.
            if (!TeamBlackboard.ClaimRevive(_reviveTarget.ObjectId, _self.ObjectId)) { DoOverwatch(pos); return; }
            SetTorch(true);
            float d2 = BotPerception.Flat2(_reviveTarget.transform.position, pos);
            if (d2 <= WorkReach * WorkReach)
            {
                _self.ServerBotSetReviveTarget(_reviveTarget.ObjectId);
                Face(pos, _reviveTarget.transform.position);
                Halt();
            }
            else
            {
                _self.ServerBotSetReviveTarget(-1);
                Steer(pos, _reviveTarget.transform.position, sprint: d2 > SprintBeyond * SprintBeyond);
            }
        }

        private void DoOverwatch(Vector3 pos)
        {
            if (_reviveTarget == null) { DoSweep(pos); return; }
            // One overwatch slot, claimed on commit like the rescue itself. A third body arriving has
            // nothing useful to add and everything to lose, so it goes back to sweeping.
            if (!TeamBlackboard.ClaimOverwatch(_reviveTarget.ObjectId, _self.ObjectId)) { DoSweep(pos); return; }
            // Stand off the body and keep the light where the threat is believed to be: a held beam
            // dazzles, which locks roar and grab for the length of the rescue.
            Vector3 body = _reviveTarget.transform.position;
            Vector3 threat = _yeti.Who != null ? _yeti.At : _belief.Best();
            Vector3 outward = body - threat;
            outward.y = 0f;
            if (outward.sqrMagnitude < 1f) outward = transform.forward;
            Vector3 post = body + outward.normalized * 7f;

            SetTorch(true);
            _self.ServerBotSetReviveTarget(-1);
            if (BotPerception.Flat2(post, pos) > 9f) Steer(pos, post, sprint: true);
            else { Face(pos, threat); Halt(); }
        }

        private void DoBank(Vector3 pos)
        {
            Vector3 duffel = WorldBuilder.DuffelPosition();
            SetTorch(true);
            if (GameManager.AtDuffel(pos))
            {
                _self.ServerBotDeposit();
                Halt();
            }
            else Steer(pos, duffel, sprint: BotPerception.Flat2(duffel, pos) > SprintBeyond * SprintBeyond);
        }

        private void DoRecover(Vector3 pos)
        {
            if (_pileTarget == null) { DoSweep(pos); return; }
            // Claim on commit — two bots must not cross the map for one bag. See DoRevive.
            if (!TeamBlackboard.ClaimPile(_pileTarget.ObjectId, _self.ObjectId)) { DoSweep(pos); return; }
            SetTorch(true);
            Vector3 at = _pileTarget.transform.position;
            float d2 = BotPerception.Flat2(at, pos);
            if (d2 <= WorkReach * WorkReach * 4f) // the server's PileRadius is generous; get close enough
            {
                _self.ServerBotRecoverPile(_pileTarget.ObjectId);
                Face(pos, at);
                Halt();
            }
            else Steer(pos, at, sprint: d2 > SprintBeyond * SprintBeyond);
        }

        private void DoCollect(Vector3 pos)
        {
            if (_collectTarget == null) { DoSweep(pos); return; }
            SetTorch(true);
            Vector3 at = _collectTarget.transform.position;
            float d2 = BotPerception.Flat2(at, pos);
            if (d2 <= WorkReach * WorkReach)
            {
                _self.ServerBotSetCollectTarget(_collectTarget.ObjectId);
                Face(pos, at);
                Halt();
                MaybeMark(pos);
            }
            else
            {
                _self.ServerBotSetCollectTarget(-1);
                Steer(pos, at, sprint: false);
            }
        }

        private void DoInvestigate(Vector3 pos)
        {
            SetTorch(true);
            MaybeMark(pos);
            MaybePing(pos);
            float d2 = BotPerception.Flat2(_lead, pos);
            if (d2 <= WorkReach * WorkReach) { Halt(); return; }
            Steer(pos, _lead, sprint: d2 > SprintBeyond * SprintBeyond);
        }

        /// <summary>
        /// Cover ground that is both plausible and unswept — the replacement for random roam.
        ///
        /// The goal comes from the belief field minus the team's coverage and claims, so four bots
        /// sharing one map naturally take four different directions, and the bot claims its target so
        /// nobody else picks it. That claim is the entire reason a bot team stops walking as a clump.
        /// </summary>
        private void DoSweep(Vector3 pos)
        {
            if (Time.time >= _sweepRepickAt || BotPerception.Flat2(pos, _sweepGoal) < 400f)
            {
                _sweepGoal = _belief.BestSearchTarget(pos, TeamBlackboard.Coverage, _self.ObjectId);
                // A little per-bot scatter so two bots whose belief fields agree still walk to
                // slightly different places. The stream is per-bot (BotRandom), so this decorrelates
                // rather than merely randomising.
                _sweepGoal += new Vector3(_rng.Signed * 10f, 0f, _rng.Signed * 10f);
                _sweepRepickAt = Time.time + 6f;
                TeamBlackboard.Coverage.Claim(_sweepGoal, _self.ObjectId, 14f, 45f);
                Repath(_sweepGoal, force: true);
            }
            // The one state where the light is optional, so the one place battery can be saved.
            SetTorch(_self.BotBattery > BatteryReserve);
            MaybePing(pos);
            Steer(pos, _sweepGoal, sprint: false);
        }

        // --- specialty abilities -----------------------------------------------------

        /// <summary>Wren drops a trail mark on evidence worth remembering. Server enforces the cooldown;
        /// this only avoids spamming a call that would be refused.</summary>
        private void MaybeMark(Vector3 pos)
        {
            if (!_profile.CanMark || Time.time - _markedAt < 9f) return;
            _markedAt = Time.time;
            _self.ServerBotMark();
        }

        /// <summary>
        /// Drop a stakeout ping on the strongest belief peak. One per hunter and re-pinging moves it,
        /// so this is a cheap, honest way for a bot to say "I think it's over there" — and it is
        /// visible to human teammates on the map, which makes a bot team readable to play alongside.
        /// </summary>
        private void MaybePing(Vector3 pos)
        {
            if (Time.time - _pingedAt < 20f) return;
            if (_belief.Confidence < 0.06f) return; // nothing worth claiming
            Vector3 peak = _belief.Best();
            _pingedAt = Time.time;
            _self.ServerBotPing(peak.x, peak.z);
        }

        // --- lookups ------------------------------------------------------------------

        private HPPlayer FindKnownDowned(Vector3 pos, out float dist)
        {
            bool calloutLive = _takenTeammate != null && Time.time - _takenHeardAt < TakenMemory;
            HPPlayer best = null;
            float bestD2 = float.MaxValue;
            var all = HPPlayer.All;
            for (int i = 0; i < all.Count; i++)
            {
                HPPlayer p = all[i];
                if (p == null || p == _self || p.IsYeti) continue;
                if (p.Status.Value != HPPlayer.StatusIncap) continue;
                float d2 = BotPerception.Flat2(p.transform.position, pos);
                bool known = d2 <= DownSpotRange * DownSpotRange || (calloutLive && p == _takenTeammate);
                if (!known) continue;
                if (d2 < bestD2) { bestD2 = d2; best = p; }
            }
            dist = best != null ? Mathf.Sqrt(bestD2) : float.MaxValue;
            return best;
        }

        private ProofPile FindPile(Vector3 pos, out float dist)
        {
            ProofPile best = null;
            float bestD2 = PileSeekRange * PileSeekRange;
            var all = ProofPile.All;
            for (int i = 0; i < all.Count; i++)
            {
                ProofPile p = all[i];
                if (p == null || p.Total <= 0) continue;
                float d2 = BotPerception.Flat2(p.transform.position, pos);
                if (d2 < bestD2) { bestD2 = d2; best = p; }
            }
            dist = best != null ? Mathf.Sqrt(bestD2) : float.MaxValue;
            return best;
        }

        private ClueMarker FindCollectable(Vector3 pos, out float dist)
        {
            ClueMarker best = null;
            float bestD2 = CollectSeekRange * CollectSeekRange;
            var all = ClueMarker.All;
            for (int i = 0; i < all.Count; i++)
            {
                ClueMarker c = all[i];
                if (c == null || !c.IsCollectable) continue;
                // Only Mara can cast. Asking for a cast the server refuses parks the bot in a channel
                // that never completes.
                if (c.Castable.Value && !_profile.CanCast) continue;
                float d2 = BotPerception.Flat2(c.transform.position, pos);
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
            dist = best != null ? Mathf.Sqrt(bestD2) : float.MaxValue;
            return best;
        }

        // --- team events ---------------------------------------------------------------

        /// <summary>Called by GameManager when a roar goes off — sound the bot can act on without sight.</summary>
        public void OnHeardRoar(Vector3 at)
        {
            if (BotPerception.Flat2(at, transform.position) > RoarHeardRange * RoarHeardRange) return;
            _lastRoarAt = at;
            _roarHeardAt = Time.time;
            _belief?.Rumour(at, 30f, 0.6f);
        }

        /// <summary>
        /// The team's call-out that <paramref name="victim"/> has been taken. Range-free on purpose:
        /// unlike a roar this is the team noticing one of them stopped answering, and that reaches
        /// everyone. It grants KNOWLEDGE, not orders — whether anyone goes is still the scoring's
        /// call, and the blackboard makes sure at most one of them does.
        /// </summary>
        public void OnTeammateTaken(HPPlayer victim, Vector3 at)
        {
            _takenTeammate = victim;
            _takenHeardAt = Time.time;
            _lastRoarAt = at;
            _roarHeardAt = Time.time;
            // A grab is the most reliable position fix the team ever gets on the Yeti.
            _belief?.Rumour(at, 18f, 0.8f);
            TeamBlackboard.CallOutYeti(at);
        }

        // --- actuation -----------------------------------------------------------------

        private void SetTorch(bool want)
        {
            if (want) _torchWantedAt = Time.time;
            bool on = want || Time.time - _torchWantedAt < TorchIdleOffSeconds;
            if (on && _self.BotBattery <= 0f) on = false;
            _self.ServerBotSetFlashlight(on);
        }

        private void ClearChannels()
        {
            _self.ServerBotSetRecording(false);
            _self.ServerBotSetReviveTarget(-1);
            _self.ServerBotSetCollectTarget(-1);
        }

        private void Face(Vector3 pos, Vector3 at) => _self.ServerBotFace(at.x - pos.x, at.z - pos.z);

        private void Halt() =>
            _self.ServerBotDrive(new MoveInput { W = false, Dt = Mathf.Min(Time.deltaTime, 0.1f) });

        private void Steer(Vector3 pos, Vector3 goal, bool sprint)
        {
            Repath(goal);
            Vector3 target;
            if (_corners.Length > 0 && _corner < _corners.Length)
            {
                target = _corners[_corner];
                if (BotPerception.Flat2(target, pos) <= CornerReach * CornerReach && _corner < _corners.Length - 1)
                    _corner++;
            }
            else target = goal;

            Face(pos, target);
            _self.ServerBotDrive(new MoveInput { W = true, Sprint = sprint, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
        }

        private void Repath(Vector3 goal, bool force = false)
        {
            if (!force && Time.time < _repathAt) return;
            _repathAt = Time.time + RepathInterval;
            if (NavMesh.CalculatePath(transform.position, goal, NavMesh.AllAreas, _path) && _path.corners.Length > 0)
            {
                _corners = _path.corners;
                _corner = _corners.Length > 1 ? 1 : 0;
            }
        }

        // --- play-test log --------------------------------------------------------------

        /// <summary>
        /// Log action transitions, same rationale as YetiBot's: the overlay says what a bot is doing
        /// now, but a tester's "it got stuck" is always about thirty seconds ago, and only the
        /// transitions carry that. Cheap comparisons first so no string is built per frame ([perf]).
        /// </summary>
        private void LateUpdate()
        {
            if (_self == null || _chooser == null) return;
            if (ReferenceEquals(_chooser.Chosen, _loggedState)) return;
            _loggedState = _chooser.Chosen;
            HPLog.Change($"searcher.ai.{_self.ObjectId}", "AI",
                         $"{_self.CharacterName.Value} → {_chooser.Chosen} [{_chooser.DebugTop(3)}]");
        }

        private string _loggedState;
    }
}
