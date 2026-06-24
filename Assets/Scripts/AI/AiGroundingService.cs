using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;
#if ORCESTRA_META_PCA
using Meta.XR;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace VRInteraction.AI
{
    public class AiGroundingService
    {
        private const string QuestPassthroughSource =
            "quest_passthrough_camera";
#if ORCESTRA_META_PCA
        private const string ScenePermission =
            "com.oculus.permission.USE_SCENE";
        private static EnvironmentRaycastManager _environmentRaycastManager;
#endif

        public static bool IsQuestPassthroughCapture(AiImageCapture capture)
        {
            return capture != null &&
                   string.Equals(capture.source, QuestPassthroughSource,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerator WarmupQuestEnvironmentRaycast(
            System.Action<string> done)
        {
#if ORCESTRA_META_PCA
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Permission.RequestUserPermission(ScenePermission);
                done?.Invoke("MR scene permission requested. Press SEND " +
                             "again after granting it.");
                yield break;
            }
#endif
            if (!EnvironmentRaycastManager.IsSupported)
            {
                done?.Invoke("MR environment raycast is not supported on this device/session.");
                yield break;
            }

            var manager = EnsureEnvironmentRaycastManager();
            if (manager == null)
            {
                done?.Invoke("MR environment raycast manager unavailable.");
                yield break;
            }

            for (int i = 0; i < 12; i++)
                yield return null;

            done?.Invoke(null);
#else
            done?.Invoke("MR environment raycast support is not compiled in.");
            yield break;
#endif
        }

        public bool EnsureWorldWaypoints(
            AiCommandResponse response, Camera cam, out string error)
        {
            return EnsureWorldWaypoints(response, cam, null, out error);
        }

        public bool EnsureWorldWaypoints(
            AiCommandResponse response, Camera cam, AiImageCapture capture,
            out string error)
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

            SyncVisualGroundings(response);
            bool needsImageGrounding = RequiresImageGrounding(response);
            if (response.plan_ir.waypoints != null &&
                response.plan_ir.waypoints.Length > 0 &&
                !needsImageGrounding)
            {
                NormalizePlanKind(response, robot);
                RepairManipulatorPath(response, robot);
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

            if (!TryGroundTargets(response, cam, capture,
                    out List<Vector3> targets, out error))
                return false;
            if (targets.Count == 0)
            {
                error = "AI response has no targets to ground.";
                return false;
            }

            if (robot.kind == RobotKind.Mobile)
            {
                response.plan_ir.kind = "mobile_route";
                var waypoints = new AiWaypoint[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    Vector3 p = targets[i];
                    p.y = GroundY();
                    waypoints[i] = new AiWaypoint
                    {
                        position_m = AiModelUtil.Vec3(p)
                    };
                }
                response.plan_ir.waypoints = waypoints;
                return true;
            }

            response.plan_ir.kind = "manipulator_reach";
            var manipulatorWaypoints = new AiWaypoint[targets.Count];
            for (int i = 0; i < targets.Count; i++)
            {
                Vector3 standoff = targets[i] + Vector3.up *
                    Mathf.Max(0.05f, response.plan_ir.min_clearance_m);
                standoff = ClampToReach(robot, standoff);
                manipulatorWaypoints[i] = new AiWaypoint
                {
                    position_m = AiModelUtil.Vec3(standoff)
                };
            }
            response.plan_ir.waypoints = manipulatorWaypoints;
            RepairManipulatorPath(response, robot);
            return true;
        }

        public static void RepairManipulatorPath(
            AiCommandResponse response, PlacedRobot robot)
        {
            if (response == null || response.plan_ir == null ||
                response.plan_ir.waypoints == null || robot == null ||
                robot.kind == RobotKind.Mobile)
                return;

            if (!string.Equals(
                    response.plan_ir.kind,
                    "manipulator_reach",
                    System.StringComparison.OrdinalIgnoreCase))
                return;

            var points = new List<Vector3>();
            Transform tcp = CcdIkSolver.FindTcp(robot.transform);
            if (tcp != null)
                points.Add(tcp.position);
            for (int i = 0; i < response.plan_ir.waypoints.Length; i++)
            {
                var wp = response.plan_ir.waypoints[i];
                if (wp == null || wp.position_m == null ||
                    wp.position_m.Length < 3)
                    return;
                points.Add(AiModelUtil.ToVector3(wp.position_m));
            }

            int originalWaypointCount = response.plan_ir.waypoints.Length;
            var repaired = AiManipulatorPathPlanner.RepairBaseCrossingPath(
                points,
                robot.transform.position,
                ManipulatorBaseAvoidRadius(robot),
                ManipulatorBaseDetourHeight(robot));
            if (repaired.Count <= points.Count)
                return;

            int firstWaypointIndex = tcp != null ? 1 : 0;
            int waypointCount = Mathf.Max(0, repaired.Count - firstWaypointIndex);
            var waypoints = new AiWaypoint[waypointCount];
            float speedScale = response.plan_ir.speed_scale;
            for (int i = 0; i < waypoints.Length; i++)
            {
                Vector3 p = ClampToReach(robot, repaired[i + firstWaypointIndex]);
                waypoints[i] = new AiWaypoint
                {
                    position_m = AiModelUtil.Vec3(p),
                    speed_scale = speedScale
                };
            }

            response.plan_ir.waypoints = waypoints;
            Debug.Log(
                $"[RobotAI] Repaired manipulator path around base: " +
                $"{originalWaypointCount} -> {waypoints.Length} waypoints.");
        }

        private static void SyncVisualGroundings(AiCommandResponse response)
        {
            if (response == null) return;
            if (response.visual_groundings != null &&
                response.visual_groundings.Length > 0)
            {
                response.visual_grounding = response.visual_groundings[0];
                return;
            }

            if (GroundingHasContent(response.visual_grounding))
                response.visual_groundings = new[] { response.visual_grounding };
        }

        private static bool TryGroundTargets(
            AiCommandResponse response, Camera cam, AiImageCapture capture,
            out List<Vector3> targets, out string error)
        {
            targets = new List<Vector3>();
            error = null;
            var groundings = GetGroundings(response);
            if (groundings.Count == 0)
            {
                if (!TryGroundTarget(response, response.visual_grounding, cam,
                        capture, out Vector3 target, out error))
                    return false;
                targets.Add(target);
                return true;
            }

            for (int i = 0; i < groundings.Count; i++)
            {
                var grounding = groundings[i];
                if (!TryGroundTarget(response, grounding, cam,
                        capture, out Vector3 target, out error))
                {
                    string label = grounding != null ? grounding.label : "";
                    string prefix = string.IsNullOrEmpty(label)
                        ? $"target {i + 1}"
                        : $"target {i + 1} '{label}'";
                    error = $"{prefix}: {error}";
                    return false;
                }
                targets.Add(target);
            }

            return true;
        }

        private static bool TryGroundTarget(
            AiCommandResponse response, AiVisualGrounding grounding,
            Camera cam, AiImageCapture capture, out Vector3 target,
            out string error)
        {
            target = Vector3.zero;
            error = null;

            if (grounding != null &&
                grounding.world_position_m != null &&
                grounding.world_position_m.Length >= 3 &&
                grounding.world_confidence >= 0.6f)
            {
                target = AiModelUtil.ToVector3(grounding.world_position_m);
                return true;
            }

            if (TryKnownSceneObject(response, grounding, out target))
            {
                if (grounding != null)
                {
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.9f);
                }
                return true;
            }

            bool hasCaptureRayProvider = capture != null &&
                                         capture.rayProvider != null;
            if (cam == null && !hasCaptureRayProvider)
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

            if (TryGroundImageRegion(grounding, cam, capture,
                    out target, out error))
                return true;

            if (string.IsNullOrEmpty(error))
                error = "Image bbox did not intersect a collider, MR mesh, or floor plane.";
            return false;
        }

        private static bool RequiresImageGrounding(AiCommandResponse response)
        {
            var groundings = GetGroundings(response);
            if (groundings.Count == 0 && response != null)
                groundings.Add(response.visual_grounding);
            for (int i = 0; i < groundings.Count; i++)
            {
                var grounding = groundings[i];
                if (grounding == null) continue;
                if (grounding.world_position_m != null &&
                    grounding.world_position_m.Length >= 3 &&
                    grounding.world_confidence >= 0.6f)
                    continue;
                if (GroundingHasImageEvidence(grounding))
                    return true;
            }
            return false;
        }

        private static List<AiVisualGrounding> GetGroundings(
            AiCommandResponse response)
        {
            var result = new List<AiVisualGrounding>();
            if (response == null) return result;

            if (response.visual_groundings != null)
            {
                for (int i = 0; i < response.visual_groundings.Length; i++)
                    if (GroundingHasContent(response.visual_groundings[i]))
                        result.Add(response.visual_groundings[i]);
            }

            if (result.Count == 0 &&
                GroundingHasContent(response.visual_grounding))
                result.Add(response.visual_grounding);

            return result;
        }

        private static bool GroundingHasContent(AiVisualGrounding grounding)
        {
            if (grounding == null) return false;
            return !string.IsNullOrEmpty(grounding.label) ||
                   grounding.confidence > 0f ||
                   grounding.world_position_m != null ||
                   GroundingHasImageEvidence(grounding);
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
            AiVisualGrounding grounding, Camera cam, AiImageCapture capture,
            out Vector3 target, out string error)
        {
            target = Vector3.zero;
            error = null;
            bool requiresEnvironmentDepth = IsQuestPassthroughCapture(capture);

            int imageWidth = ImageWidth(cam, capture);
            int imageHeight = ImageHeight(cam, capture);
            if (!ImageEvidenceLooksUsable(grounding, imageWidth, imageHeight))
            {
                error = "AI image grounding is a low-confidence full-frame placeholder.";
                return false;
            }

            var candidates = BuildImageGroundingCandidates(
                grounding, imageWidth, imageHeight);
            string lastHitSource = "";
            if (requiresEnvironmentDepth)
            {
                bool found = false;
                float bestDistance = float.PositiveInfinity;
                Vector3 bestTarget = Vector3.zero;
                Vector2 bestPixel = Vector2.zero;
                string bestHitSource = "";
                for (int i = 0; i < candidates.Count; i++)
                {
                    Vector2 p = candidates[i];
                    if (TryRaycastImagePoint(cam, capture, p, imageWidth,
                            imageHeight, out Vector3 hitPoint,
                            out string hitSource, out float hitDistance))
                    {
                        if (!found || hitDistance < bestDistance)
                        {
                            found = true;
                            bestDistance = hitDistance;
                            bestTarget = hitPoint;
                            bestPixel = p;
                            bestHitSource = hitSource;
                        }
                    }
                    else if (!string.IsNullOrEmpty(hitSource))
                    {
                        lastHitSource = hitSource;
                    }
                }

                if (found)
                {
                    target = bestTarget;
                    grounding.preferred_point_px = new[] { bestPixel.x, bestPixel.y };
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.72f);
                    Debug.Log(
                        $"[RobotAI] Grounded Quest image bbox at pixel " +
                        $"({bestPixel.x:0.0}, {bestPixel.y:0.0}) via " +
                        $"{bestHitSource}; world=({target.x:0.000}, " +
                        $"{target.y:0.000}, {target.z:0.000}).");
                    return true;
                }

                error = "Quest passthrough grounding did not hit MR " +
                        "environment depth. " +
                        (string.IsNullOrEmpty(lastHitSource)
                            ? "No hit status was returned."
                            : lastHitSource);
                return false;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                Vector2 p = candidates[i];
                if (TryRaycastImagePoint(cam, capture, p, imageWidth,
                        imageHeight, out Vector3 hitPoint,
                        out string hitSource, out _))
                {
                    target = hitPoint;
                    grounding.preferred_point_px = new[] { p.x, p.y };
                    grounding.world_position_m = AiModelUtil.Vec3(target);
                    grounding.world_confidence = Mathf.Max(
                        grounding.world_confidence, 0.72f);
                    Debug.Log(
                        $"[RobotAI] Grounded image bbox at pixel " +
                        $"({p.x:0.0}, {p.y:0.0}) via {hitSource}; " +
                        $"world=({target.x:0.000}, {target.y:0.000}, " +
                        $"{target.z:0.000}).");
                    return true;
                }
                if (!string.IsNullOrEmpty(hitSource))
                    lastHitSource = hitSource;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                Vector2 p = candidates[i];
                if (TryGroundPlaneImagePoint(cam, capture, p, imageWidth,
                        imageHeight, out target))
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
            AiVisualGrounding grounding, int imageWidth, int imageHeight)
        {
            var points = new List<Vector2>();

            if (grounding.preferred_point_px != null &&
                grounding.preferred_point_px.Length >= 2)
                AddCandidate(points, ClampImagePoint(
                    new Vector2(
                        grounding.preferred_point_px[0],
                        grounding.preferred_point_px[1]),
                    imageWidth, imageHeight));

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
                    new Vector2((x1 + x2) * 0.5f, (y1 + y2) * 0.5f),
                    imageWidth, imageHeight));
                AddCandidate(points, ClampImagePoint(
                    new Vector2((x1 + x2) * 0.5f, Mathf.Lerp(y1, y2, 0.75f)),
                    imageWidth, imageHeight));

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
                            imageWidth, imageHeight));
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

        private static Vector2 ClampImagePoint(
            Vector2 p, int imageWidth, int imageHeight)
        {
            float maxX = Mathf.Max(0f, imageWidth - 1f);
            float maxY = Mathf.Max(0f, imageHeight - 1f);
            return new Vector2(
                Mathf.Clamp(p.x, 0f, maxX),
                Mathf.Clamp(p.y, 0f, maxY));
        }

        private static bool TryRaycastImagePoint(
            Camera cam, AiImageCapture capture, Vector2 topLeftPixel,
            int imageWidth, int imageHeight, out Vector3 target,
            out string hitSource, out float hitDistance)
        {
            target = Vector3.zero;
            hitSource = "";
            hitDistance = 0f;
            if (!TryCreateImageRay(cam, capture, topLeftPixel, imageWidth,
                    imageHeight, out Ray ray, out string raySource,
                    out string rayError))
            {
                hitSource = rayError;
                return false;
            }

            if (IsQuestPassthroughCapture(capture))
            {
                if (TryEnvironmentRaycast(ray, out target, out hitSource))
                {
                    hitDistance = Vector3.Distance(ray.origin, target);
                    return true;
                }
                return false;
            }

            var hits = Physics.RaycastAll(ray, 100f);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                if (!IsValidGroundingHit(hits[i])) continue;
                target = hits[i].point;
                hitSource = $"collider {hits[i].collider.name} ({raySource})";
                hitDistance = hits[i].distance;
                return true;
            }

            if (TryEnvironmentRaycast(ray, out target, out hitSource))
            {
                hitDistance = Vector3.Distance(ray.origin, target);
                return true;
            }

            return false;
        }

        private static bool TryGroundPlaneImagePoint(
            Camera cam, AiImageCapture capture, Vector2 topLeftPixel,
            int imageWidth, int imageHeight, out Vector3 target)
        {
            target = Vector3.zero;
            if (!TryCreateImageRay(cam, capture, topLeftPixel, imageWidth,
                    imageHeight, out Ray ray, out _, out _))
                return false;
            var plane = new Plane(Vector3.up, new Vector3(0f, GroundY(), 0f));
            if (plane.Raycast(ray, out float enter) && enter > 0f)
            {
                target = ray.GetPoint(enter);
                return true;
            }
            return false;
        }

        private static int ImageWidth(Camera cam, AiImageCapture capture)
        {
            if (capture != null && capture.width > 0) return capture.width;
            return cam != null ? cam.pixelWidth : Screen.width;
        }

        private static int ImageHeight(Camera cam, AiImageCapture capture)
        {
            if (capture != null && capture.height > 0) return capture.height;
            return cam != null ? cam.pixelHeight : Screen.height;
        }

        private static bool ImageEvidenceLooksUsable(
            AiVisualGrounding grounding, int imageWidth, int imageHeight)
        {
            if (grounding == null) return false;
            bool hasPoint = grounding.preferred_point_px != null &&
                            grounding.preferred_point_px.Length >= 2;
            bool hasBbox = grounding.bbox_xyxy_px != null &&
                           grounding.bbox_xyxy_px.Length >= 4;
            if (!hasPoint && !hasBbox) return false;

            if (grounding.confidence <= 0.05f &&
                grounding.world_confidence <= 0f)
                return false;

            if (!hasBbox || imageWidth <= 0 || imageHeight <= 0)
                return true;

            float x1 = Mathf.Min(grounding.bbox_xyxy_px[0],
                grounding.bbox_xyxy_px[2]);
            float y1 = Mathf.Min(grounding.bbox_xyxy_px[1],
                grounding.bbox_xyxy_px[3]);
            float x2 = Mathf.Max(grounding.bbox_xyxy_px[0],
                grounding.bbox_xyxy_px[2]);
            float y2 = Mathf.Max(grounding.bbox_xyxy_px[1],
                grounding.bbox_xyxy_px[3]);
            float boxArea = Mathf.Max(0f, x2 - x1) *
                            Mathf.Max(0f, y2 - y1);
            float imageArea = imageWidth * imageHeight;
            bool nearlyFullFrame = imageArea > 1f &&
                                   boxArea / imageArea > 0.80f;
            return !nearlyFullFrame || grounding.confidence >= 0.5f;
        }

        private static bool TryCreateImageRay(
            Camera cam, AiImageCapture capture, Vector2 topLeftPixel,
            int imageWidth, int imageHeight, out Ray ray, out string raySource,
            out string error)
        {
            ray = default;
            raySource = "";
            error = null;

            if (capture != null && capture.rayProvider != null)
            {
                if (capture.rayProvider.TryCreateRay(
                        topLeftPixel, imageWidth, imageHeight, out ray,
                        out error))
                {
                    raySource = capture.rayProvider.RaySource;
                    return true;
                }

                if (string.Equals(capture.source, QuestPassthroughSource,
                        System.StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (capture != null &&
                string.Equals(capture.source, QuestPassthroughSource,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                error = "Quest passthrough image has no PCA ray provider.";
                return false;
            }

            if (cam == null)
            {
                error = "No active camera for image ray.";
                return false;
            }

            float pyBottomLeft = cam.pixelHeight - topLeftPixel.y;
            ray = cam.ScreenPointToRay(new Vector3(
                topLeftPixel.x, pyBottomLeft, 0f));
            raySource = "Unity camera";
            return true;
        }

        private static bool TryEnvironmentRaycast(
            Ray ray, out Vector3 target, out string hitSource)
        {
            target = Vector3.zero;
            hitSource = "";
#if ORCESTRA_META_PCA
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(ScenePermission))
            {
                Permission.RequestUserPermission(ScenePermission);
                hitSource = "MR scene permission requested";
                return false;
            }
#endif
            if (!EnvironmentRaycastManager.IsSupported)
            {
                hitSource = "MR environment raycast is not supported";
                return false;
            }

            var manager = EnsureEnvironmentRaycastManager();
            if (manager == null)
            {
                hitSource = "MR environment raycast manager unavailable";
                return false;
            }

            if (manager.Raycast(ray, out var hit, 100f))
            {
                target = hit.point;
                hitSource = "MR environment raycast";
                return true;
            }
            if (hit.status == EnvironmentRaycastHitStatus.HitPointOccluded)
            {
                target = hit.point;
                hitSource = "MR environment raycast occluded hit";
                return true;
            }

            hitSource = $"MR environment raycast {hit.status}";
#endif
            return false;
        }

#if ORCESTRA_META_PCA
        private static EnvironmentRaycastManager EnsureEnvironmentRaycastManager()
        {
            if (_environmentRaycastManager == null)
                _environmentRaycastManager =
                    Object.FindAnyObjectByType<EnvironmentRaycastManager>(
                        FindObjectsInactive.Include);
            if (_environmentRaycastManager == null)
            {
                var go = new GameObject("RobotAI_EnvironmentRaycastManager");
                Object.DontDestroyOnLoad(go);
                _environmentRaycastManager =
                    go.AddComponent<EnvironmentRaycastManager>();
            }
            if (!_environmentRaycastManager.enabled)
                _environmentRaycastManager.enabled = true;
            return _environmentRaycastManager;
        }
#endif

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
            AiCommandResponse response, AiVisualGrounding grounding,
            out Vector3 target)
        {
            target = Vector3.zero;
            string label = "";
            if (grounding != null && !string.IsNullOrEmpty(grounding.label))
                label = grounding.label;
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

        public static float ManipulatorBaseAvoidRadius(PlacedRobot robot)
        {
            if (robot == null || robot.reachRadius <= 0f)
                return 0.24f;
            return Mathf.Clamp(robot.reachRadius * 0.2f, 0.24f, 0.65f);
        }

        public static float ManipulatorBaseDetourHeight(PlacedRobot robot)
        {
            if (robot == null || robot.reachRadius <= 0f)
                return 0.12f;
            return Mathf.Clamp(robot.reachRadius * 0.15f, 0.12f, 0.35f);
        }
    }
}
