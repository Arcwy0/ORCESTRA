using System.Collections.Generic;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.AI
{
    public class AiPlanValidator
    {
        public float minGroundingConfidence = 0.6f;
        public float reachToleranceMeters = 0.03f;
        public float ikSampleSpacingMeters = 0.015f;
        public int ikMinSegmentSamples = 12;
        public int ikValidationIterations = 80;
        public float ikWarningErrorMeters = 0.08f;

        public bool Validate(AiCommandResponse response, out string error)
        {
            if (!ValidateResponseShape(response, out error))
                return false;

            var robot = FindRobot(response.plan_ir.robot_id);
            if (robot == null)
            {
                error = "No placed robot matches the plan.";
                return false;
            }

            if (response.plan_ir.waypoints == null ||
                response.plan_ir.waypoints.Length == 0)
            {
                error = "Plan has no waypoints.";
                return false;
            }

            if (!KindMatches(robot.kind, response.plan_ir.kind))
            {
                error = "Plan kind does not match the selected robot type.";
                return false;
            }

            if (robot.kind == RobotKind.Mobile)
                return ValidateMobile(robot, response.plan_ir, out error);

            return ValidateManipulator(robot, response.plan_ir, out error);
        }

        public bool ValidateResponseShape(
            AiCommandResponse response, out string error)
        {
            error = null;
            if (response == null)
            {
                error = "No AI response.";
                return false;
            }

            if (response.error != null &&
                !string.IsNullOrEmpty(response.error.message))
            {
                error = response.error.message;
                return false;
            }

            if (response.plan_ir == null)
            {
                error = "AI response has no plan.";
                return false;
            }

            if (response.plan_ir.contact_allowed)
            {
                error = "Contact plans are disabled in v1.";
                return false;
            }

            if (response.visual_grounding != null &&
                response.visual_grounding.confidence > 0f &&
                response.visual_grounding.confidence < minGroundingConfidence)
            {
                error = "Grounding confidence is too low.";
                return false;
            }

            if (response.plan_ir.waypoints == null ||
                response.plan_ir.waypoints.Length == 0)
            {
                error = "Plan has no waypoints.";
                return false;
            }

            foreach (var wp in response.plan_ir.waypoints)
            {
                if (wp == null || wp.position_m == null ||
                    wp.position_m.Length < 3)
                {
                    error = "Plan contains an incomplete waypoint.";
                    return false;
                }
                if (!AiModelUtil.IsFinite(AiModelUtil.ToVector3(wp.position_m)))
                {
                    error = "Plan contains a non-finite waypoint.";
                    return false;
                }
            }

            return true;
        }

        private static bool KindMatches(RobotKind robotKind, string planKind)
        {
            if (string.IsNullOrEmpty(planKind)) return false;
            if (robotKind == RobotKind.Mobile)
                return planKind == "mobile_route";
            return planKind == "manipulator_reach" ||
                   planKind == "geometric_primitive";
        }

        private bool ValidateManipulator(
            PlacedRobot robot, AiPlanIr plan, out string error)
        {
            error = null;
            var ctrl = robot.GetComponentInChildren<UR3JointController>();
            var tcp = CcdIkSolver.FindTcp(robot.transform);
            if (ctrl == null || tcp == null)
            {
                error = "Manipulator controller or TCP frame is missing.";
                return false;
            }

            Vector3 reachCenter = robot.transform.TransformPoint(
                robot.reachCenterLocal);
            foreach (var wp in plan.waypoints)
            {
                if (wp == null || wp.position_m == null ||
                    wp.position_m.Length < 3)
                {
                    error = "Plan contains an incomplete waypoint.";
                    return false;
                }
                Vector3 p = AiModelUtil.ToVector3(wp.position_m);
                if (!AiModelUtil.IsFinite(p))
                {
                    error = "Plan contains a non-finite waypoint.";
                    return false;
                }
                if (Vector3.Distance(p, reachCenter) >
                    robot.reachRadius + reachToleranceMeters)
                {
                    error = "Target is outside manipulator reach.";
                    return false;
                }
            }

            var requestedTargets = new List<Vector3>();
            for (int i = 0; i < plan.waypoints.Length; i++)
                requestedTargets.Add(AiModelUtil.ToVector3(
                    plan.waypoints[i].position_m));

            var targets = BuildManipulatorSamples(tcp.position, requestedTargets);

            var tcpErrors = new List<float>();
            var solved = CcdIkSolver.SolveBatch(
                ctrl, tcp, targets, ikValidationIterations, 0.004f, tcpErrors);
            if (solved == null || solved.Count != targets.Count)
            {
                error = "IK pre-solve failed.";
                return false;
            }

            float maxError = 0f;
            for (int i = 0; i < tcpErrors.Count; i++)
                maxError = Mathf.Max(maxError, tcpErrors[i]);
            if (maxError > ikWarningErrorMeters)
            {
                Debug.LogWarning(
                    $"[RobotAI] IK validation residual is high " +
                    $"({maxError:0.000} m), but the waypoint is inside the " +
                    "declared workspace. Allowing preview; execution will " +
                    "perform the final sampled IK solve.");
            }
            return true;
        }

        private List<Vector3> BuildManipulatorSamples(
            Vector3 start, IReadOnlyList<Vector3> requestedTargets)
        {
            var samples = new List<Vector3>();
            float spacing = Mathf.Max(0.005f, ikSampleSpacingMeters);
            int minSamples = Mathf.Max(1, ikMinSegmentSamples);
            Vector3 from = start;
            for (int i = 0; i < requestedTargets.Count; i++)
            {
                Vector3 to = requestedTargets[i];
                int count = Mathf.Max(
                    minSamples,
                    Mathf.CeilToInt(Vector3.Distance(from, to) / spacing));
                for (int s = 1; s <= count; s++)
                    samples.Add(Vector3.Lerp(from, to, s / (float)count));
                from = to;
            }
            return samples;
        }

        private static bool ValidateMobile(
            PlacedRobot robot, AiPlanIr plan, out string error)
        {
            error = null;
            var mobile = robot.GetComponentInChildren<MobileBaseController>();
            if (mobile == null)
            {
                error = "Mobile controller is missing.";
                return false;
            }

            foreach (var wp in plan.waypoints)
            {
                if (wp == null || wp.position_m == null ||
                    wp.position_m.Length < 3)
                {
                    error = "Plan contains an incomplete waypoint.";
                    return false;
                }
                Vector3 p = AiModelUtil.ToVector3(wp.position_m);
                if (!AiModelUtil.IsFinite(p))
                {
                    error = "Plan contains a non-finite waypoint.";
                    return false;
                }
                p.y = mobile.Body.position.y;
                Vector3 from = mobile.Body.position;
                from.y = p.y;
                Vector3 dir = p - from;
                if (dir.sqrMagnitude < 0.0025f) continue;
                var hits = Physics.SphereCastAll(
                    from + Vector3.up * 0.2f, 0.18f, dir.normalized,
                    dir.magnitude);
                foreach (var hit in hits)
                {
                    if (hit.collider == null) continue;
                    if (hit.collider.transform.IsChildOf(robot.transform))
                        continue;
                    error = "Mobile route intersects an obstacle collider.";
                    return false;
                }
            }
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
    }
}
