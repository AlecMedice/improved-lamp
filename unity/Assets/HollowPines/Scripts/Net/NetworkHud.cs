// R1 vertical slice — throwaway on-screen controls. Real UI comes in R5; this just drives the
// host/join/invite flow so you can prove the relay works. Attach to any GameObject in the scene.
using FishNet;
using UnityEngine;

namespace HollowPines.Net
{
    public class NetworkHud : MonoBehaviour
    {
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 260, 240));
            GUILayout.Box("Hollow Pines — R1 relay slice");

            bool serverUp = InstanceFinder.ServerManager != null && InstanceFinder.ServerManager.Started;
            bool clientUp = InstanceFinder.ClientManager != null && InstanceFinder.ClientManager.Started;

            if (!serverUp && !clientUp)
            {
                if (GUILayout.Button("Host (Steam lobby)"))
                    SteamLobby.Instance.HostLobby();
                GUILayout.Label("A friend joins from the Steam overlay:\n" +
                                "their friends list → you → Join Game,\n" +
                                "or accept an invite.");
            }
            else
            {
                GUILayout.Label(serverUp ? "Hosting (listen server)" : "Connected as client");
                if (serverUp && GUILayout.Button("Invite friends"))
                    SteamLobby.Instance.InviteFriends();
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
