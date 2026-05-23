using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRInteraction.Robot;
using VRInteraction.Rig;
using VRInteraction.UI;
using VRInteraction.Placement;
using VRInteraction.Waypoints;

namespace VRInteraction.EditorTools
{
    /// <summary>
    /// One-click assembly of the UR3 demo scene. Deliberately has NO dependency
    /// on the URDF Importer package, so it always compiles. The URDF import
    /// itself is a separate, package-provided right-click action (see README).
    /// </summary>
    public static class UR3SceneBootstrapper
    {
        private const string ScenePath = "Assets/Scenes/UR3_Demo.unity";

        [MenuItem("UR3/Build Demo Scene", false, 0)]
        public static void BuildDemoScene()
        {
            var robotRoot = FindUr3Root();
            if (robotRoot == null)
            {
                EditorUtility.DisplayDialog("UR3",
                    "No imported UR3 found in the open scene.\n\n" +
                    "First right-click  Assets/RobotAssets/ur3.urdf  →  " +
                    "\"Import Robot from Selected URDF file\", then run this menu again.",
                    "OK");
                return;
            }

            SetupEnvironment();
            SetupCamera();
            SetupController(robotRoot);
            SetupHmi();

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(
                EditorSceneManager.GetActiveScene(), ScenePath);
            AssetDatabase.Refresh();

            Debug.Log($"[UR3] Demo scene built and saved to {ScenePath}. Press Play.");
            EditorUtility.DisplayDialog("UR3",
                "Demo scene assembled and saved to\n" + ScenePath +
                "\n\nPress Play. Right mouse = look, WASD = move.", "OK");
        }

        [MenuItem("UR3/Build Demo Scene", true)]
        private static bool BuildDemoSceneValidate() => !Application.isPlaying;

        // ==================================================================
        //  Placement scene: robot becomes a catalog prefab, scene starts
        //  empty, operator places robots at runtime.
        // ==================================================================
        private const string PrefabPath = "Assets/RobotAssets/Prefabs/UR3.prefab";
        private const string CatalogPath = "Assets/RobotAssets/RobotCatalog.asset";

        [MenuItem("UR3/Setup Placement Scene", false, 2)]
        public static void SetupPlacementScene()
        {
            // 1) Make sure we have a clean UR3 prefab.
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            var sceneRobot = FindUr3Root();

            if (sceneRobot != null)
            {
                SetupController(sceneRobot);                 // strip demo, pin, bind
                EnsureFolder("Assets/RobotAssets", "Prefabs");
                prefab = PrefabUtility.SaveAsPrefabAsset(sceneRobot, PrefabPath);
                Object.DestroyImmediate(sceneRobot);         // start empty
            }
            else if (prefab != null)
            {
                // Scene is already empty but a prefab exists from an earlier
                // run — re-process it so script default changes (stiffness,
                // solver iterations, etc.) propagate without forcing the user
                // to re-import the URDF.
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                SetupController(inst);
                prefab = PrefabUtility.SaveAsPrefabAsset(inst, PrefabPath);
                Object.DestroyImmediate(inst);
            }

            if (prefab == null)
            {
                EditorUtility.DisplayDialog("UR3",
                    "No UR3 prefab and no imported UR3 in the scene.\n\n" +
                    "Import the URDF and run \"UR3 → Fix Imported Robot\" first.",
                    "OK");
                return;
            }

            // 2) Catalog asset.
            var catalog = AssetDatabase.LoadAssetAtPath<RobotCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<RobotCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }
            // Upsert the UR3 entry (don't wipe KUKA / Scout entries that may
            // have been added via UR3 → Catalog → …).
            int ur3Idx = catalog.entries.FindIndex(
                e => e.displayName == "Universal Robots UR3");
            var ur3Entry = new RobotCatalog.Entry
            {
                displayName = "Universal Robots UR3",
                kind = RobotKind.Manipulator,
                prefab = prefab
            };
            if (ur3Idx >= 0) catalog.entries[ur3Idx] = ur3Entry;
            else catalog.entries.Add(ur3Entry);
            EditorUtility.SetDirty(catalog);

            // 3) Strip old fixed-position scene objects.
            foreach (var p in Object.FindObjectsByType<UR3HmiPanel>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                Object.DestroyImmediate(p.gameObject);
            var oldHmi = GameObject.Find("UR3_HMI");
            if (oldHmi != null) Object.DestroyImmediate(oldHmi);

            // 4) Environment + pointer + placement controller.
            SetupEnvironment();
            SetupCamera();
            if (Camera.main != null &&
                Camera.main.GetComponent<DesktopMousePointer>() == null)
                Camera.main.gameObject.AddComponent<DesktopMousePointer>();

            var pc = Object.FindAnyObjectByType<RobotPlacementController>();
            if (pc == null)
                pc = new GameObject("RobotPlacement")
                    .AddComponent<RobotPlacementController>();
            pc.catalog = catalog;
            EditorUtility.SetDirty(pc);

            // Waypoint / scenario tool lives on the same controller GO.
            if (pc.GetComponent<WaypointController>() == null)
                pc.gameObject.AddComponent<WaypointController>();

            UiKit.EnsureEventSystem();

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(
                EditorSceneManager.GetActiveScene(), ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[UR3] Placement scene ready. Press Play, then click " +
                      "\"ROBOT CATALOG\".");
            EditorUtility.DisplayDialog("UR3",
                "Placement scene ready.\n\nPress Play → click \"ROBOT " +
                "CATALOG\" → pick UR3 → point at the floor & click → " +
                "fine-tune X/Y/Z → CONFIRM.", "OK");
        }

        [MenuItem("UR3/Setup Placement Scene", true)]
        private static bool SetupPlacementSceneValidate() => !Application.isPlaying;

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
                AssetDatabase.CreateFolder(parent, child);
        }

        // ------------------------------------------------------------------
        private static GameObject FindUr3Root()
        {
            // Prefer the GameObject that carries the importer's UrdfRobot
            // component — that is the true top container (e.g. "ur3_robot"),
            // the parent of base_link, where the demo scripts also live.
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                if (mb.GetType().Name == "UrdfRobot")
                {
                    var go = mb.gameObject;
                    if (HasUr3Links(go.transform)) return go;
                }
            }

            // Fallback: find any subtree that contains the UR3 links, then
            // climb to the highest ancestor still part of the robot.
            foreach (var ab in Object.FindObjectsByType<ArticulationBody>(
                         FindObjectsSortMode.None))
            {
                if (HasUr3Links(ab.transform))
                {
                    var root = ab.transform;
                    while (root.parent != null &&
                           (root.parent.GetComponent<ArticulationBody>() != null ||
                            root.parent.name.Contains("ur3")))
                        root = root.parent;
                    return root.gameObject;
                }
            }
            return null;
        }

        private static bool HasUr3Links(Transform t)
        {
            bool hasShoulder = false, hasWrist3 = false;
            foreach (var child in t.GetComponentsInChildren<Transform>(true))
            {
                if (child.name == "shoulder_link") hasShoulder = true;
                if (child.name == "wrist_3_link") hasWrist3 = true;
            }
            return hasShoulder && hasWrist3;
        }

        private static void SetupEnvironment()
        {
            if (GameObject.Find("Ground") == null)
            {
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = new Vector3(2f, 1f, 2f);
                var mr = ground.GetComponent<MeshRenderer>();
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"))
                    { color = new Color(0.22f, 0.23f, 0.25f) };
                mr.sharedMaterial = mat;
            }

            if (Object.FindAnyObjectByType<Light>() == null)
            {
                var lgo = new GameObject("Directional Light");
                var l = lgo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 1.1f;
                l.shadows = LightShadows.Soft;
                lgo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.37f, 0.40f);
        }

        private static void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera", typeof(Camera));
                go.tag = "MainCamera";
                cam = go.GetComponent<Camera>();
            }
            cam.transform.position = new Vector3(-1.2f, 1.4f, -1.2f);
            cam.transform.rotation = Quaternion.Euler(15f, 45f, 0f);
            cam.nearClipPlane = 0.02f;
            if (cam.GetComponent<DesktopFlyCamera>() == null)
                cam.gameObject.AddComponent<DesktopFlyCamera>();
        }

        [MenuItem("UR3/Fix Imported Robot", false, 1)]
        public static void FixImportedRobotMenu()
        {
            var robotRoot = FindUr3Root();
            if (robotRoot == null)
            {
                EditorUtility.DisplayDialog("UR3",
                    "No imported UR3 found in the open scene.", "OK");
                return;
            }
            SetupController(robotRoot);
            SetupHmi();
            EditorSceneManager.MarkSceneDirty(
                EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveScene(
                EditorSceneManager.GetActiveScene());
            Debug.Log("[UR3] Imported robot fixed (demo scripts removed, base " +
                      "pinned, controller bound). Press Play.");
            EditorUtility.DisplayDialog("UR3",
                "Robot fixed: URDF-Importer demo scripts removed, base pinned, " +
                "UR3JointController bound.\n\nPress Play.", "OK");
        }

        [MenuItem("UR3/Fix Imported Robot", true)]
        private static bool FixImportedRobotValidate() => !Application.isPlaying;

        /// <summary>
        /// The URDF Importer attaches demo components (Controller, FKRobot)
        /// that use the legacy Input class and overwrite the joint drives every
        /// frame. Remove them so our UR3JointController owns the robot.
        /// </summary>
        private static void StripImporterDemoScripts(GameObject robotRoot)
        {
            int removed = 0;
            var all = robotRoot.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var mb in all)
            {
                if (mb == null) continue;
                var t = mb.GetType();
                string n = t.Name;
                string ns = t.Namespace ?? "";
                bool isDemo =
                    n == "Controller" || n == "FKRobot" || n == "FkRobot" ||
                    (ns.Contains("Urdf") && ns.Contains("Control"));
                if (isDemo)
                {
                    Object.DestroyImmediate(mb);
                    removed++;
                }
            }
            if (removed > 0)
                Debug.Log($"[UR3] Removed {removed} URDF-Importer demo " +
                          "component(s) from the robot.");
        }

        private static void SetupController(GameObject robotRoot)
        {
            StripImporterDemoScripts(robotRoot);

            // Pin the actual articulation root so the arm does not fall over.
            // (The root ArticulationBody is usually on base_link, a child of
            //  the ur3_robot GameObject — not on ur3_robot itself.)
            foreach (var ab in robotRoot.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (ab.isRoot)
                {
                    ab.immovable = true;
                    break;
                }
            }

            // Remove any stale controllers from earlier runs, then add one
            // fresh on the true robot root.
            foreach (var old in robotRoot.GetComponentsInChildren<UR3JointController>(true))
                Object.DestroyImmediate(old);

            var ctrl = robotRoot.AddComponent<UR3JointController>();
            ctrl.AutoBind();
            EditorUtility.SetDirty(ctrl);
        }

        private static void SetupHmi()
        {
            var hmiGo = GameObject.Find("UR3_HMI");
            if (hmiGo == null) hmiGo = new GameObject("UR3_HMI");
            var panel = hmiGo.GetComponent<UR3HmiPanel>();
            if (panel == null) panel = hmiGo.AddComponent<UR3HmiPanel>();
            panel.controller = Object.FindAnyObjectByType<UR3JointController>();
            EditorUtility.SetDirty(panel);
        }
    }
}
