using System.Collections.Generic;
using UnityEngine;
using VRInteraction.Placement;
using VRInteraction.Robot;

namespace VRInteraction.AI
{
    public class AiMotionExecutor : MonoBehaviour
    {
        public float manipulatorPlaybackRate = 30f;
        public float mobileArriveRadius = 0.25f;
        public float mobileTurnInPlaceDeg = 35f;

        private enum Mode { Idle, Manipulator, Mobile }
        private Mode _mode = Mode.Idle;

        private UR3JointController _ctrl;
        private Transform _tcp;
        private readonly List<float[]> _jointPath = new List<float[]>();
        private float _simT;

        private MobileBaseController _mobile;
        private Vector3[] _route;
        private int _routeIndex;

        public bool IsRunning => _mode != Mode.Idle;

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

            var targets = new List<Vector3>();
            foreach (var wp in plan.waypoints)
                targets.Add(AiModelUtil.ToVector3(wp.position_m));

            _jointPath.Clear();
            var start = new float[6];
            for (int i = 0; i < 6; i++) start[i] = _ctrl.GetMeasuredDeg(i);
            _jointPath.Add(start);
            _jointPath.AddRange(CcdIkSolver.SolveBatch(
                _ctrl, _tcp, targets, 40, 0.004f));
            if (_jointPath.Count < 2)
            {
                error = "IK solve produced no trajectory.";
                return false;
            }

            _ctrl.externalControl = true;
            _simT = 0f;
            _mode = Mode.Manipulator;
            return true;
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
            else if (_mode == Mode.Mobile) StepMobile();
        }

        private void StepManipulator()
        {
            if (_ctrl == null || _jointPath.Count == 0)
            {
                Stop();
                return;
            }

            if (_jointPath.Count == 1)
            {
                WriteDrives(_jointPath[0]);
                Stop();
                return;
            }

            _simT += Time.fixedDeltaTime;
            float fIndex = _simT * manipulatorPlaybackRate;
            int i = Mathf.FloorToInt(fIndex);
            if (i >= _jointPath.Count - 1)
            {
                WriteDrives(_jointPath[_jointPath.Count - 1]);
                Stop();
                return;
            }

            float frac = fIndex - i;
            var a = _jointPath[i];
            var b = _jointPath[i + 1];
            for (int j = 0; j < 6; j++)
                WriteDrive(j, Mathf.LerpAngle(a[j], b[j], frac));
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
