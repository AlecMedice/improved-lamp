// R1 vertical slice — Steam lobby + FishNet relay wiring for host-authoritative play.
//
// This is the R.E.P.O. topology: one player hosts (runs the FishNet server AND a local client, i.e.
// a listen server); friends join through a Steam lobby, and traffic rides Steam Datagram Relay via
// the FishySteamworks transport (NAT punch-through + relay fallback, no port-forwarding).
//
// REQUIRES (see unity/README.md): FishNet, Steamworks.NET (provides `SteamManager`), and the
// FishySteamworks transport set on the NetworkManager's TransportManager. These packages are why
// this file does NOT compile outside a configured Unity project — that's expected.
using FishNet;
using Steamworks;
using UnityEngine;

namespace HollowPines.Net
{
    public class SteamLobby : MonoBehaviour
    {
        public static SteamLobby Instance { get; private set; }

        // Lobby metadata key: the host advertises its SteamID64 here so joiners know who to dial.
        private const string HostAddressKey = "HostAddress";

        private CSteamID _currentLobby;

        // Steamworks.NET callbacks (kept as fields so the GC doesn't collect them).
        private Callback<LobbyCreated_t> _lobbyCreated;
        private Callback<GameLobbyJoinRequested_t> _joinRequested;
        private Callback<LobbyEnter_t> _lobbyEntered;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            if (!SteamManager.Initialized)
            {
                Debug.LogError("[SteamLobby] Steam not initialized. Is the Steam client running, and is " +
                               "steam_appid.txt (containing 480) beside the executable / project root?");
                return;
            }
            _lobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            _lobbyEntered = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
        }

        /// <summary>Host: create a friends-only Steam lobby. Starting the server happens in the callback.</summary>
        public void HostLobby(int maxPlayers = 6)
        {
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
        }

        /// <summary>Host: open the Steam overlay invite dialog for the current lobby.</summary>
        public void InviteFriends()
        {
            if (_currentLobby != CSteamID.Nil)
                SteamFriends.ActivateGameOverlayInviteDialog(_currentLobby);
        }

        // --- callbacks ---

        private void OnLobbyCreated(LobbyCreated_t cb)
        {
            if (cb.m_eResult != EResult.k_EResultOK)
            {
                Debug.LogError($"[SteamLobby] Lobby creation failed: {cb.m_eResult}");
                return;
            }
            _currentLobby = new CSteamID(cb.m_ulSteamIDLobby);
            // Advertise our SteamID so joiners can set it as the client address.
            SteamMatchmaking.SetLobbyData(_currentLobby, HostAddressKey, SteamUser.GetSteamID().ToString());

            // Listen server: server + local client on the same process.
            InstanceFinder.ServerManager.StartConnection();
            InstanceFinder.ClientManager.StartConnection();
        }

        private void OnJoinRequested(GameLobbyJoinRequested_t cb)
        {
            // Friend clicked "Join Game" / accepted an invite in the Steam overlay.
            SteamMatchmaking.JoinLobby(cb.m_steamIDLobby);
        }

        private void OnLobbyEntered(LobbyEnter_t cb)
        {
            _currentLobby = new CSteamID(cb.m_ulSteamIDLobby);

            // The host also "enters" its own lobby — it's already serving, so don't dial itself.
            if (InstanceFinder.ServerManager.Started) return;

            string hostAddress = SteamMatchmaking.GetLobbyData(_currentLobby, HostAddressKey);
            var transport = InstanceFinder.TransportManager.Transport as FishySteamworks.FishySteamworks;
            if (transport == null)
            {
                Debug.LogError("[SteamLobby] FishySteamworks is not the active transport on the TransportManager.");
                return;
            }
            transport.SetClientAddress(hostAddress); // host SteamID64; relay resolves the route
            InstanceFinder.ClientManager.StartConnection();
        }
    }
}
