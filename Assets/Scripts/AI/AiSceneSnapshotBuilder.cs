using System.Collections.Generic;
using System.Text;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.AI
{
    public static class AiSceneSnapshotBuilder
    {
        public static AiCommandRequest Build(
            string sessionId, string commandText, string imageSource,
            int imageWidth, int imageHeight)
        {
            var cam = Camera.main;
            return new AiCommandRequest
            {
                session_id = sessionId,
                command_text = commandText,
                image_source = imageSource,
                audio_format = "",
                audio_sample_rate_hz = 0,
                camera = BuildCamera(cam, imageWidth, imageHeight),
                robots = BuildRobots(),
                mr_planes = BuildPlanes(),
                known_scene_objects = BuildKnownSceneObjects()
            };
        }

        private static AiCameraSnapshot BuildCamera(
            Camera cam, int imageWidth, int imageHeight)
        {
            if (cam == null)
            {
                return new AiCameraSnapshot
                {
                    world_from_camera = AiModelUtil.Matrix(Matrix4x4.identity),
                    projection = AiModelUtil.Matrix(Matrix4x4.identity),
                    width = imageWidth,
                    height = imageHeight
                };
            }

            return new AiCameraSnapshot
            {
                world_from_camera = AiModelUtil.Matrix(cam.cameraToWorldMatrix),
                projection = AiModelUtil.Matrix(cam.projectionMatrix),
                width = imageWidth,
                height = imageHeight
            };
        }

        private static AiRobotSnapshot[] BuildRobots()
        {
            var robots = Object.FindObjectsByType<PlacedRobot>(
                FindObjectsSortMode.None);
            var list = new List<AiRobotSnapshot>(robots.Length);

            foreach (var robot in robots)
            {
                if (robot == null) continue;
                if (robot.kind == RobotKind.Mobile)
                    list.Add(BuildMobile(robot));
                else
                    list.Add(BuildManipulator(robot));
            }

            return list.ToArray();
        }

        private static AiRobotSnapshot BuildManipulator(PlacedRobot robot)
        {
            var ctrl = robot.GetComponentInChildren<UR3JointController>();
            var tcp = CcdIkSolver.FindTcp(robot.transform);
            var joints = new float[ctrl != null ? ctrl.JointCount : 0];
            if (ctrl != null)
                for (int i = 0; i < ctrl.JointCount; i++)
                    joints[i] = ctrl.GetMeasuredDeg(i);

            return new AiRobotSnapshot
            {
                id = robot.displayName,
                kind = "manipulator",
                root_position_m = AiModelUtil.Vec3(robot.transform.position),
                root_rotation_xyzw = AiModelUtil.Quat(robot.transform.rotation),
                tcp_position_m = AiModelUtil.Vec3(
                    tcp != null ? tcp.position : robot.transform.position),
                reach_center_m = AiModelUtil.Vec3(
                    robot.transform.TransformPoint(robot.reachCenterLocal)),
                reach_radius_m = robot.reachRadius,
                joint_deg = joints
            };
        }

        private static AiRobotSnapshot BuildMobile(PlacedRobot robot)
        {
            var mobile = robot.GetComponentInChildren<MobileBaseController>();
            var body = mobile != null ? mobile.Body : robot.transform;
            return new AiRobotSnapshot
            {
                id = robot.displayName,
                kind = "mobile",
                root_position_m = AiModelUtil.Vec3(body.position),
                root_rotation_xyzw = AiModelUtil.Quat(body.rotation),
                tcp_position_m = AiModelUtil.Vec3(body.position),
                reach_center_m = AiModelUtil.Vec3(body.position),
                reach_radius_m = 0f,
                joint_deg = new float[0]
            };
        }

        private static AiPlaneSnapshot[] BuildPlanes()
        {
            var ground = GameObject.Find("Ground");
            if (ground == null) return new AiPlaneSnapshot[0];
            return new[]
            {
                new AiPlaneSnapshot
                {
                    id = "ground",
                    center_m = AiModelUtil.Vec3(ground.transform.position),
                    normal = AiModelUtil.Vec3(Vector3.up),
                    extent_m = new[] { 60f, 60f }
                }
            };
        }

        private static AiKnownSceneObject[] BuildKnownSceneObjects()
        {
            var markers = Object.FindObjectsByType<AiKnownSceneObjectMarker>(
                FindObjectsSortMode.None);
            var list = new List<AiKnownSceneObject>(markers.Length);
            foreach (var marker in markers)
            {
                if (marker == null || !marker.isActiveAndEnabled) continue;
                list.Add(new AiKnownSceneObject
                {
                    id = string.IsNullOrEmpty(marker.objectId)
                        ? marker.name
                        : marker.objectId,
                    label = string.IsNullOrEmpty(marker.label)
                        ? marker.name
                        : marker.label,
                    position_m = AiModelUtil.Vec3(marker.transform.position),
                    size_m = AiModelUtil.Vec3(marker.sizeMeters)
                });
            }

            AppendAutoKnownSceneObjects(list);
            return list.ToArray();
        }

        private static void AppendAutoKnownSceneObjects(
            List<AiKnownSceneObject> list)
        {
            var renderers = Object.FindObjectsByType<MeshRenderer>(
                FindObjectsSortMode.None);
            foreach (var renderer in renderers)
            {
                if (!TryBuildAutoKnownSceneObject(
                        renderer, list.Count, out AiKnownSceneObject obj))
                    continue;
                list.Add(obj);
            }
        }

        private static bool TryBuildAutoKnownSceneObject(
            MeshRenderer renderer, int index, out AiKnownSceneObject obj)
        {
            obj = null;
            if (ShouldSkipAutoObject(renderer)) return false;
            if (!TryGetVisibleObjectColor(renderer, out Color color))
                return false;

            string colorName = ColorName(color);
            if (string.IsNullOrEmpty(colorName)) return false;

            string shapeName = ShapeName(renderer);
            Bounds bounds = ObjectBounds(renderer);
            if (bounds.size.sqrMagnitude < 1e-6f) return false;

            string label = colorName + " " + shapeName;
            string id = "auto_" + SafeId(label) + "_" + index;
            obj = new AiKnownSceneObject
            {
                id = id,
                label = label,
                position_m = AiModelUtil.Vec3(bounds.center),
                size_m = AiModelUtil.Vec3(bounds.size)
            };
            return true;
        }

        private static bool ShouldSkipAutoObject(Renderer renderer)
        {
            if (renderer == null || !renderer.enabled ||
                !renderer.gameObject.activeInHierarchy)
                return true;
            if (renderer.GetComponentInParent<PlacedRobot>() != null)
                return true;
            if (renderer.GetComponentInParent<Canvas>() != null)
                return true;

            string name = renderer.gameObject.name.ToLowerInvariant();
            return name.Contains("preview") ||
                   name.Contains("waypoint") ||
                   name.Contains("path") ||
                   name.Contains("label") ||
                   name.Contains("backplate") ||
                   name.Contains("ground") ||
                   name.Contains("table") ||
                   name.Contains("panel") ||
                   name.Contains("controller");
        }

        private static bool TryGetVisibleObjectColor(
            Renderer renderer, out Color color)
        {
            color = Color.clear;
            var mat = renderer.sharedMaterial;
            if (mat == null) return false;

            if (mat.HasProperty("_BaseColor"))
                color = mat.GetColor("_BaseColor");
            else if (mat.HasProperty("_Color"))
                color = mat.GetColor("_Color");
            else
                return false;

            if (color.a < 0.2f) return false;
            Color.RGBToHSV(color, out _, out float saturation, out float value);
            return saturation >= 0.35f && value >= 0.15f;
        }

        private static string ColorName(Color color)
        {
            Color.RGBToHSV(color, out float hue, out float saturation,
                out float value);
            if (saturation < 0.35f || value < 0.15f) return "";

            if (hue < 0.035f || hue >= 0.94f) return "red";
            if (hue < 0.10f) return "orange";
            if (hue < 0.18f) return "yellow";
            if (hue < 0.42f) return "green";
            if (hue < 0.52f) return "cyan";
            if (hue < 0.72f) return "blue";
            if (hue < 0.86f) return "purple";
            return "magenta";
        }

        private static string ShapeName(MeshRenderer renderer)
        {
            if (renderer.GetComponent<BoxCollider>() != null)
                return "cube";
            if (renderer.GetComponent<SphereCollider>() != null)
                return "sphere";
            if (renderer.GetComponent<CapsuleCollider>() != null)
                return "capsule";

            var filter = renderer.GetComponent<MeshFilter>();
            string meshName = filter != null && filter.sharedMesh != null
                ? filter.sharedMesh.name.ToLowerInvariant()
                : "";
            if (meshName.Contains("cube")) return "cube";
            if (meshName.Contains("sphere")) return "sphere";
            if (meshName.Contains("cylinder")) return "cylinder";
            if (meshName.Contains("capsule")) return "capsule";
            return "object";
        }

        private static Bounds ObjectBounds(Renderer renderer)
        {
            var collider = renderer.GetComponent<Collider>();
            return collider != null ? collider.bounds : renderer.bounds;
        }

        private static string SafeId(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '_' || c == '-') sb.Append('_');
            }
            return sb.Length > 0 ? sb.ToString() : "object";
        }
    }
}
