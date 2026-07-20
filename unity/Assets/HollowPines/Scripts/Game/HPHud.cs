// Throwaway IMGUI HUD for the gameplay-functional milestone — the Unity stand-in for the web
// build's DOM HUD (index.html + ui/HUD.ts). Real UI lands in R5. Reads replicated state from
// GameManager/HPPlayer SyncVars; the local player's own bars read the fresher local sim.
using FishNet;
using UnityEngine;

namespace HollowPines.Game
{
    public class HPHud : MonoBehaviour
    {
        private static float _roarFlashUntil;
        private static Vector3 _roarPos;
        private static float _roarAt = -999f;
        private static Texture2D _blobTex;
        private static float _flashAt = -999f;
        private static Vector3 _flashPos;
        private static float _toastAt = -999f;
        private static string _toast;
        private static bool _toastGood;
        private Vector2 _briefScroll;
        /// <summary>Height reserved under the briefing's scroll area for the GOT IT row.</summary>
        private const float BriefingButtonRow = 40f;
        /// <summary>Mirrors GameManager's GrabRadius — the prompt must not promise a grab the server refuses.</summary>
        private const float GrabPromptRange = 3.5f;
        /// <summary>Esc pause overlay (resume/settings/leave). Toggled by HPPlayer's Esc handler.</summary>
        public static bool PauseOpen;
        /// <summary>
        /// The dusk briefing is up and OWNS THE CURSOR — it has a GOT IT button to click. HPPlayer
        /// checks this so its click-to-recapture doesn't steal the mouse back out from under it.
        /// </summary>
        public static bool BriefingOpen;
        private bool _wantsBigfoot;
        private bool _showHelp;
        private int _lastNight = 1;
        private float _nightFadeAt = -999f;
        private byte _lastPhase = 255;
        private static bool _briefingDismissed = true;
        private float _briefingShownAt;

        /// <summary>
        /// One dusk-briefing card per searcher persona: their job title, who they are (from
        /// docs/STORY.md), and what they can actually DO. The perk lines quote real shipped numbers,
        /// computed from the same constants the systems use (Specialties multipliers × the base
        /// values in MapView/GameManager) so the card can never drift from the mechanics.
        ///
        /// Only SHIPPED abilities are listed — a card must never teach a control that does nothing.
        /// (As of the evidence-system build, every persona's full kit IS implemented.)
        /// </summary>
        private struct PersonaCard
        {
            public string Role;
            public string Story;
            public string[] Perks;
        }

        private static PersonaCard CardFor(string specialty)
        {
            // Effective values, derived rather than duplicated.
            float clueWin = MapView.ClueWindow * (float)Sim.Specialties.ClueWindowMul(specialty);
            float evSight = MapView.EvidenceSight * (float)Sim.Specialties.EvidenceSightMul(specialty);
            float hear = MapView.HearRange * (float)Sim.Specialties.HearRangeMul(specialty);
            float filmRange = (float)(GameManager.FilmRange * Sim.Specialties.FilmRangeMul(specialty));
            float revive = (float)(GameManager.ReviveSeconds * Sim.Specialties.ReviveMul(specialty));
            float stamMax = (float)Sim.Specialties.StaminaMax(specialty);
            string markKey = HPKeybinds.Label(HPAction.Mark);
            string holdKey = HPKeybinds.Label(HPAction.Revive);
            string flashKey = HPKeybinds.Label(HPAction.Flash);

            switch (specialty)
            {
                case "analysis":
                    return new PersonaCard
                    {
                        Role = "Lead Cryptozoologist",
                        Story = "Calm, exacting, the one who keeps the team from bolting. You came here\n" +
                                "for a paper, not a legend — and you intend to leave with proof either way.",
                        Perks = new[]
                        {
                            "You alone carry the casting kit — nobody else can work a print",
                            $"Find a deep, fresh track and hold [{holdKey}] to cast it: that's proof",
                            "You never have to get near it to do your job — but the cast takes time",
                        },
                    };

                case "photo":
                    return new PersonaCard
                    {
                        Role = "Wildlife Photographer",
                        Story = "You've photographed grizzlies at six feet and lived. You believe,\n" +
                                "with some evidence, that the lens is braver than you are.",
                        Perks = new[]
                        {
                            $"Your camera films from {filmRange:0} m — 25% farther than anyone's",
                            $"[{flashKey}] fires the flash: it stuns Bigfoot on the spot (3 s)",
                            "...and paints you for it to find. One charge per night",
                        },
                    };

                case "tracking":
                    return new PersonaCard
                    {
                        Role = "Park Ranger & Tracker",
                        Story = "You know these woods better than the maps do. You lost a friend to\n" +
                                "them once, and you have never once said so out loud.",
                        Perks = new[]
                        {
                            $"Fresh tracks stay readable for {clueWin:0.#} s — 50% longer than anyone's",
                            $"You spot evidence from {evSight:0} m — twice as far",
                            "You move quietly: your footsteps carry at half volume",
                            $"[{markKey}] plants a trail mark your whole team can see",
                            "Find the deep prints and lead Mara to them — she does the casting",
                        },
                    };

                case "sound":
                    return new PersonaCard
                    {
                        Role = "Audio Engineer & Podcaster",
                        Story = "You came out here for a season finale. You are staying for your life.\n" +
                                "The parabolic mic hears what the dark is hiding long before your eyes do.",
                        Perks = new[]
                        {
                            $"You hear Bigfoot from {hear:0} m — nearly twice as far",
                            "A roar paints its direction on your HUD for 10 s",
                            "Your recordings bank 15% faster than the team's",
                        },
                    };

                case "endurance":
                    return new PersonaCard
                    {
                        Role = "Survivalist & Field Medic",
                        Story = "Ex-search-and-rescue. You treat panic like a wound to be dressed:\n" +
                                "quickly, without comment, and before it spreads to anyone else.",
                        Perks = new[]
                        {
                            $"You revive downed teammates in {revive:0.#} s — nearly twice as fast",
                            $"Deeper reserves: {stamMax:0} stamina instead of 100",
                            "Sprinting, leaping and climbing cost you 15% less",
                            $"[{holdKey}] near a teammate hands them a spare battery (+50, once a night)",
                        },
                    };

                default: // no persona dealt (solo play, or more than five searchers)
                    return new PersonaCard
                    {
                        Role = "Searcher",
                        Story = "One of five who walked in past the trailhead sign at dusk.",
                        Perks = new[] { "No specialty assigned this match" },
                    };
            }
        }

        /// <summary>Called via ObserversRpc when Bigfoot roars anywhere — flash a warning; Theo also gets a bearing.</summary>
        public static void NotifyRoar(Vector3 pos)
        {
            _roarFlashUntil = Time.time + 2.5f;
            _roarPos = pos;
            _roarAt = Time.time;
        }

        private void OnGUI()
        {
            var gm = GameManager.Instance;
            if (gm == null || !InstanceFinder.IsClientStarted) return; // LocalNetworkHud handles connecting
            var me = HPPlayer.Local;

            // A fresh match start raises the dusk briefing.
            if (gm.MatchPhase.Value != _lastPhase)
            {
                if (gm.MatchPhase.Value == GameManager.PhasePlaying)
                {
                    _briefingDismissed = false;
                    _briefingShownAt = Time.time;
                    BriefingOpen = true;
                }
                else
                {
                    BriefingOpen = false;
                }
                _lastPhase = gm.MatchPhase.Value;
            }

            switch (gm.MatchPhase.Value)
            {
                case GameManager.PhaseLobby: DrawLobby(gm, me); break;
                case GameManager.PhasePlaying: DrawPlaying(gm, me); break;
                case GameManager.PhaseResults: DrawResults(gm); break;
            }

            if (me != null && gm.MatchPhase.Value != GameManager.PhaseLobby) DrawHelpToggle(me);
            if (me != null && gm.MatchPhase.Value == GameManager.PhasePlaying && !_briefingDismissed)
                DrawBriefing(gm, me);
            if (PauseOpen) DrawPause(gm);
        }

        // ------------------------------------------------------------------ lobby

        private void DrawLobby(GameManager gm, HPPlayer me)
        {
            // Clamped to the window like the briefing card — the player list grows, and a fixed
            // 420 px panel ran off the bottom of a short view.
            float lw = Mathf.Min(340f, Screen.width - 40f);
            float lh = Mathf.Min(420f, Screen.height - 80f);
            GUILayout.BeginArea(new Rect((Screen.width - lw) / 2f, 40f, lw, lh));
            GUILayout.Box("HOLLOW PINES — camp lobby");
            GUILayout.Label("Hold right-mouse to look around while you wait — release to click.");
            GUILayout.Space(6f);

            foreach (var p in HPPlayer.All)
            {
                if (p == null) continue;
                string tag = p.WantsBigfoot.Value ? "  [wants Bigfoot]" : "";
                string self = p == me ? "  (you)" : "";
                GUILayout.Label($"• {p.PlayerName.Value}{tag}{self}");
            }
            GUILayout.Space(6f);

            if (me != null)
            {
                bool w = GUILayout.Toggle(_wantsBigfoot, " I want to play Bigfoot");
                if (w != _wantsBigfoot)
                {
                    _wantsBigfoot = w;
                    me.ServerSetWantsBigfoot(w);
                }
            }
            if (InstanceFinder.IsHostStarted && GUILayout.Button("START MATCH", GUILayout.Height(34f)))
                gm.ServerStartMatch();
            if (!InstanceFinder.IsHostStarted)
                GUILayout.Label("(waiting for the host to start)");
            GUILayout.Space(8f);
            if (GUILayout.Button("LEAVE", GUILayout.Height(24f))) Disconnect();
            GUILayout.EndArea();

            // While you're holding right-mouse you ARE controlling a person standing in the dark,
            // which on its own looks exactly like a frozen frame. A crosshair and a line of text are
            // the cheapest way to say "this is live, you're driving".
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                GUI.Box(new Rect(Screen.width / 2f - 2f, Screen.height / 2f - 2f, 5f, 5f), GUIContent.none);
                var hint = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 13 };
                Color oc = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, 0.75f);
                GUI.Label(new Rect(0f, Screen.height - 46f, Screen.width, 22f),
                    "WASD to walk  ·  release right-mouse to return to the view", hint);
                GUI.color = oc;
            }
        }

        private static void Disconnect()
        {
            if (InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started)
                InstanceFinder.ClientManager.StopConnection();
            if (InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started)
                InstanceFinder.ServerManager.StopConnection(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true; // the TitleMenu takes over from here
        }

        // ------------------------------------------------------------------ playing

        private void DrawPlaying(GameManager gm, HPPlayer me)
        {
            // World-anchored layers first so HUD text draws over them.
            if (me != null)
            {
                DrawSenses(me);
                DrawRevealed(me);
                DrawNametags(me);
            }

            // Top bar: night, clock, phase, and PROOF — footage plus physical evidence, broken out so
            // the team can see which half is fragile (footage dies with a grab; evidence never does).
            float t = gm.TimeOfDay.Value;
            int proof = gm.VideosCaptured.Value + gm.EvidenceCollected.Value;
            string top = $"Night {gm.NightNumber.Value}/{gm.TotalNights.Value}    {GameManager.ClockString(t)}    ({GameManager.PhaseName(t)})    " +
                         $"STORED {proof}/{gm.VideosRequired.Value}  ({gm.VideosCaptured.Value} film · {gm.EvidenceCollected.Value} casts)";
            GUI.Box(new Rect(Screen.width / 2f - 290f, 8f, 580f, 26f), top);

            if (Time.time < _roarFlashUntil)
            {
                var style = new GUIStyle(GUI.skin.box) { fontSize = 22 };
                GUI.color = new Color(1f, 0.35f, 0.25f);
                GUI.Box(new Rect(Screen.width / 2f - 160f, 44f, 320f, 34f), "A ROAR ECHOES THROUGH THE PINES", style);
                GUI.color = Color.white;
            }

            if (me == null) return;

            // Crosshair.
            GUI.Box(new Rect(Screen.width / 2f - 2f, Screen.height / 2f - 2f, 5f, 5f), GUIContent.none);

            if (!me.IsBigfoot) DrawSearcher(gm, me);
            else DrawBigfoot(gm, me);

            DrawRoarDirection(me);
            DrawStatusOverlay(me);
            DrawDuffelManifest(gm, me);
            DrawPrompts(me);
            DrawToast();
            DrawScreenTints(gm, me);
        }

        private void DrawSearcher(GameManager gm, HPPlayer me)
        {
            float y = Screen.height - 78f;
            Bar(new Rect(16f, y, 220f, 16f), me.OwnBattery / 100f, $"Battery {(int)me.OwnBattery}%", me.OwnFlashOn ? new Color(1f, 0.95f, 0.6f) : Color.gray);
            // Stamina: drains while sprinting (18/s), regenerates at 12/s. Turns amber while actually
            // sprinting and red once exhausted, so the mechanic is legible instead of a silent number.
            float stamMax = (float)Sim.Specialties.StaminaMax(me.Specialty.Value);
            bool exhausted = me.OwnExhausted;
            Color stamCol = exhausted ? new Color(1f, 0.45f, 0.4f)
                : me.OwnSprinting ? new Color(1f, 0.8f, 0.35f) : new Color(0.5f, 0.9f, 0.5f);
            string stamLabel = exhausted ? $"Stamina {(int)me.OwnStamina} — EXHAUSTED"
                : me.OwnSprinting ? $"Stamina {(int)me.OwnStamina} — sprinting" : $"Stamina {(int)me.OwnStamina}";
            Bar(new Rect(16f, y + 22f, 220f, 16f), me.OwnStamina / stamMax, stamLabel, stamCol);
            if (me.CharacterName.Value != "")
                GUI.Label(new Rect(16f, y + 42f, 300f, 22f), $"{me.CharacterName.Value} — {me.Specialty.Value}");

            // What you're CARRYING — the single most decision-relevant number a searcher has. It is
            // worth nothing until it's in the duffel and it dies with you, so it shouts.
            if (me.CarriedTotal > 0)
            {
                var parts = new System.Collections.Generic.List<string>();
                if (me.CarriedFilm.Value > 0) parts.Add($"{me.CarriedFilm.Value} tape{(me.CarriedFilm.Value == 1 ? "" : "s")}");
                if (me.CarriedCasts.Value > 0) parts.Add($"{me.CarriedCasts.Value} cast{(me.CarriedCasts.Value == 1 ? "" : "s")}");
                float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 3f);
                Color oc = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.45f, pulse);
                var st = new GUIStyle(GUI.skin.box) { fontSize = 15, fontStyle = FontStyle.Bold };
                GUI.Box(new Rect(Screen.width / 2f - 190f, 40f, 380f, 26f),
                    $"CARRYING {string.Join(" · ", parts)} — UNSAVED", st);
                GUI.color = oc;
            }

            // Persona ability charge (Eli's flash, Sam's spare battery) — only shown to who has one.
            if (me.Specialty.Value == "photo" || me.Specialty.Value == "endurance")
            {
                bool ready = me.AbilityCharges.Value > 0;
                string what = me.Specialty.Value == "photo"
                    ? $"Camera flash [{HPKeybinds.Label(HPAction.Flash)}]"
                    : $"Spare battery [{HPKeybinds.Label(HPAction.Revive)}]";
                Color oc = GUI.color;
                GUI.color = ready ? new Color(0.7f, 0.9f, 1f) : new Color(0.55f, 0.55f, 0.55f);
                GUI.Label(new Rect(16f, y + 62f, 340f, 22f),
                    ready ? $"{what} — ready" : $"{what} — used (returns at dusk)");
                GUI.color = oc;
            }

            // Collecting physical evidence: a slow channel, so it needs a visible bar.
            if (me.CollectProgress01.Value > 0f)
            {
                var r = new Rect(Screen.width / 2f - 110f, Screen.height / 2f + 56f, 220f, 20f);
                Bar(r, me.CollectProgress01.Value,
                    $"Casting the print…  {(int)(me.CollectProgress01.Value * 100)}%", new Color(0.9f, 0.82f, 0.55f));
            }

            // Flash gave you away — Bigfoot can see exactly where you are.
            if (me.RevealedFor.Value > 0f)
            {
                Color oc = GUI.color;
                GUI.color = new Color(1f, 0.55f, 0.5f);
                GUI.Label(new Rect(Screen.width / 2f - 120f, Screen.height / 2f + 82f, 240f, 22f),
                    $"IT SEES YOU — {me.RevealedFor.Value:0.0}s",
                    new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold });
                GUI.color = oc;
            }

            // Filming feedback (server-authoritative progress).
            if (me.Filming.Value || me.FilmProgress.Value > 0f)
            {
                GUI.color = new Color(1f, 0.3f, 0.3f);
                GUI.Box(new Rect(Screen.width / 2f - 90f, Screen.height / 2f + 30f, 180f, 24f),
                    me.FilmProgress.Value > 0f ? $"● REC  {(int)(me.FilmProgress.Value * 100)}%" : "● REC  (no subject)");
                GUI.color = Color.white;
            }
        }

        private void DrawBigfoot(GameManager gm, HPPlayer me)
        {
            float y = Screen.height - 98f; // taller stack: stamina + charge + leap + roar
            float stamMax = 100f;
            Bar(new Rect(16f, y, 220f, 16f), me.OwnStamina / stamMax, $"Stamina {(int)me.OwnStamina}", new Color(0.9f, 0.6f, 0.4f));
            if (me.Dazzled.Value)
            {
                GUI.color = Color.white;
                GUI.Box(new Rect(Screen.width / 2f - 120f, Screen.height / 2f + 30f, 240f, 24f), "DAZZLED — roar and grab locked!");
            }
            // Ability readouts, ported from the web build's Bigfoot HUD lines. Every ability states
            // itself even when READY — a label that only appears during cooldown reads as "nothing
            // is happening" and made charge look broken.
            Color old = GUI.color;
            string sprintKey = HPKeybinds.Label(HPAction.Sprint), jumpKey = HPKeybinds.Label(HPAction.Jump);

            // Sprint: Bigfoot's replacement for the old charge burst. It outruns a searcher outright,
            // but it spends the same stamina pool the leap does — so the chase is a resource decision.
            bool exhausted = me.OwnExhausted;
            GUI.color = exhausted ? new Color(1f, 0.45f, 0.4f)
                : me.OwnSprinting ? new Color(1f, 0.8f, 0.35f) : new Color(0.7f, 0.9f, 0.7f);
            GUI.Label(new Rect(16f, y + 22f, 300f, 22f),
                exhausted ? "Winded — let it recover"
                    : me.OwnSprinting ? "RUNNING THEM DOWN" : $"Sprint ({sprintKey}) — faster than they are");

            // Leap (Space): this is what actually SPENDS Bigfoot's stamina (30 per leap).
            bool canLeap = me.OwnStamina >= (float)Sim.Player.LeapStaminaCost;
            GUI.color = canLeap ? new Color(0.7f, 0.9f, 0.7f) : new Color(1f, 0.6f, 0.5f);
            GUI.Label(new Rect(16f, y + 42f, 300f, 22f),
                canLeap ? $"Leap ready ({jumpKey})  −{(int)Sim.Player.LeapStaminaCost}" : "Leap: low stamina");

            // Roar cooldown is server-owned (escalates per night) and arrives as a SyncVar.
            float rr = me.RoarCooldownLeft;
            GUI.color = me.Dazzled.Value ? new Color(1f, 0.6f, 0.5f)
                : rr > 0f ? new Color(0.8f, 0.8f, 0.8f) : new Color(1f, 0.55f, 0.35f);
            GUI.Label(new Rect(16f, y + 62f, 300f, 22f),
                me.Dazzled.Value ? "Roar: DAZZLED" : rr > 0f ? $"Roar in {rr:0.0}s" : "ROAR READY — RMB");
            GUI.color = old;
        }

        private void DrawStatusOverlay(HPPlayer me)
        {
            int secs = Mathf.CeilToInt(me.StatusEndsIn.Value);
            if (me.Status.Value == HPPlayer.StatusFrozen)
            {
                Banner($"FROZEN IN FEAR — {secs}s", new Color(0.6f, 0.8f, 1f));
            }
            else if (me.Status.Value == HPPlayer.StatusIncap)
            {
                Banner(me.BeingRevived.Value
                    ? $"DOWN — a teammate is reviving you...  {(int)(me.ReviveProgress01.Value * 100)}%"
                    : $"DOWN — {secs}s (a teammate can revive you faster)", new Color(1f, 0.5f, 0.4f));
            }
            else
            {
                // Reviving someone else?
                foreach (var p in HPPlayer.All)
                {
                    if (p == null || p == me || p.Status.Value != HPPlayer.StatusIncap || !p.BeingRevived.Value) continue;
                    if ((p.transform.position - me.transform.position).sqrMagnitude < 16f)
                    {
                        Banner($"Reviving {p.PlayerName.Value}...  {(int)(p.ReviveProgress01.Value * 100)}% — keep holding {HPKeybinds.Label(HPAction.Revive)}",
                            new Color(0.6f, 1f, 0.6f));
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Theo (Sound) hears WHERE the roar came from: a bearing arrow on a ring around the
        /// crosshair, persisting roarDirPersistSec (10 s) and fading out. Everyone else only gets
        /// the text flash. Bearing is relative to the local look yaw (sim convention).
        /// </summary>
        private void DrawRoarDirection(HPPlayer me)
        {
            if (me.IsBigfoot || me.Specialty.Value != "sound") return;
            float persist = (float)Sim.Specialties.RoarDirPersistSec(me.Specialty.Value);
            float age = Time.time - _roarAt;
            if (persist <= 0f || age < 0f || age > persist) return;

            float dx = _roarPos.x - me.transform.position.x;
            float dz = _roarPos.z - me.transform.position.z;
            if (dx * dx + dz * dz < 1e-4f) return;
            float yaw = me.SimYawFromTransform();
            // Project onto the sim basis: forward = (-sin, -cos), right = (cos, -sin).
            float fwd = dx * -Mathf.Sin(yaw) + dz * -Mathf.Cos(yaw);
            // Negated: that expression is the SIM's right, which is the player's visual LEFT in
            // Unity's mirrored XZ plane (see the handedness note in HPPlayer.StepSim). Without this
            // the arrow points to the wrong side of the screen.
            float right = -(dx * Mathf.Cos(yaw) + dz * -Mathf.Sin(yaw));
            float screenAngle = Mathf.Atan2(right, fwd); // 0 = ahead, +90deg = to your right

            var center = new Vector2(Screen.width / 2f, Screen.height / 2f);
            float radius = 110f;
            var pos = center + new Vector2(Mathf.Sin(screenAngle), -Mathf.Cos(screenAngle)) * radius;

            float alpha = Mathf.Clamp01(1f - age / persist);
            Color old = GUI.color;
            GUI.color = new Color(1f, 0.45f, 0.3f, 0.25f + 0.75f * alpha);
            var style = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(screenAngle * Mathf.Rad2Deg, pos);
            GUI.Label(new Rect(pos.x - 18f, pos.y - 18f, 36f, 36f), "▲", style);
            GUI.matrix = m;
            GUI.Label(new Rect(pos.x - 40f, pos.y + 14f, 80f, 20f),
                new GUIContent("roar"), new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter });
            GUI.color = old;
        }

        /// <summary>
        /// Bigfoot's senses overlay (V) — predator vision: searchers pulse warm through the trees,
        /// and Bigfoot's own recent scent trail glows green. Screen-space blobs (drawn regardless of
        /// occlusion), client-only, exactly the web build's render-toggle idea.
        /// </summary>
        private void DrawSenses(HPPlayer me)
        {
            if (!me.IsBigfoot || !HPPlayer.SensesOn) return;
            var cam = Camera.main;
            if (cam == null) return;
            EnsureBlobTex();
            Color old = GUI.color;

            foreach (var p in HPPlayer.All)
            {
                if (p == null || p == me || p.IsBigfoot) continue;
                float d = Vector3.Distance(p.transform.position, me.transform.position);
                if (d > 140f) continue;
                Vector3 sp = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 1.2f);
                if (sp.z <= 0f) continue;
                float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * 5f);
                if (p.RevealedFor.Value > 0f) pulse = 1f; // a flashed searcher blazes
                float alpha = Mathf.Clamp01(1f - d / 140f) * pulse;
                float size = Mathf.Lerp(14f, 52f, Mathf.Clamp01(1f - d / 140f));
                GUI.color = new Color(1f, 0.55f, 0.25f, alpha * 0.85f);
                GUI.DrawTexture(new Rect(sp.x - size / 2f, Screen.height - sp.y - size / 2f, size, size), _blobTex);
            }

            foreach (var (pos, born) in HPPlayer.ScentTrail)
            {
                float age = Time.time - born;
                if (age > HPPlayer.ScentLifetime) continue;
                Vector3 sp = cam.WorldToScreenPoint(pos + Vector3.up * 0.4f);
                if (sp.z <= 0f) continue;
                float alpha = 0.5f * (1f - age / HPPlayer.ScentLifetime);
                GUI.color = new Color(0.35f, 1f, 0.5f, alpha);
                GUI.DrawTexture(new Rect(sp.x - 7f, Screen.height - sp.y - 7f, 14f, 14f), _blobTex);
            }
            GUI.color = old;
        }

        /// <summary>
        /// Bigfoot sees a flashed searcher blazing through the trees for RevealedFor seconds —
        /// the price Eli pays for the stun. Independent of the senses overlay (V): the reveal is
        /// something the flash DID to Eli, not something Bigfoot chose to switch on.
        /// </summary>
        private void DrawRevealed(HPPlayer me)
        {
            if (!me.IsBigfoot) return;
            var cam = Camera.main;
            if (cam == null) return;
            EnsureBlobTex();
            Color old = GUI.color;

            foreach (var p in HPPlayer.All)
            {
                if (p == null || p.IsBigfoot || p.RevealedFor.Value <= 0f) continue;
                Vector3 sp = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 1.4f);
                if (sp.z <= 0f) continue;

                float flare = 0.6f + 0.4f * Mathf.Sin(Time.time * 9f);
                float size = 64f;
                GUI.color = new Color(1f, 0.95f, 0.85f, 0.55f * flare);
                GUI.DrawTexture(new Rect(sp.x - size / 2f, Screen.height - sp.y - size / 2f, size, size), _blobTex);
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                GUI.Label(new Rect(sp.x - 60f, Screen.height - sp.y + 26f, 120f, 20f), "FLASH",
                    new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontStyle = FontStyle.Bold });
            }
            GUI.color = old;
        }

        /// <summary>Called via ObserversRpc when a camera flash fires — a white bloom for everyone near it.</summary>
        public static void NotifyFlash(Vector3 pos, bool hitBigfoot)
        {
            _flashAt = Time.time;
            _flashPos = pos;
        }

        /// <summary>Proof stored in the duffel — the one moment in this game that is purely good news.</summary>
        public static void NotifyDeposit(int count)
        {
            _toastAt = Time.time;
            _toast = count == 1 ? "EVIDENCE STORED — that one is safe for good"
                                : $"{count} PIECES STORED — they're safe for good";
            _toastGood = true;
        }

        /// <summary>Carried proof destroyed by a grab.</summary>
        public static void NotifyProofLost(int count)
        {
            _toastAt = Time.time;
            _toast = count == 1 ? "IT TOOK THE TAPE — one piece lost, unsaved"
                                : $"{count} PIECES LOST — everything they were carrying";
            _toastGood = false;
        }

        private void DrawToast()
        {
            float age = Time.time - _toastAt;
            if (age < 0f || age > 3.5f || _toast == null) return;
            Color old = GUI.color;
            float a = age < 2.5f ? 1f : 1f - (age - 2.5f);
            GUI.color = _toastGood ? new Color(0.65f, 1f, 0.7f, a) : new Color(1f, 0.5f, 0.45f, a);
            var st = new GUIStyle(GUI.skin.box) { fontSize = 16, fontStyle = FontStyle.Bold };
            GUI.Box(new Rect(Screen.width / 2f - 230f, Screen.height * 0.24f, 460f, 30f), _toast, st);
            GUI.color = old;
        }

        /// <summary>Searchers see teammate nametags (name + persona) — never Bigfoot's.</summary>
        private void DrawNametags(HPPlayer me)
        {
            if (me.IsBigfoot) return;
            var cam = Camera.main;
            if (cam == null) return;
            Color old = GUI.color;
            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 12 };

            foreach (var p in HPPlayer.All)
            {
                if (p == null || p == me || p.IsBigfoot) continue;
                float d = Vector3.Distance(p.transform.position, me.transform.position);
                if (d > 80f) continue;
                Vector3 sp = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 2.25f);
                if (sp.z <= 0f) continue;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1.15f - d / 80f));
                string line = p.PlayerName.Value;
                if (p.CharacterName.Value != "") line += "\n" + p.CharacterName.Value;
                GUI.Label(new Rect(sp.x - 80f, Screen.height - sp.y, 160f, 34f), line, style);
            }
            GUI.color = old;
        }

        /// <summary>Full-screen tints: frozen (icy), incapacitated (fade to black), dazzle (white-out), night rollover fade.</summary>
        private void DrawScreenTints(GameManager gm, HPPlayer me)
        {
            Color old = GUI.color;
            var full = new Rect(0f, 0f, Screen.width, Screen.height);

            if (me.Status.Value == HPPlayer.StatusFrozen)
            {
                GUI.color = new Color(0.55f, 0.75f, 1f, 0.20f);
                GUI.DrawTexture(full, Texture2D.whiteTexture);
            }
            else if (me.Status.Value == HPPlayer.StatusIncap)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.45f + 0.1f * Mathf.Sin(Time.time * 2.2f));
                GUI.DrawTexture(full, Texture2D.whiteTexture);
            }
            if (me.IsBigfoot && me.Dazzled.Value)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
                GUI.DrawTexture(full, Texture2D.whiteTexture);
            }

            // A camera flash going off nearby blooms the screen white — brighter the closer you are.
            float fAge = Time.time - _flashAt;
            if (fAge >= 0f && fAge < 0.45f)
            {
                float dist = Vector3.Distance(_flashPos, me.transform.position);
                float near = Mathf.Clamp01(1f - dist / 35f);
                if (near > 0f)
                {
                    GUI.color = new Color(1f, 1f, 0.97f, (1f - fAge / 0.45f) * near * 0.85f);
                    GUI.DrawTexture(full, Texture2D.whiteTexture);
                }
            }

            // Fade to black and back when a night rolls over (the web build's between-nights fade).
            if (gm.NightNumber.Value != _lastNight)
            {
                _lastNight = gm.NightNumber.Value;
                _nightFadeAt = Time.time;
            }
            float ft = Time.time - _nightFadeAt;
            if (ft >= 0f && ft < 3f)
            {
                GUI.color = new Color(0f, 0f, 0f, 1f - Mathf.Abs(ft - 1.5f) / 1.5f);
                GUI.DrawTexture(full, Texture2D.whiteTexture);
                if (ft > 1f && ft < 2f)
                {
                    var st = new GUIStyle(GUI.skin.label) { fontSize = 26, alignment = TextAnchor.MiddleCenter };
                    GUI.color = new Color(1f, 1f, 1f, 1f - Mathf.Abs(ft - 1.5f) * 2f);
                    GUI.Label(new Rect(0f, Screen.height / 2f - 30f, Screen.width, 60f), $"NIGHT {gm.NightNumber.Value}", st);
                }
            }
            GUI.color = old;
        }

        /// <summary>The dusk briefing — the web build's night-1 tutorial card, per role/persona.</summary>
        private void DrawBriefing(GameManager gm, HPPlayer me)
        {
            if (Time.time - _briefingShownAt > 45f) { DismissBriefing(); return; }

            // The match-start teleport locks the cursor (TargetTeleport) and this card needs it back
            // to be clickable. Re-freeing it every frame is deliberate: the teleport TargetRpc and the
            // matchPhase SyncVar are separate messages with no guaranteed order, so a one-shot unlock
            // could be undone by a teleport that lands a frame later. Self-healing beats ordering.
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            // Keyboard dismissal too — a stuck cursor must never be able to trap you on this card.
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame))
            {
                DismissBriefing();
                return;
            }
#endif

            // Fit the card to the window instead of assuming a big one. These were fixed 560x400
            // boxes, which clipped on a short editor Game view (and would clip on any small window):
            // clamp to the screen, centre it, and scroll anything that still doesn't fit so no line
            // is ever unreachable. The GOT IT row lives OUTSIDE the scroll so it's always visible.
            float w = Mathf.Min(560f, Screen.width - 40f);
            float h = Mathf.Min(me.IsBigfoot ? 250f : 400f, Screen.height - 60f);
            var r = new Rect((Screen.width - w) / 2f, Mathf.Max(20f, (Screen.height - h) / 2f - 10f), w, h);
            GUI.Box(r, GUIContent.none);
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 16f, w - 40f, h - 30f));
            _briefScroll = GUILayout.BeginScrollView(_briefScroll, GUIStyle.none, GUI.skin.verticalScrollbar,
                GUILayout.Height(h - 30f - BriefingButtonRow));

            var title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, wordWrap = true };
            var role = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Italic };
            role.normal.textColor = new Color(0.85f, 0.72f, 0.5f);
            var story = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            story.normal.textColor = new Color(0.78f, 0.80f, 0.80f);
            var header = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            header.normal.textColor = new Color(0.65f, 0.85f, 0.95f);
            var perk = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            perk.normal.textColor = new Color(0.90f, 0.92f, 0.90f);
            var objective = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            objective.normal.textColor = new Color(0.95f, 0.85f, 0.7f);

            if (me.IsBigfoot)
            {
                GUILayout.Label("YOU ARE THE ONE THEY'RE LOOKING FOR", title);
                GUILayout.Space(4f);
                GUILayout.Label("Older than the road in, and patient. They have cameras and torches;\n" +
                                "you have the dark, the distance, and legs that outrun any of them.", story);
                GUILayout.Space(8f);
                GUILayout.Label("YOUR NIGHT", header);
                GUILayout.Label($"• Survive all {gm.TotalNights.Value} nights — they must STORE {gm.VideosRequired.Value} pieces of proof\n" +
                                "• RMB roars — everything close enough freezes where it stands\n" +
                                "• LMB takes a frozen searcher — everything they were carrying is gone\n" +
                                "• They carry it back to a bag at the camp. You cannot touch the bag.\n" +
                                "• So take them on the walk home, while their hands are full\n" +
                                "• Soft ground holds your deepest tracks — tread them out before they cast them", perk);
            }
            else
            {
                PersonaCard card = CardFor(me.Specialty.Value);
                string who = me.CharacterName.Value != "" ? me.CharacterName.Value.ToUpperInvariant() : "A SEARCHER";
                string holdKey = HPKeybinds.Label(HPAction.Revive);

                GUILayout.Label("YOU ARE " + who, title);
                GUILayout.Label(card.Role, role);
                GUILayout.Space(6f);
                GUILayout.Label(card.Story, story);
                GUILayout.Space(8f);

                GUILayout.Label("YOUR EDGE", header);
                foreach (string p in card.Perks) GUILayout.Label("• " + p, perk);
                GUILayout.Space(8f);

                GUILayout.Label("THE JOB", header);
                GUILayout.Label($"• Store {gm.VideosRequired.Value} pieces of proof in the duffel by the RV, in {gm.TotalNights.Value} nights\n" +
                                "• FILM it (hold RMB in range) — fast, but you have to get close\n" +
                                "• Or CAST its deepest tracks (Mara's kit) — safe, but slow\n" +
                                "• What you carry is worth NOTHING until it's in the bag — and it dies with you\n" +
                                "• The duffel is the one thing out here it cannot touch. Walk it home.", objective);
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("GOT IT", GUILayout.Width(120f), GUILayout.Height(28f))) DismissBriefing();
            GUILayout.Label("   (or press Space)   ·   [H] shows controls anytime",
                new GUIStyle(GUI.skin.label) { fontSize = 12 });
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>Close the briefing and hand the mouse back to the game. Safe to call twice.</summary>
        public static void DismissBriefing()
        {
            if (_briefingDismissed && !BriefingOpen) return;
            _briefingDismissed = true;
            BriefingOpen = false;

            var gm = GameManager.Instance;
            if (!PauseOpen && !MapView.IsOpen && gm != null && gm.MatchPhase.Value == GameManager.PhasePlaying)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        /// <summary>Esc pause overlay: resume, live settings, leave. (The web build's gear menu.)</summary>
        private void DrawPause(GameManager gm)
        {
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float w = Mathf.Min(360f, Screen.width - 40f);
            float ph = Mathf.Min(330f, Screen.height - 60f);
            var r = new Rect((Screen.width - w) / 2f, Mathf.Max(20f, (Screen.height - ph) / 2f), w, ph);
            GUI.Box(r, "PAUSED");
            GUILayout.BeginArea(new Rect(r.x + 20f, r.y + 30f, w - 40f, r.height - 44f));

            GUILayout.Label($"mouse sensitivity  ({HPSettings.MouseSensMul:0.00}x)");
            HPSettings.MouseSensMul = GUILayout.HorizontalSlider(HPSettings.MouseSensMul, 0.2f, 3f);
            GUILayout.Space(6f);
            GUILayout.Label($"master volume  ({(int)(HPSettings.MasterVolume * 100)}%)");
            HPSettings.MasterVolume = GUILayout.HorizontalSlider(HPSettings.MasterVolume, 0f, 1f);
            GUILayout.Space(6f);
            // The single biggest frame-rate lever — lower renders fewer pixels and upscales.
            GUILayout.Label($"resolution scale  ({(int)(HPSettings.RenderScale * 100)}%)  — lower = faster");
            HPSettings.RenderScale = Mathf.Round(
                GUILayout.HorizontalSlider(HPSettings.RenderScale, 0.4f, 1f) * 20f) / 20f;
            HPSettings.Apply();
            GUILayout.Space(14f);

            if (GUILayout.Button("RESUME", GUILayout.Height(32f)))
            {
                PauseOpen = false;
                HPSettings.Save();
                if (gm.MatchPhase.Value == GameManager.PhasePlaying)
                {
                    Cursor.lockState = CursorLockMode.Locked;
                    Cursor.visible = false;
                }
            }
            GUILayout.Space(6f);
            if (GUILayout.Button("LEAVE GAME", GUILayout.Height(26f)))
            {
                PauseOpen = false;
                HPSettings.Save();
                Disconnect();
            }
            GUILayout.EndArea();
        }

        /// <summary>
        /// Standing at the duffel opens its manifest — the bag is a place you READ as well as deposit
        /// into. Shows exactly what the team has banked, broken down by kind, what you're about to add,
        /// and how much is still missing. Searchers only: Bigfoot can't interact with the duffel at all,
        /// and already sees the running total on the top bar.
        /// </summary>
        private void DrawDuffelManifest(GameManager gm, HPPlayer me)
        {
            if (me.IsBigfoot || me.Status.Value != HPPlayer.StatusActive) return;
            if (!GameManager.AtDuffel(me.transform.position)) return;

            int film = gm.VideosCaptured.Value, casts = gm.EvidenceCollected.Value;
            int stored = film + casts, need = gm.VideosRequired.Value;

            float w = Mathf.Min(300f, Screen.width - 40f);
            var r = new Rect(Screen.width - w - 20f, Mathf.Max(70f, Screen.height * 0.28f), w, 168f);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.72f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Box(r, GUIContent.none);

            GUILayout.BeginArea(new Rect(r.x + 14f, r.y + 10f, r.width - 28f, r.height - 20f));
            var head = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            head.normal.textColor = new Color(1f, 0.88f, 0.6f);
            GUILayout.Label("EVIDENCE DUFFEL", head);

            var row = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            row.normal.textColor = new Color(0.88f, 0.9f, 0.88f);
            GUILayout.Label($"Video tapes ....... {film}", row);
            GUILayout.Label($"Plaster casts ..... {casts}", row);
            GUILayout.Space(4f);

            var total = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            total.normal.textColor = stored >= need ? new Color(0.6f, 1f, 0.65f) : new Color(0.85f, 0.92f, 1f);
            GUILayout.Label($"SECURED  {stored} / {need}", total);

            var note = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            note.normal.textColor = new Color(0.7f, 0.75f, 0.72f);
            if (stored >= need) GUILayout.Label("That's the case. Get out of the woods.", note);
            else GUILayout.Label($"{need - stored} more piece{(need - stored == 1 ? "" : "s")} needed.", note);

            if (me.CarriedTotal > 0)
            {
                GUILayout.Space(4f);
                var add = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
                add.normal.textColor = new Color(1f, 0.85f, 0.45f);
                GUILayout.Label($"+{me.CarriedTotal} in your pack, unsaved", add);
            }
            GUILayout.EndArea();
        }

        /// <summary>Contextual key prompts from the shared sim (vault a log, climb a structure, revive).</summary>
        private void DrawPrompts(HPPlayer me)
        {
            if (Cursor.lockState != CursorLockMode.Locked || me.Status.Value != HPPlayer.StatusActive) return;
            var world = WorldBuilder.World;
            Vector3 pos = me.transform.position;
            string prompt = null;

            if (me.IsBigfoot)
            {
                // Dragging someone already? The grab key drops them.
                HPPlayer dragged = null, nearestFrozen = null;
                float bestD = GrabPromptRange * GrabPromptRange;
                foreach (var p in HPPlayer.All)
                {
                    if (p == null || p == me || p.IsBigfoot) continue;
                    if (p.GrabberObjectId.Value == me.ObjectId) { dragged = p; break; }
                    if (p.Status.Value != HPPlayer.StatusFrozen) continue;
                    float d = (p.transform.position - pos).sqrMagnitude;
                    if (d <= bestD) { bestD = d; nearestFrozen = p; }
                }

                if (dragged != null) prompt = $"LMB — drop {dragged.PlayerName.Value}";
                else if (nearestFrozen != null) prompt = $"LMB — grab {nearestFrozen.PlayerName.Value}";
                else
                {
                    // Standing in a cave mouth: the fast-travel network is only reachable through the
                    // map, so without this prompt the whole cave system is invisible (the web build
                    // shows the same line — Game.ts's caveReady prompt).
                    int cave = Sim.Caves.NearestCaveIndex(world.Caves, pos.x, pos.z);
                    if (cave >= 0)
                    {
                        prompt = me.CaveReadyIn > 0f
                            ? $"cave system recharging ({me.CaveReadyIn:0.0}s)"
                            : $"[{HPKeybinds.Label(HPAction.Map)}] — travel to another cave";
                    }
                    else
                    {
                        var support = Sim.Collision.ClimbSupport(world.Climbables, world.GetHeight, pos.x, pos.z,
                            Sim.Player.Radius, Sim.Player.ClimbReach);
                        if (support.HasValue && !support.Value.Over)
                            prompt = $"hold {HPKeybinds.Label(HPAction.Jump)} — climb";
                    }
                }
            }
            else
            {
                if (me.OwnGrounded && Sim.Collision.LogOverlap(world.FallenLogs, pos.x, pos.z, Sim.Player.Radius) > 0)
                    prompt = $"{HPKeybinds.Label(HPAction.Jump)} — vault the log";

                // The hold-action prompt comes from the same resolver the input uses, so the prompt
                // can never advertise a different action than the key will actually perform.
                var hold = me.HoldActionTarget();
                if (hold.Kind != HPPlayer.HoldAction.None) prompt = hold.Label;
            }
            if (prompt == null) return;
            var style = new GUIStyle(GUI.skin.box) { fontSize = 14 };
            GUI.Box(new Rect(Screen.width / 2f - 130f, Screen.height - 130f, 260f, 26f), prompt, style);
        }

        private static void EnsureBlobTex()
        {
            if (_blobTex != null) return;
            const int s = 32;
            _blobTex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            for (int y = 0; y < s; y++)
            {
                for (int x = 0; x < s; x++)
                {
                    float dx = (x - s / 2f) / (s / 2f), dy = (y - s / 2f) / (s / 2f);
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    _blobTex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            }
            _blobTex.Apply();
        }

        // ------------------------------------------------------------------ results

        private void DrawResults(GameManager gm)
        {
            string text = gm.Winner.Value == GameManager.WinnerHunters
                ? "THE FOOTAGE IS OUT THERE — the searchers win!"
                : "THE FOREST KEEPS ITS SECRET — Bigfoot survives all three nights!";
            GUILayout.BeginArea(new Rect(Screen.width / 2f - 220f, Screen.height / 2f - 70f, 440f, 150f));
            var style = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = true };
            GUILayout.Box(text, style, GUILayout.Height(60f));
            if (InstanceFinder.IsHostStarted && GUILayout.Button("RETURN TO LOBBY", GUILayout.Height(30f)))
                gm.ServerReturnToLobby();
            if (GUILayout.Button("LEAVE GAME", GUILayout.Height(24f))) Disconnect();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------------ bits

        private void DrawHelpToggle(HPPlayer me)
        {
#if ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.hKey.wasPressedThisFrame) _showHelp = !_showHelp;
#endif
            GUI.Label(new Rect(Screen.width - 150f, Screen.height - 24f, 145f, 22f), "[H] controls");
            if (!_showHelp) return;

            // Key names come from HPKeybinds so the card follows any rebinding.
            string sprint = HPKeybinds.Label(HPAction.Sprint), jump = HPKeybinds.Label(HPAction.Jump);
            string crouch = HPKeybinds.Label(HPAction.Crouch), flash = HPKeybinds.Label(HPAction.Flashlight);
            string revive = HPKeybinds.Label(HPAction.Revive), mark = HPKeybinds.Label(HPAction.Mark);
            string senses = HPKeybinds.Label(HPAction.Senses);
            string map = HPKeybinds.Label(HPAction.Map), ping = HPKeybinds.Label(HPAction.Ping);
            string flashAbility = HPKeybinds.Label(HPAction.Flash);
            string help = me.IsBigfoot
                ? $"WASD move · mouse look · {jump} leap / hold near a boulder-RV-tower to CLIMB\n" +
                  $"{sprint} SPRINT (faster than they are) · RMB ROAR (freeze) · LMB GRAB / drop\n" +
                  $"{senses} senses overlay · {crouch} crouch · {map} map (cave fast-travel)\n" +
                  "tread on your own deep tracks to ruin them · survive all 3 nights"
                : $"WASD move · mouse look · {sprint} sprint · {jump} jump / VAULT a log\n" +
                  $"{flash} flashlight (dazzles Bigfoot if held on it) · RMB hold = FILM Bigfoot\n" +
                  $"{revive} hold = STORE proof at the duffel / revive / cast (Mara) / battery (Sam)\n" +
                  $"{crouch} crouch · {mark} trail mark (Wren) · {flashAbility} camera flash (Eli)\n" +
                  $"{map} map · {ping} stakeout ping · store {NeededProof()} proof in the duffel to win";
            var style = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
            GUI.Box(new Rect(Screen.width / 2f - 280f, Screen.height - 146f, 560f, 114f), help, style);
        }

        /// <summary>The replicated proof target — never hardcode it in copy; it changed 3 → 6 once already.</summary>
        private static int NeededProof()
        {
            return GameManager.Instance != null ? GameManager.Instance.VideosRequired.Value : 6;
        }

        private static void Bar(Rect r, float frac, string label, Color color)
        {
            GUI.Box(r, GUIContent.none);
            var fill = new Rect(r.x + 2f, r.y + 2f, (r.width - 4f) * Mathf.Clamp01(frac), r.height - 4f);
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 6f, r.y - 1f, r.width, r.height + 4f), label);
        }

        private static void Banner(string text, Color color)
        {
            var style = new GUIStyle(GUI.skin.box) { fontSize = 18 };
            Color old = GUI.color;
            GUI.color = color;
            GUI.Box(new Rect(Screen.width / 2f - 200f, Screen.height / 2f + 70f, 400f, 32f), text, style);
            GUI.color = old;
        }
    }
}
