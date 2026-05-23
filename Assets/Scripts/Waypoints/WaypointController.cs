using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using VRInteraction.Placement;
using VRInteraction.Robot;
using VRInteraction.UI;

namespace VRInteraction.Waypoints
{
    /// <summary>
    /// Operator flow for teaching a TCP path:
    ///   Idle → "WAYPOINTS" → pick which placed robot (it glows on hover) →
    ///   confirm → its reach sphere appears → drop points (rejected + toast
    ///   if outside reach) → UNDO / DONE → spline is built through them →
    ///   EDIT (add/fix) or RUN (the arm plays the scenario along the spline).
    ///
    /// The pointer is the same abstraction the placement tool uses, so an XR
    /// controller ray drops in later without touching this logic.
    /// </summary>
    public class WaypointController : MonoBehaviour
    {
        [Tooltip("Work-plane height nudge in metres.")]
        public float heightStep = 0.05f;
        [Tooltip("How densely the spline is sampled (points per span).")]
        public int splineResolution = 24;
        [Tooltip("CCD iterations per sample during the offline pre-solve.")]
        public int ikIterations = 40;
        [Tooltip("Stop CCD on a sample once TCP is within this distance (m).")]
        public float ikReachTol = 0.002f;
        [Tooltip("Trajectory playback rate (samples per second).")]
        public float playbackRate = 30f;

        [Header("Mobile follow (skid-steer)")]
        [Tooltip("How close (m) the base must get before advancing to the next " +
                 "floor waypoint.")]
        public float arriveRadius = 0.25f;
        [Tooltip("If the heading error to the next point exceeds this (deg) the " +
                 "base turns in place before driving forward.")]
        public float turnInPlaceDeg = 35f;
        [Tooltip("Flip if the base follows the path backwards (heading sign " +
                 "depends on the imported forward axis + wheel-sign tuning).")]
        public bool mobileHeadingInvert = false;

        private enum State { Idle, PickRobot, Placing, Review, Restoring, Simulating }
        private State _state = State.Idle;

        private PlacementPointer _pointer;

        // selected robot
        private PlacedRobot _robot;
        private RobotKind _kind = RobotKind.Manipulator;
        private UR3JointController _ctrl;
        private MobileBaseController _mobile;   // set when _kind == Mobile
        private Transform _tcp;
        private Vector3 _center;
        private float _reach;
        private float _placeHeight;
        private int _wpIndex;                   // current target during mobile follow
        private float _calibT;                  // drive-forward calibration timer

        // record state (captured when a robot is chosen, used when saving)
        private Vector3 _recBasePos;
        private Quaternion _recBaseRot = Quaternion.identity;
        private float[] _recStartJoints;        // manipulator start pose, or null

        // restore-before-play state (used when loading an episode)
        private float[] _restoreJoints;
        private float _restoreTimeout;

        // visuals
        private GameObject _highlight;     // body glow while picking
        private GameObject _sphere;        // reach volume
        private GameObject _held;          // marker on the pointer
        private Renderer _heldR;
        private readonly List<Vector3> _points = new List<Vector3>();
        private readonly List<GameObject> _markers = new List<GameObject>();
        private LineRenderer _spline;
        private readonly List<Vector3> _sampled = new List<Vector3>();

        // sim — pre-solved joint trajectory played back through external
        // control. Online IK servoing oscillated; this is rigid and stops
        // exactly at the last sample.
        private readonly List<float[]> _path = new List<float[]>();
        private float _simT;

        // UI
        private GameObject _pickPanel, _placePanel, _reviewPanel, _simPanel;
        private GameObject _episodePanel;
        private Transform _pickList, _episodeList;
        private Text _hint, _toast, _placeInfo, _heightInfo;
        private Coroutine _toastCo;

        private void Start()
        {
            _pointer = FindAnyObjectByType<PlacementPointer>();
            UiKit.EnsureEventSystem();
            BuildUi();
            SetState(State.Idle);
        }

        private void Update()
        {
            // The placement controller creates the desktop pointer in its
            // own Start(); component start order is undefined, so fetch lazily.
            if (_pointer == null)
            {
                _pointer = FindAnyObjectByType<PlacementPointer>();
                if (_pointer == null) return;
            }

            if (_state == State.Placing)
            {
                bool valid = ComputeHeldPoint(out var p);
                if (_held != null)
                {
                    _held.SetActive(valid);
                    if (valid)
                    {
                        _held.transform.position = p;
                        SetColor(_heldR, InReach(p)
                            ? new Color(0.30f, 1f, 0.45f)
                            : new Color(1f, 0.32f, 0.26f));
                    }
                }

                if (valid && _pointer.ConfirmPressedThisFrame())
                    TryAddPoint(p);
                else if (_pointer.CancelPressedThisFrame())
                    CancelTask();
            }
            else if (_state == State.Simulating || _state == State.Restoring)
            {
                if (_pointer.CancelPressedThisFrame()) StopSim();
            }
        }

        private void FixedUpdate()
        {
            if (_state == State.Restoring) { RestoreStep(); return; }
            if (_state != State.Simulating) return;
            if (_kind == RobotKind.Mobile) MobileFollowStep();
            else PlaybackStep();
        }

        // ----------------------------------------------------------- entry
        private void OnWaypointsButton()
        {
            if (AppState.Mode == AppMode.Placement)
            {
                ShowToast("Finish the placement task first.");
                return;
            }
            if (_state != State.Idle) { CancelTask(); return; }
            SetState(State.PickRobot);
            PopulateRobotList();
        }

        private void PopulateRobotList()
        {
            foreach (Transform c in _pickList) Destroy(c.gameObject);

            var robots = FindObjectsByType<PlacedRobot>(FindObjectsSortMode.None);
            float y = 0f;
            if (robots.Length == 0)
            {
                var t = UiKit.Text("None", _pickList, "No robots placed yet.",
                    22, TextAnchor.MiddleCenter);
                UiKit.TopRow(UiKit.Rt(t), 8, 56);
            }
            foreach (var r in robots)
            {
                var rr = r;
                var b = UiKit.Button(rr.name, _pickList, rr.displayName, 22,
                    new Color(0.20f, 0.22f, 0.26f, 1f), () => ConfirmRobot(rr));
                UiKit.TopRow(UiKit.Rt(b), y, 60);
                AddHover(b.gameObject,
                    () => Highlight(rr.gameObject),
                    () => { if (_state == State.PickRobot) ClearHighlight(); });
                y += 72f;
            }
        }

        private void ConfirmRobot(PlacedRobot r)
        {
            _robot = r;
            _kind = r.kind;
            ClearHighlight();

            if (_kind == RobotKind.Mobile)
            {
                // Mobile platform: no reach sphere (it roams), points are
                // dropped on the floor, and a go-to-goal follower drives it.
                _mobile = r.GetComponentInChildren<MobileBaseController>();
                _ctrl = null;
                _tcp = null;
                _placeHeight = GroundY();
                DestroySphere();
            }
            else
            {
                // Manipulator: reach is spherical around the SHOULDER (or
                // whatever the entry says), not the base-on-floor — otherwise
                // half the sphere is buried under the ground plane.
                _mobile = null;
                _ctrl = r.GetComponentInChildren<UR3JointController>();
                _tcp = FindTcp(r.transform);
                _center = r.transform.TransformPoint(r.reachCenterLocal);
                _reach = Mathf.Max(0.1f, r.reachRadius);
                _placeHeight = _center.y;
                BuildSphere();
            }

            // Capture the record-time state: the base frame the points are
            // stored relative to, plus the manipulator's start joint pose.
            var rootT = RobotRoot();
            _recBasePos = rootT.position;
            _recBaseRot = rootT.rotation;
            if (_kind == RobotKind.Manipulator && _ctrl != null)
            {
                _recStartJoints = new float[6];
                for (int i = 0; i < 6; i++)
                    _recStartJoints[i] = _ctrl.GetMeasuredDeg(i);
            }
            else _recStartJoints = null;

            BuildHeld();
            ClearPath();
            SetState(State.Placing);
            RefreshPlaceInfo();
        }

        // The frame waypoints are anchored to: the live base for a mobile
        // platform, the (immovable) container for a manipulator.
        private Transform RobotRoot() =>
            (_kind == RobotKind.Mobile && _mobile != null)
                ? _mobile.Body : _robot.transform;

        private static float GroundY()
        {
            var g = GameObject.Find("Ground");
            return g != null ? g.transform.position.y : 0f;
        }

        // ------------------------------------------------------- placing
        private bool ComputeHeldPoint(out Vector3 p)
        {
            // PC stand-in: ray ∩ horizontal work-plane at the chosen height.
            // (An XR build will instead read the controller tip via the
            //  pointer abstraction — the rest of the flow is unchanged.)
            var ray = _pointer.GetRay();
            var plane = new Plane(Vector3.up, new Vector3(0, _placeHeight, 0));
            if (plane.Raycast(ray, out float enter) && enter > 0f)
            {
                p = ray.GetPoint(enter);
                return true;
            }
            p = Vector3.zero;
            return false;
        }

        private bool InReach(Vector3 p) =>
            _kind == RobotKind.Mobile || Vector3.Distance(p, _center) <= _reach;

        private void TryAddPoint(Vector3 p)
        {
            if (!InReach(p))
            {
                ShowToast("Out of reach — move closer to the robot.");
                return;
            }
            _points.Add(p);
            var m = MakeSphere(0.05f, new Color(1f, 0.62f, 0.12f));
            m.transform.position = p;
            _markers.Add(m);
            RefreshPlaceInfo();
        }

        private void UndoPoint()
        {
            if (_points.Count == 0) return;
            _points.RemoveAt(_points.Count - 1);
            var m = _markers[_markers.Count - 1];
            _markers.RemoveAt(_markers.Count - 1);
            if (m != null) Destroy(m);
            RefreshPlaceInfo();
        }

        private void FinishPoints()
        {
            // Mobile route prepends the base position, so 1 dropped point is a
            // valid 2-node path. A manipulator needs ≥2 taught points.
            int minPts = _kind == RobotKind.Mobile ? 1 : 2;
            if (_points.Count < minPts)
            {
                ShowToast($"Place at least {minPts} point" +
                          (minPts > 1 ? "s." : "."));
                return;
            }
            BuildSpline();
            if (_held != null) _held.SetActive(false);
            SetState(State.Review);
        }

        private void EditPoints()
        {
            if (_spline != null) _spline.enabled = false;
            if (_held != null) _held.SetActive(true);
            SetState(State.Placing);
            RefreshPlaceInfo();
        }

        // -------------------------------------------------------- spline
        private void BuildSpline()
        {
            _sampled.Clear();

            // The trajectory starts at the robot's current pose and goes
            // through every dropped point — operator never teaches the start.
            // Manipulator: start = TCP.  Mobile: start = base on the floor.
            var pts = new List<Vector3>(_points.Count + 1);
            if (_kind == RobotKind.Mobile)
            {
                // Start from the LIVE base (base_link), not the static container.
                if (_mobile != null)
                {
                    var bp = _mobile.Body.position;
                    bp.y = _placeHeight;
                    pts.Add(bp);
                }
            }
            else if (_tcp != null) pts.Add(_tcp.position);
            pts.AddRange(_points);

            int n = pts.Count;
            for (int s = 0; s < n - 1; s++)
            {
                Vector3 p0 = pts[Mathf.Max(0, s - 1)];
                Vector3 p1 = pts[s];
                Vector3 p2 = pts[s + 1];
                Vector3 p3 = pts[Mathf.Min(n - 1, s + 2)];
                int steps = Mathf.Max(2, splineResolution);
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps;
                    _sampled.Add(CatmullRom(p0, p1, p2, p3, t));
                }
            }
            _sampled.Add(pts[n - 1]);

            if (_spline == null)
            {
                var go = new GameObject("WaypointSpline");
                go.transform.SetParent(transform, false);
                _spline = go.AddComponent<LineRenderer>();
                _spline.widthMultiplier = 0.012f;
                _spline.useWorldSpace = true;
                var sh = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Sprites/Default");
                _spline.material = new Material(sh);
                _spline.startColor = new Color(0.25f, 0.85f, 1f, 1f);
                _spline.endColor = new Color(0.25f, 0.85f, 1f, 1f);
                _spline.numCornerVertices = 4;
            }
            _spline.enabled = true;
            _spline.positionCount = _sampled.Count;
            _spline.SetPositions(_sampled.ToArray());
        }

        private static Vector3 CatmullRom(
            Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * (
                2f * p1 +
                (-p0 + p2) * t +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        // ----------------------------------------------------- simulation
        private void RunScenario()
        {
            // ---- Mobile: real-time go-to-goal follower ----------------------
            if (_kind == RobotKind.Mobile)
            {
                if (_mobile == null)
                {
                    ShowToast("This robot has no drive controller.");
                    return;
                }
                if (_points.Count < 1) { ShowToast("Nothing to drive to."); return; }
                _wpIndex = 0;
                _calibT = 0.6f;            // brief straight pulse to learn forward
                _mobile.ResetEmergencyStop();
                SetState(State.Simulating);
                return;
            }

            // ---- Manipulator: offline IK pre-solve + rigid playback ---------
            if (_ctrl == null || _tcp == null)
            {
                ShowToast("This robot has no joint controller.");
                return;
            }
            // Rebuild the spline now so its start matches the arm's CURRENT pose
            // (after a load+restore the TCP only just returned to the recorded
            // state — a spline built earlier would start from the wrong place).
            BuildSpline();
            if (_sampled.Count < 2) { ShowToast("Nothing to play."); return; }

            // Pre-solve the full joint-space trajectory IN ONE FRAME so the
            // arm can play it back rigidly. Online IK fights itself across
            // physics steps and oscillates; an offline solve gives one
            // committed joint configuration per spline sample and the
            // playback just chases it.
            _path.Clear();
            _path.AddRange(CcdIkSolver.SolveBatch(
                _ctrl, _tcp, _sampled, ikIterations, ikReachTol));
            if (_path.Count < 2) { ShowToast("IK failed."); return; }

            // External control: the joint controller stops running its own
            // ramp loop and we write drive targets directly.
            _ctrl.externalControl = true;
            _simT = 0f;
            SetState(State.Simulating);
        }

        private void StopSim()
        {
            if (_kind == RobotKind.Mobile)
            {
                if (_mobile != null) _mobile.Stop();
                SetState(State.Review);
                return;
            }

            if (_ctrl != null)
            {
                _ctrl.externalControl = false;
                _ctrl.SnapStateToMeasured();   // do NOT drag the arm anywhere
            }
            SetState(State.Review);
        }

        // Real-time skid-steer follower: turn toward the next floor waypoint,
        // drive forward once roughly aligned, advance when within arriveRadius,
        // stop after the last point. Pure go-to-goal — no pre-solve needed since
        // the platform corrects its heading continuously as physics carries it.
        private void MobileFollowStep()
        {
            if (_mobile == null || _points.Count == 0) return;

            // IMPORTANT: physics drives base_link (the root ArticulationBody),
            // NOT the importer container the PlacedRobot sits on. Read the live
            // pose from the body, or the heading is frozen and the base just
            // spins in place forever.
            Transform body = _mobile.Body;

            // Calibration: a short straight pulse at the start so the controller
            // learns its true drive-forward axis before steering — otherwise the
            // first leg can shoot off the wrong way.
            if (!_mobile.ForwardLearned && _calibT > 0f)
            {
                _calibT -= Time.fixedDeltaTime;
                _mobile.SetDrive(1f, 0f);
                return;
            }

            if (_wpIndex >= _points.Count)
            {
                _mobile.Stop();
                ShowToast("Route complete.");
                StopSim();
                return;
            }

            Vector3 target = _points[_wpIndex];
            Vector3 to = target - body.position;
            to.y = 0f;
            float dist = to.magnitude;

            if (dist < arriveRadius)
            {
                _wpIndex++;
                if (_wpIndex >= _points.Count)
                {
                    _mobile.Stop();
                    ShowToast("Route complete.");
                    StopSim();
                }
                return;
            }

            // Use the auto-learned drive-forward direction (the import axis is
            // ambiguous; the controller learns the real one from motion). This
            // is why the base no longer drives off in the wrong direction.
            Vector3 fwd = _mobile.DriveForward;
            fwd.y = 0f;
            if (mobileHeadingInvert) fwd = -fwd;
            if (fwd.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f) return;
            fwd.Normalize();
            to.Normalize();

            // + heading error = target is to the LEFT (CCW about world up),
            // which MobileBaseController turns toward with a +turn command.
            float headErr = Vector3.SignedAngle(fwd, to, Vector3.up);
            float turn = Mathf.Clamp(headErr / 45f, -1f, 1f);
            float forward = Mathf.Abs(headErr) > turnInPlaceDeg ? 0f : 1f;

            _mobile.SetDrive(forward, turn);
        }

        // ===================================================================
        //  Episodes — save / load / restore-then-play
        // ===================================================================
        private void SaveCurrentEpisode()
        {
            if (_robot == null || _points.Count == 0)
            {
                ShowToast("Nothing to save.");
                return;
            }

            var ep = new WaypointEpisode
            {
                name = $"{_robot.displayName} {System.DateTime.Now:MMdd-HHmmss}",
                robotName = _robot.displayName,
                kind = (int)_kind,
                basePos = _recBasePos,
                baseRot = _recBaseRot,
                jointDegs = _recStartJoints,
                playbackRate = playbackRate,
                savedUtc = System.DateTime.UtcNow.ToString("o"),
                localPoints = new Vector3[_points.Count]
            };
            for (int i = 0; i < _points.Count; i++)
                ep.localPoints[i] =
                    Quaternion.Inverse(_recBaseRot) * (_points[i] - _recBasePos);

            EpisodeStore.Save(ep);
            ShowToast($"Saved \"{ep.name}\".");
        }

        private void OpenEpisodes()
        {
            if (AppState.Mode == AppMode.Placement)
            {
                ShowToast("Finish the placement task first.");
                return;
            }
            if (_state != State.Idle) CancelTask();
            PopulateEpisodeList();
            if (_pickPanel != null) _pickPanel.SetActive(false);
            if (_placePanel != null) _placePanel.SetActive(false);
            if (_reviewPanel != null) _reviewPanel.SetActive(false);
            if (_simPanel != null) _simPanel.SetActive(false);
            if (_episodePanel != null) _episodePanel.SetActive(true);
            AppState.Mode = AppMode.Waypoints;
            SetHint("Pick a saved episode to load & replay.");
        }

        private void PopulateEpisodeList()
        {
            foreach (Transform c in _episodeList) Destroy(c.gameObject);

            var eps = EpisodeStore.LoadAll();
            if (eps.Count == 0)
            {
                var t = UiKit.Text("None", _episodeList, "No saved episodes.",
                    22, TextAnchor.MiddleCenter);
                UiKit.TopRow(UiKit.Rt(t), 8, 56);
                return;
            }

            float y = 0f;
            foreach (var ep in eps)
            {
                var e = ep;
                string label = $"{ep.name}   ({(RobotKind)ep.kind})";
                var b = UiKit.Button(ep.name, _episodeList, label, 20,
                    new Color(0.20f, 0.22f, 0.26f, 1f), () => LoadEpisode(e));
                UiKit.TopRow(UiKit.Rt(b), y, 56);
                y += 66f;
            }
        }

        private void LoadEpisode(WaypointEpisode ep)
        {
            var kind = (RobotKind)ep.kind;
            var robot = FindRobotForEpisode(ep, kind);
            if (robot == null)
            {
                ShowToast($"Place a {kind} robot first.");
                return;
            }

            // Bind robot.
            _robot = robot;
            _kind = kind;
            if (_kind == RobotKind.Mobile)
            {
                _mobile = robot.GetComponentInChildren<MobileBaseController>();
                _ctrl = null; _tcp = null;
                DestroySphere();
                // Return the base to the recorded pose BEFORE rebuilding points
                // so the relative points reattach exactly where they were taught.
                TeleportBase(ep.basePos, ep.baseRot);
            }
            else
            {
                _mobile = null;
                _ctrl = robot.GetComponentInChildren<UR3JointController>();
                _tcp = FindTcp(robot.transform);
                _center = robot.transform.TransformPoint(robot.reachCenterLocal);
                _reach = Mathf.Max(0.1f, robot.reachRadius);
                BuildSphere();
            }
            _placeHeight = GroundY();
            playbackRate = ep.playbackRate > 0f ? ep.playbackRate : playbackRate;

            // Rebuild world points from the stored relative points. Mobile: use
            // the recorded base frame directly (we just teleported the base back
            // there). Manipulator: use the CURRENT container frame so a relocated
            // arm carries its path along.
            ClearPath();
            Vector3 anchorPos; Quaternion anchorRot;
            if (_kind == RobotKind.Mobile)
            {
                anchorPos = ep.basePos; anchorRot = ep.baseRot;
            }
            else
            {
                var rt = RobotRoot();
                anchorPos = rt.position; anchorRot = rt.rotation;
            }
            foreach (var lp in ep.localPoints)
            {
                Vector3 wp = anchorPos + anchorRot * lp;
                _points.Add(wp);
                var m = MakeSphere(0.05f, new Color(1f, 0.62f, 0.12f));
                m.transform.position = wp;
                _markers.Add(m);
            }

            _restoreJoints = ep.jointDegs;
            BeginRestoreAndPlay();
        }

        private static PlacedRobot FindRobotForEpisode(
            WaypointEpisode ep, RobotKind kind)
        {
            PlacedRobot firstOfKind = null;
            foreach (var r in FindObjectsByType<PlacedRobot>(
                         FindObjectsSortMode.None))
            {
                if (r.kind != kind) continue;
                if (r.displayName == ep.robotName) return r;   // exact instance
                if (firstOfKind == null) firstOfKind = r;
            }
            return firstOfKind;                                // any of right kind
        }

        // Restore the recorded state, then start playback.
        private void BeginRestoreAndPlay()
        {
            BuildSpline();
            SetState(State.Restoring);

            if (_kind == RobotKind.Mobile)
            {
                _restoreTimeout = 0.4f;        // brief settle after the teleport
            }
            else if (_ctrl != null && _restoreJoints != null &&
                     _restoreJoints.Length == 6)
            {
                _ctrl.externalControl = false;
                _ctrl.ResetEmergencyStop();
                for (int i = 0; i < 6; i++)
                    _ctrl.SetGoalDeg(i, _restoreJoints[i]);
                _restoreTimeout = 8f;          // homing timeout safety
            }
            else
            {
                _restoreTimeout = 0f;          // nothing to restore → play now
            }
        }

        private void RestoreStep()
        {
            _restoreTimeout -= Time.fixedDeltaTime;
            bool done;

            if (_kind == RobotKind.Mobile)
            {
                done = _restoreTimeout <= 0f;
            }
            else if (_ctrl != null && _restoreJoints != null &&
                     _restoreJoints.Length == 6)
            {
                done = true;
                for (int i = 0; i < 6; i++)
                    if (Mathf.Abs(Mathf.DeltaAngle(
                            _ctrl.GetMeasuredDeg(i), _restoreJoints[i])) > 1.5f)
                    { done = false; break; }
                if (_restoreTimeout <= 0f) done = true;   // give up waiting
            }
            else done = true;

            if (done) RunScenario();
        }

        private void TeleportBase(Vector3 pos, Quaternion rot)
        {
            if (_mobile == null || _mobile.root == null) return;
            _mobile.Stop();
            _mobile.root.TeleportRoot(pos, rot);
        }

        // Steady-rate playback: at time t we are at trajectory index
        // floor(t * rate); linearly blend joint angles between adjacent
        // pre-solved configurations and write them straight to the drives.
        private void PlaybackStep()
        {
            if (_ctrl == null || _ctrl.joints == null || _path.Count == 0)
                return;

            _simT += Time.fixedDeltaTime;
            float fIndex = _simT * playbackRate;
            int i = Mathf.FloorToInt(fIndex);

            // Reached (or past) the last sample — pin to the final pose and
            // stop. This is what makes the arm actually rest at the last
            // taught point instead of drifting.
            if (i >= _path.Count - 1)
            {
                WriteDrivesDirect(_path[_path.Count - 1]);
                ShowToast("Scenario complete.");
                StopSim();
                return;
            }

            float frac = fIndex - i;
            var a = _path[i];
            var b = _path[i + 1];
            for (int j = 0; j < 6; j++)
            {
                float deg = Mathf.LerpAngle(a[j], b[j], frac);
                WriteDrive(j, deg);
            }
        }

        private void WriteDrivesDirect(float[] degs)
        {
            for (int j = 0; j < 6; j++) WriteDrive(j, degs[j]);
        }

        private void WriteDrive(int j, float deg)
        {
            var ab = _ctrl.joints[j];
            if (ab == null) return;
            var d = ab.xDrive;
            d.target = Mathf.Clamp(deg, d.lowerLimit, d.upperLimit);
            ab.xDrive = d;
        }

        // (CCD solver lives in VRInteraction.Robot.CcdIkSolver — used here
        //  and by the HMI panel's TCP-jog mode.)

        private static Transform FindTcp(Transform root) =>
            CcdIkSolver.FindTcp(root);

        // ------------------------------------------------------- highlight
        private void Highlight(GameObject robot)
        {
            ClearHighlight();
            _highlight = GhostBuilder.CreateGhost(
                robot, new Color(0.40f, 0.90f, 1f, 0.55f));
            _highlight.transform.SetPositionAndRotation(
                robot.transform.position, robot.transform.rotation);
            _highlight.transform.localScale = robot.transform.localScale * 1.01f;
        }

        private void ClearHighlight()
        {
            if (_highlight != null) Destroy(_highlight);
            _highlight = null;
        }

        // --------------------------------------------------------- visuals
        private void BuildSphere()
        {
            DestroySphere();
            _sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere.name = "ReachVolume";
            var col = _sphere.GetComponent<Collider>();
            if (col != null) Destroy(col);
            _sphere.transform.position = _center;
            _sphere.transform.localScale = Vector3.one * (_reach * 2f);
            var mr = _sphere.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GhostBuilder.TransparentMaterial(
                new Color(0.30f, 0.75f, 1f, 0.07f));
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        private void DestroySphere()
        {
            if (_sphere != null) Destroy(_sphere);
            _sphere = null;
        }

        private void BuildHeld()
        {
            if (_held != null) Destroy(_held);
            _held = MakeSphere(0.045f, new Color(0.30f, 1f, 0.45f));
            _held.name = "HeldMarker";
            _heldR = _held.GetComponent<Renderer>();
            _held.SetActive(false);
        }

        private static GameObject MakeSphere(float d, Color c)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var col = s.GetComponent<Collider>();
            if (col != null) Destroy(col);
            s.transform.localScale = Vector3.one * d;
            var mr = s.GetComponent<MeshRenderer>();
            var sh = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Standard");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            mr.sharedMaterial = m;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            return s;
        }

        private static void SetColor(Renderer r, Color c)
        {
            if (r == null) return;
            var m = r.sharedMaterial;
            if (m == null) return;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        private void ClearPath()
        {
            foreach (var m in _markers) if (m != null) Destroy(m);
            _markers.Clear();
            _points.Clear();
            _sampled.Clear();
            if (_spline != null) _spline.enabled = false;
        }

        // ---------------------------------------------------------- flow
        private void CancelTask()
        {
            if (_ctrl != null && _ctrl.externalControl)
            {
                _ctrl.externalControl = false;
                _ctrl.SnapStateToMeasured();
            }
            if (_mobile != null) _mobile.Stop();
            _path.Clear();
            ClearHighlight();
            DestroySphere();
            if (_held != null) { Destroy(_held); _held = null; }
            ClearPath();
            _robot = null;
            _ctrl = null;
            _mobile = null;
            _tcp = null;
            _kind = RobotKind.Manipulator;
            _restoreJoints = null;
            SetState(State.Idle);
        }

        private void Nudge(int axis, float delta)
        {
            // axis 1 == work-plane height (only nudgeable in Placing).
            // Mobile points live on the floor — height is fixed there.
            if (_state != State.Placing || _kind == RobotKind.Mobile) return;
            _placeHeight += delta;
            RefreshPlaceInfo();
        }

        private void SetState(State s)
        {
            _state = s;
            if (_pickPanel != null) _pickPanel.SetActive(s == State.PickRobot);
            if (_placePanel != null) _placePanel.SetActive(s == State.Placing);
            if (_reviewPanel != null) _reviewPanel.SetActive(s == State.Review);
            if (_simPanel != null)
                _simPanel.SetActive(s == State.Simulating || s == State.Restoring);
            if (_episodePanel != null) _episodePanel.SetActive(false);

            if (s == State.Idle)
            {
                if (AppState.Mode == AppMode.Waypoints)
                    AppState.Mode = AppMode.Idle;
            }
            else AppState.Mode = AppMode.Waypoints;

            switch (s)
            {
                case State.Idle:
                    SetHint("Place robots first, then teach a path — or load " +
                            "a saved episode.");
                    break;
                case State.PickRobot:
                    SetHint("Hover a robot to highlight it, click to choose.");
                    break;
                case State.Placing:
                    SetHint(_kind == RobotKind.Mobile
                        ? "Click on the floor to drop a route point. " +
                          "Esc = cancel."
                        : "Click inside the sphere to drop a point. " +
                          "− / + change height. Esc = cancel.");
                    break;
                case State.Review:
                    SetHint("Spline built. SAVE it, EDIT it, or RUN it.");
                    break;
                case State.Restoring:
                    SetHint("Returning the robot to the recorded state…");
                    break;
                case State.Simulating:
                    SetHint("Playing the scenario… Esc / STOP to halt.");
                    break;
            }
        }

        private void SetHint(string s) { if (_hint != null) _hint.text = s; }

        private void RefreshPlaceInfo()
        {
            if (_placeInfo != null)
                _placeInfo.text = _robot != null
                    ? $"{_robot.displayName}   ·   Points: {_points.Count}"
                    : $"Points: {_points.Count}";
            if (_heightInfo != null)
                _heightInfo.text = $"Height  {_placeHeight,6:0.000} m";
        }

        private void ShowToast(string msg)
        {
            if (_toast == null) return;
            _toast.text = msg;
            _toast.gameObject.SetActive(true);
            if (_toastCo != null) StopCoroutine(_toastCo);
            _toastCo = StartCoroutine(HideToast());
        }

        private IEnumerator HideToast()
        {
            yield return new WaitForSeconds(1f);
            if (_toast != null) _toast.gameObject.SetActive(false);
        }

        private static void AddHover(GameObject go,
            System.Action enter, System.Action exit)
        {
            var et = go.AddComponent<EventTrigger>();
            var e1 = new EventTrigger.Entry
                { eventID = EventTriggerType.PointerEnter };
            e1.callback.AddListener(_ => enter());
            et.triggers.Add(e1);
            var e2 = new EventTrigger.Entry
                { eventID = EventTriggerType.PointerExit };
            e2.callback.AddListener(_ => exit());
            et.triggers.Add(e2);
        }

        // ------------------------------------------------------------- UI
        private void BuildUi()
        {
            var bar = UiKit.WorldCanvas("Waypoint_Toolbar", transform,
                new Vector3(0f, 0.60f, 1.4f), new Vector3(0, 180, 0),
                new Vector2(900, 140), 0.0016f);
            UiKit.Panel(bar.transform, new Color(0.10f, 0.13f, 0.12f, 0.92f));
            var open = UiKit.Button("Waypoints", bar.transform,
                "◎  WAYPOINTS", 24,
                new Color(0.18f, 0.46f, 0.40f, 1f), OnWaypointsButton);
            UiKit.Box(UiKit.Rt(open), 16, 16, 300, 56);
            var load = UiKit.Button("Episodes", bar.transform,
                "▤  EPISODES", 24,
                new Color(0.20f, 0.38f, 0.50f, 1f), OpenEpisodes);
            UiKit.Box(UiKit.Rt(load), 324, 16, 260, 56);

            _hint = UiKit.Text("Hint", bar.transform, "", 19,
                TextAnchor.MiddleLeft);
            UiKit.Box(UiKit.Rt(_hint), 16, 80, 868, 44);

            _toast = UiKit.Text("Toast", bar.transform, "", 24,
                TextAnchor.MiddleCenter);
            _toast.color = new Color(1f, 0.55f, 0.40f);
            UiKit.Box(UiKit.Rt(_toast), 596, 16, 288, 56);
            _toast.gameObject.SetActive(false);

            BuildPickPanel();
            BuildPlacePanel();
            BuildReviewPanel();
            BuildEpisodePanel();
            BuildSimPanel();
        }

        private Canvas SidePanel(string name, out GameObject root,
            Vector2 size, string title)
        {
            var c = UiKit.WorldCanvas(name, transform,
                new Vector3(0.0f, 1.32f, 1.45f), new Vector3(0, 180, 0),
                size, 0.0016f);
            root = c.gameObject;
            UiKit.Panel(c.transform, new Color(0.10f, 0.12f, 0.13f, 0.96f));
            var header = UiKit.Image("Header", c.transform,
                new Color(0.18f, 0.46f, 0.40f, 1f));
            UiKit.TopRow(UiKit.Rt(header), 0, 56, 0);
            var t = UiKit.Text("Title", header.transform, title, 22,
                TextAnchor.MiddleLeft);
            UiKit.Stretch(t.rectTransform, 16, 0, 0, 0);
            return c;
        }

        private void BuildPickPanel()
        {
            var c = SidePanel("Waypoint_Pick", out _pickPanel,
                new Vector2(560, 520), "  CHOOSE A ROBOT");
            var listGo = UiKit.Image("List", c.transform,
                new Color(0, 0, 0, 0));
            UiKit.Box(UiKit.Rt(listGo), 16, 70, 528, 360);
            _pickList = listGo.transform;
            var cancel = UiKit.Button("Cancel", c.transform, "CANCEL", 22,
                new Color(0.55f, 0.18f, 0.18f, 1f), CancelTask);
            UiKit.Box(UiKit.Rt(cancel), 16, 446, 528, 56);
        }

        private void BuildPlacePanel()
        {
            var c = SidePanel("Waypoint_Place", out _placePanel,
                new Vector2(620, 460), "  TEACH TCP POINTS");
            _placeInfo = UiKit.Text("Info", c.transform, "Points: 0", 24,
                TextAnchor.MiddleLeft);
            UiKit.Box(UiKit.Rt(_placeInfo), 24, 76, 560, 44);

            _heightInfo = UiKit.Text("H", c.transform, "Height  0.000 m", 22,
                TextAnchor.MiddleCenter);
            UiKit.Box(UiKit.Rt(_heightInfo), 200, 132, 220, 44);
            var c1 = new Color(0.30f, 0.34f, 0.38f, 1f);
            var hMinus = UiKit.Button("H-", c.transform, "−", 26, c1,
                () => Nudge(1, -heightStep));
            UiKit.Box(UiKit.Rt(hMinus), 24, 128, 150, 52);
            var hPlus = UiKit.Button("H+", c.transform, "+", 26, c1,
                () => Nudge(1, heightStep));
            UiKit.Box(UiKit.Rt(hPlus), 446, 128, 150, 52);

            var undo = UiKit.Button("Undo", c.transform, "UNDO LAST", 22,
                new Color(0.42f, 0.36f, 0.20f, 1f), UndoPoint);
            UiKit.Box(UiKit.Rt(undo), 24, 210, 572, 56);
            var done = UiKit.Button("Done", c.transform, "DONE — BUILD SPLINE",
                22, new Color(0.20f, 0.45f, 0.28f, 1f), FinishPoints);
            UiKit.Box(UiKit.Rt(done), 24, 278, 572, 62);
            var cancel = UiKit.Button("Cancel", c.transform, "CANCEL", 22,
                new Color(0.55f, 0.18f, 0.18f, 1f), CancelTask);
            UiKit.Box(UiKit.Rt(cancel), 24, 352, 572, 52);
        }

        private void BuildReviewPanel()
        {
            var c = SidePanel("Waypoint_Review", out _reviewPanel,
                new Vector2(620, 470), "  REVIEW PATH");
            var edit = UiKit.Button("Edit", c.transform, "EDIT POINTS", 22,
                new Color(0.22f, 0.40f, 0.52f, 1f), EditPoints);
            UiKit.Box(UiKit.Rt(edit), 24, 80, 572, 56);
            var run = UiKit.Button("Run", c.transform, "RUN SIMULATION", 22,
                new Color(0.20f, 0.45f, 0.28f, 1f), RunScenario);
            UiKit.Box(UiKit.Rt(run), 24, 146, 572, 60);
            var save = UiKit.Button("Save", c.transform, "SAVE EPISODE", 22,
                new Color(0.24f, 0.42f, 0.50f, 1f), SaveCurrentEpisode);
            UiKit.Box(UiKit.Rt(save), 24, 216, 572, 56);
            var cancel = UiKit.Button("Cancel", c.transform, "CANCEL", 22,
                new Color(0.55f, 0.18f, 0.18f, 1f), CancelTask);
            UiKit.Box(UiKit.Rt(cancel), 24, 282, 572, 52);
        }

        private void BuildEpisodePanel()
        {
            var c = SidePanel("Waypoint_Episodes", out _episodePanel,
                new Vector2(640, 560), "  LOAD EPISODE");
            var listGo = UiKit.Image("List", c.transform, new Color(0, 0, 0, 0));
            UiKit.Box(UiKit.Rt(listGo), 16, 70, 608, 408);
            _episodeList = listGo.transform;
            var cancel = UiKit.Button("Cancel", c.transform, "CANCEL", 22,
                new Color(0.55f, 0.18f, 0.18f, 1f), CancelTask);
            UiKit.Box(UiKit.Rt(cancel), 16, 488, 608, 56);
        }

        private void BuildSimPanel()
        {
            var c = SidePanel("Waypoint_Sim", out _simPanel,
                new Vector2(620, 300), "  PLAYING SCENARIO");
            var stop = UiKit.Button("Stop", c.transform, "STOP", 24,
                new Color(0.55f, 0.18f, 0.18f, 1f), StopSim);
            UiKit.Box(UiKit.Rt(stop), 24, 80, 572, 66);
        }
    }
}
