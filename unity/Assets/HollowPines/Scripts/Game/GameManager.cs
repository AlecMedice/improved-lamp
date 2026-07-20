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
        private const double CastStompRadius = 2.2;   // Bigfoot ruins a print by treading on it
        private const string CasterSpecialty = "analysis"; // Mara — see the note in HoldActionTarget

        // --- The evidence duffel (camp) ---
        // Proof is only ever WON here. Everything a searcher gathers is carried, unsafe, until they
        // walk it back and store it; the duffel itself is untouchable by Bigfoot, which makes the trip
        // home the risk rather than the bag. Accepts every evidence type, present and future.
        private const double DuffelRadius = 3.0;
        private const double DepositSeconds = 1.2; // brief, deliberate — not an instant tap

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
        [Tooltip("Real seconds per night (8pm -> 8am). 600 = the web build's pace; lower for quick tests.")]
        [SerializeField] private float _nightSeconds = 600f;

        // --- Replicated match state ---
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
        public readonly SyncVar<byte> Winner = new SyncVar<byte>(WinnerNone);
        public readonly SyncVar<float> EscSpeed = new SyncVar<float>(1f);
        public readonly SyncVar<float> EscBattery = new SyncVar<float>(1f);
        public readonly SyncVar<float> EscStamina = new SyncVar<float>(1f);
        public readonly SyncVar<float> RoarCooldownSec = new SyncVar<float>((float)RoarCooldown);

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
        private readonly Dictionary<HPPlayer, NetworkObject> _collectIntent = new Dictionary<HPPlayer, NetworkObject>();
        private readonly Dictionary<NetworkObject, double> _collectProgress = new Dictionary<NetworkObject, double>();
        private readonly Dictionary<HPPlayer, double> _revealedUntil = new Dictionary<HPPlayer, double>();
        private readonly Dictionary<HPPlayer, NetworkObject> _pingOwner = new Dictionary<HPPlayer, NetworkObject>();
        private readonly Dictionary<HPPlayer, double> _caveReadyAt = new Dictionary<HPPlayer, double>();
        private readonly System.Random _rng = new System.Random();
        private GameWorld _world;
        private float _displayTod; // client-side smoothed clock for the sky
        private int _lastVideos, _lastNightHeard;   // client-side audio edge detection
        private byte _lastWinnerHeard = WinnerNone;

        private void Awake()
        {
            Instance = this;
            _world = WorldBuilder.EnsureWorld();
        }

        public override void OnStartServer()
        {
            InstanceFinder.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
        }

        public override void OnStopServer()
        {
            if (InstanceFinder.SceneManager != null)
                InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        }

        public override void OnStartNetwork()
        {
            base.TimeManager.OnTick += OnTick;
        }

        public override void OnStopNetwork()
        {
            if (base.TimeManager != null) base.TimeManager.OnTick -= OnTick;
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
            _collectIntent.Clear();
            _collectProgress.Clear();
            _revealedUntil.Clear();
            EvidenceCollected.Value = 0;
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
            // A grab destroys everything this searcher was CARRYING — and only that. Proof already
            // stored in the duffel is untouchable, because the duffel is. This replaces the old
            // wipe-the-whole-team's-footage rule: the punishment is now proportional to how greedy
            // that one person was being, which is a decision they made, rather than a team-wide
            // reset nobody could influence.
            int lost = best.CarriedTotal;
            best.CarriedFilm.Value = 0;
            best.CarriedCasts.Value = 0;
            if (lost > 0) RpcProofLost(best.transform.position, lost);
        }

        // ------------------------------------------------------------------ casting tracks

        /// <summary>Mara declares intent to cast a specific print (held action, like revive).</summary>
        public void SetCollectTarget(HPPlayer p, int clueObjectId)
        {
            if (clueObjectId < 0) { _collectIntent.Remove(p); return; }
            foreach (var nob in _castablePrints)
            {
                if (nob != null && nob.ObjectId == clueObjectId) { _collectIntent[p] = nob; return; }
            }
            _collectIntent.Remove(p);
        }

        /// <summary>
        /// Drive every in-progress cast. Progress lives on the PRINT, not the caster, so if Mara is
        /// interrupted and comes back the work isn't lost outright — it bleeds off, it doesn't reset.
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
                if (p.Specialty.Value != CasterSpecialty) continue; // only Mara carries the casting kit
                if (Vector3.Distance(p.transform.position, nob.transform.position) > CastRadius) continue;

                double prog = (_collectProgress.TryGetValue(nob, out double v) ? v : 0) + dt;
                advanced.Add(nob);

                if (prog >= CastSeconds)
                {
                    finished.Add(nob);
                    _collectProgress.Remove(nob);
                }
                else
                {
                    _collectProgress[nob] = prog;
                    p.CollectProgress01.Value = (float)(prog / CastSeconds);
                }
            }

            foreach (NetworkObject nob in finished)
            {
                // Like footage: Mara now CARRIES the cast until she stores it in the duffel.
                foreach (var kv in _collectIntent)
                    if (kv.Value == nob && kv.Key != null) { kv.Key.CarriedCasts.Value++; break; }
                RpcCastTaken(nob.transform.position);
                RemoveCastablePrint(nob, despawn: true);

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

        /// <summary>Bigfoot ruins a workable print by treading on it — its own trail is its liability.</summary>
        private void StompPrintsUnderBigfoot(List<HPPlayer> bigfoots)
        {
            for (int i = _castablePrints.Count - 1; i >= 0; i--)
            {
                NetworkObject nob = _castablePrints[i];
                if (nob == null) { _castablePrints.RemoveAt(i); continue; }
                foreach (var bf in bigfoots)
                {
                    if (bf.Status.Value != HPPlayer.StatusActive) continue;
                    if (Vector3.Distance(bf.transform.position, nob.transform.position) > CastStompRadius) continue;
                    RpcPrintRuined(nob.transform.position);
                    RemoveCastablePrint(nob, despawn: true);
                    break;
                }
            }
        }

        /// <summary>Forget a print (and optionally despawn it). Clue expiry also routes through here.</summary>
        private void RemoveCastablePrint(NetworkObject nob, bool despawn)
        {
            _castablePrints.Remove(nob);
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

        [ObserversRpc]
        private void RpcPrintRuined(Vector3 at)
        {
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.EvidenceDestroyed, at, 0.55f, 20f);
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
            int stored = p.CarriedTotal;
            p.CarriedFilm.Value = 0;
            p.CarriedCasts.Value = 0;
            RpcDeposited(p.transform.position, stored);
        }

        [ObserversRpc]
        private void RpcProofLost(Vector3 at, int count)
        {
            HPHud.NotifyProofLost(count);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayAt(HPAudio.EvidenceDestroyed, at, 0.6f, 22f);
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
            StompPrintsUnderBigfoot(bigfoots);
            UpdateCasting(dt);
            UpdateRevives(dt, hunters);
            UpdateStatuses();
            UpdateDazzle(dt, hunters, bigfoots);
            UpdateFilming(dt, hunters, bigfoots);

            if (hunters.Count > 0)
            {
                // Proof is footage PLUS physical evidence — either path, or any mix, gets there.
                if (VideosCaptured.Value + EvidenceCollected.Value >= VideosRequired.Value) Winner.Value = WinnerHunters;
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
                _lastTrack[bf] = new Vec2(pos.x, pos.z);
            }
        }

        private void SpawnClue(byte ctype, double x, double z, float yawRad, bool castable = false)
        {
            while (_clues.Count >= MaxClues)
            {
                if (_clues[0].nob != null)
                {
                    _castablePrints.Remove(_clues[0].nob);
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

            if (!castable) return;
            // Newer workable prints override older ones, so the map never fills with them.
            while (_castablePrints.Count >= MaxCastablePrints)
            {
                NetworkObject oldest = _castablePrints[0];
                RemoveCastablePrint(oldest, despawn: true);
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
                    // A workable print goes cold with the rest of the trail — "available for a time".
                    _castablePrints.Remove(_clues[i].nob);
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
            WorldBuilder.Instance.SetTimeOfDay(MatchPhase.Value == PhasePlaying ? _displayTod : 0.05f);
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
