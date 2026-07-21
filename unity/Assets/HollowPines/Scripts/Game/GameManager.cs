// The authoritative match loop — ForestRoom.ts reborn as a FishNet host system. Runs ONLY on
// the host (server): night clock + escalation, clue trail, roar/freeze, grab/incapacitation,
// revive, dazzle, server-recomputed filming (range + aim cone + line-of-sight), and win/loss.
// Everything replicates through SyncVars here and on HPPlayer; clients never decide outcomes.
//
// Tunables are verbatim from server/src/rooms/ForestRoom.ts (the web build's source of truth).
using System.Collections.Generic;
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using HollowPines.Sim;
using UnityEngine;

namespace HollowPines.Game
{
    public class GameManager : NetworkBehaviour
    {
        public const byte PhaseLobby = 0;
        public const byte PhasePlaying = 1;
        public const byte PhaseResults = 2;
        public const byte WinnerNone = 0;
        public const byte WinnerHunters = 1;
        public const byte WinnerBigfoot = 2;

        // --- Tuning (ForestRoom.ts values) ---
        private const int TotalNightsCount = 3;
        private const double Stride = 2.4;
        private const double BranchChance = 0.18;
        private const int MaxClues = 80;
        public const double FilmRange = 38; // public: the briefing quotes Eli's real reach
        private static readonly double FilmAimCos = System.Math.Cos(0.6);
        private const double FilmSeconds = 3.0;
        private const double FilmDecay = 0.5;
        private const double RoarRadius = 25;
        private const double RoarCooldown = 25;
        private const double FreezeSeconds = 30;
        private const double GrabRadius = 3.5;
        private const double IncapSeconds = 60;
        private const double SlowSeconds = 30;
        // (The Bigfoot charge burst was removed 2026-07-19 — owner call. Bigfoot simply SPRINTS now,
        // and the sim's BigfootSpeedMul already makes that faster than a searcher's sprint. Roar plus
        // a permanent speed edge was judged enough without a cooldown ability on top.)
        private const double ReviveRadius = 3.5;
        public const double ReviveSeconds = 4; // public: the briefing quotes Sam's real revive time
        private const double ReviveDecay = 2;
        private const double DazzleRange = 40;
        private static readonly double DazzleAimCos = System.Math.Cos(0.38);
        private const double DazzleFillSeconds = 1.2;
        private const double DazzleSeconds = 3;
        private const double DazzleDecay = 2;
        // Wren's trail mark (TRACKING_MARK in shared/sim/specialties.ts — data not in the C# table).
        private const double MarkCooldownSec = 8;
        private const double MarkLifetimeSec = 50;
        private const int MaxMarks = 24;
        // Hunter stakeout pings.
        private const double PingLifetime = 35;
        private const int MaxPings = 12;

        // --- Casting tracks — the searchers' second win path (CHARACTER_FUNC_DEV §8) ---
        // Bigfoot does NOT shed evidence. It leaves TRACKS; a plaster cast is something a person makes
        // from one. Some prints land in ground soft and deep enough to be worth working (`Castable`),
        // only a few are live at a time (newer ones override older), and only MARA carries the kit.
        // Slow and stationary, but it never requires closing on Bigfoot — the safe, patient win path.
        private const double CastableChance = 0.16;   // chance a dropped footprint is deep enough
        private const int MaxCastablePrints = 4;      // older workable prints are overridden by newer
        private const double CastSeconds = 6;         // a cast takes real time to set
        private const double CastRadius = 2.2;
        private const double CastDecay = 2;           // progress bleeds off if you stop working
        private const string CasterSpecialty = "analysis"; // Mara — see the note in HoldActionTarget

        // --- Hair samples — the second evidence type, and the one ANYONE can take ---
        // Casting is deliberately Mara's, but gating the whole second win path behind one persona made
        // it dead weight whenever she was downed, absent, or simply not in the deal. Hair is the
        // hedge, and it sheds two ways (owner design, 2026-07-20):
        //
        //   1. AT RANDOM along the trail, rolled per stride like a castable print — Bigfoot is simply
        //      shedding as it moves, so a trail is worth following even when no branch snapped.
        //   2. GUARANTEED wherever it shoulders past a TREE. This is the good one: it turns the
        //      forest's own density into evidence, and it means the thing Bigfoot does constantly
        //      (weave through trunks at speed) is the thing that incriminates it. Running the tight
        //      lines is fast and leaves a bright trail; the open ground is clean but exposed.
        //
        // Both are suppressed by crouching, like every other kind of track.
        private const double HairChance = 0.09;        // per stride, rolled alongside the print
        private const int MaxHairSamples = 3;          // newer tufts override older, as with prints
        private const double HairCollectSeconds = 2.5; // quick next to a 6 s cast, but not a tap
        /// <summary>Contact slack past tree radius + body radius — a brush, not a dead-on collision.</summary>
        private const double TreeBrushSlack = 0.25;
        /// <summary>
        /// Minimum gap between tree-shed tufts. Without it, standing inside a trunk's collider is a
        /// hair farm, and worse, a Bigfoot pinned against a tree in a chase would carpet the spot.
        /// </summary>
        private const double TreeHairCooldown = 7;

        // --- Dropped proof piles (CHARACTER_FUNC_DEV §8, "still open" → shipped) ---
        // A grab used to DELETE what the victim was carrying. It now spills it on the ground instead,
        // which turns the worst moment in a searcher's night into a decision for everyone else rather
        // than a silent subtraction. Crucially Bigfoot cannot destroy a pile — same rule as the duffel
        // — it can only GUARD one. That's the positional strategy the extraction loop was missing:
        // camp the spill and dare them to come back for it. The pile's only enemy is the clock.
        private const double PileLifetime = 120;   // long enough for a real recovery run, short enough to hurt
        /// <summary>How close you must be to gather a spill. Public so the PROMPT uses the same reach
        /// the server enforces — a prompt must never offer an action the server will refuse.</summary>
        public const float PileRadius = 2.5f;
        /// <summary>A beat of standing still, like the duffel. Public because the CLIENT runs the hold
        /// timer — the two must be one number or the bar fills at a different rate than the rule.</summary>
        public const float PileRecoverSeconds = 1.5f;

        /// <summary>
        /// How close a searcher must get to a cave mouth to put it on the team's map. Generous on
        /// purpose — it should fire when you can SEE the mouth, so the reveal reads as "we found it"
        /// rather than as a proximity trigger you have to hunt for by walking into rocks.
        /// </summary>
        private const float CaveDiscoverRadius = 22f;

        // --- The evidence duffel (camp) ---
        // Proof is only ever WON here. Everything a searcher gathers is carried, unsafe, until they
        // walk it back and store it; the duffel itself is untouchable by Bigfoot, which makes the trip
        // home the risk rather than the bag. Accepts every evidence type, present and future.
        private const double DuffelRadius = 3.0;
        /// <summary>Brief, deliberate — not an instant tap. Public for the same reason as
        /// PileRecoverSeconds: the client owns the hold timer, so there can only be one value.</summary>
        public const float DepositSeconds = 1.2f;

        // --- Eli's camera flash (SPECIALTIES.photo.flash) ---
        private const double FlashRange = 22;                            // short reach (< dazzle's 40)
        private static readonly double FlashAimCos = System.Math.Cos(0.5); // ~29 deg — an aimed shot
        private const double FlashDazzleSeconds = 3;                     // locks Bigfoot's roar/grab
        private const double FlashRevealSeconds = 5;                     // ...but screams "here I am"
        private const int FlashChargesPerNight = 1;

        // --- Sam's spare battery hand-off (SPECIALTIES.endurance.batteryGift) ---
        private const float BatteryGiftAmount = 50f;
        private const double BatteryGiftRadius = 3.5;
        private const int BatteryChargesPerNight = 1;

        private struct Esc { public double Speed, Battery, Stamina, RoarCd, Freeze, ClueLife; }
        private static readonly Esc[] Escalation =
        {
            new Esc { Speed = 1.0,  Battery = 1.0,  Stamina = 1.0,  RoarCd = 1.0,  Freeze = 1.0, ClueLife = 1.0 },
            new Esc { Speed = 1.1,  Battery = 1.25, Stamina = 1.15, RoarCd = 0.85, Freeze = 1.1, ClueLife = 0.8 },
            new Esc { Speed = 1.22, Battery = 1.55, Stamina = 1.3,  RoarCd = 0.7,  Freeze = 1.2, ClueLife = 0.65 },
        };

        public static GameManager Instance { get; private set; }

        [SerializeField] private NetworkObject _playerPrefab;
        [SerializeField] private NetworkObject _cluePrefab;
        [SerializeField] private NetworkObject _markPrefab;
        [SerializeField] private NetworkObject _pingPrefab;
        [SerializeField] private NetworkObject _pilePrefab;
        /// <summary>
        /// Real seconds per night. Raised 600 → 720 (2026-07-20): the forest went from 700 trees to
        /// ~2,400, cave mouths must now be FOUND before they can be staked out, and the extraction
        /// loop wants round trips rather than one-way runs — all of which spend time the old sparse
        /// map didn't. A camp→cave→camp trip is ~100 s on foot and the map is ~1,130 m corner to
        /// corner, so 600 s left very little room for anything going wrong on the way home.
        ///
        /// Public because GameSceneSetup writes it into the scene: the scene serialises its own copy,
        /// so changing the field default alone would never reach an already-built Forest.unity.
        /// </summary>
        public const float DefaultNightSeconds = 480f;

        [Tooltip("Real seconds per night (8pm -> 8am). 480 = the shipped pace; lower for quick tests.")]
        [SerializeField] private float _nightSeconds = DefaultNightSeconds;

        // --- Replicated match state ---
        /// <summary>
        /// This session's forest. Everything seed-derived hangs off it — terrain, tree placement,
        /// the trail network and, most importantly, WHERE THE CAVES ARE. A fixed seed meant a group
        /// that played twice already knew every lair; rolling it per session puts Bigfoot's map back
        /// in play. Clients rebuild their world when this lands (see the OnChange wiring below).
        /// </summary>
        public readonly SyncVar<uint> WorldSeed = new SyncVar<uint>(Sim.World.Seed);
        /// <summary>
        /// Bitmask of cave mouths the searchers have physically found (bit i = world.Caves[i]).
        /// Team-wide and permanent for the match; Bigfoot ignores it and always sees all five.
        /// </summary>
        public readonly SyncVar<int> CavesFound = new SyncVar<int>(0);
        /// <summary>True if searchers have discovered cave <paramref name="index"/> — the map asks this.</summary>
        public bool IsCaveFound(int index) => index >= 0 && index < 31 && (CavesFound.Value & (1 << index)) != 0;
        public readonly SyncVar<byte> MatchPhase = new SyncVar<byte>(PhaseLobby);
        public readonly SyncVar<float> TimeOfDay = new SyncVar<float>(0f);
        public readonly SyncVar<int> NightNumber = new SyncVar<int>(1);
        public readonly SyncVar<int> TotalNights = new SyncVar<int>(TotalNightsCount);
        public readonly SyncVar<int> VideosCaptured = new SyncVar<int>(0);
        /// <summary>
        /// Total PROOF needed to win — footage and casts both count toward it. Raised 3 → 6 with the
        /// evidence system: there are now two ways to gather proof (and more evidence types planned),
        /// so the old target was reachable far too quickly.
        /// </summary>
        public readonly SyncVar<int> VideosRequired = new SyncVar<int>(6);
        /// <summary>
        /// Casts taken from workable prints. Counted separately from footage only so the HUD can show
        /// the team where their case came from — a grab erases BOTH (see TryGrab). Casting is the
        /// patient path: it never requires closing on Bigfoot, but it is no safer once banked.
        /// </summary>
        public readonly SyncVar<int> EvidenceCollected = new SyncVar<int>(0);
        /// <summary>Hair samples stored. Counted apart from casts only so the duffel manifest can
        /// itemise the case; every proof type is worth exactly the same one point.</summary>
        public readonly SyncVar<int> HairCollected = new SyncVar<int>(0);
        /// <summary>
        /// The ONE place proof is totalled. Every evidence type counts the same, so adding another
        /// means adding a counter here and nowhere else — the win check, the top bar and the manifest
        /// all read this. (Three call sites each adding their own terms is exactly how a HUD ends up
        /// disagreeing with the win condition.)
        /// </summary>
        public int StoredProof => VideosCaptured.Value + EvidenceCollected.Value + HairCollected.Value;
        public readonly SyncVar<byte> Winner = new SyncVar<byte>(WinnerNone);
        public readonly SyncVar<float> EscSpeed = new SyncVar<float>(1f);
        public readonly SyncVar<float> EscBattery = new SyncVar<float>(1f);
        public readonly SyncVar<float> EscStamina = new SyncVar<float>(1f);
        public readonly SyncVar<float> RoarCooldownSec = new SyncVar<float>((float)RoarCooldown);
        /// <summary>
        /// How long a clue survives on the CURRENT night (escalation shortens it: 50 → 40 → 32.5 s).
        /// Replicated so clients can fade the trail on exactly the host's window — otherwise a print
        /// looks as fresh at 49 s as at 1 s and then simply vanishes.
        /// </summary>
        public readonly SyncVar<float> ClueLifetimeSec = new SyncVar<float>((float)Clue.Lifetime);

        // --- Server-only working state ---
        private double _elapsed, _nightElapsed;
        private readonly HashSet<HPPlayer> _recording = new HashSet<HPPlayer>();
        private readonly Dictionary<HPPlayer, HPPlayer> _reviveIntent = new Dictionary<HPPlayer, HPPlayer>();
        private readonly Dictionary<HPPlayer, double> _reviveProgress = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _frozenUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _incapUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _slowUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, HPPlayer> _grabbedBy = new Dictionary<HPPlayer, HPPlayer>();
        private readonly Dictionary<HPPlayer, double> _dazzleFill = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _dazzledUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _roarReadyAt = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, double> _chargeReadyAt = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, Vec2> _lastTrack = new Dictionary<HPPlayer, Vec2>();
        private readonly List<(NetworkObject nob, double born)> _clues = new List<(NetworkObject, double)>();
        private readonly Dictionary<HPPlayer, double> _markReadyAt = new Dictionary<HPPlayer, double>();
        private readonly List<(NetworkObject nob, double born)> _marks = new List<(NetworkObject, double)>();
        private readonly List<(NetworkObject nob, double born)> _pings = new List<(NetworkObject, double)>();
        private readonly List<NetworkObject> _castablePrints = new List<NetworkObject>();
        private readonly List<NetworkObject> _hairSamples = new List<NetworkObject>();
        private readonly Dictionary<HPPlayer, double> _treeHairReadyAt = new Dictionary<HPPlayer, double>();
        private readonly List<(NetworkObject nob, double born)> _piles = new List<(NetworkObject, double)>();
        private readonly Dictionary<HPPlayer, NetworkObject> _collectIntent = new Dictionary<HPPlayer, NetworkObject>();
        private readonly Dictionary<NetworkObject, double> _collectProgress = new Dictionary<NetworkObject, double>();
        private readonly Dictionary<HPPlayer, double> _revealedUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, NetworkObject> _pingOwner = new Dictionary<HPPlayer, NetworkObject>();
        private readonly Dictionary<HPPlayer, double> _caveReadyAt = new Dictionary<HPPlayer, double>();
        private readonly System.Random _rng = new System.Random();

        /// <summary>
        /// The live world. A PROPERTY, not a cached field: the forest is rebuilt when the replicated
        /// seed arrives, and a field captured in Awake would go on pointing at the throwaway default
        /// world — colliders and cave positions silently one session out of date.
        /// </summary>
        private GameWorld _world => WorldBuilder.EnsureWorld();
        private float _displayTod; // client-side smoothed clock for the sky
        private int _lastVideos, _lastNightHeard;   // client-side audio edge detection
        private byte _lastWinnerHeard = WinnerNone;

        private void Awake()
        {
            Instance = this;
            WorldBuilder.EnsureWorld();
        }

        public override void OnStartServer()
        {
            InstanceFinder.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;

            // Roll this session's forest. Rolled ONCE per hosting session rather than per match:
            // players stand in the camp lobby in the same world they'll play in, and swapping the
            // ground out from under a lobby full of people to save one re-roll is a bad trade.
            // (Per-match instead is a one-line move into StartMatch, if replay variety ever wants it.)
            // A dev override (title screen) pins the seed so a bug found in one forest is reproducible.
            WorldSeed.Value = HPSettings.DevWorldSeed != 0
                ? HPSettings.DevWorldSeed
                : (uint)_rng.Next(1, int.MaxValue);
            WorldBuilder.SetSeed(WorldSeed.Value);
        }

        public override void OnStopServer()
        {
            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        }

        public override void OnStartNetwork()
        {
            base.TimeManager.OnTick += OnTick;

            // Adopt the host's forest. Both halves are needed: OnChange catches the value arriving
            // later, and the direct call catches the case where it was already in the spawn payload
            // (a client joining an in-progress session), which fires no change at all.
            WorldSeed.OnChange += OnWorldSeedChanged;
            WorldBuilder.SetSeed(WorldSeed.Value);
        }

        public override void OnStopNetwork()
        {
            if (base.TimeManager != null) base.TimeManager.OnTick -= OnTick;
            WorldSeed.OnChange -= OnWorldSeedChanged;
        }

        private void OnWorldSeedChanged(uint prev, uint next, bool asServer)
        {
            // The host already rebuilt in OnStartServer; rebuilding again on its own write would
            // throw away the geometry it just made. SetSeed early-outs on an unchanged seed anyway,
            // but being explicit keeps the intent readable.
            if (asServer) return;
            WorldBuilder.SetSeed(next);
        }

        private void OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer || _playerPrefab == null) return;
            Vector3 pos = CampSpot();
            NetworkObject nob = Instantiate(_playerPrefab, pos, Quaternion.Euler(0f, 180f, 0f));
            var hp = nob.GetComponent<HPPlayer>();
            hp.PlayerName.Value = "Player " + (conn.ClientId + 1);
            InstanceFinder.ServerManager.Spawn(nob, conn);
        }

        private Vector3 CampSpot()
        {
            double x = (_rng.NextDouble() - 0.5) * 8;
            double z = 18 + (_rng.NextDouble() - 0.5) * 4;
            return new Vector3((float)x, (float)_world.GetHeight(x, z), (float)z);
        }

        // ------------------------------------------------------------------ lifecycle RPCs

        /// <summary>Host-only: assign roles (one Bigfoot if 2+ or someone opted in), deal specialties, start night 1.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void ServerStartMatch(NetworkConnection sender = null)
        {
            if (sender == null || !sender.IsHost || MatchPhase.Value != PhaseLobby) return;
            var players = LivePlayers();
            if (players.Count == 0) return;

            // Bigfoot pick: whoever opted in wins the coin toss; otherwise random when 2+; solo = no Bigfoot.
            var wanting = players.FindAll(p => p.WantsBigfoot.Value);
            HPPlayer bigfoot = null;
            if (wanting.Count > 0) bigfoot = wanting[_rng.Next(wanting.Count)];
            else if (players.Count >= 2) bigfoot = players[_rng.Next(players.Count)];

            ResetMatchState(players);

            var searcherIds = new List<string>();
            foreach (var p in players)
            {
                p.Role.Value = p == bigfoot ? HPPlayer.RoleBigfoot : HPPlayer.RoleSearcher;
                if (p != bigfoot) searcherIds.Add(p.ObjectId.ToString());
            }

            // DEV persona overrides (title-screen picker). DealSpecialties honours forced picks and
            // pulls them out of the random pool, so the remaining searchers still get distinct
            // characters. Ids were validated when they were set (HPPlayer.ServerSetDevSpecialty).
            Dictionary<string, string> forced = null;
            foreach (var p in players)
            {
                if (p == bigfoot || !Specialties.IsSpecialtyId(p.DevSpecialty.Value)) continue;
                if (forced == null) forced = new Dictionary<string, string>();
                forced[p.ObjectId.ToString()] = p.DevSpecialty.Value;
            }

            var deal = Specialties.DealSpecialties(searcherIds, forced, _rng.NextDouble);
            foreach (var p in players)
            {
                if (p == bigfoot || !deal.TryGetValue(p.ObjectId.ToString(), out string spec))
                {
                    p.Specialty.Value = "";
                    p.CharacterName.Value = "";
                    p.Stamina.Value = 100f;
                }
                else
                {
                    p.Specialty.Value = spec;
                    p.CharacterName.Value = Specialties.CharacterName[spec];
                    p.Stamina.Value = (float)Specialties.StaminaMax(spec);
                }

                if (p == bigfoot)
                {
                    var cave = _world.Caves[_rng.Next(_world.Caves.Count)];
                    var emerge = Caves.CaveEmergePoint(cave);
                    var pos = new Vector3((float)emerge.X, (float)_world.GetHeight(emerge.X, emerge.Z), (float)emerge.Z);
                    p.TargetTeleport(p.Owner, pos, (float)emerge.Yaw);
                }
                else
                {
                    p.TargetTeleport(p.Owner, CampSpot(), 0f);
                }
            }

            RefillNightlyCharges(players); // night 1's flash / battery charge
            MatchPhase.Value = PhasePlaying;
        }

        /// <summary>Host-only, from the results screen: everyone back to the camp lobby.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void ServerReturnToLobby(NetworkConnection sender = null)
        {
            if (sender == null || !sender.IsHost || MatchPhase.Value != PhaseResults) return;
            var players = LivePlayers();
            ResetMatchState(players);
            foreach (var p in players)
            {
                p.Role.Value = HPPlayer.RoleSearcher;
                p.TargetTeleport(p.Owner, CampSpot(), 0f);
            }
            MatchPhase.Value = PhaseLobby;
        }

        private void ResetMatchState(List<HPPlayer> players)
        {
            Winner.Value = WinnerNone;
            NightNumber.Value = 1;
            TimeOfDay.Value = 0f;
            VideosCaptured.Value = 0;
            // The map goes blank again: a new match re-hides every lair, so night 1 is always a hunt
            // for the caves even when the group replays the same seeded forest.
            CavesFound.Value = 0;
            _elapsed = 0;
            _nightElapsed = 0;
            _recording.Clear();
            _reviveIntent.Clear();
            _reviveProgress.Clear();
            _frozenUntil.Clear();
            _incapUntil.Clear();
            _slowUntil.Clear();
            _grabbedBy.Clear();
            _dazzleFill.Clear();
            _dazzledUntil.Clear();
            _roarReadyAt.Clear();
            _chargeReadyAt.Clear();
            _lastTrack.Clear();
            foreach (var (nob, _) in _clues)
                if (nob != null) InstanceFinder.ServerManager.Despawn(nob);
            _clues.Clear();
            foreach (var (nob, _) in _marks)
                if (nob != null) InstanceFinder.ServerManager.Despawn(nob);
            _marks.Clear();
            _markReadyAt.Clear();
            foreach (var (nob, _) in _pings)
                if (nob != null) InstanceFinder.ServerManager.Despawn(nob);
            _pings.Clear();
            _pingOwner.Clear();
            _caveReadyAt.Clear();
            _castablePrints.Clear(); // the clue despawn loop above already removed the objects
            _hairSamples.Clear();    // ...likewise: hair samples are clues
            _treeHairReadyAt.Clear();
            _collectIntent.Clear();
            _collectProgress.Clear();
            _revealedUntil.Clear();
            foreach (var (nob, _) in _piles)
                if (nob != null) InstanceFinder.ServerManager.Despawn(nob);
            _piles.Clear();
            EvidenceCollected.Value = 0;
            HairCollected.Value = 0;
            foreach (var p in players)
            {
                p.Status.Value = HPPlayer.StatusActive;
                p.StatusEndsIn.Value = 0f;
                p.Slowed.Value = false;
                p.Dazzled.Value = false;
                p.BeingRevived.Value = false;
                p.Filming.Value = false;
                p.FilmProgress.Value = 0f;
                p.GrabberObjectId.Value = -1;
                p.RoarReadyIn.Value = 0f;
                p.ReviveProgress01.Value = 0f;
                p.CollectProgress01.Value = 0f;
                p.RevealedFor.Value = 0f;
                p.AbilityCharges.Value = 0;
                p.CarriedFilm.Value = 0;
                p.CarriedCasts.Value = 0;
                p.CarriedHair.Value = 0;
                p.Battery.Value = 100f;
                p.FlashOn.Value = false;
                p.Specialty.Value = "";
                p.CharacterName.Value = "";
            }
        }

        // ------------------------------------------------------------------ ability entry points (from HPPlayer)

        public void SetRecording(HPPlayer p, bool recording)
        {
            if (recording) _recording.Add(p); else _recording.Remove(p);
            p.Filming.Value = recording && !p.IsBigfoot;
        }

        public void SetReviveTarget(HPPlayer reviver, int targetObjectId)
        {
            if (targetObjectId < 0) { _reviveIntent.Remove(reviver); return; }
            var target = FindLive(targetObjectId);
            if (target != null) _reviveIntent[reviver] = target;
        }

        public void TryRoar(HPPlayer bf)
        {
            if (MatchPhase.Value != PhasePlaying || !bf.IsBigfoot) return;
            if (_elapsed < Get(_dazzledUntil, bf)) return; // blinded — can't roar
            if (_elapsed < Get(_roarReadyAt, bf)) return;
            Esc e = CurrentEsc();
            _roarReadyAt[bf] = _elapsed + RoarCooldown * e.RoarCd;
            foreach (var h in LivePlayers())
            {
                if (h.IsBigfoot || h.Status.Value != HPPlayer.StatusActive) continue;
                if (Dist2(h, bf) <= RoarRadius * RoarRadius)
                {
                    h.Status.Value = HPPlayer.StatusFrozen;
                    _frozenUntil[h] = _elapsed + FreezeSeconds * e.Freeze;
                }
            }
            RpcRoared(bf.transform.position);
        }

        public void TryGrab(HPPlayer bf)
        {
            if (MatchPhase.Value != PhasePlaying || !bf.IsBigfoot) return;
            if (_elapsed < Get(_dazzledUntil, bf)) return; // blinded — can't grab

            // Dragging someone already? This drops them where they are.
            bool released = false;
            var toRelease = new List<HPPlayer>();
            foreach (var kv in _grabbedBy) if (kv.Value == bf) toRelease.Add(kv.Key);
            foreach (var victim in toRelease)
            {
                _grabbedBy.Remove(victim);
                victim.GrabberObjectId.Value = -1;
                released = true;
            }
            if (released) return;

            HPPlayer best = null;
            double bestD = GrabRadius * GrabRadius;
            foreach (var h in LivePlayers())
            {
                if (h.Status.Value != HPPlayer.StatusFrozen) continue;
                double d = Dist2(h, bf);
                if (d <= bestD) { bestD = d; best = h; }
            }
            if (best == null) return;

            best.Status.Value = HPPlayer.StatusIncap;
            best.Filming.Value = false;
            best.FilmProgress.Value = 0f;
            _recording.Remove(best);
            _frozenUntil.Remove(best);
            _incapUntil[best] = _elapsed + IncapSeconds;
            _grabbedBy[best] = bf;
            best.GrabberObjectId.Value = bf.ObjectId;
            // A grab SPILLS everything this searcher was carrying — it doesn't destroy it. The pack
            // bursts where they went down and the proof lies there, recoverable, until it goes cold.
            // Proof already stored in the duffel is untouchable, because the duffel is.
            //
            // This is the third and best version of the rule. The original wiped the whole team's
            // footage (swingy, and nobody but the victim could influence it); the interim version
            // deleted just the victim's carry (proportional, but still a silent subtraction the
            // moment you were downed). Spilling keeps the proportionality AND gives the loss a
            // second act: the team can mount a recovery run, Bigfoot can stand over the spill and
            // dare them, and the downed player watches their own night hang in the balance.
            int spilled = best.CarriedTotal;
            if (spilled > 0)
            {
                SpawnProofPile(best.transform.position, best.CarriedFilm.Value, best.CarriedCasts.Value,
                               best.CarriedHair.Value, best.PlayerName.Value);
                best.CarriedFilm.Value = 0;
                best.CarriedCasts.Value = 0;
                best.CarriedHair.Value = 0;
                RpcProofSpilled(best.transform.position, spilled);
            }
        }

        // ------------------------------------------------------------------ casting tracks

        /// <summary>
        /// A searcher declares intent to work a specific piece of evidence (held action, like revive).
        /// Accepts both workable prints and hair samples; the specialty gate lives in UpdateCasting,
        /// so this only has to answer "is that a real, still-live piece of evidence".
        /// </summary>
        public void SetCollectTarget(HPPlayer p, int clueObjectId)
        {
            if (clueObjectId < 0) { _collectIntent.Remove(p); return; }
            foreach (var nob in _castablePrints)
                if (nob != null && nob.ObjectId == clueObjectId) { _collectIntent[p] = nob; return; }
            foreach (var nob in _hairSamples)
                if (nob != null && nob.ObjectId == clueObjectId) { _collectIntent[p] = nob; return; }
            _collectIntent.Remove(p);
        }

        /// <summary>
        /// Drive every in-progress collection — plaster casts and hair samples both. Progress lives on
        /// the EVIDENCE, not the collector, so if the work is interrupted and someone comes back it
        /// isn't lost outright: it bleeds off, it doesn't reset.
        ///
        /// The two kinds differ only in who may work them and how long it takes. Keeping them on one
        /// channel means the progress bar, the bleed-off, the prompt and the interrupt rules can
        /// never disagree between evidence types.
        /// </summary>
        private void UpdateCasting(double dt)
        {
            var advanced = new HashSet<NetworkObject>();
            var finished = new List<NetworkObject>();

            foreach (var kv in _collectIntent)
            {
                HPPlayer p = kv.Key;
                NetworkObject nob = kv.Value;
                if (p == null || nob == null) continue;
                if (p.IsBigfoot || p.Status.Value != HPPlayer.StatusActive) continue;

                bool hair = IsHairSample(nob);
                // Only Mara carries the casting kit. Hair needs no kit — you just bag it.
                if (!hair && p.Specialty.Value != CasterSpecialty) continue;
                if (Vector3.Distance(p.transform.position, nob.transform.position) > CastRadius) continue;

                double needed = hair ? HairCollectSeconds : CastSeconds;
                double prog = (_collectProgress.TryGetValue(nob, out double v) ? v : 0) + dt;
                advanced.Add(nob);

                if (prog >= needed)
                {
                    finished.Add(nob);
                    _collectProgress.Remove(nob);
                }
                else
                {
                    _collectProgress[nob] = prog;
                    p.CollectProgress01.Value = (float)(prog / needed);
                }
            }

            foreach (NetworkObject nob in finished)
            {
                // Like footage: the collector CARRIES it until they store it in the duffel.
                bool hair = IsHairSample(nob);
                foreach (var kv in _collectIntent)
                {
                    if (kv.Value != nob || kv.Key == null) continue;
                    if (hair) kv.Key.CarriedHair.Value++; else kv.Key.CarriedCasts.Value++;
                    break;
                }
                RpcCastTaken(nob.transform.position);
                RemoveEvidence(nob, despawn: true);

                var clear = new List<HPPlayer>();
                foreach (var kv in _collectIntent) if (kv.Value == nob) clear.Add(kv.Key);
                foreach (var p in clear)
                {
                    _collectIntent.Remove(p);
                    if (p != null) p.CollectProgress01.Value = 0f;
                }
            }

            // Bleed off progress on any print nobody is working any more.
            var decaying = new List<NetworkObject>();
            foreach (var kv in _collectProgress) if (!advanced.Contains(kv.Key)) decaying.Add(kv.Key);
            foreach (NetworkObject nob in decaying)
            {
                double v = _collectProgress[nob] - dt * CastDecay;
                if (v <= 0) _collectProgress.Remove(nob); else _collectProgress[nob] = v;
            }

            foreach (var p in LivePlayers())
            {
                if (p.IsBigfoot || p.CollectProgress01.Value == 0f) continue;
                if (!_collectIntent.ContainsKey(p)) p.CollectProgress01.Value = 0f;
            }
        }

        // (Bigfoot used to RUIN a workable print by treading on it — removed 2026-07-20, owner call.
        // Evidence now leaves play exactly two ways: it goes cold on the clue lifetime, or a searcher
        // collects it. Bigfoot has no delete button anywhere in the evidence loop — not on prints, not
        // on hair, not on a spilled pack. Its answer to evidence is positional: be where the evidence
        // is. That is one rule instead of three exceptions, and it means a searcher who finds a
        // workable print can always trust that getting there in time is enough.)

        private bool IsHairSample(NetworkObject nob)
        {
            foreach (var h in _hairSamples) if (h == nob) return true;
            return false;
        }

        /// <summary>Forget a piece of evidence (and optionally despawn it). Clue expiry routes here too.</summary>
        private void RemoveEvidence(NetworkObject nob, bool despawn)
        {
            _castablePrints.Remove(nob);
            _hairSamples.Remove(nob);
            _collectProgress.Remove(nob);
            if (!despawn || nob == null) return;
            for (int i = _clues.Count - 1; i >= 0; i--) if (_clues[i].nob == nob) _clues.RemoveAt(i);
            InstanceFinder.ServerManager.Despawn(nob);
        }

        [ObserversRpc]
        private void RpcCastTaken(Vector3 at)
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.EvidenceBanked, 0.6f);
        }


        // ------------------------------------------------------------------ the evidence duffel

        /// <summary>Is this player standing at the duffel? (Shared by the deposit rule and the prompt.)</summary>
        public static bool AtDuffel(Vector3 pos)
        {
            Vector3 d = WorldBuilder.DuffelPosition() - pos;
            return d.x * d.x + d.z * d.z <= DuffelRadius * DuffelRadius;
        }

        /// <summary>
        /// Store everything a searcher is carrying. This is the ONLY way proof is banked, and once
        /// banked it is permanent — Bigfoot has no interaction with the duffel at all. Deliberately
        /// type-agnostic: it moves whatever the player holds into the team totals, so adding a new
        /// evidence kind means adding a carried counter and nothing here.
        /// </summary>
        public void TryDeposit(HPPlayer p)
        {
            if (MatchPhase.Value != PhasePlaying || p.IsBigfoot) return;
            if (p.Status.Value != HPPlayer.StatusActive) return;
            if (p.CarriedTotal <= 0 || !AtDuffel(p.transform.position)) return;

            VideosCaptured.Value += p.CarriedFilm.Value;
            EvidenceCollected.Value += p.CarriedCasts.Value;
            HairCollected.Value += p.CarriedHair.Value;
            int stored = p.CarriedTotal;
            p.CarriedFilm.Value = 0;
            p.CarriedCasts.Value = 0;
            p.CarriedHair.Value = 0;
            RpcDeposited(p.transform.position, stored);
        }

        // ------------------------------------------------------------------ dropped proof piles

        /// <summary>Spill a searcher's pack where they went down. Contents are set before Spawn so
        /// they arrive with the payload and the visual can build the right scatter first frame.</summary>
        private void SpawnProofPile(Vector3 at, int film, int casts, int hair, string ownerName)
        {
            if (_pilePrefab == null) return;
            float y = (float)_world.GetHeight(at.x, at.z);
            NetworkObject nob = Instantiate(_pilePrefab, new Vector3(at.x, y, at.z), Quaternion.identity);
            var pile = nob.GetComponent<ProofPile>();
            pile.Film.Value = film;
            pile.Casts.Value = casts;
            pile.Hair.Value = hair;
            pile.OwnerName.Value = ownerName;
            InstanceFinder.ServerManager.Spawn(nob);
            _piles.Add((nob, _elapsed));
        }

        /// <summary>
        /// Pick a spill back up. Any active searcher can — including the one who dropped it, if their
        /// team gets them up in time. It goes straight back into the carrier's pack, still unsafe:
        /// recovering proof is not the same as saving it, and the walk home still has to happen.
        /// </summary>
        public void TryRecoverPile(HPPlayer p, int pileObjectId)
        {
            if (MatchPhase.Value != PhasePlaying || p.IsBigfoot) return;
            if (p.Status.Value != HPPlayer.StatusActive) return;

            for (int i = 0; i < _piles.Count; i++)
            {
                NetworkObject nob = _piles[i].nob;
                if (nob == null || nob.ObjectId != pileObjectId) continue;
                if (Vector3.Distance(p.transform.position, nob.transform.position) > PileRadius) return;

                var pile = nob.GetComponent<ProofPile>();
                if (pile == null) return;
                p.CarriedFilm.Value += pile.Film.Value;
                p.CarriedCasts.Value += pile.Casts.Value;
                p.CarriedHair.Value += pile.Hair.Value;
                RpcProofRecovered(nob.transform.position, pile.Total);
                _piles.RemoveAt(i);
                InstanceFinder.ServerManager.Despawn(nob);
                return;
            }
        }

        /// <summary>
        /// Cave mouths are UNKNOWN to the searchers until somebody walks up to one.
        ///
        /// The map used to hand the team all five lairs at spawn, which quietly deleted the entire
        /// exploration half of the game: you could stake out Bigfoot's front doors on night 1 without
        /// ever having seen the forest. Discovery is TEAM-WIDE and permanent for the match — one
        /// scout's find lights it up for everyone, so scouting is worth doing and worth shouting
        /// about, and a downed searcher's knowledge isn't lost with them.
        ///
        /// Host-side from replicated positions, never client-reported: movement is still
        /// client-authoritative, so a self-reported "I found a cave" would be a free full map.
        /// Bigfoot is not involved — it always sees its own lairs.
        /// </summary>
        private void DiscoverCaves(List<HPPlayer> hunters)
        {
            var world = _world;
            if (world == null) return;
            int found = CavesFound.Value;
            for (int i = 0; i < world.Caves.Count && i < 31; i++)
            {
                int bit = 1 << i;
                if ((found & bit) != 0) continue;
                foreach (var h in hunters)
                {
                    if (h.Status.Value != HPPlayer.StatusActive) continue;
                    Vector3 p = h.transform.position;
                    double dx = p.x - world.Caves[i].X, dz = p.z - world.Caves[i].Z;
                    if (dx * dx + dz * dz > CaveDiscoverRadius * CaveDiscoverRadius) continue;
                    found |= bit;
                    RpcCaveFound(i, h.CharacterName.Value != "" ? h.CharacterName.Value : h.PlayerName.Value);
                    break;
                }
            }
            if (found != CavesFound.Value) CavesFound.Value = found;
        }

        [ObserversRpc]
        private void RpcCaveFound(int index, string by)
        {
            HPHud.NotifyCaveFound(index + 1, by);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.EvidenceBanked, 0.4f);
        }

        /// <summary>
        /// Spills go cold. This is the pile's ONLY enemy — Bigfoot has no way to destroy one (the same
        /// guarantee the duffel has), so guarding it and running out the clock is the play, and the
        /// searchers' answer is to make Bigfoot choose which spill to stand over.
        /// </summary>
        private void ExpirePiles()
        {
            for (int i = _piles.Count - 1; i >= 0; i--)
            {
                if (_piles[i].nob == null) { _piles.RemoveAt(i); continue; }
                if (_elapsed - _piles[i].born <= PileLifetime) continue;
                RpcPileLost(_piles[i].nob.transform.position);
                InstanceFinder.ServerManager.Despawn(_piles[i].nob);
                _piles.RemoveAt(i);
            }
        }

        [ObserversRpc]
        private void RpcProofSpilled(Vector3 at, int count)
        {
            HPHud.NotifyProofSpilled(count);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.EvidenceDestroyed, at, 0.6f, 22f);
        }

        [ObserversRpc]
        private void RpcProofRecovered(Vector3 at, int count)
        {
            HPHud.NotifyProofRecovered(count);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.EvidenceBanked, 0.6f);
        }

        [ObserversRpc]
        private void RpcPileLost(Vector3 at)
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.EvidenceDestroyed, at, 0.45f, 26f);
        }

        [ObserversRpc]
        private void RpcDeposited(Vector3 at, int count)
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.EvidenceBanked, 0.65f);
            HPHud.NotifyDeposit(count);
        }

        // ------------------------------------------------------------------ Eli's flash / Sam's battery

        /// <summary>
        /// Eli's camera flash: a short, tight, aimed burst that dazzles Bigfoot like a held torch does
        /// — but instantly, and at the cost of painting Eli's own position for it to hunt. One charge
        /// per night. The whole trade is "I can stop it right now, and it will know exactly where I am."
        /// </summary>
        public void TryFlash(HPPlayer p)
        {
            if (MatchPhase.Value != PhasePlaying || p.IsBigfoot) return;
            if (p.Specialty.Value != "photo" || p.Status.Value != HPPlayer.StatusActive) return;
            if (p.AbilityCharges.Value <= 0) return;
            p.AbilityCharges.Value--;

            bool hit = false;
            foreach (var bf in LivePlayers())
            {
                if (!bf.IsBigfoot) continue;
                if (!ConeVisible(p, bf, FlashRange, FlashAimCos)) continue;
                _dazzledUntil[bf] = _elapsed + FlashDazzleSeconds;
                _dazzleFill[bf] = 0; // a fresh burst, not a continuation of a torch hold
                bf.Dazzled.Value = true;
                hit = true;
            }

            // Fired or not, the flash gives Eli away — that's the point of the ability.
            _revealedUntil[p] = _elapsed + FlashRevealSeconds;
            RpcFlashFired(p.transform.position, hit);
        }

        [ObserversRpc]
        private void RpcFlashFired(Vector3 at, bool hit)
        {
            HPHud.NotifyFlash(at, hit);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.CameraFlash, at, 0.7f, 25f);
        }

        /// <summary>
        /// Sam hands a teammate a spare battery. Note this is the ONE place a player's battery may go
        /// UP: HPPlayer.ServerVitals enforces battery-only-decreases, so the receiver's local sim must
        /// be told to raise its own value (TargetGrantBattery) or it would immediately push it back down.
        /// </summary>
        public void TryBatteryGift(HPPlayer giver, int targetObjectId)
        {
            if (MatchPhase.Value != PhasePlaying || giver.IsBigfoot) return;
            if (giver.Specialty.Value != "endurance" || giver.Status.Value != HPPlayer.StatusActive) return;
            if (giver.AbilityCharges.Value <= 0) return;

            HPPlayer target = FindLive(targetObjectId);
            if (target == null || target == giver || target.IsBigfoot) return;
            if (target.Battery.Value >= 99f) return;
            if (Dist2(giver, target) > BatteryGiftRadius * BatteryGiftRadius) return;

            giver.AbilityCharges.Value--;
            float newBattery = Mathf.Min(100f, target.Battery.Value + BatteryGiftAmount);
            target.Battery.Value = newBattery;
            target.TargetGrantBattery(target.Owner, newBattery);
            RpcBatteryGiven(target.transform.position);
        }

        [ObserversRpc]
        private void RpcBatteryGiven(Vector3 at)
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.BatterySwap, at, 0.5f, 12f);
        }

        /// <summary>Refill the per-night ability charges (Eli's flash, Sam's battery) for everyone.</summary>
        private void RefillNightlyCharges(List<HPPlayer> players)
        {
            foreach (var p in players)
            {
                int charges = p.Specialty.Value == "photo" ? FlashChargesPerNight
                    : p.Specialty.Value == "endurance" ? BatteryChargesPerNight : 0;
                p.AbilityCharges.Value = charges;
            }
        }

        /// <summary>Wren drops a team-visible trail marker at her feet (ForestRoom's `mark` handler).</summary>
        public void TryMark(HPPlayer p)
        {
            if (MatchPhase.Value != PhasePlaying || _markPrefab == null) return;
            if (p.Specialty.Value != "tracking" || p.Status.Value != HPPlayer.StatusActive) return;
            if (_elapsed < Get(_markReadyAt, p)) return; // cooldown
            _markReadyAt[p] = _elapsed + MarkCooldownSec;

            while (_marks.Count >= MaxMarks)
            {
                if (_marks[0].nob != null) InstanceFinder.ServerManager.Despawn(_marks[0].nob);
                _marks.RemoveAt(0);
            }
            Vector3 at = p.transform.position;
            float y = (float)_world.GetHeight(at.x, at.z);
            NetworkObject nob = Instantiate(_markPrefab, new Vector3(at.x, y, at.z), Quaternion.identity);
            InstanceFinder.ServerManager.Spawn(nob);
            _marks.Add((nob, _elapsed));
        }

        /// <summary>
        /// A hunter drops a stakeout ping (ForestRoom's `ping` handler): one live ping per hunter —
        /// re-pinging moves it — clamped to the world, capped, and expired by lifetime.
        /// </summary>
        public void TryPing(HPPlayer p, float x, float z)
        {
            if (MatchPhase.Value != PhasePlaying || _pingPrefab == null) return;
            if (p.IsBigfoot || p.Status.Value != HPPlayer.StatusActive) return;

            float half = (float)Sim.World.Size / 2f;
            x = Mathf.Clamp(x, -half, half);
            z = Mathf.Clamp(z, -half, half);

            RemovePing(p);
            while (_pings.Count >= MaxPings)
            {
                if (_pings[0].nob != null) InstanceFinder.ServerManager.Despawn(_pings[0].nob);
                _pings.RemoveAt(0);
            }

            NetworkObject nob = Instantiate(_pingPrefab, new Vector3(x, (float)_world.GetHeight(x, z), z), Quaternion.identity);
            InstanceFinder.ServerManager.Spawn(nob);
            _pings.Add((nob, _elapsed));
            _pingOwner[p] = nob;
        }

        private void RemovePing(HPPlayer owner)
        {
            if (!_pingOwner.TryGetValue(owner, out NetworkObject nob)) return;
            _pingOwner.Remove(owner);
            for (int i = _pings.Count - 1; i >= 0; i--)
                if (_pings[i].nob == nob) _pings.RemoveAt(i);
            if (nob != null) InstanceFinder.ServerManager.Despawn(nob);
        }

        private void ExpirePings()
        {
            for (int i = _pings.Count - 1; i >= 0; i--)
            {
                if (_pings[i].nob == null) { _pings.RemoveAt(i); continue; }
                if (_elapsed - _pings[i].born > PingLifetime)
                {
                    NetworkObject nob = _pings[i].nob;
                    _pings.RemoveAt(i);
                    ClearPingOwner(nob);
                    InstanceFinder.ServerManager.Despawn(nob);
                }
            }
        }

        private void ClearPingOwner(NetworkObject nob)
        {
            foreach (var kv in _pingOwner)
            {
                if (kv.Value != nob) continue;
                _pingOwner.Remove(kv.Key);
                return;
            }
        }

        /// <summary>
        /// Bigfoot fast-travels between cave mouths (ForestRoom's `caveTravel` handler): it must be
        /// standing in SOME mouth, pick a different valid cave, and clear the cooldown. The emerge
        /// point comes from the shared sim, so host and client land on the same spot and heading.
        /// </summary>
        public void TryCaveTravel(HPPlayer p, int index)
        {
            if (MatchPhase.Value != PhasePlaying) return;
            if (!p.IsBigfoot || p.Status.Value != HPPlayer.StatusActive) return;
            if (index < 0 || index >= _world.Caves.Count) return;

            Vector3 at = p.transform.position;
            int here = Caves.NearestCaveIndex(_world.Caves, at.x, at.z);
            if (here < 0 || here == index) return;
            if (_elapsed < Get(_caveReadyAt, p)) return;
            _caveReadyAt[p] = _elapsed + CaveRules.TravelCooldown;

            var emerge = Caves.CaveEmergePoint(_world.Caves[index]);
            var pos = new Vector3((float)emerge.X, (float)_world.GetHeight(emerge.X, emerge.Z), (float)emerge.Z);
            p.TargetTeleport(p.Owner, pos, (float)emerge.Yaw);
        }

        private void ExpireMarks()
        {
            for (int i = _marks.Count - 1; i >= 0; i--)
            {
                if (_marks[i].nob == null) { _marks.RemoveAt(i); continue; }
                if (_elapsed - _marks[i].born > MarkLifetimeSec)
                {
                    InstanceFinder.ServerManager.Despawn(_marks[i].nob);
                    _marks.RemoveAt(i);
                }
            }
        }

        [ObserversRpc]
        private void RpcRoared(Vector3 pos)
        {
            HPHud.NotifyRoar(pos);
            // Positional so it carries beyond the freeze radius and you can tell WHERE it came from.
            // The roaring Bigfoot already heard its own roar up close when it pressed the button.
            var me = HPPlayer.Local;
            if (HPAudio.Instance != null && (me == null || !me.IsBigfoot))
                HPAudio.Instance.PlayAt(HPAudio.Roar, pos, 0.95f, 30f);
        }

        // ------------------------------------------------------------------ host tick

        /// <summary>
        /// DEV — end the current night immediately (F3 overlay, host only).
        ///
        /// Exists because verifying anything per-night — the moon's phase and arc, the escalation
        /// table, Eli's flash and Sam's battery refilling at dusk — otherwise means sitting through
        /// two full nights to reach night 3. It deliberately just runs the clock out rather than
        /// duplicating the rollover: the next tick takes the normal path, so a skipped night is
        /// identical to an elapsed one and this can't drift from the real logic.
        /// </summary>
        public void DevSkipNight()
        {
            if (!base.IsServerStarted || MatchPhase.Value != PhasePlaying) return;
            _nightElapsed = _nightSeconds;
        }

        private void OnTick()
        {
            if (!base.IsServerStarted) return;
            double dt = base.TimeManager.TickDelta;
            if (MatchPhase.Value != PhasePlaying) return;

            _elapsed += dt;
            _nightElapsed += dt;

            TimeOfDay.Value = Mathf.Min(1f, (float)(_nightElapsed / _nightSeconds));
            bool nightsComplete = false;
            if (TimeOfDay.Value >= 1f)
            {
                if (NightNumber.Value < TotalNights.Value)
                {
                    NightNumber.Value++;
                    _nightElapsed = 0;
                    TimeOfDay.Value = 0f;
                    RefillNightlyCharges(LivePlayers()); // Eli's flash + Sam's battery come back at dusk
                }
                else nightsComplete = true;
            }

            Esc e = CurrentEsc();
            EscSpeed.Value = (float)e.Speed;
            EscBattery.Value = (float)e.Battery;
            EscStamina.Value = (float)e.Stamina;
            RoarCooldownSec.Value = (float)(RoarCooldown * e.RoarCd);
            ClueLifetimeSec.Value = (float)(Clue.Lifetime * e.ClueLife);

            var players = LivePlayers();
            var bigfoots = players.FindAll(p => p.IsBigfoot);
            var hunters = players.FindAll(p => !p.IsBigfoot);

            // Publish the roar cooldown so Bigfoot's HUD can count it down honestly.
            foreach (var bf in bigfoots)
            {
                float rr = (float)System.Math.Max(0, Get(_roarReadyAt, bf) - _elapsed);
                if (bf.RoarReadyIn.Value != rr) bf.RoarReadyIn.Value = rr;
            }

            // Countdown (not a deadline) so clients need no shared clock to read it.
            foreach (var h in hunters)
            {
                float rev = (float)System.Math.Max(0, Get(_revealedUntil, h) - _elapsed);
                if (h.RevealedFor.Value != rev) h.RevealedFor.Value = rev;
            }

            DropClues(bigfoots);
            ExpireClues(e);
            ExpireMarks();
            ExpirePings();
            ExpirePiles();
            ShedHairOnTrees(bigfoots);
            DiscoverCaves(hunters);
            UpdateCasting(dt);
            UpdateRevives(dt, hunters);
            UpdateStatuses();
            UpdateDazzle(dt, hunters, bigfoots);
            UpdateFilming(dt, hunters, bigfoots);

            if (hunters.Count > 0)
            {
                // Proof is footage PLUS physical evidence — either path, or any mix, gets there.
                if (StoredProof >= VideosRequired.Value) Winner.Value = WinnerHunters;
                else if (nightsComplete) Winner.Value = WinnerBigfoot;
                if (Winner.Value != WinnerNone) MatchPhase.Value = PhaseResults;
            }
        }

        private void DropClues(List<HPPlayer> bigfoots)
        {
            if (_cluePrefab == null) return;
            foreach (var bf in bigfoots)
            {
                Vector3 pos = bf.transform.position;
                if (!_lastTrack.TryGetValue(bf, out Vec2 last))
                {
                    _lastTrack[bf] = new Vec2(pos.x, pos.z);
                    continue;
                }
                double dx = pos.x - last.X, dz = pos.z - last.Z;
                if (dx * dx + dz * dz < Stride * Stride) continue;

                // Crouching Bigfoot leaves NO trail — no prints, no snapped branches, no castable
                // tracks. The sim already halves crouch speed, so this is a real trade rather than a
                // free upgrade: move silently, or move quickly. The stride counter still advances, so
                // standing up doesn't immediately dump the print you just avoided leaving.
                if (bf.Crouched.Value)
                {
                    _lastTrack[bf] = new Vec2(pos.x, pos.z);
                    continue;
                }

                // Some prints land in ground soft and deep enough to be worth casting. Only a few are
                // live at once — a newer workable print overrides the oldest.
                bool castable = _rng.NextDouble() < CastableChance;
                SpawnClue(ClueMarker.TypeFootprint, pos.x, pos.z, bf.SimYawFromTransform(), castable);

                if (_rng.NextDouble() < BranchChance)
                    SpawnClue(ClueMarker.TypeBranch,
                        pos.x + (_rng.NextDouble() - 0.5) * 1.6,
                        pos.z + (_rng.NextDouble() - 0.5) * 1.6,
                        (float)(_rng.NextDouble() * System.Math.PI * 2));

                // Shedding as it goes — rolled per stride, independent of everything else, so a plain
                // trail across open ground can still be worth something to a searcher who finds it.
                if (_rng.NextDouble() < HairChance)
                    SpawnClue(ClueMarker.TypeHair,
                        pos.x + (_rng.NextDouble() - 0.5) * 1.2,
                        pos.z + (_rng.NextDouble() - 0.5) * 1.2,
                        (float)(_rng.NextDouble() * System.Math.PI * 2));

                _lastTrack[bf] = new Vec2(pos.x, pos.z);
            }
        }

        /// <summary>
        /// Bigfoot sheds hair on any tree it shoulders past. Detected HOST-SIDE from the replicated
        /// position rather than reported by the client: movement is still client-authoritative, so a
        /// self-reported "I hit a tree" would be a free evidence-suppression switch for a cheater.
        /// Nothing new goes over the wire for this — the host already knows where Bigfoot is standing.
        ///
        /// A tree is any collider with no ClimbH; every structure in the world (RV, cave boulders,
        /// lookout tower) is climbable, so that test needs no change to the parity-locked sim.
        /// </summary>
        private void ShedHairOnTrees(List<HPPlayer> bigfoots)
        {
            if (_world == null) return;
            foreach (var bf in bigfoots)
            {
                if (bf.Status.Value != HPPlayer.StatusActive) continue;
                if (bf.Crouched.Value) continue;             // crouching leaves no trace of any kind
                if (_elapsed < Get(_treeHairReadyAt, bf)) continue;

                Vector3 pos = bf.transform.position;
                foreach (var c in _world.Colliders)
                {
                    if (c.ClimbH.HasValue) continue;         // a structure, not a tree
                    double dx = pos.x - c.X, dz = pos.z - c.Z;
                    double reach = c.R + Player.Radius + TreeBrushSlack;
                    if (dx * dx + dz * dz > reach * reach) continue;

                    // Put the tuft on the trunk face it brushed, not at Bigfoot's feet — the searcher
                    // should read it as "it squeezed past HERE", which is a direction as well as proof.
                    double d = System.Math.Sqrt(dx * dx + dz * dz);
                    double nx = d > 0.001 ? dx / d : 1, nz = d > 0.001 ? dz / d : 0;
                    SpawnClue(ClueMarker.TypeHair, c.X + nx * (c.R + 0.1), c.Z + nz * (c.R + 0.1),
                              (float)System.Math.Atan2(-nx, -nz));
                    _treeHairReadyAt[bf] = _elapsed + TreeHairCooldown;
                    break;
                }
            }
        }

        private void SpawnClue(byte ctype, double x, double z, float yawRad, bool castable = false)
        {
            while (_clues.Count >= MaxClues)
            {
                if (_clues[0].nob != null)
                {
                    _castablePrints.Remove(_clues[0].nob);
                    _hairSamples.Remove(_clues[0].nob);
                    _collectProgress.Remove(_clues[0].nob);
                    InstanceFinder.ServerManager.Despawn(_clues[0].nob);
                }
                _clues.RemoveAt(0);
            }
            float y = (float)_world.GetHeight(x, z);
            NetworkObject nob = Instantiate(_cluePrefab, new Vector3((float)x, y, (float)z), Quaternion.identity);
            var marker = nob.GetComponent<ClueMarker>();
            marker.CType.Value = ctype;
            marker.YawRad.Value = yawRad;
            marker.Castable.Value = castable; // set BEFORE Spawn so it arrives with the payload
            InstanceFinder.ServerManager.Spawn(nob);
            _clues.Add((nob, _elapsed));

            if (ctype == ClueMarker.TypeHair)
            {
                // Newer tufts override older, exactly as prints do — the map shows where the work is,
                // and "where the work is" has to stay a short list or it stops meaning anything.
                while (_hairSamples.Count >= MaxHairSamples)
                    RemoveEvidence(_hairSamples[0], despawn: true);
                _hairSamples.Add(nob);
                return;
            }

            if (!castable) return;
            // Newer workable prints override older ones, so the map never fills with them.
            while (_castablePrints.Count >= MaxCastablePrints)
            {
                NetworkObject oldest = _castablePrints[0];
                RemoveEvidence(oldest, despawn: true);
            }
            _castablePrints.Add(nob);
        }

        private void ExpireClues(Esc e)
        {
            double lifetime = Clue.Lifetime * e.ClueLife;
            for (int i = _clues.Count - 1; i >= 0; i--)
            {
                if (_clues[i].nob == null) { _clues.RemoveAt(i); continue; }
                if (_elapsed - _clues[i].born > lifetime)
                {
                    // Workable evidence goes cold with the rest of the trail — "available for a time".
                    _castablePrints.Remove(_clues[i].nob);
                    _hairSamples.Remove(_clues[i].nob);
                    _collectProgress.Remove(_clues[i].nob);
                    InstanceFinder.ServerManager.Despawn(_clues[i].nob);
                    _clues.RemoveAt(i);
                }
            }
        }

        private void UpdateRevives(double dt, List<HPPlayer> hunters)
        {
            var revivedThisTick = new HashSet<HPPlayer>();
            var doneIntents = new List<HPPlayer>();

            foreach (var kv in _reviveIntent)
            {
                HPPlayer reviver = kv.Key, target = kv.Value;
                if (reviver == null || target == null) { continue; }
                if (reviver.IsBigfoot || reviver.Status.Value != HPPlayer.StatusActive) continue;
                if (target.Status.Value != HPPlayer.StatusIncap) continue;
                if (Dist2(reviver, target) > ReviveRadius * ReviveRadius) continue;

                double prog = Get(_reviveProgress, target) + dt;
                revivedThisTick.Add(target);
                double needed = ReviveSeconds * Specialties.ReviveMul(reviver.Specialty.Value); // Sam revives faster
                if (prog >= needed)
                {
                    target.Status.Value = HPPlayer.StatusActive;
                    target.ReviveProgress01.Value = 0f;
                    _incapUntil.Remove(target);
                    _grabbedBy.Remove(target);
                    target.GrabberObjectId.Value = -1;
                    _slowUntil[target] = _elapsed + SlowSeconds;
                    _reviveProgress.Remove(target);
                    doneIntents.Add(reviver);
                }
                else
                {
                    _reviveProgress[target] = prog;
                    target.ReviveProgress01.Value = (float)(prog / needed);
                }
            }
            foreach (var r in doneIntents) _reviveIntent.Remove(r);

            var decayed = new List<HPPlayer>();
            foreach (var kv in _reviveProgress)
            {
                if (revivedThisTick.Contains(kv.Key)) continue;
                decayed.Add(kv.Key);
            }
            foreach (var t in decayed)
            {
                double v = _reviveProgress[t] - dt * ReviveDecay;
                if (v <= 0)
                {
                    _reviveProgress.Remove(t);
                    if (t != null) t.ReviveProgress01.Value = 0f;
                }
                else
                {
                    _reviveProgress[t] = v;
                    if (t != null) t.ReviveProgress01.Value = (float)(v / ReviveSeconds);
                }
            }
            foreach (var p in LivePlayers())
            {
                bool flag = revivedThisTick.Contains(p);
                if (p.BeingRevived.Value != flag) p.BeingRevived.Value = flag;
            }
        }

        private void UpdateStatuses()
        {
            foreach (var p in LivePlayers())
            {
                if (p.IsBigfoot) continue;

                if (p.Status.Value == HPPlayer.StatusFrozen)
                {
                    if (_elapsed >= Get(_frozenUntil, p))
                    {
                        p.Status.Value = HPPlayer.StatusActive;
                        _frozenUntil.Remove(p);
                    }
                    else p.StatusEndsIn.Value = (float)(Get(_frozenUntil, p) - _elapsed);
                }
                else if (p.Status.Value == HPPlayer.StatusIncap)
                {
                    if (_elapsed >= Get(_incapUntil, p))
                    {
                        p.Status.Value = HPPlayer.StatusActive;
                        _incapUntil.Remove(p);
                        _grabbedBy.Remove(p);
                        p.GrabberObjectId.Value = -1;
                        _slowUntil[p] = _elapsed + SlowSeconds;
                    }
                    else p.StatusEndsIn.Value = (float)(Get(_incapUntil, p) - _elapsed);
                }
                if (p.Status.Value == HPPlayer.StatusActive && p.StatusEndsIn.Value != 0f)
                    p.StatusEndsIn.Value = 0f;

                bool slowed = _slowUntil.TryGetValue(p, out double su) && _elapsed < su;
                if (!slowed && _slowUntil.ContainsKey(p) && _elapsed >= su) _slowUntil.Remove(p);
                if (p.Slowed.Value != slowed) p.Slowed.Value = slowed;
            }
        }

        private void UpdateDazzle(double dt, List<HPPlayer> hunters, List<HPPlayer> bigfoots)
        {
            foreach (var bf in bigfoots)
            {
                bool aimed = false;
                foreach (var h in hunters)
                {
                    if (h.Status.Value != HPPlayer.StatusActive || !h.FlashOn.Value) continue;
                    if (ConeVisible(h, bf, DazzleRange, DazzleAimCos)) { aimed = true; break; }
                }

                double fill = aimed
                    ? System.Math.Min(DazzleFillSeconds, Get(_dazzleFill, bf) + dt)
                    : System.Math.Max(0, Get(_dazzleFill, bf) - dt * DazzleDecay);
                _dazzleFill[bf] = fill;
                if (fill >= DazzleFillSeconds) _dazzledUntil[bf] = _elapsed + DazzleSeconds;

                bool dazzled = _elapsed < Get(_dazzledUntil, bf);
                if (bf.Dazzled.Value != dazzled) bf.Dazzled.Value = dazzled;
            }
        }

        private void UpdateFilming(double dt, List<HPPlayer> hunters, List<HPPlayer> bigfoots)
        {
            foreach (var h in hunters)
            {
                if (h.Status.Value != HPPlayer.StatusActive) continue;
                bool gaining = _recording.Contains(h) && bigfoots.Exists(b => ConeVisible(h, b, FilmRange * Specialties.FilmRangeMul(h.Specialty.Value), FilmAimCos));

                if (gaining)
                {
                    float p = h.FilmProgress.Value + (float)(dt / FilmSeconds * Specialties.FilmProgressMul(h.Specialty.Value));
                    if (p >= 1f)
                    {
                        h.FilmProgress.Value = 0f;
                        // A finished clip is CARRIED, not banked — it counts for nothing until it's
                        // in the duffel. This is the whole risk loop: keep shooting, or walk it home.
                        h.CarriedFilm.Value++;
                    }
                    else h.FilmProgress.Value = p;
                }
                else if (h.FilmProgress.Value > 0f)
                {
                    h.FilmProgress.Value = Mathf.Max(0f, h.FilmProgress.Value - (float)(dt * FilmDecay));
                }
            }
        }

        /// <summary>Range + aim-cone (from replicated yaw) + line-of-sight — the filming/dazzle visibility check.</summary>
        private bool ConeVisible(HPPlayer from, HPPlayer to, double range, double aimCos)
        {
            double dx = to.transform.position.x - from.transform.position.x;
            double dz = to.transform.position.z - from.transform.position.z;
            double dist = System.Math.Sqrt(dx * dx + dz * dz);
            if (dist > range || dist < 1e-3) return false;
            double yaw = from.SimYawFromTransform();
            double dot = (dx / dist) * -System.Math.Sin(yaw) + (dz / dist) * -System.Math.Cos(yaw);
            if (dot < aimCos) return false;
            return !HollowPines.Sim.Collision.LineBlocked(_world.Colliders,
                new Vec2(from.transform.position.x, from.transform.position.z),
                new Vec2(to.transform.position.x, to.transform.position.z));
        }

        // ------------------------------------------------------------------ client-side sky/clock

        private void Update()
        {
            UpdateClientAudio();
            if (WorldBuilder.Instance == null) return;
            _displayTod = Mathf.Abs(TimeOfDay.Value - _displayTod) > 0.1f
                ? TimeOfDay.Value
                : Mathf.Lerp(_displayTod, TimeOfDay.Value, Time.deltaTime * 1.5f);
            // Night number drives the moon's phase and arc; the lobby/results sky holds night 1's
            // full moon, which is the brightest and reads best as a backdrop.
            WorldBuilder.Instance.SetTimeOfDay(
                MatchPhase.Value == PhasePlaying ? _displayTod : 0.05f,
                MatchPhase.Value == PhasePlaying ? NightNumber.Value : 1);
        }

        /// <summary>
        /// Client-side cues driven by replicated match state: a banked video, the night rolling over,
        /// and the win/loss sting. Purely observational — this never changes any state.
        /// </summary>
        private void UpdateClientAudio()
        {
            var audio = HPAudio.Instance;
            if (audio == null) return;

            if (VideosCaptured.Value > _lastVideos && MatchPhase.Value == PhasePlaying)
                audio.PlayOnce(HPAudio.VideoCaptured, 0.6f);
            _lastVideos = VideosCaptured.Value;

            if (NightNumber.Value != _lastNightHeard)
            {
                if (_lastNightHeard != 0 && MatchPhase.Value == PhasePlaying)
                    audio.PlayOnce(HPAudio.NightSting, 0.7f);
                _lastNightHeard = NightNumber.Value;
            }

            if (Winner.Value != _lastWinnerHeard)
            {
                _lastWinnerHeard = Winner.Value;
                var me = HPPlayer.Local;
                if (Winner.Value != WinnerNone && me != null)
                {
                    bool won = me.IsBigfoot ? Winner.Value == WinnerBigfoot : Winner.Value == WinnerHunters;
                    audio.PlayOnce(won ? HPAudio.Victory : HPAudio.Defeat, 0.7f);
                }
            }
        }

        /// <summary>"11:47 PM"-style clock — the night runs 8pm to 8am over timeOfDay 0..1.</summary>
        public static string ClockString(float t)
        {
            double hours = 20.0 + t * 12.0;
            int h = (int)hours % 24;
            int m = (int)((hours - System.Math.Floor(hours)) * 60);
            string ampm = h >= 12 ? "PM" : "AM";
            int h12 = h % 12; if (h12 == 0) h12 = 12;
            return $"{h12}:{m:00} {ampm}";
        }

        public static string PhaseName(float t)
        {
            if (t < 0.15f) return "dusk";
            if (t < 0.45f) return "nightfall";
            if (t < 0.75f) return "midnight";
            if (t < 0.95f) return "witching hour";
            return "dawn";
        }

        // ------------------------------------------------------------------ helpers

        private Esc CurrentEsc()
        {
            int i = Mathf.Clamp(NightNumber.Value, 1, Escalation.Length) - 1;
            return Escalation[i];
        }

        private static List<HPPlayer> LivePlayers()
        {
            var list = new List<HPPlayer>();
            foreach (var p in HPPlayer.All) if (p != null) list.Add(p);
            return list;
        }

        private static HPPlayer FindLive(int objectId)
        {
            foreach (var p in HPPlayer.All) if (p != null && p.ObjectId == objectId) return p;
            return null;
        }

        private static double Get(Dictionary<HPPlayer, double> map, HPPlayer key)
        {
            return map.TryGetValue(key, out double v) ? v : 0;
        }

        private static double Dist2(HPPlayer a, HPPlayer b)
        {
            double dx = a.transform.position.x - b.transform.position.x;
            double dz = a.transform.position.z - b.transform.position.z;
            return dx * dx + dz * dz;
        }
    }
}
