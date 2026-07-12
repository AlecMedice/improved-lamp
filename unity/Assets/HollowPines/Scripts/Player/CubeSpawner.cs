// R1 vertical slice — spawns one PlayerCube per connected client. Canonical FishNet spawner pattern
// (mirrors the sample PlayerSpawner): the server listens for each client finishing scene load and
// spawns a NetworkObject owned by that connection. Attach to a scene object and assign the cube
// prefab + a spawn origin.
using FishNet;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;

namespace HollowPines.Player
{
    public class CubeSpawner : MonoBehaviour
    {
        [SerializeField] private NetworkObject _playerPrefab;
        [SerializeField] private Vector3 _spawnOrigin = new Vector3(0f, 0.5f, 0f);
        [SerializeField] private float _spacing = 2f;

        private NetworkManager _networkManager;
        private int _spawned;

        private void Start()
        {
            _networkManager = InstanceFinder.NetworkManager;
            if (_networkManager == null)
            {
                Debug.LogError("[CubeSpawner] No NetworkManager found in the scene.");
                return;
            }
            _networkManager.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
        }

        private void OnDestroy()
        {
            if (_networkManager != null)
                _networkManager.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        }

        private void OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
        {
            if (!asServer) return; // spawn only on the authoritative side
            if (_playerPrefab == null)
            {
                Debug.LogError("[CubeSpawner] Player prefab is not assigned.");
                return;
            }
            Vector3 pos = _spawnOrigin + new Vector3(_spawned++ * _spacing, 0f, 0f);
            NetworkObject nob = Instantiate(_playerPrefab, pos, Quaternion.identity);
            _networkManager.ServerManager.Spawn(nob, conn); // owned by the joining connection
        }
    }
}
