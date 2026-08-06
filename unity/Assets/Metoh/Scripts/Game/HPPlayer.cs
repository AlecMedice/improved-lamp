// The networked player — searcher or Yeti. Unity port of the web build's LocalPlayer +
// RemotePlayer, driven by the shared deterministic sim (Metoh.Sim.Movement.StepPlayer):
// the owner samples input, steps the sim at a fixed 20 Hz, and streams its transform
// (client-authoritative NetworkTransform for THIS milestone — host-side re-validation returns
// with the FishNet prediction phase, see docs/NETWORKING.md N3). The host stays authoritative
// for every gameplay OUTCOME: status, roar/grab, filming, dazzle, revive — all SyncVars here
// are written only by the server (GameManager).
//
// Sim convention: yaw in radians, forward = (-sin yaw, -cos yaw). Unity body rotation is
// yaw*Rad2Deg + 180 so transform.forward matches the sim's heading exactly.
using System.Collections.Generic;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Component.Transforming;
using Metoh.Sim;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Metoh.Game
{
    public class HPPlayer : NetworkBehaviour
    {
        public const byte RoleSearcher = 0;
        public const byte RoleYeti = 1;
        public const byte StatusActive = 0;
        public const byte StatusFrozen = 1;
        public const byte StatusIncap = 2;

        // --- Replicated state (server-written; see GameManager) ---
        public readonly SyncVar<string> PlayerName = new SyncVar<string>("");
        public readonly SyncVar<byte> Role = new SyncVar<byte>(RoleSearcher);
        public readonly SyncVar<byte> Status = new SyncVar<byte>(StatusActive);
        /// <summary>Seconds until a frozen/incapacitated status expires (HUD countdown; 0 while active).</summary>
        public readonly SyncVar<float> StatusEndsIn = new SyncVar<float>(0f);
        public readonly SyncVar<bool> Slowed = new SyncVar<bool>(false);
        public readonly SyncVar<bool> Dazzled = new SyncVar<bool>(false);
        public readonly SyncVar<bool> BeingRevived = new SyncVar<bool>(false);
        public readonly SyncVar<string> Specialty = new SyncVar<string>("");
        public readonly SyncVar<string> CharacterName = new SyncVar<string>("");
        public readonly SyncVar<bool> FlashOn = new SyncVar<bool>(false);
        /// <summary>Crouching. Replicated because the host suppresses Yeti's trail while it is.</summary>
        public readonly SyncVar<bool> Crouched = new SyncVar<bool>(false);
        public readonly SyncVar<float> Battery = new SyncVar<float>(100f);
        public readonly SyncVar<float> Stamina = new SyncVar<float>(100f);
        public readonly SyncVar<bool> Filming = new SyncVar<bool>(false);
        public readonly SyncVar<float> FilmProgress = new SyncVar<float>(0f);
        public readonly SyncVar<int> GrabberObjectId = new SyncVar<int>(-1);
        /// <summary>
        /// Seconds left on the haul while this player is being carried; 0 otherwise.
        ///
        /// Replicated because the timed carry's entire justification is that BOTH sides can read it —
        /// a fixed haul the team can time a rescue against. A timer nobody can see is just a shorter
        /// grab, so this is part of the mechanic, not a HUD nicety.
        /// </summary>
        public readonly SyncVar<float> CarryEndsIn = new SyncVar<float>(0f);
        public readonly SyncVar<bool> WantsYeti = new SyncVar<bool>(false);
        /// <summary>DEV: the persona this player asked to be dealt ("" = normal random deal).</summary>
        public readonly SyncVar<string> DevSpecialty = new SyncVar<string>("");
        /// <summary>Yeti only: seconds until the next roar is available (HUD cooldown).</summary>
        public readonly SyncVar<float> RoarReadyIn = new SyncVar<float>(0f);
        /// <summary>Incapacitated only: 0..1 revive progress a teammate has accrued on this player.</summary>
        public readonly SyncVar<float> ReviveProgress01 = new SyncVar<float>(0f);
        /// <summary>0..1 progress on the evidence this searcher is currently collecting.</summary>
        public readonly SyncVar<float> CollectProgress01 = new SyncVar<float>(0f);
        /// <summary>
        /// Proof this searcher is CARRYING and has not yet stored in the duffel — tapes, casts and
        /// hair counted separately for the HUD, but they behave identically: unsafe until deposited,
        /// and SPILLED on the ground as a recoverable pile if Yeti takes you (GameManager.TryGrab
        /// → SpawnProofPile). Any future evidence type joins these and needs no change to the deposit
        /// path (see GameManager.TryDeposit / CarriedTotal).
        /// </summary>
        public readonly SyncVar<int> CarriedFilm = new SyncVar<int>(0);
        public readonly SyncVar<int> CarriedCasts = new SyncVar<int>(0);
        public readonly SyncVar<int> CarriedHair = new SyncVar<int>(0);

        /// <summary>Everything unsaved on this player, whatever kind it is.</summary>
        public int CarriedTotal => CarriedFilm.Value + CarriedCasts.Value + CarriedHair.Value;

        // Match stats, for the between-nights recap. Searcher: proof BANKED into the duffel (the real
        // contribution — carried proof can still be spilled). Yeti: searchers TAKEN (incaps landed).
        public readonly SyncVar<int> StatBanked = new SyncVar<int>(0);
        public readonly SyncVar<int> StatIncaps = new SyncVar<int>(0);
        /// <summary>Per-night ability charges — Eli's flash, Sam's spare battery. 0 for everyone else.</summary>
        public readonly SyncVar<int> AbilityCharges = new SyncVar<int>(0);
        /// <summary>
        /// Seconds this searcher stays lit up on Yeti's screen after firing a camera flash —
        /// the cost side of Eli's ability. A countdown, so no shared clock is needed to read it.
        /// </summary>
        public readonly SyncVar<float> RevealedFor = new SyncVar<float>(0f);

        /// <summary>Every spawned player on this client (remote + local), for HUD/targeting scans.</summary>
        public static readonly List<HPPlayer> All = new List<HPPlayer>();
        public static HPPlayer Local { get; private set; }

        public bool IsYeti => Role.Value == RoleYeti;

        // --- Owner-side sim ---
        /// <summary>Longest frame the sim will step in one go — a hitch stalls you, never teleports you.</summary>
        private const float MaxStepDt = 0.1f;

        private PlayerSimState _sim;

        /// <summary>
        /// The live world. A PROPERTY, not a cached field: the forest is rebuilt when the host's
        /// replicated seed arrives, and a reference captured in OnStartClient would keep stepping
        /// this player against the throwaway default world's colliders.
        /// </summary>
        private GameWorld _world => WorldBuilder.EnsureWorld();
        private float _yaw;    // radians, sim convention
        private float _pitch;  // radians, camera only
        private bool _pressedJump, _pressedLeap, _pressedVault, _pressedMount;
        private float _caveReadyAt; // local cave-travel cooldown echo (the host owns the real gate)
        private float _lobbyCam01 = 1f; // 0 = lobby cinematic orbit, 1 = first-person (blended)
        private float _roarEchoUntil; // local roar cooldown echo, covers the RoarReadyIn round trip
        private float _vitalsTimer;
        private float _bobPhase, _bobOffset;
        private StepResult _lastStep;
        private bool _crouching;
        private float _stepTimer;                 // own-footstep cadence
        private byte _prevStatusHeard = StatusActive;
        private float _reviveTickTimer;
        private int _reviveTargetHeard = -1;      // who we were reviving last frame (for the success cue)
        private Vector3 _lastAudioPos;            // remote footsteps: ground covered since the last step
        private float _remoteStepDist;
        private bool _audioPosInit;
        private float _scentTimer;
        private bool _sentRecording, _lastFlashSent = false, _lastCrouchSent = false;
        private int _reviveTargetSent = -1;
        private int _collectTargetSent = -1;
        private float _depositHeld; // how long the store-at-duffel hold has been running
        private float _recoverHeld; // ...and the gather-up-a-spilled-pack hold
        private Camera _cam;

        // --- Visuals ---
        private Transform _visualRoot;
        private Material _bodyMat, _gearMat, _eyeMat;
        private Light _flashlight;
        private Renderer _recDot;
        private byte _builtRole = 255;
        private Color _baseBodyColor;
        private Avatar _avatar;
        private TorchBeam _beam;
        // Animation drive. Speed and yaw rate are measured from the TRANSFORM rather than read from the
        // sim, so the owner and a remote (whose transform is interpolated by FishNet) go down one code
        // path and a remote's gait matches the motion actually being drawn rather than the motion the
        // owner reported some milliseconds ago.
        private Vector3 _lastVisPos;
        private float _lastVisYaw, _visSpeed, _visYawRate;
        private bool _visPosInit;

        // --- Senses overlay (V, Yeti, client-only render toggle like the web build) ---
        public static bool SensesOn;
        /// <summary>Yeti's own recent positions — its scent trail for the senses overlay.</summary>
        public static readonly List<(Vector3 pos, float time)> ScentTrail = new List<(Vector3, float)>();
        public const float ScentLifetime = 30f;

        private static readonly Dictionary<string, int> SpecialtyColors = new Dictionary<string, int>
        {
            { "analysis", 0x7a9ac2 }, { "photo", 0xc2b27a }, { "tracking", 0x8ac28a },
            { "sound", 0xb28ac2 }, { "endurance", 0xc28a7a }, { "", 0x9aa2aa },
        };

        // ------------------------------------------------------------------ lifecycle

        public override void OnStartNetwork()
        {
            // Runs once whether we're server, client, or listen-host — the registry GameManager scans.
            if (!All.Contains(this)) All.Add(this);
        }

        public override void OnStopNetwork()
        {
            All.Remove(this);
        }

        public override void OnStartClient()
        {
            WorldBuilder.EnsureWorld();
            BuildVisuals();
            Role.OnChange += OnRoleChanged;

            if (base.IsOwner)
            {
                Local = this;
                HPHud.PauseOpen = false; // fresh session, no stale pause
                _sim = NewSimState(transform.position, IsYeti);
                _yaw = SimYawFromTransform();
                _cam = Camera.main;
                if (_cam != null)
                {
                    _cam.transform.SetParent(transform, false);
                    _cam.transform.localPosition = new Vector3(0f, (float)_sim.CurEye, 0f);
                    _cam.transform.localRotation = Quaternion.identity;
                    _cam.nearClipPlane = 0.08f;
                    _cam.farClipPlane = 900f;
                    _defaultFov = _cam.fieldOfView; // remembered so binocular zoom can restore it
                }
                // Don't grab the mouse in the camp lobby — its panel needs clicking. RMB captures
                // it for walking (HandleCursor); the match-start teleport locks it for real.
                if (!string.IsNullOrWhiteSpace(HPSettings.PlayerName))
                    ServerSetName(HPSettings.PlayerName);
                // Chosen on the title screen, before we connected — send it once on spawn.
                if (!string.IsNullOrEmpty(HPSettings.DevSpecialty))
                    ServerSetDevSpecialty(HPSettings.DevSpecialty);
            }
        }

        public override void OnStopClient()
        {
            Role.OnChange -= OnRoleChanged;
            if (Local == this) Local = null;
            if (base.IsOwner && _cam != null) _cam.transform.SetParent(null, true);
        }

        /// <summary>
        /// Fires on the host when this player despawns — which, for a human, means their client
        /// disconnected. `this` is still valid here, so it's the timing-safe place to let the manager
        /// scrub every server-side reference to us (grab, revive, ping, collect…) and decide whether
        /// the match is still playable. A bot despawns too, but bots leave via the match ending, not
        /// a disconnect, so forgetting one is harmless.
        /// </summary>
        public override void OnStopServer()
        {
            GameManager.Instance?.ServerForgetPlayer(this);
        }

        private void OnRoleChanged(byte prev, byte next, bool asServer)
        {
            if (!asServer) BuildVisuals();
            if (base.IsOwner && _sim != null)
            {
                _sim.IsYeti = next == RoleYeti;
                _sim.EyeHeight = next == RoleYeti ? 2.4 : Sim.Player.EyeHeight;
                if (PostFX.Instance != null) PostFX.Instance.SetYetiVision(next == RoleYeti);
                // The other half of Yeti's vision trade: brighter up close, murkier far away.
                WorldBuilder.FogMul = next == RoleYeti ? 1.35f : 1f;
                if (WorldBuilder.Instance != null) WorldBuilder.Instance.InvalidatePalette();
                SensesOn = false;
                ScentTrail.Clear();
            }
        }

        private static PlayerSimState NewSimState(Vector3 pos, bool yeti)
        {
            var world = WorldBuilder.EnsureWorld();
            double gy = world.GetHeight(pos.x, pos.z);
            return new PlayerSimState
            {
                X = pos.x, Z = pos.z, FeetY = gy, GroundY = gy, Grounded = true,
                Stamina = 100, Battery = 100, EyeHeight = yeti ? 2.4 : Sim.Player.EyeHeight,
                CurEye = yeti ? 2.4 : Sim.Player.EyeHeight, IsYeti = yeti,
            };
        }

        /// <summary>Server → owner: hard-place the player (spawn, match start). Resets the local sim.</summary>
        [FishNet.Object.TargetRpc]
        public void TargetTeleport(FishNet.Connection.NetworkConnection conn, Vector3 pos, float yawRad)
        {
            _sim = NewSimState(pos, IsYeti);
            _sim.Stamina = Stamina.Value;
            _yaw = yawRad;
            _pitch = 0f;
            transform.position = new Vector3(pos.x, (float)_sim.FeetY, pos.z);
            ApplyBodyYaw();
            MapView.Close(); // cave travel is picked ON the map — drop the player straight back into the forest
            Cursor.lockState = CursorLockMode.Locked; // the match (or a rematch lobby) placed us — take the mouse
            Cursor.visible = false;
        }

        // ------------------------------------------------------------------ owner loop

        private void Update()
        {
            if (base.IsOwner) OwnerUpdate();
            UpdateSharedVisuals();
        }

        private void OwnerUpdate()
        {
            HandleCursor();

            bool playing = GameManager.Instance != null && GameManager.Instance.MatchPhase.Value == GameManager.PhasePlaying;

            // Camp lobby: keep the title card's drifting camera running instead of parking the player
            // in a motionless first-person shot. Holding right-mouse takes control (the same gesture
            // that grabs the pointer); releasing hands the shot back to the cinematic.
            if (UpdateLobbyCinematic(playing)) return;

            HandleLook();
            // The between-nights recap freezes the world: no movement, no abilities, just the stats
            // card. Look still works so you're not staring at a locked view.
            bool intermission = GameManager.Instance != null && GameManager.Instance.IntermissionActive;
            bool canAct = Status.Value == StatusActive && !intermission;

            // Dragged while incapacitated: mirror the grabber so the client-auth transform follows it.
            if (Status.Value == StatusIncap && GrabberObjectId.Value >= 0)
            {
                var grabber = FindByObjectId(GrabberObjectId.Value);
                if (grabber != null)
                {
                    // Trail BEHIND the grabber, never onto it. Homing on grabber.transform.position —
                    // what this did originally — parked both bodies at identical coordinates, and
                    // since players carry no colliders and movement is the shared sim, nothing pushes
                    // them apart again: on release you were left standing inside the Yeti, which
                    // reads as both of you being stuck.
                    Vector3 goal = GameManager.DragTrailPoint(grabber);
                    transform.position = Vector3.Lerp(transform.position, goal, Time.deltaTime * 10f);
                    SyncSimTo(transform.position);
                }
            }
            else if (canAct)
            {
                if (_onLadder) UpdateLadder();
                else StepSim();
            }

            if (canAct) HandleAbilities(playing);
            if (canAct && !IsYeti) UpdateBinoculars(); else EndGlassing();
            PushVitals();
            UpdateBob();
            UpdateScent();
            UpdateOwnFootsteps();
            UpdateStatusStings();

            // Camera posing happens in LateUpdate — see ApplyCameraPose.
        }

        /// <summary>
        /// The camera's pose is written HERE, in LateUpdate, and nowhere else.
        ///
        /// Two bugs came out of doing it inside Update. First, whatever ran later in the frame won
        /// — and in the lobby that was the cinematic, which re-posed the camera every frame and
        /// slerped your aim back toward its shot, so looking around while walking fought a moving
        /// target and felt like the view was stuck on one axis. Second, the lobby pose was built
        /// from the PREVIOUS frame's yaw/pitch, because it ran before HandleLook.
        ///
        /// LateUpdate fixes both: it is after all input and simulation, so the pose is always built
        /// from this frame's values and nothing can overwrite it afterwards. This is the standard
        /// place to drive a camera and it should have been here from the start.
        /// </summary>
        private void LateUpdate()
        {
            if (!base.IsOwner || _cam == null) return;

            if (_lobbyPosing)
            {
                ApplyLobbyCameraPose();
            }
            else if (_cam.transform.parent == transform)
            {
                // Local-space write, valid only while parented — un-parented, a localPosition write
                // would fling the camera to the world origin.
                _cam.transform.localPosition = new Vector3(0f, (float)_sim.CurEye + _bobOffset, 0f);
                _cam.transform.localRotation = Quaternion.Euler(_pitch * Mathf.Rad2Deg, 0f, 0f);
            }
        }

        /// <summary>
        /// The lobby shot. Once the player has fully taken control the cinematic is NOT evaluated at
        /// all: calling it and slerping against it means every mouse movement is competing with a
        /// camera that is still flying its own path, which is precisely what made the look feel
        /// stuck. It only runs while the blend is actually in progress.
        /// </summary>
        private void ApplyLobbyCameraPose()
        {
            Vector3 fpPos = transform.position + Vector3.up * (float)_sim.CurEye;
            Quaternion fpRot = Quaternion.Euler(_pitch * Mathf.Rad2Deg, _yaw * Mathf.Rad2Deg + 180f, 0f);

            if (_lobbyCam01 >= 0.999f)
            {
                _cam.transform.SetPositionAndRotation(fpPos, fpRot);
                return;
            }

            TitleMenu.OrbitCamp(_cam);
            Vector3 orbitPos = _cam.transform.position;
            Quaternion orbitRot = _cam.transform.rotation;
            float k = Mathf.SmoothStep(0f, 1f, _lobbyCam01);
            _cam.transform.SetPositionAndRotation(
                Vector3.Lerp(orbitPos, fpPos, k), Quaternion.Slerp(orbitRot, fpRot, k));
        }

        /// <summary>True while the lobby cinematic owns the camera; read by LateUpdate's posing.</summary>
        private bool _lobbyPosing;

        /// <summary>
        /// While waiting in the camp lobby, let the camera fly the title-card orbit. Returns true if
        /// the cinematic is driving this frame (so the owner loop skips look/move/abilities entirely).
        /// The camera is un-parented for the orbit and re-parented the moment control is taken back or
        /// the match starts, which is also what makes the hand-off to first-person seamless.
        /// This decides STATE only — LateUpdate writes the pose.
        /// </summary>

        private bool UpdateLobbyCinematic(bool playing)
        {
            if (_cam == null) { _lobbyPosing = false; return false; }

            bool wantsControl = false;
#if ENABLE_INPUT_SYSTEM
            wantsControl = Mouse.current != null && Mouse.current.rightButton.isPressed;
#endif
            bool inLobby = !playing && !HPHud.PauseOpen && !MapView.IsOpen;

            if (!inLobby)
            {
                // Match (or a menu) owns the camera: re-attach and hand back to the normal path.
                _lobbyCam01 = 1f;
                _lobbyPosing = false;
                if (_cam.transform.parent != transform)
                {
                    _cam.transform.SetParent(transform, false);
                    _cam.transform.localPosition = new Vector3(0f, (float)_sim.CurEye, 0f);
                    _cam.transform.localRotation = Quaternion.Euler(_pitch * Mathf.Rad2Deg, 0f, 0f);
                }
                return false;
            }

            // Blend between the drifting shot and first-person rather than cutting. A hard cut to a
            // stationary first-person view is indistinguishable from a frozen image — the movement IS
            // the feedback that you just took control.
            float target = wantsControl ? 1f : 0f;
            _lobbyCam01 = Mathf.MoveTowards(_lobbyCam01, target, Time.deltaTime * 2.6f);

            // Drive the camera in world space for the whole blend; parenting would snap it.
            if (_cam.transform.parent != null) _cam.transform.SetParent(null, true);
            _lobbyPosing = true; // LateUpdate does the actual posing, after this frame's look input

            // Holding RMB means you're driving: fall through to the normal look/move path.
            return !wantsControl;
        }

        /// <summary>Head-bob from the sim's step result — same constants as the web LocalPlayer.</summary>
        private void UpdateBob()
        {
            bool bobbing = _lastStep.Moving && _sim.Grounded;
            float freq = (float)(_lastStep.Sprinting ? Sim.Player.BobFreqSprint : Sim.Player.BobFreqWalk);
            float amp = (float)(_lastStep.Sprinting ? Sim.Player.BobAmpSprint : Sim.Player.BobAmpWalk);
            if (bobbing)
            {
                _bobPhase += Time.deltaTime * freq * Mathf.PI * 2f;
                _bobOffset = Mathf.Sin(_bobPhase) * amp;
            }
            else
            {
                _bobOffset = Mathf.Lerp(_bobOffset, 0f, Time.deltaTime * 8f);
            }
        }

        /// <summary>
        /// Own footsteps: only on the ground, with a slower/quieter cadence while crouching (stealth).
        /// Wren treads quietly, so her own steps are halved too (Specialties.FootstepVolumeMul).
        /// </summary>
        private void UpdateOwnFootsteps()
        {
            var audio = HPAudio.Instance;
            if (audio == null) return;
            // Crouching is silent for BOTH roles — the same rule that suppresses Yeti's tracks.
            // Half speed buys you leaving no trace at all, in prints or in sound.
            if (_crouching) { _stepTimer = 0f; return; }
            if (!_lastStep.Moving || !_sim.Grounded) { _stepTimer = 0f; return; }

            _stepTimer -= Time.deltaTime;
            if (_stepTimer > 0f) return;
            var w = WorldBuilder.World;
            bool deepSnow = !IsYeti && w != null
                && Movement.DeepSnowDepth(w, transform.position.x, transform.position.z) > 0.35;
            audio.PlayFootstep(_lastStep.Sprinting, IsYeti, (float)Specialties.FootstepVolumeMul(Specialty.Value), deepSnow);
            _stepTimer = (float)(_lastStep.Sprinting
                ? Sim.Player.StepIntervalSprint
                : Sim.Player.StepIntervalWalk * (_crouching ? 1.6 : 1));
        }

        /// <summary>Stings when the server changes OUR status: roared (frozen) or taken (incapacitated).</summary>
        private void UpdateStatusStings()
        {
            if (Status.Value == _prevStatusHeard) return;
            _prevStatusHeard = Status.Value;
            var audio = HPAudio.Instance;
            if (audio == null) return;
            if (Status.Value == StatusFrozen) audio.PlayOnce(HPAudio.FreezeSting, 0.85f);
            else if (Status.Value == StatusIncap) audio.PlayOnce(HPAudio.GrabImpact, 0.95f);
        }

        /// <summary>Yeti leaves a scent memory of where it's been (fuel for the senses overlay).</summary>
        private void UpdateScent()
        {
            if (!IsYeti) return;
            _scentTimer += Time.deltaTime;
            if (_scentTimer < 1.5f) return;
            _scentTimer = 0f;
            ScentTrail.Add((transform.position, Time.time));
            while (ScentTrail.Count > 0 && Time.time - ScentTrail[0].time > ScentLifetime) ScentTrail.RemoveAt(0);
            while (ScentTrail.Count > 24) ScentTrail.RemoveAt(0);
        }

        private void HandleCursor()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                // Esc backs out of the map first; otherwise it's the pause overlay (the web gear menu).
                if (MapView.IsOpen)
                {
                    MapView.Close();
                }
                else if (HPHud.BriefingOpen)
                {
                    HPHud.DismissBriefing(); // Esc skips the card rather than stacking a pause on it
                }
                else
                {
                    HPHud.PauseOpen = !HPHud.PauseOpen;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            if (mouse == null || HPHud.PauseOpen || MapView.IsOpen || HPHud.BriefingOpen) return;

            bool playingPhase = GameManager.Instance != null &&
                                GameManager.Instance.MatchPhase.Value == GameManager.PhasePlaying;

            if (playingPhase)
            {
                // In a match the mouse belongs to the game; either button recaptures it after Esc.
                if (Cursor.lockState != CursorLockMode.Locked &&
                    (mouse.rightButton.wasPressedThisFrame || mouse.leftButton.wasPressedThisFrame))
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
                return;
            }

            // LOBBY: hold RMB to look around, release to get the pointer back. A press-to-LOCK rule
            // here trapped players behind their own UI — the lobby panel needs clicking, and Esc (the
            // only way out) opens the pause menu, which is not an obvious thing to reach for.
            bool wantLook = mouse.rightButton.isPressed;
            if (wantLook && Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (!wantLook && Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
#endif
        }

        // --- look diagnostics (F3 overlay) -----------------------------------------
        // Deliberately raw. The point is to tell apart three failures that look identical in play:
        //   * the mouse delta itself losing an axis        -> DbgLookDelta stops changing
        //   * _pitch updating but the camera not following -> DbgPitchDeg moves, DbgCamPitchDeg doesn't
        //   * _yaw updating but the body not following     -> DbgYawDeg moves, DbgBodyYawDeg doesn't
        /// <summary>Raw mouse delta this frame, captured before any gating.</summary>
        public Vector2 DbgLookDelta { get; private set; }
        public float DbgYawDeg => _yaw * Mathf.Rad2Deg;
        public float DbgPitchDeg => _pitch * Mathf.Rad2Deg;
        public float DbgBodyYawDeg => transform.eulerAngles.y;
        public float DbgCamPitchDeg => _cam == null ? 0f : _cam.transform.localEulerAngles.x;
        public string DbgCamParent =>
            _cam == null ? "none" : _cam.transform.parent == null ? "UNPARENTED" : _cam.transform.parent.name;
        public bool DbgLookGated { get; private set; }

        private void HandleLook()
        {
#if ENABLE_INPUT_SYSTEM
            // Capture the delta BEFORE the gate, so the overlay can distinguish "the mouse reported
            // nothing" from "we chose to ignore it" — those need completely different fixes.
            if (Mouse.current != null) DbgLookDelta = Mouse.current.delta.ReadValue();
            DbgLookGated = Cursor.lockState != CursorLockMode.Locked;

            if (Cursor.lockState != CursorLockMode.Locked || Mouse.current == null) return;
            Vector2 d = Mouse.current.delta.ReadValue();
            float sens = (float)Sim.Player.MouseSensitivity * HPSettings.MouseSensMul;
            _yaw += d.x * sens;
            _pitch = Mathf.Clamp(_pitch - d.y * sens, -1.45f, 1.45f);
            ApplyBodyYaw();
#endif
        }

        private void ApplyBodyYaw()
        {
            transform.rotation = Quaternion.Euler(0f, _yaw * Mathf.Rad2Deg + 180f, 0f);
        }

        private void StepSim()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            // Movement stays live while the map is up (the map frees the cursor so it can be clicked,
            // but a game about being chased must never stop you dead to read it). Mouse LOOK is still
            // gated on the locked cursor, so with the map open you keep walking on your current
            // heading. The pause menu is the one overlay that genuinely halts everything.
            bool locked = Cursor.lockState == CursorLockMode.Locked ||
                          (MapView.IsOpen && !HPHud.PauseOpen);

            if (locked && HPKeybinds.Pressed(kb, HPAction.Jump))
            {
                _pressedJump = true;
                _pressedMount = true; // consumed by the ladder-mount check after the step
                if (IsYeti) _pressedLeap = true; else _pressedVault = true;
            }

            // Step ONCE PER FRAME with the real frame delta — exactly what the web build's LocalPlayer
            // does. This used to run a fixed 20 Hz step and render an interpolation between the last
            // two sim states, which put the camera a whole step (50 ms) in the past and made motion
            // visibly steppy whenever the frame rate dipped. StepPlayer is pure and takes dt as an
            // input, so variable-dt stepping is safe; when host-authoritative prediction lands
            // (NETWORKING.md N3) FishNet's tick loop owns the cadence again and this reverts.
            {
                float frameDt = Mathf.Min(Time.deltaTime, MaxStepDt); // clamp hitches, don't teleport
                var input = new MoveInput
                {
                    W = locked && kb.wKey.isPressed,
                    S = locked && kb.sKey.isPressed,
                    // A/D are deliberately CROSSED. The shared sim is the web build's, whose (x,z)
                    // basis is Three.js right-handed: right = (cos yaw, -sin yaw). Unity's XZ plane
                    // is mirrored (left-handed), so a body rotated to match the sim's FORWARD
                    // (yaw*Rad2Deg + 180, see ApplyBodyYaw) has the OPPOSITE right — Unity's right is
                    // (-cos yaw, sin yaw). Feeding the strafe crossed reconciles the two frames at the
                    // single point where it matters; forward, aim cones and positions all still agree.
                    // (The map mirrors its x axis for the same reason — see MapView.ToMap.)
                    A = locked && kb.dKey.isPressed,
                    D = locked && kb.aKey.isPressed,
                    Yaw = _yaw,
                    Jump = _pressedJump,
                    Leap = _pressedLeap,
                    Vault = _pressedVault,
                    Climb = locked && IsYeti && HPKeybinds.Down(kb, HPAction.Jump),
                    // Yeti sprints too (it replaced the charge burst): the sim's YetiSpeedMul
                    // makes its sprint outrun a searcher's outright, and sprinting drains its stamina
                    // the same way — which is what finally gives Yeti's stamina bar a purpose.
                    Sprint = locked && HPKeybinds.Down(kb, HPAction.Sprint),
                    Crouch = locked && HPKeybinds.Down(kb, HPAction.Crouch),
                    Dt = frameDt,
                };
                _crouching = input.Crouch;
                _pressedJump = _pressedLeap = _pressedVault = false;

                _lastStep = Movement.StepPlayer(_sim, input, _world, CurrentModifiers());
                transform.position = new Vector3((float)_sim.X, (float)_sim.FeetY, (float)_sim.Z);

                // Mount the tower ladder: a searcher pressing jump while alongside the ladder line
                // starts climbing instead of hopping. (Yeti scales the tower with its own climb.)
                if (_pressedMount && !IsYeti && NearLadder()) EnterLadder();
                _pressedMount = false;
            }
#endif
        }

        // --- tower ladder (client-side; no parity change — the tower is already climbable in the sim,
        //     so the sim holds the searcher on the platform once the ladder lifts them to the top) ---
        private bool _onLadder;

        /// <summary>Within mount reach of the ladder line, and inside its vertical span.</summary>
        private bool NearLadder()
        {
            Vector2 me = new Vector2(transform.position.x, transform.position.z);
            if ((me - WorldBuilder.LadderXZ).sqrMagnitude > WorldBuilder.LadderReach * WorldBuilder.LadderReach) return false;
            float y = transform.position.y;
            return y >= WorldBuilder.LadderBottomY - 1f && y <= WorldBuilder.LadderTopY + 0.5f;
        }

        private void EnterLadder()
        {
            _onLadder = true;
            _sim.Vy = 0;
            _sim.Grounded = false;
            _sim.X = WorldBuilder.LadderXZ.x;
            _sim.Z = WorldBuilder.LadderXZ.y;
            // Face into the tower so W (up the rungs) reads naturally.
            Vector2 intoTower = new Vector2((float)WorldData.Lookout.X, (float)WorldData.Lookout.Z) - WorldBuilder.LadderXZ;
            ServerBotFaceless(intoTower.x, intoTower.y);
        }

        /// <summary>
        /// One frame on the ladder. W/S drive vertical travel (pinned to the ladder line); reaching
        /// the top steps you onto the platform, where the sim's climbable-top logic takes over;
        /// reaching the bottom drops you back to the ground; jump hops off at any height.
        /// </summary>
        private void UpdateLadder()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            if (kb == null) return;
            bool locked = Cursor.lockState == CursorLockMode.Locked;
            float dt = Mathf.Min(Time.deltaTime, MaxStepDt);

            float dir = 0f;
            if (locked && kb.wKey.isPressed) dir += 1f;
            if (locked && kb.sKey.isPressed) dir -= 1f;

            _sim.FeetY += dir * Sim.Player.ClimbSpeed * dt;
            _sim.X = WorldBuilder.LadderXZ.x;
            _sim.Z = WorldBuilder.LadderXZ.y;
            _sim.GroundY = WorldBuilder.LadderBottomY;
            _sim.Grounded = false;
            _sim.Vy = 0;

            bool hopOff = locked && HPKeybinds.Pressed(kb, HPAction.Jump);

            if (_sim.FeetY >= WorldBuilder.LadderTopY)
            {
                // Onto the deck: nudge toward the tower centre so we're over the footprint, where the
                // sim raises groundY to the platform and holds us there.
                _sim.FeetY = WorldBuilder.LadderTopY;
                Vector2 c = new Vector2((float)WorldData.Lookout.X, (float)WorldData.Lookout.Z);
                Vector2 step = (c - WorldBuilder.LadderXZ).normalized * 0.9f;
                _sim.X += step.x;
                _sim.Z += step.y;
                _sim.GroundY = WorldBuilder.LadderTopY;
                _sim.Grounded = true;
                ExitLadder();
            }
            else if (_sim.FeetY <= WorldBuilder.LadderBottomY || hopOff)
            {
                _sim.FeetY = System.Math.Max(_sim.FeetY, WorldBuilder.LadderBottomY);
                _sim.GroundY = WorldBuilder.LadderBottomY;
                _sim.Grounded = !hopOff; // a hop leaves you briefly airborne, the sim resolves the landing
                ExitLadder();
            }

            transform.position = new Vector3((float)_sim.X, (float)_sim.FeetY, (float)_sim.Z);
#endif
        }

        private void ExitLadder()
        {
            _onLadder = false;
            EndGlassing(); // can't glass mid-climb; drop it if we somehow were
        }

        // --- binoculars (searcher, on the lookout platform only) --------------------
        private float _defaultFov = 60f;
        private bool _glassing;
        private const float GlassFov = 26f; // zoomed-in field of view while glassing

        /// <summary>On the tower deck: within the footprint and at platform height.</summary>
        private bool OnLookoutPlatform()
        {
            Vector2 towerXZ = new Vector2((float)WorldData.Lookout.X, (float)WorldData.Lookout.Z);
            Vector2 me = new Vector2(transform.position.x, transform.position.z);
            if ((me - towerXZ).sqrMagnitude > WorldData.Lookout.R * WorldData.Lookout.R) return false;
            return transform.position.y >= WorldBuilder.LadderTopY - 0.4f;
        }

        /// <summary>
        /// Hold the binocular key while standing on the lookout to glass the forest: the view zooms and
        /// switches to image-intensified night vision (PostFX). Only up the tower — that's the whole
        /// point of climbing it. Filming still needs the normal RMB; this is a separate scouting tool.
        /// </summary>
        private void UpdateBinoculars()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            bool want = kb != null && !IsYeti && Status.Value == StatusActive && !_onLadder &&
                        Cursor.lockState == CursorLockMode.Locked &&
                        OnLookoutPlatform() && HPKeybinds.Down(kb, HPAction.Binoculars);

            if (want && !_glassing) BeginGlassing();
            else if (!want && _glassing) EndGlassing();
#endif
        }

        private void BeginGlassing()
        {
            _glassing = true;
            if (_cam != null) _cam.fieldOfView = GlassFov;
            if (PostFX.Instance != null) PostFX.Instance.SetNightVision(true);
        }

        private void EndGlassing()
        {
            if (!_glassing) return;
            _glassing = false;
            if (_cam != null) _cam.fieldOfView = _defaultFov;
            if (PostFX.Instance != null) PostFX.Instance.SetNightVision(false);
        }

        /// <summary>Face a world XZ direction (yaw only). Named apart from ServerBotFace so it's clearly
        /// an owner-side helper, but the maths is identical: sim forward is (-sin yaw, -cos yaw).</summary>
        private void ServerBotFaceless(float dx, float dz)
        {
            if (dx * dx + dz * dz < 1e-6f) return;
            _yaw = Mathf.Atan2(-dx, -dz);
        }

        private StepModifiers CurrentModifiers()
        {
            var gm = GameManager.Instance;
            float esc = gm != null ? gm.EscSpeed.Value : 1f;
            double speedMul = 1;
            if (IsYeti) speedMul = esc;               // per-night escalation only
            else if (Slowed.Value) speedMul = Sim.Player.SlowFactor;

            // DEV (F3): the CPU Yeti's speed lever. Applied here, inside the modifiers the shared sim
            // already takes, so a slowed bot is slowed by the sim rather than by a second movement
            // path that could drift from it. Only ever touches a bot — a human Yeti is unaffected.
            if (IsYeti && _isBot) speedMul *= YetiBot.SpeedMul;

            return new StepModifiers
            {
                SpeedMul = speedMul,
                BatteryDrainMul = gm != null ? gm.EscBattery.Value : 1f,
                StaminaDrainMul = (gm != null ? gm.EscStamina.Value : 1f) * Specialties.StaminaDrainMul(Specialty.Value),
                StaminaMax = Specialties.StaminaMax(Specialty.Value),
            };
        }

        private void HandleAbilities(bool playing)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null || Cursor.lockState != CursorLockMode.Locked) return;

            var audio = HPAudio.Instance;
            if (IsYeti)
            {
                // Roar is gated CLIENT-SIDE as well as server-side (the web build's tryRoar does the
                // same). Without this the request fires on every click: the server correctly refuses
                // it, but we'd already have played the roar sound locally — so it *sounded* like an
                // uncooldowned roar. RoarReadyIn is the server's truth, replicated; _roarEchoUntil
                // covers the round-trip before that SyncVar comes back.
                if (playing && mouse.rightButton.wasPressedThisFrame && CanRoar())
                {
                    float cd = GameManager.Instance != null ? GameManager.Instance.RoarCooldownSec.Value : 25f;
                    _roarEchoUntil = Time.time + cd;
                    ServerRoar();
                    if (audio != null) audio.PlayOnce(HPAudio.Roar, 0.9f); // our own roar, up close
                }
                if (playing && mouse.leftButton.wasPressedThisFrame)
                {
                    ServerGrab();
                    if (audio != null) audio.PlayOnce(HPAudio.GrabImpact, 0.5f); // the swing
                }
                if (HPKeybinds.Pressed(kb, HPAction.Senses)) SensesOn = !SensesOn; // client-only render toggle
            }
            else
            {
                if (HPKeybinds.Pressed(kb, HPAction.Flashlight) && _sim.Battery > 0)
                {
                    _sim.FlashlightOn = !_sim.FlashlightOn;
                    if (audio != null) audio.PlayOnce(HPAudio.FlashlightClick, 0.35f);
                }

                bool wantRecord = playing && mouse.rightButton.isPressed;
                if (wantRecord != _sentRecording)
                {
                    _sentRecording = wantRecord;
                    ServerSetRecording(wantRecord);
                }

                // Wren (Tracking) drops a team-visible trail marker; the server re-validates the specialty.
                if (playing && HPKeybinds.Pressed(kb, HPAction.Mark) && Specialty.Value == "tracking")
                {
                    ServerMark();
                    if (audio != null) audio.PlayOnce(HPAudio.PingDrop, 0.4f);
                }

                // Eli's camera flash — an aimed burst that stuns Yeti and paints Eli for it.
                if (playing && HPKeybinds.Pressed(kb, HPAction.Flash) &&
                    Specialty.Value == "photo" && AbilityCharges.Value > 0)
                    ServerFlash();

                // Hold-E resolves to ONE of three actions. Priority is life-critical first, and
                // HoldActionTarget() is shared with the HUD prompt so what you're told is exactly
                // what will happen — never a guess that disagrees with the server.
                int reviveTarget = -1, collectTarget = -1, batteryTarget = -1, recoverTarget = -1;
                bool depositing = false;
                if (playing && HPKeybinds.Down(kb, HPAction.Revive))
                {
                    var hold = HoldActionTarget();
                    reviveTarget = hold.Kind == HoldAction.Revive ? hold.ObjectId : -1;
                    collectTarget = hold.Kind == HoldAction.Collect ? hold.ObjectId : -1;
                    batteryTarget = hold.Kind == HoldAction.Battery ? hold.ObjectId : -1;
                    recoverTarget = hold.Kind == HoldAction.Recover ? hold.ObjectId : -1;
                    depositing = hold.Kind == HoldAction.Deposit;
                }

                // Storing takes a beat of standing at the bag, so it can't be tapped mid-sprint.
                _depositHeld = depositing ? _depositHeld + Time.deltaTime : 0f;
                if (_depositHeld >= GameManager.DepositSeconds) { _depositHeld = 0f; ServerDeposit(); }

                // Gathering up a spilled pack is the same shape — a beat of standing still, which is
                // exactly the beat Yeti is waiting for if it chose to guard the spill.
                _recoverHeld = recoverTarget >= 0 ? _recoverHeld + Time.deltaTime : 0f;
                if (_recoverHeld >= GameManager.PileRecoverSeconds) { _recoverHeld = 0f; ServerRecoverPile(recoverTarget); }

                if (reviveTarget != _reviveTargetSent)
                {
                    _reviveTargetSent = reviveTarget;
                    ServerSetReviveTarget(reviveTarget);
                }
                if (collectTarget != _collectTargetSent)
                {
                    _collectTargetSent = collectTarget;
                    ServerSetCollectTarget(collectTarget);
                }
                // The battery hand-off is instantaneous, not a channel — fire once per key press.
                if (batteryTarget >= 0 && HPKeybinds.Pressed(kb, HPAction.Revive))
                    ServerBatteryGift(batteryTarget);

                UpdateReviveAudio(reviveTarget, audio);
            }
#endif
        }

        /// <summary>
        /// The revive channel: a soft tick every 0.22 s while you're holding the revive on someone,
        /// and a warm triad the moment they get back up (server-confirmed, not predicted).
        /// </summary>
        private void UpdateReviveAudio(int reviveTarget, HPAudio audio)
        {
            if (audio == null) return;

            if (reviveTarget >= 0)
            {
                _reviveTickTimer -= Time.deltaTime;
                if (_reviveTickTimer <= 0f)
                {
                    audio.PlayOnce(HPAudio.ReviveChannel, 0.5f);
                    _reviveTickTimer = 0.22f;
                }
            }
            else _reviveTickTimer = 0f;

            // The teammate we were channelling is on their feet again → success.
            if (_reviveTargetHeard >= 0 && reviveTarget < 0)
            {
                var was = FindByObjectId(_reviveTargetHeard);
                if (was != null && was.Status.Value == StatusActive) audio.PlayOnce(HPAudio.ReviveSuccess, 0.5f);
            }
            _reviveTargetHeard = reviveTarget;
        }

        private void PushVitals()
        {
            _vitalsTimer += Time.deltaTime;
            bool flashChanged = _sim.FlashlightOn != _lastFlashSent;
            // Crouch is replicated because the HOST decides whether to drop a footprint, and a
            // crouching Yeti leaves no trail. Send it the instant it changes rather than waiting
            // for the 5 Hz tick, or you'd shed a print or two after going quiet.
            bool crouchChanged = _crouching != _lastCrouchSent;
            if (_vitalsTimer < 0.2f && !flashChanged && !crouchChanged) return;
            _vitalsTimer = 0f;
            _lastFlashSent = _sim.FlashlightOn;
            _lastCrouchSent = _crouching;
            ServerVitals(_sim.FlashlightOn, (float)_sim.Battery, (float)_sim.Stamina, _crouching);
        }

        public enum HoldAction { None, Revive, Deposit, Collect, Battery, Recover }

        /// <summary>What the hold-action key will do right now, and to what.</summary>
        public struct HoldTarget
        {
            public HoldAction Kind;
            public int ObjectId;
            public string Label; // ready-made prompt text, so the HUD can't describe a different action
        }

        /// <summary>
        /// Resolve the one thing hold-E does here. Priority: a downed teammate always wins (it's
        /// life-critical and time-limited), then evidence on the ground, then Sam's battery hand-off.
        /// Single source of truth for both the input path and the HUD prompt.
        /// </summary>
        public HoldTarget HoldActionTarget()
        {
            string reviveKey = HPKeybinds.Label(HPAction.Revive);

            var downed = NearestIncapTeammate(3.5f);
            if (downed != null)
                return new HoldTarget
                {
                    Kind = HoldAction.Revive, ObjectId = downed.ObjectId,
                    Label = $"hold {reviveKey} — revive {downed.PlayerName.Value}",
                };

            // At the duffel holding something: storing it is almost always what you came here to do.
            if (CarriedTotal > 0 && GameManager.AtDuffel(transform.position))
                return new HoldTarget
                {
                    Kind = HoldAction.Deposit, ObjectId = ObjectId,
                    Label = $"hold {reviveKey} — store {CarriedTotal} in the duffel (safe for good)",
                };

            // A spilled pack outranks fresh evidence: it's proof someone already paid for, and it's
            // on a timer. Picking it up is pure upside — the work is done, only the walk is left.
            ProofPile pile = null;
            float bestP = GameManager.PileRadius * GameManager.PileRadius;
            foreach (var pl in ProofPile.All)
            {
                if (pl == null) continue;
                float d = (pl.transform.position - transform.position).sqrMagnitude;
                if (d <= bestP) { bestP = d; pile = pl; }
            }
            if (pile != null)
            {
                string whose = pile.OwnerName.Value == "" ? "a dropped" : $"{pile.OwnerName.Value}'s";
                return new HoldTarget
                {
                    Kind = HoldAction.Recover, ObjectId = pile.ObjectId,
                    Label = $"hold {reviveKey} — recover {whose} pack ({pile.Total} unsaved)",
                };
            }

            // Casting a print is Mara's work — she carries the kit. (Wren FINDS prints, and her
            // longer evidence sight is what leads the team to them; the casting itself is lab work,
            // which is why it sits with the scientist rather than the tracker.) Hair needs no kit at
            // all, so anyone can bag it — that's the whole point of it existing.
            ClueMarker nearest = null;
            float bestD = 2.2f * 2.2f;
            foreach (var c in ClueMarker.Castables)
            {
                if (c == null) continue;
                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d <= bestD) { bestD = d; nearest = c; }
            }
            foreach (var c in ClueMarker.Hairs)
            {
                if (c == null) continue;
                float d = (c.transform.position - transform.position).sqrMagnitude;
                if (d <= bestD) { bestD = d; nearest = c; }
            }
            if (nearest != null)
            {
                bool isHair = nearest.CType.Value == ClueMarker.TypeHair;
                bool canWork = isHair || Specialty.Value == "analysis";
                return new HoldTarget
                {
                    Kind = canWork ? HoldAction.Collect : HoldAction.None,
                    ObjectId = canWork ? nearest.ObjectId : -1,
                    Label = !canWork ? "a castable print — only Mara carries the kit"
                        : isHair ? $"hold {reviveKey} — bag this hair sample"
                        : $"hold {reviveKey} — cast this print",
                };
            }

            if (Specialty.Value == "endurance" && AbilityCharges.Value > 0)
            {
                HPPlayer lowBattery = null;
                float bestB = 3.5f * 3.5f;
                foreach (var p in All)
                {
                    if (p == null || p == this || p.IsYeti) continue;
                    if (p.Status.Value != StatusActive || p.Battery.Value >= 99f) continue;
                    float d = (p.transform.position - transform.position).sqrMagnitude;
                    if (d <= bestB) { bestB = d; lowBattery = p; }
                }
                if (lowBattery != null)
                    return new HoldTarget
                    {
                        Kind = HoldAction.Battery, ObjectId = lowBattery.ObjectId,
                        Label = $"{reviveKey} — give {lowBattery.PlayerName.Value} a spare battery",
                    };
            }

            return new HoldTarget { Kind = HoldAction.None, ObjectId = -1, Label = null };
        }

        private HPPlayer NearestIncapTeammate(float radius)
        {
            HPPlayer best = null;
            float bestD = radius * radius;
            foreach (var p in All)
            {
                if (p == this || p.IsYeti || p.Status.Value != StatusIncap) continue;
                float d = (p.transform.position - transform.position).sqrMagnitude;
                if (d <= bestD) { bestD = d; best = p; }
            }
            return best;
        }

        public static HPPlayer FindByObjectId(int objectId)
        {
            foreach (var p in All) if (p.ObjectId == objectId) return p;
            return null;
        }

        private void SyncSimTo(Vector3 pos)
        {
            if (_sim == null) return;
            _sim.X = pos.x; _sim.Z = pos.z;
            _sim.GroundY = _world.GetHeight(pos.x, pos.z);
            _sim.FeetY = System.Math.Max(_sim.GroundY, pos.y);
            _sim.Grounded = true;
            _sim.Vy = 0;
        }

        /// <summary>
        /// Server-side drag for a victim with no owner (a CPU searcher). ServerBotDrive refuses to
        /// move anything that isn't <see cref="StatusActive"/> — correct for walking, but it meant a
        /// grabbed bot was never hauled anywhere: the Yeti "carried" it while its body stayed put.
        /// This writes the transform directly, the same way ServerBotDrive does, and keeps the sim in
        /// step so the bot resumes from where it was actually dropped.
        /// </summary>
        public void ServerDragTo(Vector3 goal)
        {
            if (!_isBot) return;
            transform.position = Vector3.Lerp(transform.position, goal, Time.deltaTime * 10f);
            SyncSimTo(transform.position);
        }

        /// <summary>Sim yaw from the replicated body rotation (server uses this for aim cones).</summary>
        public float SimYawFromTransform()
        {
            return (transform.eulerAngles.y - 180f) * Mathf.Deg2Rad;
        }

        // ================= Server-driven bot (single-player CPU) =================
        //
        // A bot is an ordinary HPPlayer spawned WITHOUT a NetworkConnection owner. That one fact does
        // most of the work: with no owner, base.IsOwner is false on every machine including the host,
        // so OwnerUpdate() never runs and the bot never reads a keyboard or camera. The host drives it
        // instead — ServerBotDrive below — and the (owner-less, client-authoritative) NetworkTransform
        // replicates the host's transform writes to every client exactly as it would a human's.
        //
        // Crucially the bot runs the SAME shared sim (Movement.StepPlayer) and fires abilities through
        // the SAME host methods (GameManager.Try*) a human's ServerRpc lands in. There is no parallel
        // "AI movement" or "AI grab" to drift out of sync — the only thing the brain supplies is intent.

        private bool _isBot;
        private float _botLogAt; // throttle for the [bot] movement diagnostic
        public bool IsBot => _isBot;

        /// <summary>
        /// Flag this as a bot at SPAWN, before roles are dealt. ServerStartMatch reads IsBot to place
        /// it server-side (a bot has no Owner, so the TargetRpc teleport can't reach it) and to leave
        /// its role to the normal deal — the bot just carries WantsYeti so the deal hands it Yeti.
        /// </summary>
        public void ServerMarkBot() => _isBot = true;

        /// <summary>
        /// Server-side placement for a bot, standing in for TargetTeleport (which needs an Owner).
        /// Called from ServerStartMatch AFTER the role is assigned, so ServerBecomeBot builds the sim
        /// and brain for the correct role.
        /// </summary>
        public void ServerBotPlace(Vector3 pos, float yawRad)
        {
            transform.position = pos;
            ServerBecomeBot();
            _yaw = yawRad;
            if (_sim != null) { _sim.X = pos.x; _sim.Z = pos.z; }
            ApplyBodyYaw();
            Debug.Log($"[bot] ServerBotPlace -> {pos}  (yeti={IsYeti}, brain={GetComponent<YetiBot>() != null})");
        }

        /// <summary>
        /// Turn this server-owned player into a CPU bot. Host only. Initialises the server-side sim
        /// (OnStartClient's owner init is skipped for an un-owned object) and attaches the brain that
        /// will drive it each tick. Idempotent — ServerBotPlace may call it after ServerMarkBot.
        /// </summary>
        public void ServerBecomeBot()
        {
            _isBot = true;
            _sim = NewSimState(transform.position, IsYeti);
            _yaw = SimYawFromTransform();

            // The player prefab's NetworkTransform is CLIENT-authoritative (humans own and drive their
            // own body). A bot has no owning client, so that NT has no owner to take transforms from —
            // and on the host it can hold the transform at the spawn point, snapping back over every
            // write the brain makes. That is the "stands at 178 m and never moves" bug. The bot's
            // transform is driven entirely server-side here, and in single-player the host is the only
            // machine that renders it, so the NT has no job: disable it and let the brain own the
            // transform outright. (When co-op bots arrive they'll need a server-auth NT instead.)
            var nt = GetComponent<NetworkTransform>();
            if (nt != null) nt.enabled = false;

            // The brain is a plain host-side component; it needs no networking of its own because it
            // only ever calls this player's public server methods. Added dynamically so the shared
            // player prefab stays exactly what a human uses.
            //
            // Which brain depends on the role, so this must run AFTER Role is dealt — and it is
            // re-entrant on purpose: a bot that gets re-placed between nights comes back through here,
            // and swapping roles between matches has to swap brains rather than stack them.
            if (IsYeti)
            {
                if (SearcherBrain != null) Destroy(SearcherBrain);
                SearcherBrain = null;
                if (YetiBrain == null) YetiBrain = gameObject.AddComponent<YetiBot>();
            }
            else
            {
                if (YetiBrain != null) Destroy(YetiBrain);
                YetiBrain = null;
                if (SearcherBrain == null) SearcherBrain = gameObject.AddComponent<SearcherBot>();
            }
        }

        /// <summary>
        /// This bot's brain, held rather than fetched. Host-side only — a pure client never adds one,
        /// so these stay null there exactly as the old GetComponent calls returned null.
        ///
        /// They exist because the F3 overlay read them with GetComponent from inside OnGUI, which
        /// Unity runs at least twice a frame (Layout and Repaint) — so the panel whose job is to
        /// report the frame cost was adding a per-player component lookup to every frame it measured.
        /// </summary>
        public YetiBot YetiBrain { get; private set; }
        public SearcherBot SearcherBrain { get; private set; }

        /// <summary>DEV (F3 `[J]`): per-bot movement trace to the Console. Off by default — see
        /// ServerBotDrive for why it can't be left on with five bots in the match.</summary>
        public static bool BotDriveTrace;

        /// <summary>
        /// One host tick of bot movement. The brain supplies a <see cref="MoveInput"/> (direction via
        /// yaw + W, sprint, etc.); this steps the shared sim and writes the transform, so the bot
        /// obeys the identical collision, terrain, log and stamina rules every human does. Returns the
        /// step result so the brain can read whether it actually moved (stuck detection).
        /// </summary>
        public StepResult ServerBotDrive(MoveInput input)
        {
            if (!_isBot || _sim == null) return default;
            if (Status.Value != StatusActive) return default; // a frozen/incap bot is as stuck as a human

            _sim.IsYeti = IsYeti;
            input.Yaw = _yaw;
            var before = new Vector2((float)_sim.X, (float)_sim.Z);
            var mods = CurrentModifiers();
            _lastStep = Movement.StepPlayer(_sim, input, _world, mods);
            transform.position = new Vector3((float)_sim.X, (float)_sim.FeetY, (float)_sim.Z);
            ApplyBodyYaw();
            // Keep the replicated vitals honest — a human streams them via ServerVitals, and a bot has
            // no such path, so write them here.
            //
            // Battery used to be skipped on the grounds that "the bot never lights a torch". That was
            // true while the only bot was the Yeti; CPU SEARCHERS do light torches, and a stale
            // Battery makes them invisible to Sam's spare-battery scan (which skips anyone at >= 99%)
            // and to the HUD. Cheap to write, and wrong to leave.
            if (!Mathf.Approximately(Stamina.Value, (float)_sim.Stamina)) Stamina.Value = (float)_sim.Stamina;
            if (!Mathf.Approximately(Battery.Value, (float)_sim.Battery)) Battery.Value = (float)_sim.Battery;

            // DEV diagnostic (throttled), OFF by default: pinpoints where movement breaks if a bot
            // ever stalls.
            //  simMoved 0     -> StepPlayer isn't moving it (input.W false, speedMul 0, world issue)
            //  simMoved > 0   -> the sim IS moving; if it still looks stuck, something overrides the
            //                    transform after this write (a live NetworkTransform).
            //
            // Gated because it is PER BOT: written when the Yeti was the only one, it now fires for
            // five, and an editor Debug.Log captures a stack trace every time. Turn it on from the
            // F3 overlay only while actually chasing a stall.
            if (BotDriveTrace && Time.time >= _botLogAt)
            {
                _botLogAt = Time.time + 1.5f;
                float simMoved = ((float)_sim.X - before.x) * ((float)_sim.X - before.x) +
                                 ((float)_sim.Z - before.y) * ((float)_sim.Z - before.y);
                Debug.Log($"[bot] {PlayerName.Value} W={input.W} speedMul={mods.SpeedMul:0.00} " +
                          $"simMoved={Mathf.Sqrt(simMoved):0.000}/frame " +
                          $"grounded={_sim.Grounded} pos=({_sim.X:0},{_sim.Z:0})");
            }
            return _lastStep;
        }

        /// <summary>Point the bot along a world direction (XZ). Sim forward is (-sin yaw, -cos yaw), so
        /// the yaw that faces (dx,dz) is atan2(-dx,-dz). The brain calls this before ServerBotDrive.</summary>
        public void ServerBotFace(float dx, float dz)
        {
            if (dx * dx + dz * dz < 1e-6f) return;
            _yaw = Mathf.Atan2(-dx, -dz);
        }

        /// <summary>Bot ability triggers — thin pass-throughs to the same authority a human hits.</summary>
        public void ServerBotRoar() { if (_isBot) GameManager.Instance?.TryRoar(this); }
        public void ServerBotGrab() { if (_isBot) GameManager.Instance?.TryGrab(this); }
        public void ServerBotCaveTravel(int index) { if (_isBot) GameManager.Instance?.TryCaveTravel(this, index); }

        // --- searcher bot entry points ---------------------------------------------
        // Same shape and the same reason as the Yeti's: every searcher action a human takes travels
        // through a [ServerRpc], which needs an owning connection a bot does not have. These land in
        // the identical GameManager authority, so a CPU searcher is bound by the same range, cone,
        // line-of-sight, channel-duration and cooldown rules a person is. There is deliberately no
        // "AI filming" or "AI casting" that could drift from the real thing.

        public void ServerBotSetRecording(bool recording) { if (_isBot) GameManager.Instance?.SetRecording(this, recording); }
        public void ServerBotSetReviveTarget(int targetObjectId) { if (_isBot) GameManager.Instance?.SetReviveTarget(this, targetObjectId); }
        public void ServerBotSetCollectTarget(int evidenceObjectId) { if (_isBot) GameManager.Instance?.SetCollectTarget(this, evidenceObjectId); }
        public void ServerBotDeposit() { if (_isBot) GameManager.Instance?.TryDeposit(this); }
        public void ServerBotRecoverPile(int pileObjectId) { if (_isBot) GameManager.Instance?.TryRecoverPile(this, pileObjectId); }
        public void ServerBotFlash() { if (_isBot) GameManager.Instance?.TryFlash(this); }
        public void ServerBotPing(float x, float z) { if (_isBot) GameManager.Instance?.TryPing(this, x, z); }
        public void ServerBotMark() { if (_isBot) GameManager.Instance?.TryMark(this); }

        /// <summary>
        /// A bot's torch. Writes the sim (so <see cref="Movement.StepPlayer"/> drains its battery on
        /// the same schedule a human's does) and the replicated flag together, because that flag is
        /// not cosmetic: <c>YetiBot</c> sees a lit torch from more than twice as far as an unlit
        /// searcher. A bot that lights up is genuinely giving away its position, which is the whole
        /// trade the flashlight exists to create.
        /// </summary>
        public void ServerBotSetFlashlight(bool on)
        {
            if (!_isBot || _sim == null) return;
            bool lit = on && _sim.Battery > 0d;
            _sim.FlashlightOn = lit;
            if (FlashOn.Value != lit) FlashOn.Value = lit;
        }

        /// <summary>Bot crouch — silent movement, and no snow prints (see GameManager.DropSnowPrints).</summary>
        public void ServerBotSetCrouched(bool crouched)
        {
            if (!_isBot) return;
            if (Crouched.Value != crouched) Crouched.Value = crouched;
        }

        /// <summary>Battery level for the brain's own decisions (the sim is fresher than the SyncVar).</summary>
        public float BotBattery => _sim != null ? (float)_sim.Battery : 0f;
        /// <summary>Stamina level for the brain's own decisions.</summary>
        public float BotStamina => _sim != null ? (float)_sim.Stamina : 0f;

        /// <summary>
        /// Server-side teleport for a BOT, used by crevasse fast-travel.
        ///
        /// <see cref="TargetTeleport"/> cannot serve here: it is a TargetRpc that runs on the OWNER's
        /// client and touches MapView/Cursor, and a bot has no owning connection — so cave travel
        /// would silently do nothing for the CPU Yeti. This is the same state change, server-side,
        /// minus the client-only bits. Stamina carries over rather than refilling, so travelling is
        /// not a free rest (ServerBotPlace would reset it — that one is for match placement).
        /// </summary>
        public void ServerBotTeleport(Vector3 pos, float yawRad)
        {
            if (!_isBot) return;
            float stamina = _sim != null ? (float)_sim.Stamina : Stamina.Value;
            _sim = NewSimState(pos, IsYeti);
            _sim.Stamina = stamina;
            _yaw = yawRad;
            transform.position = new Vector3(pos.x, (float)_sim.FeetY, pos.z);
            ApplyBodyYaw();
        }

        // Owner HUD accessors (local sim is fresher than the SyncVars for your own bars).
        public bool OwnGrounded => _sim == null || _sim.Grounded;
        /// <summary>HUD hints for the tower (searcher, owner). All computed from the live transform.</summary>
        public bool OwnOnLadder => _onLadder;
        public bool OwnNearLadder => !IsYeti && !_onLadder && NearLadder();
        public bool OwnOnLookout => !IsYeti && OnLookoutPlatform();
        public bool OwnGlassing => _glassing;
        /// <summary>True while the sim is actually sprinting this frame (not merely holding the key).</summary>
        public bool OwnSprinting => _lastStep.Sprinting;
        /// <summary>True while the sim is actually moving this frame (the F3 look readout uses it).</summary>
        public bool OwnMoving => _lastStep.Moving;
        /// <summary>Stamina hit 0 — no sprinting until it regenerates past the recovery threshold.</summary>
        public bool OwnExhausted => _sim != null && _sim.Exhausted;
        public float OwnBattery => _sim != null ? (float)_sim.Battery : Battery.Value;
        public float OwnStamina => _sim != null ? (float)_sim.Stamina : Stamina.Value;
        public bool OwnFlashOn => _sim != null && _sim.FlashlightOn;
        /// <summary>Seconds until the next roar, max of the server's truth and our local echo.</summary>
        public float RoarCooldownLeft => Mathf.Max(RoarReadyIn.Value, _roarEchoUntil - Time.time);
        /// <summary>Roar is available: off cooldown (server + local echo) and not dazzled.</summary>
        public bool CanRoar() => RoarCooldownLeft <= 0f && !Dazzled.Value;
        /// <summary>Local echo of the cave-travel cooldown (the server owns the real one) for the map hint.</summary>
        public float CaveReadyIn => Mathf.Max(0f, _caveReadyAt - Time.time);

        // ------------------------------------------------------------------ map actions

        /// <summary>Searcher drops a stakeout ping (Q at your feet, or a click on the open map).</summary>
        public void RequestPing(float x, float z)
        {
            if (!base.IsOwner || IsYeti || Status.Value != StatusActive) return;
            ServerPing(x, z);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.PingDrop, 0.5f);
        }

        /// <summary>Yeti picks a destination cave off the map; the server re-validates every rule.</summary>
        public void RequestCaveTravel(int index)
        {
            if (!base.IsOwner || !IsYeti || Status.Value != StatusActive) return;
            if (Time.time < _caveReadyAt) return;

            // Mirror the server's checks before spending the local cooldown — otherwise a request the
            // host was always going to refuse still locks the player out of the network for 2 s.
            var world = WorldBuilder.EnsureWorld();
            if (index < 0 || index >= world.Caves.Count) return;
            int here = Caves.NearestCaveIndex(world.Caves, transform.position.x, transform.position.z);
            if (here < 0 || here == index) return;

            _caveReadyAt = Time.time + (float)CaveRules.TravelCooldown;
            ServerCaveTravel(index);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.CaveWhoosh, 0.6f);
        }

        // ------------------------------------------------------------------ RPCs (owner -> server)

        [ServerRpc]
        private void ServerVitals(bool flashOn, float battery, float stamina, bool crouched)
        {
            Crouched.Value = crouched;
            // Resource envelope (mirrors the web build's bounds): battery only ever decreases and a
            // dead battery forces the light off; stamina is clamped to the specialty ceiling.
            float b = Mathf.Clamp(Mathf.Min(battery, Battery.Value), 0f, 100f);
            Battery.Value = b;
            FlashOn.Value = flashOn && b > 0f;
            Stamina.Value = Mathf.Clamp(stamina, 0f, (float)Specialties.StaminaMax(Specialty.Value));
        }

        [ServerRpc]
        private void ServerSetRecording(bool recording)
        {
            if (GameManager.Instance != null) GameManager.Instance.SetRecording(this, recording);
        }

        [ServerRpc]
        private void ServerSetReviveTarget(int targetObjectId)
        {
            if (GameManager.Instance != null) GameManager.Instance.SetReviveTarget(this, targetObjectId);
        }

        [ServerRpc]
        private void ServerSetCollectTarget(int evidenceObjectId)
        {
            if (GameManager.Instance != null) GameManager.Instance.SetCollectTarget(this, evidenceObjectId);
        }

        [ServerRpc]
        private void ServerDeposit()
        {
            if (GameManager.Instance != null) GameManager.Instance.TryDeposit(this);
        }

        [ServerRpc]
        private void ServerRecoverPile(int pileObjectId)
        {
            if (GameManager.Instance != null) GameManager.Instance.TryRecoverPile(this, pileObjectId);
        }

        [ServerRpc]
        private void ServerFlash()
        {
            if (GameManager.Instance != null) GameManager.Instance.TryFlash(this);
        }

        [ServerRpc]
        private void ServerBatteryGift(int targetObjectId)
        {
            if (GameManager.Instance != null) GameManager.Instance.TryBatteryGift(this, targetObjectId);
        }

        /// <summary>
        /// Server → receiver: raise the LOCAL sim's battery after Sam's hand-off. Required because
        /// ServerVitals enforces battery-only-decreases; without this the client would immediately
        /// report its old, lower value back and undo the gift on the next vitals push.
        /// </summary>
        [FishNet.Object.TargetRpc]
        public void TargetGrantBattery(FishNet.Connection.NetworkConnection conn, float newBattery)
        {
            if (_sim != null) _sim.Battery = newBattery;
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.BatterySwap, 0.6f);
        }

        [ServerRpc]
        private void ServerPing(float x, float z)
        {
            if (GameManager.Instance != null) GameManager.Instance.TryPing(this, x, z);
        }

        [ServerRpc]
        private void ServerCaveTravel(int index)
        {
            if (GameManager.Instance != null) GameManager.Instance.TryCaveTravel(this, index);
        }

        [ServerRpc]
        private void ServerMark()
        {
            if (GameManager.Instance != null) GameManager.Instance.TryMark(this);
        }

        [ServerRpc]
        private void ServerRoar()
        {
            if (GameManager.Instance != null) GameManager.Instance.TryRoar(this);
        }

        [ServerRpc]
        private void ServerGrab()
        {
            if (GameManager.Instance != null) GameManager.Instance.TryGrab(this);
        }

        [ServerRpc]
        public void ServerSetWantsYeti(bool wants)
        {
            WantsYeti.Value = wants;
        }

        /// <summary>DEV persona request. Validated here so a client can't invent a specialty id.</summary>
        [ServerRpc]
        private void ServerSetDevSpecialty(string id)
        {
            DevSpecialty.Value = Specialties.IsSpecialtyId(id) ? id : "";
        }

        [ServerRpc]
        private void ServerSetName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return; // keep the server-assigned "Player N"
            if (name.Length > 16) name = name.Substring(0, 16);
            PlayerName.Value = name;
        }

        // ------------------------------------------------------------------ visuals

        private void BuildVisuals()
        {
            if (_builtRole == Role.Value) return;
            _builtRole = Role.Value;
            DisposeVisuals();
            _visualRoot = new GameObject("Visual").transform;
            _visualRoot.SetParent(transform, false);

            // Deterministic per-player shape variation, from the network id rather than any RNG — the
            // same rule the forest follows ([rng-lockstep]). Five searchers built from one hash would be five
            // identical mannequins, and the lumps are most of what stops that.
            int variant = Mathf.Abs(ObjectId) * 31 + 7;

            if (IsYeti)
            {
                _baseBodyColor = MeshUtil.Rgb(0x2a2018);
                // Matted fur, not painted plastic. Low smoothness so it stays a light SINK — the Yeti
                // reading as a silhouette that swallows the torch is half of what makes it scary.
                _bodyMat = MeshUtil.Surface(_baseBodyColor, 0.08f, ProcTex.FurNormal, 1.15f, 2.2f);
                _eyeMat = MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffcc55), 3.5f);
                _avatar = Avatar.BuildYeti(_visualRoot, _bodyMat, _eyeMat, variant);
            }
            else
            {
                int hex = SpecialtyColors.TryGetValue(Specialty.Value ?? "", out int c) ? c : 0x9aa2aa;
                _baseBodyColor = MeshUtil.Rgb(hex);
                // Technical outerwear: a little sheen, woven surface.
                _bodyMat = MeshUtil.Surface(_baseBodyColor, 0.24f, ProcTex.FabricNormal, 0.8f, 3f);
                // Pack and hood in webbing brown, deliberately NOT the specialty colour: the colour is
                // how you tell teammates apart at range, so spending it on the gear too would blur the
                // one signal it carries.
                _gearMat = MeshUtil.Surface(MeshUtil.Rgb(0x3a3630), 0.16f, ProcTex.FabricNormal, 0.7f, 3.5f);
                _avatar = Avatar.BuildSearcher(_visualRoot, _bodyMat, _gearMat, variant);

                // The torch rides the HAND now, not a point floating at eye height. A remote searcher's
                // beam therefore swings with their arm and sweeps as they walk, which is most of how
                // you read where somebody else is looking from across a valley — and it is the reason
                // the hand is the avatar's TorchAnchor rather than the head.
                var lightGo = new GameObject("Flashlight");
                lightGo.transform.SetParent(_avatar.TorchAnchor, false);
                lightGo.transform.localPosition = new Vector3(0f, -0.06f, 0.06f);
                lightGo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // hand hangs -Y; aim +Z
                _flashlight = lightGo.AddComponent<Light>();
                _flashlight.type = LightType.Spot;
                // A searcher's torch is their main tool in the dark, so it reaches and punches harder
                // than the original port did (range 60 -> 90, intensity 11 -> 20, hotter core). Three's
                // `angle` was a HALF-angle (0.5 rad -> ~57 deg full cone); the wider inner angle throws
                // a broader lit pool so you can actually read the forest floor while moving. It stays a
                // trade: the brighter the beam, the farther Yeti sees you carrying it (the bot's
                // TorchSightRange, and the dazzle beam, both key off the same light being ON).
                _flashlight.range = 90f;
                _flashlight.spotAngle = 62f;
                _flashlight.innerSpotAngle = 38f;
                _flashlight.intensity = 20f;
                // Warmer and slightly greener than the old flat 0xfff4dc: a real headtorch LED is not
                // white, and against snow — which is the coldest surface in the frame and takes the
                // moon's blue almost unchanged — the warmth of the beam is what separates "lit by me"
                // from "lit by the sky". That separation is the whole navigational value of the torch.
                _flashlight.color = MeshUtil.Rgb(0xffe9c4);
                // ONLY the owner's torch casts shadows, and only on the expensive tier. A shadow-casting
                // spot per searcher is five extra shadow maps rendered every frame on a machine whose
                // whole performance story is integrated graphics — and you cannot see the shadows a
                // teammate's beam casts 40 m away anyway. Yours you see constantly.
                _flashlight.shadows = base.IsOwner && HPQuality.HighDetail ? LightShadows.Soft : LightShadows.None;
                _flashlight.shadowStrength = 0.75f;
                _flashlight.shadowBias = 0.08f;
                _flashlight.enabled = false;

                // The beam itself, as geometry. A spot light with nothing in the air is invisible until
                // it lands on something, so in open snowfield a searcher's torch simply had no presence
                // in the frame — you saw a lit patch of ground with no shaft connecting it to a person.
                _beam = TorchBeam.Build(lightGo.transform, _flashlight.range, _flashlight.spotAngle);

                // REC light — a red bead above a filming searcher's head (like the web rec light).
                var rec = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(rec.GetComponent<UnityEngine.Collider>());
                rec.transform.SetParent(_avatar.HeadAnchor, false);
                rec.transform.localScale = Vector3.one * 0.07f;
                rec.transform.localPosition = new Vector3(0f, 0.30f, 0f);
                _recDot = rec.GetComponent<MeshRenderer>();
                _recDot.sharedMaterial = MeshUtil.Emissive(Color.black, Color.red, 4f);
                _recDot.enabled = false;
            }

            // First person: hide your own body, keep your light.
            if (base.IsOwner)
            {
                _avatar.SetVisible(false);
                if (_recDot != null) _recDot.enabled = false;
            }
            else
            {
                // Breath on remotes only — see Weather.AttachBreath for why the owner does not get it.
                Weather.AttachBreath(_avatar.HeadAnchor, IsYeti);
            }
        }

        /// <summary>
        /// Tear down the generated body. Meshes and materials are both native objects Unity's GC does
        /// NOT collect, and a role swap between matches rebuilds the whole figure — ~19 meshes plus
        /// three materials each time. The world had exactly this leak before the realism pass swept it.
        /// </summary>
        private void DisposeVisuals()
        {
            _avatar?.Dispose();
            _avatar = null;
            if (_bodyMat != null) Destroy(_bodyMat);
            if (_gearMat != null) Destroy(_gearMat);
            if (_eyeMat != null) Destroy(_eyeMat);
            _bodyMat = _gearMat = _eyeMat = null;
            if (_visualRoot != null) Destroy(_visualRoot.gameObject);
            _visualRoot = null;
            _flashlight = null;
            _recDot = null;
            _beam = null;
        }

        private void OnDestroy()
        {
            DisposeVisuals();
        }

        /// <summary>
        /// Remote players' footsteps: accrue the ground they cover and emit a positional step each
        /// stride (Yeti's gait is longer and heavier). This is how you hear someone in the dark.
        /// </summary>
        private void UpdateRemoteFootsteps()
        {
            var audio = HPAudio.Instance;
            if (audio == null || Status.Value != StatusActive) return;
            // A crouching player makes no sound anyone else can hear either. Keep the position
            // tracking up to date so uncrouching doesn't emit a burst of backdated steps.
            if (Crouched.Value) { _lastAudioPos = transform.position; _remoteStepDist = 0f; return; }

            Vector3 pos = transform.position;
            if (!_audioPosInit) { _audioPosInit = true; _lastAudioPos = pos; return; }
            float moved = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(_lastAudioPos.x, _lastAudioPos.z));
            _lastAudioPos = pos;
            if (moved > 3f) return; // a jump this large is a teleport (cave travel), not a step

            float stride = IsYeti ? 2.3f : 1.7f;
            _remoteStepDist += moved;
            if (_remoteStepDist < stride) return;
            _remoteStepDist = 0f;
            audio.PlayAt(IsYeti ? HPAudio.FootstepHeavy : HPAudio.FootstepSoft, pos,
                (IsYeti ? 0.7f : 0.4f) * (float)Specialties.FootstepVolumeMul(Specialty.Value), 7f);
        }

        private void UpdateSharedVisuals()
        {
            if (!base.IsOwner) UpdateRemoteFootsteps();

            // The owner's flashlight rides the CAMERA so it points exactly where you look, pitch
            // included — the web build attaches it to the camera for the same reason. Remotes keep
            // theirs on the body (they only need the beam to leave the right silhouette). The camera
            // isn't assigned yet when BuildVisuals runs, so the reparent happens on the first frame
            // it exists; the parent check keeps this a no-op afterwards.
            if (base.IsOwner && _flashlight != null && _cam != null &&
                _flashlight.transform.parent != _cam.transform)
            {
                _flashlight.transform.SetParent(_cam.transform, false);
                _flashlight.transform.localPosition = new Vector3(0.15f, -0.12f, 0.1f);
                _flashlight.transform.localRotation = Quaternion.identity; // spot shines down local +Z
            }

            if (_flashlight != null)
                _flashlight.enabled = base.IsOwner ? OwnFlashOn : FlashOn.Value;

            // REC bead blinks while a remote searcher is filming (owner sees their own REC on the HUD).
            if (_recDot != null && !base.IsOwner)
                _recDot.enabled = Filming.Value && (Time.time % 0.8f) < 0.55f;

            if (_bodyMat != null)
            {
                Color target = _baseBodyColor;
                if (Status.Value == StatusFrozen) target = Color.Lerp(_baseBodyColor, MeshUtil.Rgb(0x9ac8ff), 0.65f);
                else if (Status.Value == StatusIncap) target = Color.Lerp(_baseBodyColor, Color.black, 0.55f);
                else if (Dazzled.Value) target = Color.Lerp(_baseBodyColor, Color.white, 0.5f);
                _bodyMat.SetColor("_BaseColor", target);
            }

            if (_beam != null) _beam.SetOn(base.IsOwner ? OwnFlashOn : FlashOn.Value);

            // Body colour can change when the specialty is dealt mid-session.
            if (!IsYeti && !string.IsNullOrEmpty(Specialty.Value))
            {
                int hex = SpecialtyColors.TryGetValue(Specialty.Value, out int c) ? c : 0x9aa2aa;
                _baseBodyColor = MeshUtil.Rgb(hex);
            }

            UpdateAvatar();
        }

        /// <summary>
        /// Measure how this body is actually moving, and hand it to the animation layer.
        ///
        /// Speed and yaw rate come from the TRANSFORM, not from the sim, and that is deliberate: a
        /// remote's transform is what FishNet is interpolating and therefore what is being drawn, so a
        /// gait derived from it cannot slide against the motion on screen. Deriving it from replicated
        /// velocity instead would put the feet a network frame out of step with the body they belong to.
        /// </summary>
        private void UpdateAvatar()
        {
            if (_avatar == null) return;

            float dt = Time.deltaTime;
            Vector3 pos = transform.position;
            float yaw = transform.eulerAngles.y * Mathf.Deg2Rad;

            if (!_visPosInit)
            {
                _visPosInit = true;
                _lastVisPos = pos;
                _lastVisYaw = yaw;
            }
            else if (dt > 1e-4f)
            {
                float moved = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(_lastVisPos.x, _lastVisPos.z));
                // A jump this big is a teleport (cave travel, match-start placement), not a step. Left
                // unfiltered it spikes the stride rate and the legs blur for a frame on arrival.
                float inst = moved > 3f ? 0f : moved / dt;
                _visSpeed = Mathf.Lerp(_visSpeed, inst, 1f - Mathf.Exp(-12f * dt));
                float dYaw = Mathf.DeltaAngle(_lastVisYaw * Mathf.Rad2Deg, yaw * Mathf.Rad2Deg) * Mathf.Deg2Rad;
                _visYawRate = Mathf.Lerp(_visYawRate, dYaw / dt, 1f - Mathf.Exp(-9f * dt));
                _lastVisPos = pos;
                _lastVisYaw = yaw;
            }

            // The owner's own body is hidden (first person), so there is nothing to animate and no
            // reason to spend the frame on it.
            if (base.IsOwner) return;

            var inp = new AvatarInput
            {
                Speed = _visSpeed,
                Sprinting = _visSpeed > (IsYeti ? 5.5f : 4.2f),
                Crouched = Crouched.Value,
                Status = Status.Value,
                Filming = Filming.Value,
                Carrying = IsYeti && IsCarryingSomeone(),
                BeingCarried = GrabberObjectId.Value >= 0,
                YawRate = _visYawRate,
            };
            _avatar.Tick(in inp, dt);
        }

        /// <summary>
        /// Is this Yeti hauling a searcher right now? Derived by scanning, not replicated: the victim
        /// already carries its grabber's id, so a second SyncVar on the Yeti would be the same fact
        /// stored twice and able to disagree with itself. Six players makes the scan free.
        /// </summary>
        private bool IsCarryingSomeone()
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].GrabberObjectId.Value == ObjectId) return true;
            return false;
        }

        /// <summary>
        /// Play the roar pose on whichever body is the Yeti. Called from the roar RPC every client
        /// already receives, so the pose needs no message of its own.
        /// </summary>
        public static void PlayRoarPose()
        {
            for (int i = 0; i < All.Count; i++)
                if (All[i] != null && All[i].IsYeti) All[i]._avatar?.TriggerRoar();
        }
    }
}
