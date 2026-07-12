// R1 local slice — the NO-STEAM launcher (fast path to a launchable desktop build). Uses FishNet's
// default Tugboat (UDP) transport, so you can Host and Client on one machine (or across a LAN) without
// any Steam packages. When you're ready for the relay, install the Steam packages, add the HP_STEAM
// scripting define, and use SteamLobby/NetworkHud instead (they're dormant until then).
//
// Attach this to the same GameObject as the NetworkManager.
using FishNet;
using UnityEngine;

namespace HollowPines.Net
{
    public class LocalNetworkHud : MonoBehaviour
    {
        [SerializeField] private string _address = "127.0.0.1"; // localhost by default; a LAN IP for two machines

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 260, 240));
            GUILayout.Box("Hollow Pines — R1 local slice");

            bool serverUp = InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started;
            bool clientUp = InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started;

            if (!serverUp && !clientUp)
            {
                if (GUILayout.Button("Host (server + client)"))
                {
                    InstanceFinder.ServerManager.StartConnection();
                    InstanceFinder.ClientManager.StartConnection();
                }
                if (GUILayout.Button($"Client -> {_address}"))
                    InstanceFinder.ClientManager.StartConnection(_address);
                if (GUILayout.Button("Server only"))
                    InstanceFinder.ServerManager.StartConnection();
            }
            else
            {
                GUILayout.Label(serverUp && clientUp ? "Hosting (listen server)" : serverUp ? "Server" : "Client");
                if (GUILayout.Button("Disconnect"))
                {
                    if (clientUp) InstanceFinder.ClientManager.StopConnection();
                    if (serverUp) InstanceFinder.ServerManager.StopConnection(true);
                }
            }
            GUILayout.EndArea();
        }
    }
}
