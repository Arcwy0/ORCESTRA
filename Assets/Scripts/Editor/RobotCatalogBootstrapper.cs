using UnityEditor;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.EditorTools
{
    /// <summary>
    /// Registers the extra robot models (KUKA KR600 manipulator, Scout V2
    /// mobile base) into the shared <see cref="RobotCatalog"/> so the runtime
    /// placement tool can spawn them.
    ///
    /// Workflow per robot:
    ///   1. Right-click the .urdf in Assets/RobotAssets/ →
    ///      "Import Robot from Selected URDF file".
    ///   2. With the imported robot in the open scene, run the matching menu
    ///      item below. It strips the importer demo scripts, attaches the right
    ///      controller, saves a prefab, adds/updates the catalog entry, and
    ///      removes the scene instance (placement scene stays empty).
    ///
    /// Order with "UR3 → Setup Placement Scene" no longer matters — the UR3
    /// setup upserts its own entry instead of clearing the catalog.
    /// </summary>
    public static class RobotCatalogBootstrapper
    {
        private const string PrefabDir = "Assets/RobotAssets/Prefabs";
        private const string CatalogPath = "Assets/RobotAssets/RobotCatalog.asset";

        // ===================================================================
        //  KUKA KR 600 FORTEC (R2830) — manipulator
        // ===================================================================
        [MenuItem("UR3/Catalog/Add KUKA KR600 (Manipulator)", false, 20)]
        public static void AddKuka()
        {
            var root = FindRobotRoot("kr600_r2830");
            if (root == null)
            {
                Dialog("No imported KUKA found.\n\nRight-click " +
                       "Assets/RobotAssets/kuka_kr600.urdf → \"Import Robot " +
                       "from Selected URDF file\" first, then run this again.");
                return;
            }

            // KUKA is huge (links up to 850 kg, joint efforts up to 14400 N·m),
            // so it needs far stiffer drives + higher torque ceilings than UR3
            // or it lags / overshoots the taught path.
            SetupManipulator(root,
                matFallback: new Color(0.96f, 0.46f, 0.10f),   // KUKA orange
                stiffness: 1.5e6f, damping: 4.0e5f,
                forceLimit: 40000f, solverIters: 60);
            var prefab = SavePrefab(root, "KUKA_KR600");
            if (prefab == null) { Dialog("Failed to save the KUKA prefab."); return; }

            UpsertEntry(new RobotCatalog.Entry
            {
                kind = RobotKind.Manipulator,
                displayName = "KUKA KR 600 FORTEC",
                prefab = prefab,
                reachRadius = 2.83f,                              // R2830 datasheet
                reachCenterLocal = new Vector3(0f, 1.045f, 0f),  // ~ shoulder height
                hmiLocalOffset = new Vector3(2.2f, 1.7f, 0f),
                hmiLocalEuler = new Vector3(0f, -90f, 0f)
            });

            Object.DestroyImmediate(root);
            Finish("KUKA KR 600 FORTEC added to the catalog.");
        }

        [MenuItem("UR3/Catalog/Add KUKA KR600 (Manipulator)", true)]
        private static bool AddKukaValidate() => !Application.isPlaying;

        // ===================================================================
        //  AgileX Scout V2 — mobile skid-steer base
        // ===================================================================
        [MenuItem("UR3/Catalog/Add Scout V2 (Mobile)", false, 21)]
        public static void AddScout()
        {
            var root = FindRobotRoot("scout_v2");
            if (root == null)
            {
                Dialog("No imported Scout found.\n\nRight-click " +
                       "Assets/RobotAssets/scout_v2.urdf → \"Import Robot " +
                       "from Selected URDF file\" first, then run this again.");
                return;
            }

            SetupMobile(root, new Color(0.17f, 0.17f, 0.19f));   // Scout dark grey
            var prefab = SavePrefab(root, "Scout_V2");
            if (prefab == null) { Dialog("Failed to save the Scout prefab."); return; }

            UpsertEntry(new RobotCatalog.Entry
            {
                kind = RobotKind.Mobile,
                displayName = "AgileX Scout V2",
                prefab = prefab,
                spawnHeightOffset = 0.235f,   // wheel radius + axle drop → wheels on floor
                driveSpeed = 1.5f,
                turnSpeed = 90f,
                wheelRadius = 0.16459f,
                halfTrack = 0.29153f,
                leftWheelNames = new[] { "front_left_wheel_link", "rear_left_wheel_link" },
                rightWheelNames = new[] { "front_right_wheel_link", "rear_right_wheel_link" },
                hmiLocalOffset = new Vector3(0f, 1.2f, 0.9f),
                hmiLocalEuler = new Vector3(0f, 180f, 0f)
            });

            Object.DestroyImmediate(root);
            Finish("AgileX Scout V2 added to the catalog.");
        }

        [MenuItem("UR3/Catalog/Add Scout V2 (Mobile)", true)]
        private static bool AddScoutValidate() => !Application.isPlaying;

        // ===================================================================
        //  Material fix (Built-in / missing shader → URP, kills the magenta)
        // ===================================================================
        // Re-fixes the EXISTING prefabs in place (no re-import needed):
        //  • KUKA  → URP materials + stiffer drives
        //  • Scout → URP materials
        [MenuItem("UR3/Catalog/Fix Robot Prefabs (URP + drives)", false, 22)]
        public static void FixRobotPrefabsMenu()
        {
            int n = 0;

            string kuka = $"{PrefabDir}/KUKA_KR600.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(kuka) != null)
            {
                var c = PrefabUtility.LoadPrefabContents(kuka);
                FixMaterialsForUrp(c, new Color(0.96f, 0.46f, 0.10f));
                var ctrl = c.GetComponentInChildren<UR3JointController>();
                if (ctrl != null)
                {
                    ctrl.driveStiffness = 1.5e6f;
                    ctrl.driveDamping = 4.0e5f;
                    ctrl.driveForceLimit = 40000f;
                    ctrl.solverIterations = 60;
                }
                PrefabUtility.SaveAsPrefabAsset(c, kuka);
                PrefabUtility.UnloadPrefabContents(c);
                n++;
            }

            string scout = $"{PrefabDir}/Scout_V2.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(scout) != null)
            {
                var c = PrefabUtility.LoadPrefabContents(scout);
                FixMaterialsForUrp(c, new Color(0.17f, 0.17f, 0.19f));
                PrefabUtility.SaveAsPrefabAsset(c, scout);
                PrefabUtility.UnloadPrefabContents(c);
                n++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Dialog(n > 0
                ? $"Fixed {n} prefab(s) (URP materials" +
                  ", KUKA drives stiffened).\n\nDelete the already-placed robots " +
                  "and place them again so the updated prefab is used."
                : "No KUKA/Scout prefab found. Add the robots first.");
        }

        private const string MatDir = "Assets/RobotAssets/Prefabs/Materials";

        /// <summary>
        /// Replaces any non-URP / missing-shader material on the robot with a
        /// fresh URP/Lit material SAVED AS A PROJECT ASSET (.mat). Runtime-only
        /// materials don't survive SaveAsPrefabAsset (they come back as missing
        /// = magenta), so they must be real assets. Preserves base colour +
        /// main texture where readable, else uses <paramref name="fallback"/>.
        /// </summary>
        private static void FixMaterialsForUrp(GameObject root, Color fallback)
        {
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            if (lit == null) return;

            if (!AssetDatabase.IsValidFolder(PrefabDir))
                AssetDatabase.CreateFolder("Assets/RobotAssets", "Prefabs");
            if (!AssetDatabase.IsValidFolder(MatDir))
                AssetDatabase.CreateFolder(PrefabDir, "Materials");

            var cache = new System.Collections.Generic.Dictionary<Material, Material>();
            Material fallbackMat = null;   // for empty (null / missing) slots
            int fixedCount = 0, nullCount = 0;

            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var shared = r.sharedMaterials;
                for (int i = 0; i < shared.Length; i++)
                {
                    var m = shared[i];

                    // Empty / missing slot → magenta. Assign a shared fallback.
                    if (m == null)
                    {
                        if (fallbackMat == null)
                            fallbackMat = CreateUrpMat(lit, fallback, null, "Robot");
                        shared[i] = fallbackMat;
                        nullCount++;
                        continue;
                    }

                    if (m.shader != null && m.shader.name.StartsWith(
                            "Universal Render Pipeline")) continue;

                    if (!cache.TryGetValue(m, out var nm))
                    {
                        nm = CreateUrpMat(lit, ReadColor(m, fallback),
                            ReadTex(m), m.name);
                        cache[m] = nm;
                    }
                    shared[i] = nm;
                    fixedCount++;
                }
                r.sharedMaterials = shared;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[Catalog] Materials → URP: converted {fixedCount}, " +
                      $"filled {nullCount} empty slot(s) on {root.name}.");
        }

        private static Material CreateUrpMat(Shader lit, Color color,
            Texture tex, string srcName)
        {
            var nm = new Material(lit);
            if (nm.HasProperty("_BaseColor")) nm.SetColor("_BaseColor", color);
            if (tex != null && nm.HasProperty("_BaseMap"))
                nm.SetTexture("_BaseMap", tex);
            if (nm.HasProperty("_Smoothness")) nm.SetFloat("_Smoothness", 0.35f);
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{MatDir}/{Sanitize(srcName)}_URP.mat");
            AssetDatabase.CreateAsset(nm, path);
            return nm;
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "mat";
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');
            return s.Replace(':', '_').Replace(' ', '_');
        }

        private static Color ReadColor(Material m, Color fallback)
        {
            foreach (var p in new[] { "_BaseColor", "_Color" })
                if (m.HasProperty(p))
                {
                    var c = m.GetColor(p);
                    if (c.a > 0.01f && (c.r + c.g + c.b) > 0.02f) return c;
                }
            return fallback;
        }

        private static Texture ReadTex(Material m)
        {
            foreach (var p in new[] { "_BaseMap", "_MainTex" })
                if (m.HasProperty(p))
                {
                    var t = m.GetTexture(p);
                    if (t != null) return t;
                }
            return null;
        }

        // ===================================================================
        //  Processing
        // ===================================================================
        private static void SetupManipulator(GameObject root, Color matFallback,
            float stiffness, float damping, float forceLimit, int solverIters)
        {
            StripImporterDemoScripts(root);
            FixMaterialsForUrp(root, matFallback);

            // Pin the articulation root so the arm holds its pose.
            foreach (var ab in root.GetComponentsInChildren<ArticulationBody>(true))
                if (ab.isRoot) { ab.immovable = true; break; }

            foreach (var old in root.GetComponentsInChildren<UR3JointController>(true))
                Object.DestroyImmediate(old);

            var ctrl = root.AddComponent<UR3JointController>();
            ctrl.driveStiffness = stiffness;
            ctrl.driveDamping = damping;
            ctrl.driveForceLimit = forceLimit;
            ctrl.solverIterations = solverIters;
            ctrl.AutoBind();   // KUKA links are link_1..6 → falls back to the
                               // "6 revolute joints in order" binding path.
            EditorUtility.SetDirty(ctrl);
        }

        private static void SetupMobile(GameObject root, Color matFallback)
        {
            StripImporterDemoScripts(root);
            FixMaterialsForUrp(root, matFallback);

            // The base must be free to move and obey gravity (it rests on its
            // wheels). MobileBaseController re-asserts this at runtime too.
            foreach (var ab in root.GetComponentsInChildren<ArticulationBody>(true))
                if (ab.isRoot) { ab.immovable = false; ab.useGravity = true; break; }

            foreach (var old in root.GetComponentsInChildren<MobileBaseController>(true))
                Object.DestroyImmediate(old);

            var ctrl = root.AddComponent<MobileBaseController>();
            ctrl.AutoBind();
            EditorUtility.SetDirty(ctrl);
        }

        // ===================================================================
        //  Helpers
        // ===================================================================
        private static GameObject FindRobotRoot(string nameContains)
        {
            string needle = nameContains.ToLowerInvariant();

            // Prefer the importer's UrdfRobot container.
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                if (mb.GetType().Name == "UrdfRobot" &&
                    mb.gameObject.name.ToLowerInvariant().Contains(needle))
                    return mb.gameObject;
            }

            // Fallback: any ArticulationBody root whose name matches.
            foreach (var ab in Object.FindObjectsByType<ArticulationBody>(
                         FindObjectsSortMode.None))
            {
                if (ab.isRoot &&
                    ab.gameObject.name.ToLowerInvariant().Contains(needle))
                    return ab.gameObject;
                if (ab.isRoot && ab.transform.parent != null &&
                    ab.transform.parent.name.ToLowerInvariant().Contains(needle))
                    return ab.transform.parent.gameObject;
            }

            return GameObject.Find(nameContains);
        }

        private static GameObject SavePrefab(GameObject inst, string fileName)
        {
            if (!AssetDatabase.IsValidFolder(PrefabDir))
                AssetDatabase.CreateFolder("Assets/RobotAssets", "Prefabs");
            string path = $"{PrefabDir}/{fileName}.prefab";
            return PrefabUtility.SaveAsPrefabAsset(inst, path);
        }

        private static void UpsertEntry(RobotCatalog.Entry entry)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<RobotCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<RobotCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            int idx = catalog.entries.FindIndex(
                e => e.displayName == entry.displayName);
            if (idx >= 0) catalog.entries[idx] = entry;
            else catalog.entries.Add(entry);
            EditorUtility.SetDirty(catalog);

            // Wire the catalog into the placement controller if it isn't yet.
            var pc = Object.FindAnyObjectByType<RobotPlacementController>();
            if (pc != null && pc.catalog == null)
            {
                pc.catalog = catalog;
                EditorUtility.SetDirty(pc);
            }
        }

        /// <summary>
        /// The URDF Importer attaches demo components (Controller, FKRobot)
        /// that drive the joints every frame with the legacy Input class.
        /// Remove them so our own controller owns the robot.
        /// </summary>
        private static void StripImporterDemoScripts(GameObject root)
        {
            foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                var t = mb.GetType();
                string n = t.Name;
                string ns = t.Namespace ?? "";
                if (n == "Controller" || n == "FKRobot" || n == "FkRobot" ||
                    (ns.Contains("Urdf") && ns.Contains("Control")))
                    Object.DestroyImmediate(mb);
            }
        }

        private static void Finish(string msg)
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            Debug.Log($"[Catalog] {msg}");
            Dialog(msg + "\n\nPress Play → ▣ ROBOT CATALOG to place it.");
        }

        private static void Dialog(string msg) =>
            EditorUtility.DisplayDialog("Robot Catalog", msg, "OK");
    }
}
