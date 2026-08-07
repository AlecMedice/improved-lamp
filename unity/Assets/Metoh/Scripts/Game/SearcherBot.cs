// The CPU searcher brain — the team you hunt in PLAY AS YETI, and the other half of offline play.
//
// STATUS: this is a SHELL. The skeleton is real and complete — perception, the priority ladder, the
// navigation, and every server hand-off are wired and working. What is deliberately shallow is the
// judgement inside a few states, and each of those is marked TODO with what "good" would look like.
// It is built to be filled in, not to be a finished opponent-team. Nothing here has been play-tested.
//
// Same architecture as YetiBot, for the same reasons (see UNITY_PORT_NOTES §6b):
//   - INTENT ONLY. It never moves a transform or resolves an action itself. It picks a direction and
//     a few booleans and hands them to HPPlayer.ServerBot*, which run the exact same shared sim and
//     the same GameManager authority a human's input lands in. A CPU searcher therefore obeys
//     identical collision, stamina, battery drain, film range/cone/LOS, channel durations and
//     cooldowns. There is no parallel "AI filming" to drift out of parity with the real thing.
//   - HOST ONLY, plain MonoBehaviour, attached at runtime by HPPlayer.ServerBecomeBot.
//
// WHAT MAKES A SEARCHER HARD TO WRITE, and why the shape below is what it is:
//
// The Yeti's brain is a pursuit problem — one target, close the distance. A searcher's is a
// RESOURCE problem with a fear layer on top, and those pull in opposite directions:
//   * The torch is both its only real sensor and a flare visible to the Yeti from 80 m. Every second
//     lit is information bought with exposure, and the battery is finite.
//   * Proof is worth nothing until it reaches the duffel, and a grab spills it. So carrying is a
//     debt: the longer you hold it, the more you stand to lose, and the trip home is when you're
//     most vulnerable.
//   * Filming means pointing yourself at the thing that is hunting you and standing still.
//   * Nobody can win alone, but clumping makes one roar catch the whole team.
// A searcher that only optimises evidence walks into the Yeti's arms; one that only avoids the Yeti
// never wins. The priority ladder below is the first cut at that trade, not the final word.
//
// All tuning is first-guess. The named constants are the dials.
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
        // --- perception (first-guess) ----------------------------------------------
        /// <summary>How far a searcher can make out the Yeti with a clear line of sight. Deliberately
        /// shorter than the Yeti's own sight range — it owns the dark, they don't.</summary>
        private const float SpotRange = 30f;
        /// <summary>...extended to this while the bot's own torch is lit and pointed roughly at it.</summary>
        private const float SpotRangeLit = 55f;
        /// <summary>A roar is heard this far and pins a direction to investigate/flee from.</summary>
        private const float RoarHeardRange = 90f;
        /// <summary>Seconds a heard roar keeps influencing decisions.</summary>
        private const float RoarMemory = 8f;
        /// <summary>
        /// How close a downed teammate has to be for the bot to notice them unprompted — walking up
        /// on a body. Beyond this it needs to have been TOLD (see <see cref="OnTeammateTaken"/>).
        ///
        /// Before this existed, TryRevive scanned every player on the map with no range limit, so one
        /// grab pulled the entire CPU team in from wherever they were — which handed the Yeti the
        /// whole team around one body, and is most of why a bot team read as aimless.
        /// </summary>
        private const float DownSpotRange = 35f;
        /// <summary>How long a "teammate taken" call-out stays actionable. Past this the trip is
        /// likely to arrive after they've come round on their own anyway.</summary>
        private const float TakenMemory = 25f;

        // --- fear / spacing (first-guess) ------------------------------------------
        /// <summary>Inside this the Yeti is a threat to run from, not a subject to film.</summary>
        private const float PanicRange = 14f;
        /// <summary>Stop retreating once this far clear.</summary>
        private const float SafeRange = 45f;
        /// <summary>Try to hold roughly this distance while filming — inside the film range, outside
        /// comfortable grab-and-roar reach.</summary>
        private const float FilmStandoff = 22f;

        // --- torch discipline (first-guess) ----------------------------------------
        /// <summary>Below this battery %, hoard what's left for emergencies.</summary>
        private const float BatteryReserve = 25f;
        /// <summary>Keep the torch off while this far from anything worth lighting, to save charge.</summary>
        private const float TorchIdleOffSeconds = 4f;

        // --- work (first-guess) ----------------------------------------------------
        /// <summary>Head home once carrying this much, rather than pushing luck for one more piece.</summary>
        private const int BankAtCarried = 2;
        /// <summary>How close to stand to work a clue / duffel (the server has the real radius).</summary>
        private const float WorkReach = 2.2f;
        /// <summary>Re-choose an exploration goal this often, or on arrival.</summary>
        private const float ExploreGoalSeconds = 12f;

        // --- movement --------------------------------------------------------------
        private const float CornerReach = 1.5f;
        private const float RepathInterval = 0.45f;
        private const float SprintBeyond = 18f;

        private HPPlayer _self;
        private NavMeshPath _path; // built in Awake, never as a field initializer (see YetiBot)
        private Vector3[] _corners = System.Array.Empty<Vector3>();
        private int _corner;
        private float _repathAt;

        // Memory.
        private Vector3 _lastRoarAt;
        private float _roarHeardAt = -999f;
        private HPPlayer _takenTeammate;      // who the last call-out was about
        private float _takenHeardAt = -999f;  // when it came in
        private Vector3 _exploreGoal;
        private float _exploreUntil;
        private float _torchWantedAt;   // last time something made the torch worth burning

        /// <summary>Current state, surfaced to the F3 overlay. Priority order, highest first:
        /// FLEE / REVIVE / BANK / FILM / COLLECT / INVESTIGATE / EXPLORE.</summary>
        public string DbgState { get; private set; } = "—";

        private void Awake()
        {
            _self = GetComponent<HPPlayer>();
            _path = new NavMeshPath();
            WorldBuilder.EnsureNavMesh(); // on-demand bake; see YetiBot.Awake
        }

        private void Update()
        {
            if (!InstanceFinder.IsServerStarted) return;
            if (_self == null || !_self.IsBot || _self.IsYeti) return;
            var gm = GameManager.Instance;
            if (gm == null || gm.MatchPhase.Value != GameManager.PhasePlaying || gm.IntermissionActive) return;

            // Frozen or downed: the sim already refuses to move us. Stop issuing intent so a frozen
            // bot doesn't keep "trying" and burn a channel the server would reject anyway.
            if (_self.Status.Value != HPPlayer.StatusActive)
            {
                DbgState = _self.Status.Value == HPPlayer.StatusIncap ? "DOWNED" : "FROZEN";
                ClearChannels();
                return;
            }

            Vector3 pos = transform.position;
            HPPlayer yeti = PerceiveYeti(pos);
            float yetiDist = yeti != null ? Mathf.Sqrt(Flat2(yeti.transform.position, pos)) : float.MaxValue;

            // Only the filming rung wants a crouch; clear it here so no other state inherits half
            // speed from the last frame it filmed in.
            if (yeti == null || yetiDist > GameManager.FilmRange) _self.ServerBotSetCrouched(false);

            // The ladder. Each rung returns once it has issued this frame's intent, so exactly one
            // behaviour owns the bot at a time and the F3 state always explains what you're seeing.
            if (TryFlee(pos, yeti, yetiDist)) return;
            if (TryRevive(pos)) return;
            if (TryBank(pos)) return;
            if (TryFilm(pos, yeti, yetiDist)) return;
            if (TryCollect(pos)) return;
            if (TryInvestigate(pos)) return;
            Explore(pos);
        }

        // --- the ladder ------------------------------------------------------------

        /// <summary>
        /// Break away from a Yeti that is too close to film safely. This outranks everything,
        /// including carrying proof home — a grab costs the carry anyway, so distance IS the play.
        /// </summary>
        private bool TryFlee(Vector3 pos, HPPlayer yeti, float yetiDist)
        {
            bool threatened = yeti != null && yetiDist < PanicRange;
            // A roar means it is close and it knows where we are, even if we can't see it.
            bool roarFresh = Time.time - _roarHeardAt < 2f &&
                             Flat2(_lastRoarAt, pos) < GameManager.RoarRadius * GameManager.RoarRadius;
            if (!threatened && !roarFresh) return false;

            Vector3 from = yeti != null ? yeti.transform.position : _lastRoarAt;
            Vector3 away = pos - from;
            away.y = 0f;
            if (away.sqrMagnitude < 1f) away = -transform.forward;

            // Run toward camp when it isn't through the Yeti — a fleeing searcher heading for the
            // duffel banks what it's carrying instead of merely surviving with it.
            Vector3 goal = pos + away.normalized * SafeRange;
            Vector3 duffel = WorldBuilder.DuffelPosition();
            if (Vector3.Dot((duffel - pos).normalized, away.normalized) > 0.2f) goal = duffel;

            // Torch ON while running: seeing where you're going beats hiding from something that has
            // already found you. TODO: a smarter bot would kill the light and break line of sight
            // instead when it has not yet been seen — that is the actual stealth play, and it needs a
            // "has it noticed me" estimate this shell doesn't have.
            SetTorch(true);
            _self.ServerBotSetRecording(false);
            DbgState = "FLEE";
            Steer(pos, goal, sprint: true);
            return true;
        }

        /// <summary>A downed teammate is a hard interrupt — they are out of the match until someone comes.</summary>
        private bool TryRevive(Vector3 pos)
        {
            // A bot goes to a body it KNOWS about: one close enough to have walked up on, or one the
            // team called out (OnTeammateTaken) recently enough that the trip is still worth making.
            // The unbounded map-wide scan this replaced meant every grab summoned the whole team.
            bool calloutLive = _takenTeammate != null && Time.time - _takenHeardAt < TakenMemory;

            HPPlayer downed = null;
            float bestD2 = float.MaxValue;
            foreach (var p in HPPlayer.All)
            {
                if (p == null || p == _self || p.IsYeti) continue;
                if (p.Status.Value != HPPlayer.StatusIncap) continue;
                float d2 = Flat2(p.transform.position, pos);
                bool known = d2 <= DownSpotRange * DownSpotRange || (calloutLive && p == _takenTeammate);
                if (!known) continue;
                if (d2 < bestD2) { bestD2 = d2; downed = p; }
            }
            if (downed == null) return false;

            // Still shallow, and still worth fixing: this doesn't weigh the walk against the incap
            // timer (arriving after they self-recover is a wasted trip), and it doesn't hang back
            // while the Yeti stands over them — reviving under the monster donates a second victim.
            SetTorch(true);
            DbgState = "REVIVE";
            if (bestD2 <= WorkReach * WorkReach)
            {
                _self.ServerBotSetReviveTarget(downed.ObjectId); // the server runs the channel + timer
                Face(pos, downed.transform.position);
                Halt();
            }
            else
            {
                _self.ServerBotSetReviveTarget(-1);
                Steer(pos, downed.transform.position, sprint: bestD2 > SprintBeyond * SprintBeyond);
            }
            return true;
        }

        /// <summary>Carrying enough (or carrying anything with the night nearly gone) — take it home.</summary>
        private bool TryBank(Vector3 pos)
        {
            int carried = _self.CarriedTotal;
            if (carried <= 0) return false;
            var gm = GameManager.Instance;
            bool lateNight = gm != null && gm.TimeOfDay.Value > 0.85f;
            if (carried < BankAtCarried && !lateNight) return false;

            Vector3 duffel = WorldBuilder.DuffelPosition();
            float d2 = Flat2(duffel, pos);
            SetTorch(true);
            DbgState = "BANK";
            if (GameManager.AtDuffel(pos))
            {
                _self.ServerBotDeposit(); // server enforces the stand-still beat
                Halt();
            }
            else Steer(pos, duffel, sprint: d2 > SprintBeyond * SprintBeyond);
            return true;
        }

        /// <summary>
        /// Film the Yeti: the fastest evidence, and the most dangerous. Holds a standoff so it doesn't
        /// drift into grab range while staring down the viewfinder.
        /// </summary>
        private bool TryFilm(Vector3 pos, HPPlayer yeti, float yetiDist)
        {
            if (yeti == null || yetiDist > GameManager.FilmRange) return false;

            // Light it up — the server's film check needs it visible, and the beam also dazzles.
            SetTorch(true);
            Face(pos, yeti.transform.position);
            _self.ServerBotSetRecording(true);
            DbgState = "FILM";

            // Back off if it is closing, ease in if it is drifting out of range, else hold and shoot.
            if (yetiDist < FilmStandoff * 0.8f)
            {
                _self.ServerBotSetCrouched(false);
                Vector3 away = pos - yeti.transform.position;
                away.y = 0f;
                Steer(pos, pos + away.normalized * 12f, sprint: false);
            }
            else if (yetiDist > FilmStandoff * 1.4f)
            {
                _self.ServerBotSetCrouched(false);
                Steer(pos, yeti.transform.position, sprint: false);
            }
            else
            {
                // Crouch while holding the shot. Crouching is silent and leaves no tracks, and the
                // usual cost — half speed — is exactly zero when you are standing still anyway. It is
                // the one place in the ladder where stealth is free.
                _self.ServerBotSetCrouched(true);
                Halt();
            }
            return true;
        }

        /// <summary>Work a castable print or a hair sample within reach — the non-film evidence paths.</summary>
        private bool TryCollect(Vector3 pos)
        {
            ClueMarker best = null;
            float bestD2 = float.MaxValue;
            foreach (var c in ClueMarker.All)
            {
                if (c == null || !c.IsCollectable) continue;
                // Only Mara can cast; anyone can bag hair. Asking for a cast the server will refuse
                // would park the bot in a channel that never completes.
                if (c.Castable.Value && _self.Specialty.Value != "analysis") continue;
                float d2 = Flat2(c.transform.position, pos);
                if (d2 < bestD2) { bestD2 = d2; best = c; }
            }
            if (best == null || bestD2 > CollectSeekRange * CollectSeekRange) return false;

            SetTorch(true);
            DbgState = "COLLECT";
            if (bestD2 <= WorkReach * WorkReach)
            {
                _self.ServerBotSetCollectTarget(best.ObjectId); // server runs the channel
                Face(pos, best.transform.position);
                Halt();
            }
            else
            {
                _self.ServerBotSetCollectTarget(-1);
                Steer(pos, best.transform.position, sprint: false);
            }
            return true;
        }

        /// <summary>Walk down a lead: the Yeti's clue trail, or where a roar came from.</summary>
        private bool TryInvestigate(Vector3 pos)
        {
            Vector3 lead;
            if (Time.time - _roarHeardAt < RoarMemory) lead = _lastRoarAt;
            else
            {
                ClueMarker fresh = FreshestTrailClue(pos);
                if (fresh == null) return false;
                lead = fresh.transform.position;
            }
            if (Flat2(lead, pos) < WorkReach * WorkReach) return false; // standing on it — nothing more to learn

            SetTorch(true);
            DbgState = "INVESTIGATE";
            Steer(pos, lead, sprint: Flat2(lead, pos) > SprintBeyond * SprintBeyond);
            return true;
        }

        /// <summary>
        /// Nothing to act on — cover ground looking for a trail.
        ///
        /// TODO: this is the weakest rung and the most valuable one to replace. It roams at random,
        /// where a real search would divide the map between teammates, sweep outward from camp, and
        /// prefer ground nobody has covered recently. Random roam is why a bot team currently reads
        /// as five people wandering rather than as a search party.
        /// </summary>
        private void Explore(Vector3 pos)
        {
            if (Time.time >= _exploreUntil || Flat2(pos, _exploreGoal) < 25f)
            {
                _exploreGoal = RandomNavPoint(pos);
                _exploreUntil = Time.time + ExploreGoalSeconds;
                Repath(_exploreGoal, force: true);
            }
            // Torch discipline: this is the one state where the light is optional, so it is the one
            // place the battery can actually be saved.
            SetTorch(_self.BotBattery > BatteryReserve);
            DbgState = "EXPLORE";
            Steer(pos, _exploreGoal, sprint: false);
        }

        /// <summary>How far the bot will divert to work a piece of evidence.</summary>
        private const float CollectSeekRange = 45f;

        // --- perception ------------------------------------------------------------

        /// <summary>
        /// The Yeti, if this searcher can actually see it: range plus a hard line-of-sight test, with
        /// the range extended while our own torch is lit. No hearing term — footsteps are a cue for a
        /// human player to interpret, and giving the bot an audio channel here would quietly make it
        /// better at detection than the person it is standing in for.
        /// </summary>
        private HPPlayer PerceiveYeti(Vector3 pos)
        {
            foreach (var p in HPPlayer.All)
            {
                if (p == null || !p.IsYeti) continue;
                if (p.Status.Value == HPPlayer.StatusIncap) continue;
                float d = Mathf.Sqrt(Flat2(p.transform.position, pos));
                float range = _self.FlashOn.Value ? SpotRangeLit : SpotRange;
                if (d > range) continue;
                if (Blocked(pos, p.transform.position)) continue;
                return p;
            }
            return null;
        }

        /// <summary>Called by GameManager when a roar goes off, so the bot reacts to sound it can't see.</summary>
        public void OnHeardRoar(Vector3 at)
        {
            if (Flat2(at, transform.position) > RoarHeardRange * RoarHeardRange) return;
            _lastRoarAt = at;
            _roarHeardAt = Time.time;
        }

        /// <summary>
        /// The team's call-out that <paramref name="victim"/> has been taken. Range-free on purpose —
        /// unlike a roar, which you have to be near enough to hear, this is the team knowing one of
        /// them stopped answering, and that reaches everyone.
        ///
        /// It grants KNOWLEDGE, not orders. All it does is make this teammate eligible for the REVIVE
        /// rung; whether the rung actually wins the frame is still the ladder's decision, and a bot
        /// that is fleeing, or carrying proof it hasn't banked, will keep doing that.
        /// </summary>
        public void OnTeammateTaken(HPPlayer victim, Vector3 at)
        {
            _takenTeammate = victim;
            _takenHeardAt = Time.time;
            // A grab means the Yeti is exactly there, which is worth knowing on its own — it is the
            // most reliable position fix the team ever gets on it.
            _lastRoarAt = at;
            _roarHeardAt = Time.time;
        }

        private ClueMarker FreshestTrailClue(Vector3 pos)
        {
            ClueMarker best = null;
            float bestScore = float.MinValue;
            float window = MapView.ClueWindow * (float)Specialties.ClueWindowMul(_self.Specialty.Value);
            foreach (var c in ClueMarker.All)
            {
                if (c == null || !c.IsYetiTrail) continue; // a searcher can't see snow prints
                float age = Time.time - c.Born;
                if (age > window) continue;
                float score = -age * 10f - Mathf.Sqrt(Flat2(c.transform.position, pos)) * 0.15f;
                if (score > bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        private bool Blocked(Vector3 a, Vector3 b)
        {
            var world = WorldBuilder.World;
            if (world == null) return false;
            return Metoh.Sim.Collision.LineBlocked(world.Colliders, new Vec2(a.x, a.z), new Vec2(b.x, b.z));
        }

        // --- actuation -------------------------------------------------------------

        /// <summary>Torch on/off with a little hysteresis, so it doesn't strobe on state flapping.</summary>
        private void SetTorch(bool want)
        {
            if (want) _torchWantedAt = Time.time;
            bool on = want || Time.time - _torchWantedAt < TorchIdleOffSeconds;
            if (on && _self.BotBattery <= 0f) on = false;
            _self.ServerBotSetFlashlight(on);
        }

        /// <summary>Drop any held channel — used when frozen/downed so nothing dangles.</summary>
        private void ClearChannels()
        {
            _self.ServerBotSetRecording(false);
            _self.ServerBotSetReviveTarget(-1);
            _self.ServerBotSetCollectTarget(-1);
        }

        private void Face(Vector3 pos, Vector3 at) => _self.ServerBotFace(at.x - pos.x, at.z - pos.z);

        /// <summary>Stand still but keep facing — the sim still ticks (stamina regen, battery drain).</summary>
        private void Halt()
        {
            _self.ServerBotDrive(new MoveInput { W = false, Dt = Mathf.Min(Time.deltaTime, 0.1f) });
        }

        private void Steer(Vector3 pos, Vector3 goal, bool sprint)
        {
            Repath(goal);
            Vector3 target;
            if (_corners.Length > 0 && _corner < _corners.Length)
            {
                target = _corners[_corner];
                if (Flat2(target, pos) <= CornerReach * CornerReach && _corner < _corners.Length - 1) _corner++;
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

        /// <summary>A roam goal a good way off; the NavMesh only refines it, never gates it (see YetiBot).</summary>
        private Vector3 RandomNavPoint(Vector3 around)
        {
            var world = WorldBuilder.World;
            float half = (float)Sim.World.Size / 2f - 12f;
            for (int i = 0; i < 6; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float dist = Mathf.Lerp(40f, 150f, Random.value);
                float x = Mathf.Clamp(around.x + Mathf.Cos(ang) * dist, -half, half);
                float z = Mathf.Clamp(around.z + Mathf.Sin(ang) * dist, -half, half);
                float y = world != null ? (float)world.GetHeight(x, z) : around.y;
                Vector3 probe = new Vector3(x, y, z);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, 14f, NavMesh.AllAreas)) return hit.position;
                if (i == 5) return probe;
            }
            return around;
        }

        private static float Flat2(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
