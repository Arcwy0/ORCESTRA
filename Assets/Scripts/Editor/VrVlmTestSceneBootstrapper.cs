using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using VRInteraction.AI;
using VRInteraction.Placement;
using VRInteraction.Rig;

namespace VRInteraction.Editor
{
    public static class VrVlmTestSceneBootstrapper
    {
        private const string ScenePath = "Assets/Scenes/VR_VLM_Test.unity";
        private const string Ur3PrefabPath = "Assets/RobotAssets/Prefabs/UR3.prefab";

        [MenuItem("UR3/Build VR VLM Test Scene", false, 5)]
        public static void BuildScene()
        {
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "VR_VLM_Test";

            CreateEnvironment();
            CreateCamera();
            CreateRobot();
            CreateTarget();
            CreateAiController();
            CreateEventSystem();

            XrSceneBootstrapper.SetupXrRig();
            XrSceneBootstrapper.SwitchToVR();

            System.IO.Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[VR VLM Test] Scene saved to " + ScenePath);
            EditorUtility.DisplayDialog(
                "VR VLM Test",
                "Scene saved to " + ScenePath + "\n\n" +
                "Open it, press Play, and test:\n" +
                "  Move the gripper to the red cube.\n" +
                "  Move the gripper in a circle in the vertical plane.",
                "OK");
        }

        [MenuItem("UR3/Build VR VLM Test Scene", true)]
        private static bool BuildSceneValidate() => !Application.isPlaying;

        private static void CreateEnvironment()
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(4f, 1f, 4f);
            var groundMat = CreateMaterial(
                "VRTest_Ground", new Color(0.19f, 0.21f, 0.23f));
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMat;

            var grid = GameObject.CreatePrimitive(PrimitiveType.Cube);
            grid.name = "Table_Platform";
            grid.transform.position = new Vector3(0.25f, 0.025f, 1.05f);
            grid.transform.localScale = new Vector3(0.9f, 0.05f, 0.55f);
            grid.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(
                "VRTest_Table", new Color(0.34f, 0.34f, 0.31f));

            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            var fillGo = new GameObject("Fill Light");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Point;
            fill.intensity = 2.0f;
            fill.range = 5f;
            fillGo.transform.position = new Vector3(-1.2f, 2.0f, -1.0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.38f, 0.40f, 0.42f);
        }

        private static void CreateCamera()
        {
            var cameraGo = new GameObject("Main Camera");
            cameraGo.tag = "MainCamera";
            cameraGo.transform.position = new Vector3(0f, 1.45f, -1.6f);
            cameraGo.transform.rotation = Quaternion.LookRotation(
                new Vector3(0.15f, -0.35f, 1.0f).normalized, Vector3.up);
            var cam = cameraGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.02f;
            cam.clearFlags = CameraClearFlags.Skybox;
            cameraGo.AddComponent<DesktopFlyCamera>();
            cameraGo.AddComponent<DesktopMousePointer>();
        }

        private static void CreateRobot()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Ur3PrefabPath);
            if (prefab == null)
            {
                Debug.LogError("[VR VLM Test] Missing UR3 prefab at " +
                               Ur3PrefabPath);
                return;
            }

            var robot = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            robot.name = "UR3_TestRobot";
            robot.transform.SetPositionAndRotation(
                new Vector3(-0.25f, 0.05f, 1.05f),
                Quaternion.Euler(0f, 180f, 0f));

            var placed = robot.GetComponent<PlacedRobot>();
            if (placed == null) placed = robot.AddComponent<PlacedRobot>();
            placed.kind = RobotKind.Manipulator;
            placed.displayName = "UR3_TestRobot";
            placed.reachRadius = 0.75f;
            placed.reachCenterLocal = new Vector3(0f, 0.18f, 0f);

            foreach (var ab in robot.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (ab.isRoot)
                {
                    ab.immovable = true;
                    break;
                }
            }
        }

        private static void CreateTarget()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "VLM_Target_RedCube";
            cube.transform.position = new Vector3(0.10f, 0.14f, 1.05f);
            cube.transform.localScale = Vector3.one * 0.12f;
            cube.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(
                "VRTest_RedTarget", new Color(0.95f, 0.05f, 0.04f));

            var marker = cube.AddComponent<AiKnownSceneObjectMarker>();
            marker.objectId = "red_cube";
            marker.label = "red cube";
            marker.sizeMeters = cube.transform.localScale;

            var label = GameObject.CreatePrimitive(PrimitiveType.Cube);
            label.name = "RedCube_Label_Backplate";
            label.transform.position = cube.transform.position +
                                       new Vector3(0f, 0.16f, 0f);
            label.transform.localScale = new Vector3(0.26f, 0.03f, 0.01f);
            label.GetComponent<MeshRenderer>().sharedMaterial = CreateMaterial(
                "VRTest_LabelPlate", new Color(0.05f, 0.05f, 0.05f));
            Object.DestroyImmediate(label.GetComponent<Collider>());
        }

        private static void CreateAiController()
        {
            var go = new GameObject("AI_RobotControl");
            go.transform.position = new Vector3(0f, 0f, 0f);
            var controller = go.AddComponent<RobotAiController>();
            controller.useLocalMock = false;
            controller.serverUrl = "http://127.0.0.1:18080/v1/robot/command";
            controller.imageSourceMode = AiImageSourceMode.UnityScreenshot;
            controller.debugCommand = "Move the gripper to the red cube.";
            controller.asrBackendMode =
                VRInteraction.AI.Speech.AiAsrBackendMode.EditorMock;
            controller.editorMockTranscript = "Move the gripper to the red cube.";

            var client = go.AddComponent<AiCommandClient>();
            client.useLocalMock = false;
            client.useJsonTransport = true;
            client.serverUrl = controller.serverUrl;
            client.timeoutSeconds = 180;
            controller.commandClient = client;
        }

        private static void CreateEventSystem()
        {
            if (Object.FindAnyObjectByType<EventSystem>() != null)
                return;
            new GameObject("EventSystem", typeof(EventSystem));
        }

        private static Material CreateMaterial(string name, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ??
                         Shader.Find("Standard");
            var mat = new Material(shader) { name = name };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;
            return mat;
        }
    }
}
