using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.Robot
{
    /// <summary>
    /// Drives the 6 revolute ArticulationBody joints of an imported UR3 robot.
    ///
    /// The robot is expected to come from the Unity URDF Importer fed with the
    /// ur3.urdf in this project. The importer creates one GameObject per link;
    /// the revolute joints live on the link GameObjects listed in
    /// <see cref="jointLinkNames"/>. Auto-binding matches by those names so no
    /// manual wiring is required after import.
    ///
    /// Joint targets are commanded in DEGREES (same convention as the real UR
    /// teach pendant). Each commanded target ramps toward its goal at a bounded
    /// speed so jogging feels like the physical pendant rather than teleporting.
    /// </summary>
    [DisallowMultipleComponent]
    public class UR3JointController : MonoBehaviour
    {
        public static readonly string[] DefaultJointLinkNames =
        {
            "shoulder_link",   // shoulder_pan_joint
            "upper_arm_link",  // shoulder_lift_joint
            "forearm_link",    // elbow_joint
            "wrist_1_link",    // wrist_1_joint
            "wrist_2_link",    // wrist_2_joint
            "wrist_3_link"     // wrist_3_joint
        };

        public static readonly string[] JointDisplayNames =
        {
            "Base",  "Shoulder", "Elbow", "Wrist 1", "Wrist 2", "Wrist 3"
        };

        [Header("Binding")]
        [Tooltip("Link GameObject names that carry the 6 revolute ArticulationBodies, in order.")]
        public string[] jointLinkNames = (string[])DefaultJointLinkNames.Clone();

        [Tooltip("Resolved revolute joints (auto-filled on Awake if left empty).")]
        public ArticulationBody[] joints = new ArticulationBody[6];

        [Header("Articulation drive")]
        [Tooltip("Position-loop stiffness of every joint drive. High value " +
                 "keeps the arm rigid, like a real industrial manipulator.")]
        public float driveStiffness = 500000f;
        [Tooltip("Position-loop damping. Heavily overdamped on purpose so " +
                 "the chain does not oscillate or bounce.")]
        public float driveDamping = 50000f;
        [Tooltip("Maximum torque each drive may apply (N*m).")]
        public float driveForceLimit = 3000f;
        [Tooltip("Per-body articulation solver iterations. Higher = more " +
                 "rigid, less spongy chain. 30 is a good industrial value.")]
        public int solverIterations = 30;
        [Tooltip("Per-body articulation velocity solver iterations.")]
        public int solverVelocityIterations = 6;

        [Header("Playback")]
        [Tooltip("When true, FixedUpdate's ramp loop is skipped — the " +
                 "scenario player (WaypointController) writes joint targets " +
                 "directly, so the arm follows a pre-solved trajectory " +
                 "exactly instead of lagging behind a smoothed goal.")]
        public bool externalControl = false;

        [Tooltip("Disable gravity on the links so the arm holds any pose rock-solid. " +
                 "Turn off later for full dynamics.")]
        public bool disableGravity = true;

        [Header("Motion")]
        [Tooltip("Base jog speed in deg/s at 100% speed.")]
        public float maxJogSpeedDeg = 60f;

        [Range(1f, 100f)]
        [Tooltip("Global speed scale in percent (teach-pendant style).")]
        public float speedPercent = 25f;

        // --- runtime state -------------------------------------------------
        private readonly float[] _goalDeg = new float[6];     // final desired angle
        private readonly float[] _cmdDeg = new float[6];      // ramped command sent to drive
        private readonly float[] _lowerDeg = new float[6];
        private readonly float[] _upperDeg = new float[6];
        private readonly int[] _jogDir = new int[6];          // -1/0/+1 active jog
        private bool _eStop;
        private bool _ready;

        public int JointCount => 6;
        public bool EmergencyStopped => _eStop;

        private void Awake()
        {
            if (!HasAllJoints())
                AutoBind();

            InitializeDrives();
            _ready = HasAllJoints();
            if (!_ready)
                Debug.LogError("[UR3JointController] Could not resolve all 6 joints. " +
                               "Make sure the UR3 URDF was imported and this component " +
                               "sits on (or above) the robot root.", this);
        }

        private bool HasAllJoints()
        {
            if (joints == null || joints.Length != 6) return false;
            for (int i = 0; i < 6; i++)
                if (joints[i] == null) return false;
            return true;
        }

        /// <summary>
        /// Find the 6 revolute ArticulationBodies. Tries to match by link name
        /// first; falls back to "the 6 revolute joints in hierarchy order"
        /// (which equals the kinematic chain order for a serial arm).
        /// </summary>
        public void AutoBind()
        {
            joints = new ArticulationBody[6];

            // Revolute bodies in depth-first hierarchy order.
            var revolute = new List<ArticulationBody>();
            foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
                if (ab.jointType == ArticulationJointType.RevoluteJoint)
                    revolute.Add(ab);

            // 1) name match
            var byName = new Dictionary<string, ArticulationBody>();
            foreach (var ab in revolute)
                if (!byName.ContainsKey(ab.name))
                    byName.Add(ab.name, ab);

            int matched = 0;
            for (int i = 0; i < jointLinkNames.Length && i < 6; i++)
                if (byName.TryGetValue(jointLinkNames[i], out var ab))
                {
                    joints[i] = ab;
                    matched++;
                }

            // 2) fallback: exactly 6 revolute joints -> take them in order
            if (matched < 6 && revolute.Count == 6)
            {
                for (int i = 0; i < 6; i++)
                    joints[i] = revolute[i];
                Debug.Log("[UR3JointController] Bound joints by hierarchy order " +
                          "(name match was incomplete).", this);
            }
        }

        private void InitializeDrives()
        {
            for (int i = 0; i < 6; i++)
            {
                var ab = joints[i];
                if (ab == null) continue;

                if (disableGravity) ab.useGravity = false;

                // Crank the per-body articulation iterations so the chain
                // simulates as a rigid kinematic structure, not a stack of
                // springs.
                ab.solverIterations = Mathf.Max(ab.solverIterations,
                    solverIterations);
                ab.solverVelocityIterations = Mathf.Max(
                    ab.solverVelocityIterations, solverVelocityIterations);

                var drive = ab.xDrive;

                // Importer sets lower/upper from URDF; keep them, just record.
                _lowerDeg[i] = drive.lowerLimit;
                _upperDeg[i] = drive.upperLimit;
                if (Mathf.Approximately(_lowerDeg[i], _upperDeg[i]))
                {
                    _lowerDeg[i] = -360f;
                    _upperDeg[i] = 360f;
                }

                drive.stiffness = driveStiffness;
                drive.damping = driveDamping;
                drive.forceLimit = driveForceLimit;

                float current = CurrentMeasuredDeg(i);
                drive.target = current;
                ab.xDrive = drive;

                _goalDeg[i] = current;
                _cmdDeg[i] = current;
            }
        }

        private float CurrentMeasuredDeg(int i)
        {
            var ab = joints[i];
            if (ab == null) return 0f;
            // Revolute jointPosition is in radians.
            return ab.jointPosition.dofCount > 0
                ? Mathf.Rad2Deg * ab.jointPosition[0]
                : 0f;
        }

        // ------------------------------------------------------------------
        //  Public API (used by the HMI panel)
        // ------------------------------------------------------------------

        public float GetMeasuredDeg(int i) =>
            (i >= 0 && i < 6) ? CurrentMeasuredDeg(i) : 0f;

        public float GetGoalDeg(int i) =>
            (i >= 0 && i < 6) ? _goalDeg[i] : 0f;

        public float GetLowerLimitDeg(int i) => (i >= 0 && i < 6) ? _lowerDeg[i] : -360f;
        public float GetUpperLimitDeg(int i) => (i >= 0 && i < 6) ? _upperDeg[i] : 360f;

        public void SetGoalDeg(int i, float deg)
        {
            if (_eStop || i < 0 || i >= 6) return;
            _goalDeg[i] = Mathf.Clamp(deg, _lowerDeg[i], _upperDeg[i]);
        }

        /// <summary>Press-and-hold jog. sign = +1 or -1; call StopJog on release.</summary>
        public void StartJog(int i, int sign)
        {
            if (_eStop || i < 0 || i >= 6) return;
            _jogDir[i] = (int)Mathf.Sign(sign);
        }

        public void StopJog(int i)
        {
            if (i < 0 || i >= 6) return;
            _jogDir[i] = 0;
            _goalDeg[i] = _cmdDeg[i]; // settle where it currently is
        }

        public void GoHome()
        {
            if (_eStop) return;
            for (int i = 0; i < 6; i++)
            {
                _jogDir[i] = 0;
                _goalDeg[i] = Mathf.Clamp(0f, _lowerDeg[i], _upperDeg[i]);
            }
        }

        public void SetSpeedPercent(float pct) =>
            speedPercent = Mathf.Clamp(pct, 1f, 100f);

        public void EngageEmergencyStop()
        {
            _eStop = true;
            for (int i = 0; i < 6; i++)
            {
                _jogDir[i] = 0;
                float here = CurrentMeasuredDeg(i);
                _cmdDeg[i] = here;
                _goalDeg[i] = here;
                if (joints[i] != null)
                {
                    var d = joints[i].xDrive;
                    d.target = here;
                    joints[i].xDrive = d;
                }
            }
        }

        public void ResetEmergencyStop()
        {
            _eStop = false;
            for (int i = 0; i < 6; i++)
            {
                float here = CurrentMeasuredDeg(i);
                _cmdDeg[i] = here;
                _goalDeg[i] = here;
            }
        }

        /// <summary>
        /// Re-syncs the internal command + goal arrays to whatever pose the
        /// joints are actually in. Used right after external trajectory
        /// playback so the controller does not try to drag the joints back
        /// to where it last set them.
        /// </summary>
        public void SnapStateToMeasured()
        {
            for (int i = 0; i < 6; i++)
            {
                float here = CurrentMeasuredDeg(i);
                _cmdDeg[i] = here;
                _goalDeg[i] = here;
                _jogDir[i] = 0;
            }
        }

        // ------------------------------------------------------------------
        private void FixedUpdate()
        {
            if (!_ready || _eStop) return;
            // Scenario player owns the drives — do not fight it with the ramp.
            if (externalControl) return;

            float step = maxJogSpeedDeg * (speedPercent / 100f) * Time.fixedDeltaTime;

            for (int i = 0; i < 6; i++)
            {
                var ab = joints[i];
                if (ab == null) continue;

                if (_jogDir[i] != 0)
                {
                    _goalDeg[i] = Mathf.Clamp(
                        _cmdDeg[i] + _jogDir[i] * step * 4f,
                        _lowerDeg[i], _upperDeg[i]);
                }

                _cmdDeg[i] = Mathf.MoveTowards(_cmdDeg[i], _goalDeg[i], step);

                var drive = ab.xDrive;
                drive.target = _cmdDeg[i];
                ab.xDrive = drive;
            }
        }
    }
}
