// The title card + main menu shown when the exe opens — mechanical structure borrowed from
// R.E.P.O.: a vertical stack of START NEW GAME / JOIN GAME / SETTINGS / QUIT over a live 3D
// backdrop (a slow drift around the dark camp clearing; the forest is already built by
// WorldBuilder). Replaces the R1 debug LocalNetworkHud in the Forest scene.
//
// Visible whenever no connection is up, so a disconnect (host quit, kicked, cancel) lands you
// back here automatically. JOIN is direct address (Tugboat) until Path B adds Steam invites —
// then JOIN becomes the friends/lobby flow and this menu doesn't need restructuring.
// IMGUI like the rest of the throwaway UI; the real face goes on in R5.
using FishNet;
using UnityEngine;

namespace HollowPines.Game
{
    public class TitleMenu : MonoBehaviour
    {
        private enum Page { Root, Join, Settings }

        private Page _page = Page.Root;
        private string _address = "127.0.0.1";
        private float _connectStartedAt = -1f; // >=0 while a join attempt is in flight
        private string _error;                 // last hosting/joining failure, shown under the buttons
        private GUIStyle _titleStyle, _subStyle, _buttonStyle, _labelStyle, _errorStyle;

        private void Awake()
        {
            HPSettings.Load();
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            HPKeybinds.UpdateCapture(); // key-rebind capture (settings page)
#endif
            bool connected = Connected();
            SetTitleLighting(!connected); // the backdrop is lit well above gameplay dusk
            if (connected) { _connectStartedAt = -1f; return; }

            // At the title: keep the cursor free and drift the camera slowly around camp.
            if (Cursor.lockState != CursorLockMode.None)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            var cam = Camera.main;
            if (cam != null && cam.transform.parent == null) OrbitCamp(cam);
        }

        /// <summary>
        /// The slow cinematic drift around the campfire. Shared with the camp lobby (HPPlayer keeps
        /// it running there whenever the player isn't holding right-mouse to take control), so the
        /// game never drops from a moving title card to a motionless first-person shot.
        /// </summary>
        public static void OrbitCamp(Camera cam)
        {
            // A close, low orbit so the fire and the RV fill the frame and the treeline sits behind
            // them — the original wide 26 m arc read as an empty dark field.
            float a = Time.time * 0.05f;
            var target = new Vector3(0f, 1.2f, 0f); // the campfire
            cam.transform.position = target + new Vector3(Mathf.Sin(a) * 12f, 3.2f, Mathf.Cos(a) * 12f);
            cam.transform.LookAt(target + new Vector3(0f, 0.6f, 0f));
        }

        /// <summary>Raise the world's brightness while the menu is up, and restore it on connect.</summary>
        private void SetTitleLighting(bool on)
        {
            if (WorldBuilder.TitleMode == on) return;
            WorldBuilder.TitleMode = on;
            if (WorldBuilder.Instance != null) WorldBuilder.Instance.InvalidatePalette(); // re-apply now
            if (PostFX.Instance != null) PostFX.Instance.SetTitleBrightness(on);

            // Join attempt timed out? (Tugboat fails quietly if nothing is listening.)
            if (_connectStartedAt >= 0f && Time.time - _connectStartedAt > 8f)
            {
                InstanceFinder.ClientManager.StopConnection();
                _connectStartedAt = -1f;
            }
        }

        private static bool Connected()
        {
            return (InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started) ||
                   (InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started);
        }

        private void OnGUI()
        {
            if (Connected()) return;
            EnsureStyles();

            // Backdrop scrim so the buttons read over the forest.
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.45f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = old;

            float cx = Screen.width / 2f;
            GUI.Label(new Rect(0, Screen.height * 0.14f, Screen.width, 90f), "HOLLOW PINES", _titleStyle);
            GUI.Label(new Rect(0, Screen.height * 0.14f + 86f, Screen.width, 30f),
                "five went looking for proof — something in the pines was looking back", _subStyle);

            switch (_page)
            {
                case Page.Root: DrawRoot(cx); break;
                case Page.Join: DrawJoin(cx); break;
                case Page.Settings: DrawSettings(cx); break;
            }

            if (!string.IsNullOrEmpty(_error))
                GUI.Label(new Rect(0f, Screen.height * 0.80f, Screen.width, 44f),
                    _error + "\n(full details in the Console)", _errorStyle);

            GUI.Label(new Rect(12f, Screen.height - 26f, 600f, 22f),
                "pre-alpha · local/LAN build · Steam relay + friend invites coming", _labelStyle);
        }

        private void DrawRoot(float cx)
        {
            float y = Screen.height * 0.42f;
            if (MenuButton(cx, ref y, "START NEW GAME"))
            {
                // Never fail silently: a menu button that does nothing is the worst failure mode.
                // StartConnection returns false on a refused socket, and a throw here (e.g. from a
                // scene NetworkObject's OnStartServer) would otherwise strand us on this screen.
                _error = null;
                try
                {
                    if (!InstanceFinder.ServerManager.StartConnection())
                        _error = "server failed to start — is port 7770 already in use?";
                    else if (!InstanceFinder.ClientManager.StartConnection())
                        _error = "server started, but the local client could not connect";
                }
                catch (System.Exception e)
                {
                    _error = "start failed: " + e.GetType().Name + " — " + e.Message;
                    Debug.LogException(e); // full stack trace lands in the Console
                }
            }
            if (MenuButton(cx, ref y, "JOIN GAME")) _page = Page.Join;
            if (MenuButton(cx, ref y, "SETTINGS")) _page = Page.Settings;
            if (MenuButton(cx, ref y, "QUIT"))
            {
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
            }
        }

        private void DrawJoin(float cx)
        {
            float y = Screen.height * 0.42f;
            bool connecting = _connectStartedAt >= 0f;

            GUI.Label(new Rect(cx - 150f, y, 300f, 22f), "host address", _labelStyle);
            y += 24f;
            GUI.enabled = !connecting;
            _address = GUI.TextField(new Rect(cx - 150f, y, 300f, 34f), _address, 64);
            GUI.enabled = true;
            y += 46f;

            if (connecting)
            {
                GUI.Label(new Rect(cx - 150f, y, 300f, 30f), "connecting...", _subStyle);
                y += 40f;
                if (MenuButton(cx, ref y, "CANCEL"))
                {
                    InstanceFinder.ClientManager.StopConnection();
                    _connectStartedAt = -1f;
                }
                return;
            }

            if (MenuButton(cx, ref y, "CONNECT"))
            {
                InstanceFinder.ClientManager.StartConnection(_address.Trim());
                _connectStartedAt = Time.time;
            }
            if (MenuButton(cx, ref y, "BACK")) _page = Page.Root;
        }

        private void DrawSettings(float cx)
        {
            float y = Screen.height * 0.30f;

            GUI.Label(new Rect(cx - 170f, y, 340f, 22f), "player name", _labelStyle);
            y += 24f;
            HPSettings.PlayerName = GUI.TextField(new Rect(cx - 170f, y, 340f, 32f), HPSettings.PlayerName, 16);
            y += 44f;

            GUI.Label(new Rect(cx - 170f, y, 340f, 22f),
                $"mouse sensitivity  ({HPSettings.MouseSensMul:0.00}x)", _labelStyle);
            y += 22f;
            HPSettings.MouseSensMul = GUI.HorizontalSlider(new Rect(cx - 170f, y, 340f, 20f), HPSettings.MouseSensMul, 0.2f, 3f);
            y += 32f;

            GUI.Label(new Rect(cx - 170f, y, 340f, 22f),
                $"master volume  ({(int)(HPSettings.MasterVolume * 100)}%)", _labelStyle);
            y += 22f;
            HPSettings.MasterVolume = GUI.HorizontalSlider(new Rect(cx - 170f, y, 340f, 20f), HPSettings.MasterVolume, 0f, 1f);
            y += 34f;

            bool fs = GUI.Toggle(new Rect(cx - 170f, y, 340f, 24f), Screen.fullScreen, " fullscreen");
            if (fs != Screen.fullScreen) Screen.fullScreen = fs;
            y += 34f;

            // Key rebinding (Esc and the mouse stay fixed, like the web build).
            GUI.Label(new Rect(cx - 170f, y, 340f, 22f), "controls (click, then press a key)", _labelStyle);
            y += 24f;
            foreach (HPAction a in System.Enum.GetValues(typeof(HPAction)))
            {
                GUI.Label(new Rect(cx - 170f, y + 3f, 150f, 22f), ActionName(a), _labelStyle);
                string keyLabel = HPKeybinds.Capturing == a ? "press a key..." : HPKeybinds.Label(a);
                if (GUI.Button(new Rect(cx - 10f, y, 180f, 24f), keyLabel))
                    HPKeybinds.Capturing = a;
                y += 27f;
            }
            if (GUI.Button(new Rect(cx - 170f, y, 150f, 24f), "reset defaults")) HPKeybinds.ResetDefaults();
            y += 36f;

            if (MenuButton(cx, ref y, "BACK"))
            {
                HPKeybinds.Capturing = null;
                HPSettings.Save();
                _page = Page.Root;
            }
        }

        private static string ActionName(HPAction a)
        {
            switch (a)
            {
                case HPAction.Sprint: return "sprint";
                case HPAction.Jump: return "jump / vault / leap / climb";
                case HPAction.Crouch: return "crouch";
                case HPAction.Flashlight: return "flashlight";
                case HPAction.Revive: return "revive (hold)";
                case HPAction.Mark: return "trail mark (Wren)";
                case HPAction.Senses: return "senses (Bigfoot)";
                case HPAction.Map: return "map";
                case HPAction.Ping: return "stakeout ping";
                case HPAction.Flash: return "camera flash (Eli)";
                default: return a.ToString();
            }
        }

        private bool MenuButton(float cx, ref float y, string label)
        {
            bool hit = GUI.Button(new Rect(cx - 150f, y, 300f, 46f), label, _buttonStyle);
            y += 56f;
            return hit;
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null) return;
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            _titleStyle.normal.textColor = new Color(0.88f, 0.93f, 0.9f);
            _subStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            _subStyle.normal.textColor = new Color(0.65f, 0.72f, 0.7f);
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 20, fontStyle = FontStyle.Bold };
            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _labelStyle.normal.textColor = new Color(0.6f, 0.65f, 0.63f);
            _errorStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14, alignment = TextAnchor.UpperCenter, wordWrap = true,
            };
            _errorStyle.normal.textColor = new Color(1f, 0.45f, 0.4f);
        }
    }
}
