using System.Collections.Generic;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.AI
{
    public class AiMotionExecutor : MonoBehaviour
    {
        public float manipulatorPlaybackRate = 30f;
        [Tooltip("Maximum Cartesian spacing between AI TCP path samples.")]
        public float manipulatorTcpSampleSpacing = 0.015f;
        [Tooltip("Minimum samples for each AI manipulator segment.")]
        public int manipulatorMinSegmentSamples = 12;
        [Tooltip("CCD iterations per AI manipulator path sample.")]
        public int manipulatorIkIterations = 80;
        [Tooltip("Reject AI manipulator IK paths whose solved TCP error exceeds this.")]
        public float manipulatorMaxIkErrorMeters = 0.08f;
        [Tooltip("Use online TCP servoing when offline IK is too inaccurate.")]
        public bool useTcpServoFallback = true;
        [Tooltip("TCP servo fallback speed in world metres per second.")]
        public float tcpServoSpeedMetersPerSecond = 0.08f;
        [Tooltip("TCP servo fallback arrival tolerance in metres.")]
        public float tcpServoArriveToleranceMeters = 0.025f;
        [Tooltip("Maximum time for TCP servo fallback before it stops.")]
        public float tcpServoTimeoutSeconds = 8f;
        [Tooltip("Final joint error allowed before AI manipulator execution stops.")]
        public float manipulatorFinalSettleToleranceDeg = 1.5f;
        [Tooltip("Maximum time to keep commanding the final AI pose before giving up.")]
        public float manipulatorFinalSettleTimeout = 6f;
        public float mobileArriveRadius = 0.25f;
        public float mobileTurnInPlaceDeg = 35f;

        private enum Mode { Idle, Manipulator, TcpServo, Mobile }
        private Mode _mode = Mode.Idle;

        private UR3JointController _ctrl;
        private Transform _tcp;
        private readonly List<float[]> _jointPath = new List<float[]>();
        private float _simT;
        private bool _holdingManipulatorFinal;
        private float _finalSettleT;
        private float[] _finalJointTarget;
        private Vector3 _finalTcpTarget;
        private Vector3 _servoTcpTarget;
        private float _servoT;
        private readonly List<Vector3> _servoTargets = new List<Vector3>();
        private int _servoIndex;

        private MobileBaseController _mobile;
        private Vector3[] _route;
        private int _routeIndex;

        public bool IsRunning => _mode != Mode.Idle;

        public static bool ShouldUseTcpServoFallback(
            bool useTcpServoFallback,
            string planKind,
            int waypointCount,
            float maxIkErrorMeters,
            float allowedMaxErrorMeters)
        {
            if (!useTcpServoFallback || waypointCount <= 0) return false;
            if (string.Equals(
                    planKind,
                    "geometric_primitive",
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
            return maxIkErrorMeters > allowedMaxErrorMeters;
        }

        public bool Execute(AiCommandResponse response, out string error)
        {
            error = null;
            Stop();

            if (response == null || response.plan_ir == null)
            {
                error = "No plan to execute.";
                return false;
            }

            var robot = FindRobot(response.plan_ir.robot_id);
            if (robot == null)
            {
                error = "Plan robot is not placed.";
                return false;
            }

            if (robot.kind == RobotKind.Mobile)
                return StartMobile(robot, response.plan_ir, out error);

            return StartManipulator(robot, response.plan_ir, out error);
        }

        public void Stop()
        {
            if (_ctrl != null && _ctrl.externalControl)
            {
                _ctrl.externalControl = false;
                _ctrl.SnapStateToMeasured();
            }
            if (_mobile != null) _mobile.Stop();

            _mode = Mode.Idle;
            _ctrl = null;
            _tcp = null;
            _jointPath.Clear();
            _mobile = null;
            _route = null;
            _routeIndex = 0;
            _simT = 0f;
            _holdingManipulatorFinal = false;
            _finalSettleT = 0f;
            _finalJointTarget = null;
            _finalTcpTarget = Vector3.zero;
            _servoTcpTarget = Vector3.zero;
            _servoT = 0f;
            _servoTargets.Clear();
            _servoIndex = 0;
        }

        private bool StartManipulator(
            PlacedRobot robot, AiPlanIr plan, out string error)
        {
            error = null;
            _ctrl = robot.GetComponentInChildren<UR3JointController>();
            _tcp = CcdIkSolver.FindTcp(robot.transform);
            if (_ctrl == null || _tcp == null)
            {
                error = "Manipulator controller or TCP is missing.";
                return false;
            }

            var requestedTargets = new List<Vector3>();
            foreach (var wp in plan.waypoints)
                requestedTargets.Add(AiModelUtil.ToVector3(wp.position_m));
            if (requestedTargets.Count == 0)
            {
                error = "Manipulator plan has no waypoints.";
                return false;
            }
            RepairManipulatorTargets(robot, plan, _tcp.position, requestedTargets);

            if (ShouldUseTcpServoFallback(
                    useTcpServoFallback,
                    plan.kind,
                    requestedTargets.Count,
                    0f,
                    manipulatorMaxIkErrorMeters) &&
                string.Equals(
                    plan.kind,
                    "geometric_primitive",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return StartManipulatorServo(requestedTargets, out error);
            }

            var targets = BuildManipulatorSamples(requestedTargets);
            _finalTcpTarget = requestedTargets[requestedTargets.Count - 1];

            _jointPath.Clear();
            var start = new float[6];
            for (int i = 0; i < 6; i++) start[i] = _ctrl.GetMeasuredDeg(i);
            _jointPath.Add(start);
            var tcpErrors = new List<float>();
            _jointPath.AddRange(CcdIkSolver.SolveBatch(
                _ctrl, _tcp, targets, manipulatorIkIterations, 0.004f,
                tcpErrors));
            if (_jointPath.Count < 2)
            {
                error = "IK solve produced no trajectory.";
                return false;
            }

            float maxError = 0f;
            for (int i = 0; i < tcpErrors.Count; i++)
                maxError = Mathf.Max(maxError, tcpErrors[i]);
            if (maxError > manipulatorMaxIkErrorMeters)
            {
                if (ShouldUseTcpServoFallback(
                        useTcpServoFallback,
                        plan.kind,
                        requestedTargets.Count,
                        maxError,
                        manipulatorMaxIkErrorMeters))
                {
                    Debug.LogWarning(
                        $"[RobotAI] Offline IK residual is high " +
                        $"({maxError:0.000} m); using TCP servo fallback " +
                        $"for {requestedTargets.Count} waypoint(s).");
                    return StartManipulatorServo(requestedTargets, out error);
                }

                error = $"Offline IK residual is too high ({maxError:0.000} m).";
                _jointPath.Clear();
                return false;
            }

            _finalJointTarget = _jointPath[_jointPath.Count - 1];

            _ctrl.externalControl = true;
            _simT = 0f;
            _holdingManipulatorFinal = false;
            _finalSettleT = 0f;
            Debug.Log(
                $"[RobotAI] Executing manipulator plan samples={targets.Count} " +
                $"path={_jointPath.Count} max_ik_error_m={maxError:0.000} " +
                $"tcp_start={_tcp.position} tcp_target={_finalTcpTarget}");
            _mode = Mode.Manipulator;
            return true;
        }

        private static void RepairManipulatorTargets(
            PlacedRobot robot,
            AiPlanIr plan,
            Vector3 tcpStart,
            List<Vector3> requestedTargets)
        {
            if (robot == null || plan == null || requestedTargets == null ||
                requestedTargets.Count == 0)
                return;
            if (!string.Equals(
                    plan.kind,
                    "manipulator_reach",
                    System.StringComparison.OrdinalIgnoreCase))
                return;

            var path = new List<Vector3> { tcpStart };
            path.AddRange(requestedTargets);
            var repaired = AiManipulatorPathPlanner.RepairBaseCrossingPath(
                path,
                robot.transform.position,
                AiGroundingService.ManipulatorBaseAvoidRadius(robot),
                AiGroundingService.ManipulatorBaseDetourHeight(robot));
            if (repaired.Count <= path.Count)
                return;

            int originalCount = requestedTargets.Count;
            requestedTargets.Clear();
            for (int i = 1; i < repaired.Count; i++)
                requestedTargets.Add(repaired[i]);
            Debug.Log(
                $"[RobotAI] Execution repaired manipulator path around base: " +
                $"{originalCount} -> {requestedTargets.Count} waypoints.");
        }

        private bool StartManipulatorServo(
            IReadOnlyList<Vector3> targets, out string error)
        {
            error = null;
            if (_ctrl == null || _tcp == null)
            {
                error = "Manipulator controller or TCP is missing.";
                return false;
            }

            _jointPath.Clear();
            _ctrl.externalControl = false;
            _ctrl.SnapStateToMeasured();
            _servoTargets.Clear();
            for (int i = 0; i < targets.Count; i++)
                _servoTargets.Add(targets[i]);
            if (_servoTargets.Count == 0)
            {
                error = "TCP servo path has no waypoints.";
                return false;
            }

            _servoIndex = 0;
            _servoTcpTarget = _servoTargets[_servoIndex];
            _servoT = 0f;
            Debug.Log(
                $"[RobotAI] Executing TCP servo path " +
                $"waypoints={_servoTargets.Count} " +
                $"tcp_start={_tcp.position} tcp_target={_servoTcpTarget}");
            _mode = Mode.TcpServo;
            return true;
        }

        private List<Vector3> BuildManipulatorSamples(
            IReadOnlyList<Vector3> requestedTargets)
        {
            var samples = new List<Vector3>();
            if (_tcp == null) return samples;

            Vector3 from = _tcp.position;
            float spacing = Mathf.Max(0.005f, manipulatorTcpSampleSpacing);
            int minSamples = Mathf.Max(1, manipulatorMinSegmentSamples);
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

        private bool StartMobile(
            PlacedRobot robot, AiPlanIr plan, out string error)
        {
            error = null;
            _mobile = robot.GetComponentInChildren<MobileBaseController>();
            if (_mobile == null)
            {
                error = "Mobile controller is missing.";
                return false;
            }

            _route = new Vector3[plan.waypoints.Length];
            for (int i = 0; i < _route.Length; i++)
            {
                _route[i] = AiModelUtil.ToVector3(plan.waypoints[i].position_m);
                _route[i].y = _mobile.Body.position.y;
            }
            if (_route.Length == 0)
            {
                error = "Mobile route is empty.";
                return false;
            }

            _routeIndex = 0;
            _mobile.ResetEmergencyStop();
            _mode = Mode.Mobile;
            return true;
        }

        private void FixedUpdate()
        {
            if (_mode == Mode.Manipulator) StepManipulator();
            else if (_mode == Mode.TcpServo) StepTcpServo();
            else if (_mode == Mode.Mobile) StepMobile();
        }

        private void StepManipulator()
        {
            if (_ctrl == null || _jointPath.Count == 0)
            {
                Stop();
                return;
            }

            if (_holdingManipulatorFinal)
            {
                HoldManipulatorFinal();
                return;
            }

            if (_jointPath.Count == 1)
            {
                WriteDrives(_jointPath[0]);
                BeginManipulatorFinalHold();
                return;
            }

            _simT += Time.fixedDeltaTime;
            float fIndex = _simT * manipulatorPlaybackRate;
            int i = Mathf.FloorToInt(fIndex);
            if (i >= _jointPath.Count - 1)
            {
                WriteDrives(_jointPath[_jointPath.Count - 1]);
                BeginManipulatorFinalHold();
                return;
            }

            float frac = fIndex - i;
            var a = _jointPath[i];
            var b = _jointPath[i + 1];
            for (int j = 0; j < 6; j++)
                WriteDrive(j, Mathf.LerpAngle(a[j], b[j], frac));
        }

        private void BeginManipulatorFinalHold()
        {
            _holdingManipulatorFinal = true;
            _finalSettleT = 0f;
            if (_finalJointTarget == null && _jointPath.Count > 0)
                _finalJointTarget = _jointPath[_jointPath.Count - 1];
        }

        private void HoldManipulatorFinal()
        {
            if (_finalJointTarget == null)
            {
                Stop();
                return;
            }

            WriteDrives(_finalJointTarget);
            _finalSettleT += Time.fixedDeltaTime;

            float maxJointError = MaxJointErrorDeg(_finalJointTarget);
            bool settled =
                maxJointError <= manipulatorFinalSettleToleranceDeg;
            bool timedOut =
                _finalSettleT >= manipulatorFinalSettleTimeout;
            if (!settled && !timedOut) return;

            float tcpError = _tcp != null
                ? Vector3.Distance(_tcp.position, _finalTcpTarget)
                : -1f;
            if (timedOut && !settled)
            {
                Debug.LogWarning(
                    $"[RobotAI] Manipulator final pose timed out " +
                    $"joint_error_deg={maxJointError:0.0} " +
                    $"tcp_error_m={tcpError:0.000}");
            }
            else
            {
                Debug.Log(
                    $"[RobotAI] Manipulator final pose settled " +
                    $"joint_error_deg={maxJointError:0.0} " +
                    $"tcp_error_m={tcpError:0.000}");
            }
            Stop();
        }

        private float MaxJointErrorDeg(float[] target)
        {
            float maxError = 0f;
            for (int i = 0; i < 6 && i < target.Length; i++)
                maxError = Mathf.Max(
                    maxError,
                    Mathf.Abs(Mathf.DeltaAngle(
                        _ctrl.GetMeasuredDeg(i), target[i])));
            return maxError;
        }

        private void StepTcpServo()
        {
            if (_ctrl == null || _tcp == null)
            {
                Stop();
                return;
            }

            _servoT += Time.fixedDeltaTime;
            Vector3 toTarget = _servoTcpTarget - _tcp.position;
            float dist = toTarget.magnitude;
            if (dist <= tcpServoArriveToleranceMeters)
            {
                _servoIndex++;
                if (_servoIndex >= _servoTargets.Count)
                {
                    Debug.Log(
                        $"[RobotAI] TCP servo path complete " +
                        $"tcp_error_m={dist:0.000}");
                    Stop();
                    return;
                }

                _servoTcpTarget = _servoTargets[_servoIndex];
                _servoT = 0f;
                return;
            }

            if (_servoT >= tcpServoTimeoutSeconds)
            {
                Debug.LogWarning(
                    $"[RobotAI] TCP servo timed out at waypoint " +
                    $"{_servoIndex + 1}/{_servoTargets.Count} " +
                    $"tcp_error_m={dist:0.000}");
                Stop();
                return;
            }

            float step = Mathf.Min(
                tcpServoSpeedMetersPerSecond * Time.fixedDeltaTime,
                dist);
            var dq = CcdIkSolver.TcpJogDeltasDeg(
                _ctrl, _tcp, toTarget.normalized * step);
            for (int i = 0; i < 6 && i < dq.Length; i++)
                _ctrl.SetGoalDeg(i, _ctrl.GetGoalDeg(i) + dq[i]);
        }

        private void StepMobile()
        {
            if (_mobile == null || _route == null || _routeIndex >= _route.Length)
            {
                Stop();
                return;
            }

            Vector3 pos = _mobile.Body.position;
            Vector3 target = _route[_routeIndex];
            pos.y = target.y;
            Vector3 toTarget = target - pos;
            float dist = toTarget.magnitude;
            if (dist <= mobileArriveRadius)
            {
                _routeIndex++;
                if (_routeIndex >= _route.Length) Stop();
                return;
            }

            Vector3 fwd = _mobile.DriveForward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = _mobile.Body.forward;
            fwd.Normalize();

            Vector3 dir = toTarget.normalized;
            float signed = Vector3.SignedAngle(fwd, dir, Vector3.up);
            float abs = Mathf.Abs(signed);
            float turn = Mathf.Clamp(signed / 60f, -1f, 1f);
            float forward = abs > mobileTurnInPlaceDeg
                ? 0f
                : Mathf.Clamp01(1f - abs / mobileTurnInPlaceDeg);
            _mobile.SetDrive(forward, turn);
        }

        private void WriteDrives(float[] degs)
        {
            for (int i = 0; i < 6 && i < degs.Length; i++)
                WriteDrive(i, degs[i]);
        }

        private void WriteDrive(int i, float deg)
        {
            if (_ctrl == null || _ctrl.joints == null ||
                i < 0 || i >= _ctrl.joints.Length || _ctrl.joints[i] == null)
                return;

            var ab = _ctrl.joints[i];
            var drive = ab.xDrive;
            drive.target = Mathf.Clamp(deg, drive.lowerLimit, drive.upperLimit);
            ab.xDrive = drive;
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
