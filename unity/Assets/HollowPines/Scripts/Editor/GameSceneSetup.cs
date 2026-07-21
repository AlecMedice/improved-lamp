// One-click wiring for the GAME scene (the ported Hollow Pines slice) — the successor to the
// R1 cube scene. Builds Assets/Scenes/Forest.unity: NetworkManager + Tugboat + connect HUD,
// the GameManager (scene NetworkObject, spawns players + runs the authoritative match loop),
// the WorldBuilder (renders the deterministic forest), and the two spawnable prefabs
// (HPPlayer, ClueMarker). Requires the HollowPines.Sim sources under Assets/ (the sync step
// copies them from the repo's csharp/HollowPines.Sim).
//
//     Hollow Pines → Set Up Game Scene (Forest)     then Play → Host → START MATCH.
//     Hollow Pines → Build Windows (Game)           → Build/Windows/HollowPines.exe
using System.IO;
using System.Linq;
using FishNet.Component.Transforming;
using FishNet.Managing;
using FishNet.Object;
using FishNet.Transporting.Tugboat;
using HollowPines.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace HollowPines.EditorTools
{
    public static class GameSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Forest.unity";
        private const string PrefabDir = "Assets/HollowPines/Prefabs";
        private const string PlayerPrefabPath = PrefabDir + "/HPPlayer.prefab";
        private const string CluePrefabPath = PrefabDir + "/ClueMarker.prefab";
        private const string MarkPrefabPath = PrefabDir + "/TrailMark.prefab";
        private const string PingPrefabPath = PrefabDir + "/PingBeacon.prefab";
        private const string PilePrefabPath = PrefabDir + "/ProofPile.prefab";
        private const string BuildPath = "Build/Windows/HollowPines.exe";

        [MenuItem("Hollow Pines/Set Up Game Scene (Forest)")]
        public static void SetUpScene()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[GameSceneSetup] Exit Play mode first.");
                return;
            }

SetInputHandlingToBoth();
            PlayerSettings.productName = "Hollow Pines"; // window title + Build/Windows/HollowPines.exe

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Camera (WorldBuilder/HPPlayer drive fog, background and parenting at runtime).
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 900f;
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(0f, 3f, 24f);

            // Networking stack (the TitleMenu drives connections — no debug HUD in the game scene).
            var nmGo = new GameObject("NetworkManager");
            var nm = nmGo.AddComponent<NetworkManager>();
            nmGo.AddComponent<Tugboat>();

            // World renderer + title menu + HUD + map overlay (pure client-side).
            var systems = new GameObject("GameSystems");
            systems.AddComponent<WorldBuilder>();
            systems.AddComponent<TitleMenu>();
            systems.AddComponent<HPHud>();
            systems.AddComponent<MapView>();

            // Prefabs.
            NetworkObject playerPrefab = CreatePlayerPrefab();
            NetworkObject cluePrefab = CreateCluePrefab();
            NetworkObject markPrefab = CreateSimplePrefab<TrailMark>("TrailMark", MarkPrefabPath);
            NetworkObject pingPrefab = CreateSimplePrefab<PingBeacon>("PingBeacon", PingPrefabPath);
            NetworkObject pilePrefab = CreateSimplePrefab<ProofPile>("ProofPile", PilePrefabPath);

            // GameManager — a SCENE NetworkObject; FishNet initializes it when the server starts.
            var gmGo = new GameObject("GameManager");
            gmGo.AddComponent<NetworkObject>();
            var gm = gmGo.AddComponent<GameManager>();
            var gmSo = new SerializedObject(gm);
            gmSo.FindProperty("_playerPrefab").objectReferenceValue = playerPrefab;
            gmSo.FindProperty("_cluePrefab").objectReferenceValue = cluePrefab;
            gmSo.FindProperty("_markPrefab").objectReferenceValue = markPrefab;
            gmSo.FindProperty("_pingPrefab").objectReferenceValue = pingPrefab;
            gmSo.FindProperty("_pilePrefab").objectReferenceValue = pilePrefab;
            gmSo.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            StampAssetPathHashes(new[] { playerPrefab, cluePrefab, markPrefab, pingPrefab, pilePrefab });
AssignDefaultPrefabObjects(nm);

            Directory.CreateDirectory("Assets/Scenes");
            AssignSceneIds(scene); // MUST run before the save — see the method's note
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            Debug.Log("[GameSceneSetup] Forest scene wired → " + ScenePath + ". Play → 'Host (server + client)' " +
                      "→ START MATCH. (If input is dead in Play mode, restart the editor once.)");
        }

        [MenuItem("Hollow Pines/Build Windows (Game)")]
        public static void BuildWindows()
        {
            var report = BuildPipeline.BuildPlayer(
                new[] { ScenePath }, BuildPath, BuildTarget.StandaloneWindows64, BuildOptions.None);
            var summary = report.summary;
            if (summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded)
                Debug.Log($"[GameSceneSetup] Build OK → {Path.GetFullPath(BuildPath)} ({summary.totalSize / (1024 * 1024)} MB)");
            else
                Debug.LogError($"[GameSceneSetup] Build {summary.result}: {summary.totalErrors} error(s) — see the log.");
        }

        /// <summary>
        /// Give every prefab we just created its FishNet AssetPathHash — the id the spawn system uses
        /// to agree on "which prefab" across the wire.
        ///
        /// Why this exists (same root cause as AssignSceneIds below): FishNet's prefab generator is an
        /// AssetPostprocessor that stamps these when a prefab is imported. This method creates several
        /// prefabs and uses them within one synchronous run, so the hashes were still 0 — and
        /// `DefaultPrefabObjects.Sort()` builds a Dictionary keyed by hash, so the SECOND unstamped
        /// prefab threw "An item with the same key has already been added. Key: 0" inside
        /// NetworkManager.Awake(). A NetworkManager whose Awake throws never registers itself, so
        /// InstanceFinder.NetworkManager was null and START NEW GAME died with a NullReferenceException
        /// that pointed at the menu rather than at the real cause.
        ///
        /// The hash formula mirrors DefaultPrefabObjects.SetAssetPathHashes: "<assetPath><goName>",
        /// lowercased, stripped to [a-z0-9], then GetStableHashU64. Both APIs used here are public.
        /// </summary>
        private static void StampAssetPathHashes(NetworkObject[] prefabs)
        {
            foreach (NetworkObject nob in prefabs)
            {
                if (nob == null) continue;
                string pathAndName = $"{AssetDatabase.GetAssetPath(nob.gameObject)}{nob.gameObject.name}".Trim().ToLowerInvariant();
                var sb = new System.Text.StringBuilder();
                foreach (char c in pathAndName)
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);

                ulong hash = GameKit.Dependencies.Utilities.Hashing.GetStableHashU64(sb.ToString());
                if (nob.AssetPathHash == hash) continue;
                nob.SetAssetPathHash(hash);
                EditorUtility.SetDirty(nob);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Force FishNet to stamp SceneIds onto the scene's NetworkObjects before we save.
        ///
        /// Why this exists: a scene NetworkObject (our GameManager) needs a persistent SceneId so the
        /// host and clients agree on which object is which. FishNet assigns those from editor
        /// callbacks (Reset/OnValidate) but THROTTLES the work to once per 250 ms — and this setup
        /// method builds the whole scene and saves it in one synchronous burst, well inside that
        /// window. The result was a GameManager saved with SceneId unset: at runtime it logged
        /// "expected to be initialized but was not", never ran OnStartServer, never spawned players,
        /// and START MATCH threw a NullReferenceException.
        ///
        /// NetworkObject.CreateSceneId is `internal` and FishNet.Runtime is its own assembly, so this
        /// goes through reflection. If FishNet ever renames it we log the manual fallback rather than
        /// silently shipping a broken scene again.
        /// </summary>
        private static void AssignSceneIds(UnityEngine.SceneManagement.Scene scene)
        {
            var method = typeof(NetworkObject).GetMethod(
                "CreateSceneId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                null,
                new[] { typeof(UnityEngine.SceneManagement.Scene), typeof(bool), typeof(int).MakeByRefType() },
                null);

            if (method == null)
            {
                Debug.LogError("[GameSceneSetup] Could not find FishNet's CreateSceneId — scene NetworkObjects " +
                               "may have no SceneId. Fix manually: Tools → Fish-Networking → Utility → " +
                               "Reserialize NetworkObjects → tick 'Reserialize Scenes' → Run Task, then save the scene.");
                return;
            }

            object[] args = { scene, true, 0 };
            method.Invoke(null, args);
            Debug.Log($"[GameSceneSetup] Assigned SceneIds to scene NetworkObjects ({args[2]} generated).");
        }

        /// <summary>
        /// The URP template ships Active Input Handling = "Input System Package (new)" ONLY, where
        /// legacy UnityEngine.Input throws and IMGUI (our OnGUI HUDs) gets no input in a standalone
        /// player. Set it to "Both" (2). Needs one editor restart to take effect in Play mode.
        /// </summary>
        internal static void SetInputHandlingToBoth()
        {
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings == null)
            {
                Debug.LogWarning("[GameSceneSetup] Couldn't load ProjectSettings.asset to adjust input handling.");
                return;
            }
            var so = new SerializedObject(settings);
            var prop = so.FindProperty("activeInputHandler");
            if (prop != null && prop.intValue != 2)
            {
                prop.intValue = 2; // "Both": legacy Input + IMGUI keep working alongside the Input System
                so.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.SaveAssets();
                Debug.Log("[GameSceneSetup] Active Input Handling set to 'Both' — restart the editor once before Play testing.");
            }
        }

        /// <summary>Point the NetworkManager's SpawnablePrefabs at FishNet's generated collection.</summary>
        internal static void AssignDefaultPrefabObjects(NetworkManager nm)
        {
            // FishNet's Generator (an AssetPostprocessor) maintains a DefaultPrefabObjects asset;
            // find it by type so we don't depend on where it was generated.
            string guid = AssetDatabase.FindAssets("t:DefaultPrefabObjects").FirstOrDefault();
            if (guid == null)
            {
                Debug.LogWarning("[GameSceneSetup] No DefaultPrefabObjects asset found — FishNet normally generates it on " +
                                 "import. Select the NetworkManager and set SpawnablePrefabs manually if spawning fails.");
                return;
            }
            var dpo = AssetDatabase.LoadAssetAtPath<ScriptableObject>(AssetDatabase.GUIDToAssetPath(guid));
            var so = new SerializedObject(nm);
            so.FindProperty("_spawnablePrefabs").objectReferenceValue = dpo; // field behind NetworkManager.SpawnablePrefabs
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static NetworkObject CreatePlayerPrefab()
        {
            var root = new GameObject("HPPlayer");
            root.AddComponent<NetworkObject>();
            var nt = root.AddComponent<NetworkTransform>();
            root.AddComponent<HPPlayer>();

            // THIS milestone runs owner-simulated movement (shared-sim stepped on the owning client),
            // so the NetworkTransform must be CLIENT-authoritative — the opposite of the R1 cube.
            // Host-authoritative prediction replaces this in networking phase N3 (docs/NETWORKING.md).
            var ntSo = new SerializedObject(nt);
            ntSo.FindProperty("_clientAuthoritative").boolValue = true;
            ntSo.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(PrefabDir);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, PlayerPrefabPath);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<NetworkObject>();
        }

        private static NetworkObject CreateCluePrefab()
        {
            return CreateSimplePrefab<ClueMarker>("ClueMarker", CluePrefabPath);
        }

        /// <summary>A static spawnable: root + NetworkObject + one behaviour, no NetworkTransform.</summary>
        private static NetworkObject CreateSimplePrefab<T>(string name, string path) where T : NetworkBehaviour
        {
            var root = new GameObject(name);
            root.AddComponent<NetworkObject>();
            root.AddComponent<T>();
            Directory.CreateDirectory(PrefabDir);
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            return prefab.GetComponent<NetworkObject>();
        }
    }
}
