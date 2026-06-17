using System.Collections.Generic;
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
            return list.ToArray();
        }
    }
}
