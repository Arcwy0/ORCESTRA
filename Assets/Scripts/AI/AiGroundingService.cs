using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.AI
{
    public class AiGroundingService
    {
        public bool EnsureWorldWaypoints(
            AiCommandResponse response, Camera cam, out string error)
        {
            error = null;
            if (response == null || response.plan_ir == null)
            {
                error = "AI response has no plan.";
                return false;
            }

            if (response.plan_ir.waypoints != null &&
                response.plan_ir.waypoints.Length > 0)
                return true;

            var robot = FindRobot(response.plan_ir.robot_id);
            if (robot == null)
            {
                error = "Plan references a robot that is not placed.";
                return false;
            }

            if (TryGenerateGeometricPrimitive(response, robot, out error))
                return true;

            if (!TryGroundTarget(response, cam, out Vector3 target, out error))
                return false;

            if (robot.kind == RobotKind.Mobile)
            {
                response.plan_ir.kind = "mobile_route";
                target.y = GroundY();
                response.plan_ir.waypoints = new[]
                {
                    new AiWaypoint { position_m = AiModelUtil.Vec3(target) }
                };
                return true;
            }

            response.plan_ir.kind = "manipulator_reach";
            Vector3 standoff = target + Vector3.up *
                Mathf.Max(0.05f, response.plan_ir.min_clearance_m);
            standoff = ClampToReach(robot, standoff);
            response.plan_ir.waypoints = new[]
            {
                new AiWaypoint { position_m = AiModelUtil.Vec3(standoff) }
            };
            return true;
        }

        private static bool TryGroundTarget(
            AiCommandResponse response, Camera cam, out Vector3 target,
            out string error)
        {
            target = Vector3.zero;
            error = null;

            var grounding = response.visual_grounding;
            if (grounding != null &&
                grounding.world_position_m != null &&
                grounding.world_position_m.Length >= 3 &&
                grounding.world_confidence >= 0.6f)
            {
                target = AiModelUtil.ToVector3(grounding.world_position_m);
                return true;
            }

            if (TryKnownSceneObject(response, out target))
            {
                if (grounding != null)
                {
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.9f);
                }
                return true;
            }

            if (cam == null)
            {
                error = "No active camera for grounding.";
                return false;
            }

            if (grounding != null &&
                (grounding.preferred_point_px == null ||
                 grounding.preferred_point_px.Length < 2) &&
                grounding.bbox_xyxy_px != null &&
                grounding.bbox_xyxy_px.Length >= 4)
            {
                grounding.preferred_point_px = new[]
                {
                    (grounding.bbox_xyxy_px[0] + grounding.bbox_xyxy_px[2]) * 0.5f,
                    (grounding.bbox_xyxy_px[1] + grounding.bbox_xyxy_px[3]) * 0.5f
                };
            }

            if (grounding == null ||
                grounding.preferred_point_px == null ||
                grounding.preferred_point_px.Length < 2)
            {
                error = "AI response has no preferred image point to ground.";
                return false;
            }

            float px = grounding.preferred_point_px[0];
            float pyTopLeft = grounding.preferred_point_px[1];
            float pyBottomLeft = cam.pixelHeight - pyTopLeft;
            var ray = cam.ScreenPointToRay(new Vector3(px, pyBottomLeft, 0f));

            if (Physics.Raycast(ray, out RaycastHit hit, 100f))
            {
                target = hit.point;
                grounding.world_position_m = AiModelUtil.Vec3(target);
                grounding.world_confidence = Mathf.Max(
                    grounding.world_confidence, 0.65f);
                return true;
            }

            var plane = new Plane(Vector3.up, new Vector3(0f, GroundY(), 0f));
            if (plane.Raycast(ray, out float enter) && enter > 0f)
            {
                target = ray.GetPoint(enter);
                grounding.world_position_m = AiModelUtil.Vec3(target);
                grounding.world_confidence = Mathf.Max(
                    grounding.world_confidence, 0.55f);
                return true;
            }

            error = "Image point did not intersect a collider or floor plane.";
            return false;
        }

        private static bool TryKnownSceneObject(
            AiCommandResponse response, out Vector3 target)
        {
            target = Vector3.zero;
            string label = "";
            if (response.visual_grounding != null &&
                !string.IsNullOrEmpty(response.visual_grounding.label))
                label = response.visual_grounding.label;
            if (string.IsNullOrEmpty(label) && response.intent != null)
                label = response.intent.target_ref;
            if (string.IsNullOrEmpty(label))
                return false;

            string query = Normalize(label);
            foreach (var marker in Object.FindObjectsByType<AiKnownSceneObjectMarker>(
                         FindObjectsSortMode.None))
            {
                if (marker == null || !marker.isActiveAndEnabled) continue;
                string markerLabel = Normalize(marker.label);
                string markerId = Normalize(marker.objectId);
                if (query.Contains(markerLabel) ||
                    markerLabel.Contains(query) ||
                    query.Contains(markerId) ||
                    markerId.Contains(query))
                {
                    target = marker.transform.position;
                    return true;
                }
            }
            return false;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value)
                ? ""
                : value.Trim().ToLowerInvariant().Replace("_", " ");
        }

        private static bool TryGenerateGeometricPrimitive(
            AiCommandResponse response, PlacedRobot robot, out string error)
        {
            error = null;
            string kind = response.plan_ir.kind ?? "";
            string primitive = response.intent != null
                ? response.intent.motion_primitive ?? ""
                : "";
            string command = (kind + " " + primitive).ToLowerInvariant();

            if (!command.Contains("circle") &&
                kind != "geometric_primitive")
                return false;

            if (robot.kind == RobotKind.Mobile)
            {
                error = "Geometric TCP primitive was requested for a mobile robot.";
                return false;
            }

            Transform tcp = CcdIkSolver.FindTcp(robot.transform);
            if (tcp == null)
            {
                error = "Cannot generate geometric primitive without TCP frame.";
                return false;
            }

            Vector3 center = tcp.position;
            Vector3 horizontal = robot.transform.right;
            if (horizontal.sqrMagnitude < 1e-6f)
                horizontal = Vector3.right;
            horizontal.y = 0f;
            horizontal.Normalize();

            const int count = 16;
            const float radius = 0.06f;
            var waypoints = new AiWaypoint[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                Vector3 p = center +
                            horizontal * Mathf.Cos(a) * radius +
                            Vector3.up * Mathf.Sin(a) * radius;
                waypoints[i] = new AiWaypoint
                {
                    position_m = AiModelUtil.Vec3(p),
                    speed_scale = Mathf.Min(response.plan_ir.speed_scale, 0.25f)
                };
            }

            response.plan_ir.kind = "geometric_primitive";
            response.plan_ir.contact_allowed = false;
            response.plan_ir.waypoints = waypoints;
            if (string.IsNullOrEmpty(response.spoken_reply))
                response.spoken_reply = "Generated a circular TCP motion. Please confirm.";
            return true;
        }

        private static PlacedRobot FindRobot(string id)
        {
            PlacedRobot first = null;
            foreach (var r in Object.FindObjectsByType<PlacedRobot>(
                         FindObjectsSortMode.None))
            {
                if (first == null) first = r;
                if (!string.IsNullOrEmpty(id) && r.displayName == id) return r;
            }
            return first;
        }

        private static float GroundY()
        {
            var ground = GameObject.Find("Ground");
            return ground != null ? ground.transform.position.y : 0f;
        }

        private static Vector3 ClampToReach(PlacedRobot robot, Vector3 point)
        {
            if (robot == null || robot.kind == RobotKind.Mobile ||
                robot.reachRadius <= 0f)
                return point;

            Vector3 center = robot.transform.TransformPoint(
                robot.reachCenterLocal);
            Vector3 delta = point - center;
            float maxRadius = Mathf.Max(robot.reachRadius * 0.98f, 0.05f);
            if (delta.magnitude <= maxRadius || delta.sqrMagnitude < 1e-8f)
                return point;
            return center + delta.normalized * maxRadius;
        }
    }
}
