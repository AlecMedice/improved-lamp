// R1 one-click scene wiring + build for Path A (local/no-Steam). Editor-only ("Editor" folder).
//
// Automates the "Scene wiring" section of unity/README.md so a fresh Unity 6 (URP) project with
// FishNet imported needs a single click:
//     Hollow Pines → Set Up R1 Scene (Path A)      then Play → "Host (server + client)".
//     Hollow Pines → Build Windows (Path A)        → Build/Windows/HollowPines.exe
// Also callable headless (editor closed):
//     Unity.exe -batchmode -quit -projectPath <proj> -executeMethod HollowPines.EditorTools.R1Setup.SetUpScene
//     Unity.exe -batchmode -quit -projectPath <proj> -executeMethod HollowPines.EditorTools.R1Setup.BuildWindows
//
// Two Unity-6-template traps this corrects (verified against FishNet 4.7.2 source):
//   1. The URP template ships Active Input Handling = "Input System Package (new)" ONLY. Legacy
//      UnityEngine.Input throws there, and IMGUI (our OnGUI HUDs) gets no input in standalone
//      players → set it to "Both" (2). Needs one editor restart to take effect in Play mode.
//   2. FishNet's NetworkTransform defaults to CLIENT-authoritative, which stomps the ServerRpc
//      host-movement pattern (the owner would keep replicating its unmoved transform back over
//      the server's moves) → force _clientAuthoritative = false on the prefab.
using System.IO;
using System.Linq;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using HollowPines.Net;
using HollowPines.Player;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HollowPines.EditorTools
{
    public static class R1Setup
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string PrefabDir = "Assets/HollowPines/Prefabs";
        private const string PrefabPath = PrefabDir + "/PlayerCube.prefab";
        private const string BuildPath = "Build/Windows/HollowPines.exe";

        [MenuItem("Hollow Pines/Set Up R1 Scene (Path A)")]
        public static void SetUpScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[R1Setup] Exit Play mode first.");
                return;
            }

            SetInputHandlingToBoth();

            // Fresh scene with the default Main Camera + Directional Light.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(4f, 1f, 4f); // 40x40 m

            // NetworkManager + Tugboat (UDP) + the no-Steam HUD, per unity/README.md.
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            nmGo.AddComponent<Tugboat>();
            nmGo.AddComponent<LocalNetworkHud>();

            NetworkObject prefabNob = CreateCubePrefab();

            // Import the new prefab so FishNet's generator adds it to DefaultPrefabObjects, then
            // point the NetworkManager's SpawnablePrefabs at that collection.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            AssignDefaultPrefabObjects(nm);

            var spawnerGo = new GameObject("CubeSpawner");
            var spawner = spawnerGo.AddComponent<CubeSpawner>();
            var spawnerSo = new SerializedObject(spawner);
            spawnerSo.FindProperty("_playerPrefab").objectReferenceValue = prefabNob;
            spawnerSo.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log("[R1Setup] R1 scene wired → " + ScenePath + ". If WASD or the HUD buttons don't " +
                      "respond in Play mode, restart the editor once (input-handling change), then " +
                      "Play → 'Host (server + client)'.");
        }

        [MenuItem("Hollow Pines/Build Windows (Path A)")]
        public static void BuildWindows()
        {
            var report = BuildPipeline.BuildPlayer(
                new[] { ScenePath }, BuildPath, BuildTarget.StandaloneWindows64, BuildOptions.None);
            var summary = report.summary;
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                Debug.Log($"[R1Setup] Build OK → {Path.GetFullPath(BuildPath)} ({summary.totalSize / (1024 * 1024)} MB)");
            else
                Debug.LogError($"[R1Setup] Build {summary.result}: {summary.totalErrors} error(s) — see the log.");
        }

        private static NetworkObject CreateCubePrefab()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "PlayerCube";
            cube.AddComponent<NetworkObject>();
            var nt = cube.AddComponent<NetworkTransform>();
            cube.AddComponent<PlayerCube>();

            // Host-authoritative movement: the server applies moves, so the owner must NOT be
            // transform-authoritative (FishNet default is client-auth — trap #2 above).
            var ntSo = new SerializedObject(nt);
            ntSo.FindProperty("_clientAuthoritative").boolValue = false;
            ntSo.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(PrefabDir);
            var prefab = PrefabUtility.SaveAsPrefabAsset(cube, PrefabPath);
            Object.DestroyImmediate(cube);
            return prefab.GetComponent<NetworkObject>();
        }

        internal static void SetInputHandlingToBoth()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings == null)
            {
                Debug.LogWarning("[R1Setup] Couldn't load ProjectSettings.asset to adjust input handling.");
                return;
            }
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("activeInputHandler");
            if (prop != null && prop.intValue != 2)
            {
                prop.intValue = 2; // "Both": legacy Input + IMGUI keep working alongside the Input System
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[R1Setup] Active Input Handling set to 'Both' — restart the editor once before Play testing.");
            }
        }

        internal static void AssignDefaultPrefabObjects(NetworkManager nm)
        {
            // FishNet's Generator (an AssetPostprocessor) maintains a DefaultPrefabObjects asset;
            // find it by type so we don't depend on where it was generated.
            string guid = AssetDatabase.FindAssets("t:DefaultPrefabObjects").FirstOrDefault();
            if (guid == null)
            {
                Debug.LogWarning("[R1Setup] No DefaultPrefabObjects asset found — FishNet normally generates it on " +
                                 "import. Select the NetworkManager and set SpawnablePrefabs manually if spawning fails.");
                return;
            }
            var dpo = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
            var so = new SerializedObject(nm);
            so.FindProperty("_spawnablePrefabs").objectReferenceValue = dpo; // field behind NetworkManager.SpawnablePrefabs
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
