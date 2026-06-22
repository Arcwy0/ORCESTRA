using System.Collections.Generic;
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

            var robot = FindRobot(response.plan_ir.robot_id);
            if (robot == null)
            {
                error = "Plan references a robot that is not placed.";
                return false;
            }

            bool needsImageGrounding = RequiresImageGrounding(response);
            if (response.plan_ir.waypoints != null &&
                response.plan_ir.waypoints.Length > 0 &&
                !needsImageGrounding)
            {
                NormalizePlanKind(response, robot);
                return true;
            }
            if (needsImageGrounding &&
                response.plan_ir.waypoints != null &&
                response.plan_ir.waypoints.Length > 0)
            {
                Debug.Log(
                    "[RobotAI] Ignoring model-supplied waypoints for " +
                    "image-grounded unknown target; Unity will lift bbox to 3D.");
                response.plan_ir.waypoints = new AiWaypoint[0];
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

            if (grounding == null ||
                !GroundingHasImageEvidence(grounding))
            {
                error = "AI response has no bbox or preferred image point to ground.";
                return false;
            }

            if (TryGroundImageRegion(grounding, cam, out target, out error))
                return true;

            if (string.IsNullOrEmpty(error))
                error = "Image bbox did not intersect a collider, MR mesh, or floor plane.";
            return false;
        }

        private static bool RequiresImageGrounding(AiCommandResponse response)
        {
            var grounding = response != null ? response.visual_grounding : null;
            if (grounding == null) return false;
            if (grounding.world_position_m != null &&
                grounding.world_position_m.Length >= 3 &&
                grounding.world_confidence >= 0.6f)
                return false;
            return GroundingHasImageEvidence(grounding);
        }

        private static bool GroundingHasImageEvidence(AiVisualGrounding grounding)
        {
            return grounding != null &&
                   ((grounding.preferred_point_px != null &&
                     grounding.preferred_point_px.Length >= 2) ||
                    (grounding.bbox_xyxy_px != null &&
                     grounding.bbox_xyxy_px.Length >= 4));
        }

        private static bool TryGroundImageRegion(
            AiVisualGrounding grounding, Camera cam, out Vector3 target,
            out string error)
        {
            target = Vector3.zero;
            error = null;

            var candidates = BuildImageGroundingCandidates(grounding, cam);
            for (int i = 0; i < candidates.Count; i++)
            {
                Vector2 p = candidates[i];
                if (TryRaycastImagePoint(cam, p, out RaycastHit hit))
                {
                    target = hit.point;
                    grounding.preferred_point_px = new[] { p.x, p.y };
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.72f);
                    Debug.Log(
                        $"[RobotAI] Grounded image bbox at pixel " +
                        $"({p.x:0.0}, {p.y:0.0}) via collider " +
                        $"{hit.collider.name}.");
                    return true;
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                Vector2 p = candidates[i];
                if (TryGroundPlaneImagePoint(cam, p, out target))
                {
                    grounding.preferred_point_px = new[] { p.x, p.y };
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.55f);
                    Debug.Log(
                        $"[RobotAI] Grounded image bbox at pixel " +
                        $"({p.x:0.0}, {p.y:0.0}) on floor plane fallback.");
                    return true;
                }
            }

            error = "Image bbox did not intersect a collider, MR mesh, or floor plane.";
            return false;
        }

        private static List<Vector2> BuildImageGroundingCandidates(
            AiVisualGrounding grounding, Camera cam)
        {
            var points = new List<Vector2>();

            if (grounding.preferred_point_px != null &&
                grounding.preferred_point_px.Length >= 2)
                AddCandidate(points, ClampImagePoint(
                    new Vector2(
                        grounding.preferred_point_px[0],
                        grounding.preferred_point_px[1]),
                    cam));

            if (grounding.bbox_xyxy_px != null &&
                grounding.bbox_xyxy_px.Length >= 4)
            {
                float x1 = Mathf.Min(grounding.bbox_xyxy_px[0],
                    grounding.bbox_xyxy_px[2]);
                float y1 = Mathf.Min(grounding.bbox_xyxy_px[1],
                    grounding.bbox_xyxy_px[3]);
                float x2 = Mathf.Max(grounding.bbox_xyxy_px[0],
                    grounding.bbox_xyxy_px[2]);
                float y2 = Mathf.Max(grounding.bbox_xyxy_px[1],
                    grounding.bbox_xyxy_px[3]);

                AddCandidate(points, ClampImagePoint(
                    new Vector2((x1 + x2) * 0.5f, (y1 + y2) * 0.5f), cam));
                AddCandidate(points, ClampImagePoint(
                    new Vector2((x1 + x2) * 0.5f, Mathf.Lerp(y1, y2, 0.75f)),
                    cam));

                const int grid = 5;
                for (int iy = 0; iy < grid; iy++)
                {
                    float ty = (iy + 0.5f) / grid;
                    for (int ix = 0; ix < grid; ix++)
                    {
                        float tx = (ix + 0.5f) / grid;
                        AddCandidate(points, ClampImagePoint(
                            new Vector2(
                                Mathf.Lerp(x1, x2, tx),
                                Mathf.Lerp(y1, y2, ty)),
                            cam));
                    }
                }
            }

            return points;
        }

        private static void AddCandidate(List<Vector2> points, Vector2 point)
        {
            for (int i = 0; i < points.Count; i++)
                if (Vector2.Distance(points[i], point) < 1f)
                    return;
            points.Add(point);
        }

        private static Vector2 ClampImagePoint(Vector2 p, Camera cam)
        {
            float maxX = Mathf.Max(0f, cam.pixelWidth - 1f);
            float maxY = Mathf.Max(0f, cam.pixelHeight - 1f);
            return new Vector2(
                Mathf.Clamp(p.x, 0f, maxX),
                Mathf.Clamp(p.y, 0f, maxY));
        }

        private static bool TryRaycastImagePoint(
            Camera cam, Vector2 topLeftPixel, out RaycastHit bestHit)
        {
            float pyBottomLeft = cam.pixelHeight - topLeftPixel.y;
            var ray = cam.ScreenPointToRay(new Vector3(
                topLeftPixel.x, pyBottomLeft, 0f));
            var hits = Physics.RaycastAll(ray, 100f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (!IsValidGroundingHit(hits[i])) continue;
                bestHit = hits[i];
                return true;
            }
            bestHit = new RaycastHit();
            return false;
        }

        private static bool TryGroundPlaneImagePoint(
            Camera cam, Vector2 topLeftPixel, out Vector3 target)
        {
            target = Vector3.zero;
            float pyBottomLeft = cam.pixelHeight - topLeftPixel.y;
            var ray = cam.ScreenPointToRay(new Vector3(
                topLeftPixel.x, pyBottomLeft, 0f));
            var plane = new Plane(Vector3.up, new Vector3(0f, GroundY(), 0f));
            if (plane.Raycast(ray, out float enter) && enter > 0f)
            {
                target = ray.GetPoint(enter);
                return true;
            }
            return false;
        }

        private static bool IsValidGroundingHit(RaycastHit hit)
        {
            if (hit.collider == null) return false;
            if (hit.collider.isTrigger) return false;
            Transform t = hit.collider.transform;
            if (t.GetComponentInParent<PlacedRobot>() != null) return false;
            if (t.GetComponentInParent<Canvas>() != null) return false;
            string name = t.name.ToLowerInvariant();
            return !name.Contains("preview") &&
                   !name.Contains("path") &&
                   !name.Contains("waypoint") &&
                   !name.Contains("controller");
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
            string task = response.intent != null
                ? response.intent.task_type ?? ""
                : "";
            string target = response.intent != null
                ? response.intent.target_ref ?? ""
                : "";
            string command = (kind + " " + task + " " + target + " " +
                              primitive + " " + response.spoken_reply)
                .ToLowerInvariant();

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

            Vector3 center = command.Contains("base")
                ? new Vector3(
                    robot.transform.position.x,
                    tcp.position.y,
                    robot.transform.position.z)
                : tcp.position;

            Vector3 axisA = robot.transform.right;
            if (axisA.sqrMagnitude < 1e-6f) axisA = Vector3.right;
            axisA.y = 0f;
            if (axisA.sqrMagnitude < 1e-6f) axisA = Vector3.right;
            axisA.Normalize();

            Vector3 axisB = command.Contains("horizontal")
                ? Vector3.Cross(Vector3.up, axisA).normalized
                : Vector3.up;

            const int count = 24;
            float radius = ParseRadiusMeters(command, 0.06f);
            var waypoints = new AiWaypoint[count + 1];
            for (int i = 0; i <= count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                Vector3 p = center +
                            axisA * Mathf.Cos(a) * radius +
                            axisB * Mathf.Sin(a) * radius;
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

        private static void NormalizePlanKind(
            AiCommandResponse response, PlacedRobot robot)
        {
            if (response == null || response.plan_ir == null || robot == null)
                return;

            string kind = response.plan_ir.kind ?? "";
            string normalizedKind = kind.Trim().ToLowerInvariant();
            if (robot.kind == RobotKind.Mobile)
            {
                if (normalizedKind != "mobile_route")
                    response.plan_ir.kind = "mobile_route";
                return;
            }

            if (normalizedKind == "manipulator_reach" ||
                normalizedKind == "geometric_primitive")
                return;

            string primitive = response.intent != null
                ? response.intent.motion_primitive ?? ""
                : "";
            string task = response.intent != null
                ? response.intent.task_type ?? ""
                : "";
            string target = response.intent != null
                ? response.intent.target_ref ?? ""
                : "";
            string descriptor = (normalizedKind + " " + task + " " +
                                 target + " " + primitive)
                .ToLowerInvariant();
            bool geometric =
                descriptor.Contains("circle") ||
                descriptor.Contains("arc") ||
                descriptor.Contains("line") ||
                descriptor.Contains("geometric") ||
                descriptor.Contains("primitive") ||
                descriptor.Contains("trajectory") ||
                descriptor.Contains("path");

            response.plan_ir.kind = geometric
                ? "geometric_primitive"
                : "manipulator_reach";
            Debug.Log(
                $"[RobotAI] Normalized plan kind '{kind}' to " +
                $"'{response.plan_ir.kind}' for manipulator.");
        }

        private static float ParseRadiusMeters(string command, float fallback)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                command,
                @"(\d+(?:[\.,]\d+)?)\s*(centimeters?|centimetres?|cm|meters?|metres?|m)\b");
            if (!match.Success) return fallback;

            if (!float.TryParse(
                    match.Groups[1].Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float value))
                return fallback;

            string unit = match.Groups[2].Value;
            if (unit == "cm" ||
                unit.StartsWith("centimeter") ||
                unit.StartsWith("centimetre"))
                return Mathf.Clamp(value / 100f, 0.01f, 0.5f);
            return Mathf.Clamp(value, 0.01f, 0.5f);
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
