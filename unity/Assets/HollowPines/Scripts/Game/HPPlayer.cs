// The networked player — searcher or Bigfoot. Unity port of the web build's LocalPlayer +
// RemotePlayer, driven by the shared deterministic sim (HollowPines.Sim.Movement.StepPlayer):
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
using HollowPines.Sim;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace HollowPines.Game
{
    public class HPPlayer : NetworkBehaviour
    {
        public const byte RoleSearcher = 0;
        public const byte RoleBigfoot = 1;
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
        /// <summary>Crouching. Replicated because the host suppresses Bigfoot's trail while it is.</summary>
        public readonly SyncVar<bool> Crouched = new SyncVar<bool>(false);
        public readonly SyncVar<float> Battery = new SyncVar<float>(100f);
        public readonly SyncVar<float> Stamina = new SyncVar<float>(100f);
        public readonly SyncVar<bool> Filming = new SyncVar<bool>(false);
        public readonly SyncVar<float> FilmProgress = new SyncVar<float>(0f);
        public readonly SyncVar<int> GrabberObjectId = new SyncVar<int>(-1);
        public readonly SyncVar<bool> WantsBigfoot = new SyncVar<bool>(false);
        /// <summary>DEV: the persona this player asked to be dealt ("" = normal random deal).</summary>
        public readonly SyncVar<string> DevSpecialty = new SyncVar<string>("");
        /// <summary>Bigfoot only: seconds until the next roar is available (HUD cooldown).</summary>
        public readonly SyncVar<float> RoarReadyIn = new SyncVar<float>(0f);
        /// <summary>Incapacitated only: 0..1 revive progress a teammate has accrued on this player.</summary>
        public readonly SyncVar<float> ReviveProgress01 = new SyncVar<float>(0f);
        /// <summary>0..1 progress on the evidence this searcher is currently collecting.</summary>
        public readonly SyncVar<float> CollectProgress01 = new SyncVar<float>(0f);
        /// <summary>
        /// Proof this searcher is CARRYING and has not yet stored in the duffel — tapes, casts and
        /// hair counted separately for the HUD, but they behave identically: unsafe until deposited,
        /// and SPILLED on the ground as a recoverable pile if Bigfoot takes you (GameManager.TryGrab
        /// → SpawnProofPile). Any future evidence type joins these and needs no change to the deposit
        /// path (see GameManager.TryDeposit / CarriedTotal).
        /// </summary>
        public readonly SyncVar<int> CarriedFilm = new SyncVar<int>(0);
        public readonly SyncVar<int> CarriedCasts = new SyncVar<int>(0);
        public readonly SyncVar<int> CarriedHair = new SyncVar<int>(0);

        /// <summary>Everything unsaved on this player, whatever kind it is.</summary>
        public int CarriedTotal => CarriedFilm.Value + CarriedCasts.Value + CarriedHair.Value;
        /// <summary>Per-night ability charges — Eli's flash, Sam's spare battery. 0 for everyone else.</summary>
        public readonly SyncVar<int> AbilityCharges = new SyncVar<int>(0);
        /// <summary>
        /// Seconds this searcher stays lit up on Bigfoot's screen after firing a camera flash —
        /// the cost side of Eli's ability. A countdown, so no shared clock is needed to read it.
        /// </summary>
        public readonly SyncVar<float> RevealedFor = new SyncVar<float>(0f);

        /// <summary>Every spawned player on this client (remote + local), for HUD/targeting scans.</summary>
        public static readonly List<HPPlayer> All = new List<HPPlayer>();
        public static HPPlayer Local { get; private set; }

        public bool IsBigfoot => Role.Value == RoleBigfoot;

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
        private bool _pressedJump, _pressedLeap, _pressedVault;
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
        private Renderer _bodyRenderer;
        private Material _bodyMat;
        private Light _flashlight;
        private Renderer _recDot;
        private byte _builtRole = 255;
        private Color _baseBodyColor;

        // --- Senses overlay (V, Bigfoot, client-only render toggle like the web build) ---
        public static bool SensesOn;
        /// <summary>Bigfoot's own recent positions — its scent trail for the senses overlay.</summary>
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
                _sim = NewSimState(transform.position, IsBigfoot);
                _yaw = SimYawFromTransform();
                _cam = Camera.main;
                if (_cam != null)
                {
                    _cam.transform.SetParent(transform, false);
                    _cam.transform.localPosition = new Vector3(0f, (float)_sim.CurEye, 0f);
                    _cam.transform.localRotation = Quaternion.identity;
                    _cam.nearClipPlane = 0.08f;
                    _cam.farClipPlane = 900f;
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

        private void OnRoleChanged(byte prev, byte next, bool asServer)
        {
            if (!asServer) BuildVisuals();
            if (base.IsOwner && _sim != null)
            {
                _sim.IsBigfoot = next == RoleBigfoot;
                _sim.EyeHeight = next == RoleBigfoot ? 2.4 : Sim.Player.EyeHeight;
                if (PostFX.Instance != null) PostFX.Instance.SetBigfootVision(next == RoleBigfoot);
                // The other half of Bigfoot's vision trade: brighter up close, murkier far away.
                WorldBuilder.FogMul = next == RoleBigfoot ? 1.35f : 1f;
                if (WorldBuilder.Instance != null) WorldBuilder.Instance.InvalidatePalette();
                SensesOn = false;
                ScentTrail.Clear();
            }
        }

        private static PlayerSimState NewSimState(Vector3 pos, bool bigfoot)
        {
            var world = WorldBuilder.EnsureWorld();
            double gy = world.GetHeight(pos.x, pos.z);
            return new PlayerSimState
            {
                X = pos.x, Z = pos.z, FeetY = gy, GroundY = gy, Grounded = true,
                Stamina = 100, Battery = 100, EyeHeight = bigfoot ? 2.4 : Sim.Player.EyeHeight,
                CurEye = bigfoot ? 2.4 : Sim.Player.EyeHeight, IsBigfoot = bigfoot,
            };
        }

        /// <summary>Server → owner: hard-place the player (spawn, match start). Resets the local sim.</summary>
        [FishNet.Object.TargetRpc]
        public void TargetTeleport(FishNet.Connection.NetworkConnection conn, Vector3 pos, float yawRad)
        {
            _sim = NewSimState(pos, IsBigfoot);
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
            bool canAct = Status.Value == StatusActive;

            // Dragged while incapacitated: mirror the grabber so the client-auth transform follows it.
            if (Status.Value == StatusIncap && GrabberObjectId.Value >= 0)
            {
                var grabber = FindByObjectId(GrabberObjectId.Value);
                if (grabber != null)
                {
                    transform.position = Vector3.Lerp(transform.position, grabber.transform.position, Time.deltaTime * 10f);
                    SyncSimTo(transform.position);
                }
            }
            else if (canAct)
            {
                StepSim();
            }

            if (canAct) HandleAbilities(playing);
            PushVitals();
            UpdateBob();
            UpdateScent();
            UpdateOwnFootsteps();
            UpdateStatusStings();

            // Only drive the LOCAL pose while the camera is actually parented to us. During the lobby
            // blend it is un-parented and positioned in world space, where a local-space write would
            // fling it to the world origin.
            if (_cam != null && _cam.transform.parent == transform)
            {
                _cam.transform.localPosition = new Vector3(0f, (float)_sim.CurEye + _bobOffset, 0f);
                _cam.transform.localRotation = Quaternion.Euler(_pitch * Mathf.Rad2Deg, 0f, 0f);
            }
        }

        /// <summary>
        /// While waiting in the camp lobby, let the camera fly the title-card orbit. Returns true if
        /// the cinematic is driving this frame (so the owner loop skips look/move/abilities entirely).
        /// The camera is un-parented for the orbit and re-parented the moment control is taken back or
        /// the match starts, which is also what makes the hand-off to first-person seamless.
        /// </summary>
        private bool UpdateLobbyCinematic(bool playing)
        {
            if (_cam == null) return false;

            bool wantsControl = false;
#if ENABLE_INPUT_SYSTEM
            wantsControl = Mouse.current != null && Mouse.current.rightButton.isPressed;
#endif
            bool inLobby = !playing && !HPHud.PauseOpen && !MapView.IsOpen;

            if (!inLobby)
            {
                // Match (or a menu) owns the camera: re-attach and hand back to the normal path.
                _lobbyCam01 = 1f;
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

            var orbitCam = _cam;
            TitleMenu.OrbitCamp(orbitCam);
            Vector3 orbitPos = orbitCam.transform.position;
            Quaternion orbitRot = orbitCam.transform.rotation;

            Vector3 fpPos = transform.position + Vector3.up * (float)_sim.CurEye;
            Quaternion fpRot = Quaternion.Euler(_pitch * Mathf.Rad2Deg, _yaw * Mathf.Rad2Deg + 180f, 0f);

            float k = Mathf.SmoothStep(0f, 1f, _lobbyCam01);
            _cam.transform.SetPositionAndRotation(
                Vector3.Lerp(orbitPos, fpPos, k), Quaternion.Slerp(orbitRot, fpRot, k));

            // Holding RMB means you're driving: fall through to the normal look/move path (which
            // runs this same frame — the pose above just trails it by one frame during the blend).
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
            // Crouching is silent for BOTH roles — the same rule that suppresses Bigfoot's tracks.
            // Half speed buys you leaving no trace at all, in prints or in sound.
            if (_crouching) { _stepTimer = 0f; return; }
            if (!_lastStep.Moving || !_sim.Grounded) { _stepTimer = 0f; return; }

            _stepTimer -= Time.deltaTime;
            if (_stepTimer > 0f) return;
            audio.PlayFootstep(_lastStep.Sprinting, IsBigfoot, (float)Specialties.FootstepVolumeMul(Specialty.Value));
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

        /// <summary>Bigfoot leaves a scent memory of where it's been (fuel for the senses overlay).</summary>
        private void UpdateScent()
        {
            if (!IsBigfoot) return;
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

        private void HandleLook()
        {
#if ENABLE_INPUT_SYSTEM
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
                if (IsBigfoot) _pressedLeap = true; else _pressedVault = true;
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
                    Climb = locked && IsBigfoot && HPKeybinds.Down(kb, HPAction.Jump),
                    // Bigfoot sprints too (it replaced the charge burst): the sim's BigfootSpeedMul
                    // makes its sprint outrun a searcher's outright, and sprinting drains its stamina
                    // the same way — which is what finally gives Bigfoot's stamina bar a purpose.
                    Sprint = locked && HPKeybinds.Down(kb, HPAction.Sprint),
                    Crouch = locked && HPKeybinds.Down(kb, HPAction.Crouch),
                    Dt = frameDt,
                };
                _crouching = input.Crouch;
                _pressedJump = _pressedLeap = _pressedVault = false;

                _lastStep = Movement.StepPlayer(_sim, input, _world, CurrentModifiers());
                transform.position = new Vector3((float)_sim.X, (float)_sim.FeetY, (float)_sim.Z);
            }
#endif
        }

        private StepModifiers CurrentModifiers()
        {
            var gm = GameManager.Instance;
            float esc = gm != null ? gm.EscSpeed.Value : 1f;
            double speedMul = 1;
            if (IsBigfoot) speedMul = esc;               // per-night escalation only
            else if (Slowed.Value) speedMul = Sim.Player.SlowFactor;

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
            if (IsBigfoot)
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

                // Eli's camera flash — an aimed burst that stuns Bigfoot and paints Eli for it.
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
                // exactly the beat Bigfoot is waiting for if it chose to guard the spill.
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
            // crouching Bigfoot leaves no trail. Send it the instant it changes rather than waiting
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
                    if (p == null || p == this || p.IsBigfoot) continue;
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
                if (p == this || p.IsBigfoot || p.Status.Value != StatusIncap) continue;
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

        /// <summary>Sim yaw from the replicated body rotation (server uses this for aim cones).</summary>
        public float SimYawFromTransform()
        {
            return (transform.eulerAngles.y - 180f) * Mathf.Deg2Rad;
        }

        // Owner HUD accessors (local sim is fresher than the SyncVars for your own bars).
        public bool OwnGrounded => _sim == null || _sim.Grounded;
        /// <summary>True while the sim is actually sprinting this frame (not merely holding the key).</summary>
        public bool OwnSprinting => _lastStep.Sprinting;
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
            if (!base.IsOwner || IsBigfoot || Status.Value != StatusActive) return;
            ServerPing(x, z);
            if (HPAudio.Instance != null) HPAudio.Instance.PlayOnce(HPAudio.PingDrop, 0.5f);
        }

        /// <summary>Bigfoot picks a destination cave off the map; the server re-validates every rule.</summary>
        public void RequestCaveTravel(int index)
        {
            if (!base.IsOwner || !IsBigfoot || Status.Value != StatusActive) return;
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
        public void ServerSetWantsBigfoot(bool wants)
        {
            WantsBigfoot.Value = wants;
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
            if (_visualRoot != null) Destroy(_visualRoot.gameObject);
            _visualRoot = new GameObject("Visual").transform;
            _visualRoot.SetParent(transform, false);

            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<UnityEngine.Collider>());
            body.transform.SetParent(_visualRoot, false);
            _bodyRenderer = body.GetComponent<MeshRenderer>();

            if (IsBigfoot)
            {
                body.transform.localScale = new Vector3(1.3f, 1.35f, 1.3f); // capsule h=2 -> ~2.7 m
                body.transform.localPosition = new Vector3(0f, 1.35f, 0f);
                _baseBodyColor = MeshUtil.Rgb(0x2a2018);
                _bodyMat = MeshUtil.Lit(_baseBodyColor);
                _bodyRenderer.sharedMaterial = _bodyMat;
                foreach (float sx in new[] { -0.16f, 0.16f })
                {
                    var eye = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(eye.GetComponent<UnityEngine.Collider>());
                    eye.transform.SetParent(_visualRoot, false);
                    eye.transform.localScale = Vector3.one * 0.12f;
                    eye.transform.localPosition = new Vector3(sx, 2.3f, 0.55f);
                    eye.GetComponent<MeshRenderer>().sharedMaterial =
                        MeshUtil.Emissive(Color.black, MeshUtil.Rgb(0xffcc55), 3.5f);
                }
            }
            else
            {
                body.transform.localScale = new Vector3(0.8f, 0.9f, 0.8f);
                body.transform.localPosition = new Vector3(0f, 0.9f, 0f);
                int hex = SpecialtyColors.TryGetValue(Specialty.Value ?? "", out int c) ? c : 0x9aa2aa;
                _baseBodyColor = MeshUtil.Rgb(hex);
                _bodyMat = MeshUtil.Lit(_baseBodyColor);
                _bodyRenderer.sharedMaterial = _bodyMat;

                var lightGo = new GameObject("Flashlight");
                lightGo.transform.SetParent(_visualRoot, false);
                lightGo.transform.localPosition = new Vector3(0.18f, (float)Sim.Player.EyeHeight - 0.15f, 0.1f);
                _flashlight = lightGo.AddComponent<Light>();
                _flashlight.type = LightType.Spot;
                // Matches the web build's SpotLight(0xfff2d6, 140, 60, 0.5, 0.4): Three's `angle` is a
                // HALF-angle in radians (0.5 rad -> ~57 deg full cone) and penumbra 0.4 gives the soft
                // edge. Intensity is eyeball-tuned for URP, which uses different units than Three.
                _flashlight.range = 60f;
                _flashlight.spotAngle = 57f;
                _flashlight.innerSpotAngle = 24f;
                _flashlight.intensity = 11f;
                _flashlight.color = MeshUtil.Rgb(0xfff2d0);
                _flashlight.shadows = LightShadows.None;
                _flashlight.enabled = false;

                // REC light — a red bead above a filming searcher's head (like the web rec light).
                var rec = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(rec.GetComponent<UnityEngine.Collider>());
                rec.transform.SetParent(_visualRoot, false);
                rec.transform.localScale = Vector3.one * 0.09f;
                rec.transform.localPosition = new Vector3(0f, 2.05f, 0f);
                _recDot = rec.GetComponent<MeshRenderer>();
                _recDot.sharedMaterial = MeshUtil.Emissive(Color.black, Color.red, 4f);
                _recDot.enabled = false;
            }

            // First person: hide your own body, keep your light.
            if (base.IsOwner)
                foreach (var r in _visualRoot.GetComponentsInChildren<Renderer>()) r.enabled = false;
        }

        /// <summary>
        /// Remote players' footsteps: accrue the ground they cover and emit a positional step each
        /// stride (Bigfoot's gait is longer and heavier). This is how you hear someone in the dark.
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

            float stride = IsBigfoot ? 2.3f : 1.7f;
            _remoteStepDist += moved;
            if (_remoteStepDist < stride) return;
            _remoteStepDist = 0f;
            audio.PlayAt(IsBigfoot ? HPAudio.FootstepHeavy : HPAudio.FootstepSoft, pos,
                (IsBigfoot ? 0.7f : 0.4f) * (float)Specialties.FootstepVolumeMul(Specialty.Value), 7f);
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

            if (_visualRoot != null)
            {
                var lying = Status.Value == StatusIncap;
                var targetRot = lying ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
                _visualRoot.localRotation = Quaternion.Slerp(_visualRoot.localRotation, targetRot, Time.deltaTime * 8f);
            }

            // Camera eye follows crouch for the owner; remotes just show the capsule.
            if (base.IsOwner && Specialty.Value != null && _bodyRenderer != null && !IsBigfoot)
            {
                // Body colour can change when the specialty is dealt mid-session.
                int hex = SpecialtyColors.TryGetValue(Specialty.Value, out int c) ? c : 0x9aa2aa;
                _baseBodyColor = MeshUtil.Rgb(hex);
            }
        }
    }
}
