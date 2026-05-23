using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.Robot
{
    /// <summary>
    /// Physical skid-steer drive for an imported 4-wheel mobile base
    /// (e.g. AgileX Scout V2). The 4 continuous wheel joints become
    /// velocity-driven ArticulationBody revolute joints; spinning them against
    /// a high-friction ground pushes the (non-immovable, gravity-enabled) base
    /// around — i.e. genuine physical locomotion, not a transform teleport.
    ///
    /// Differential drive:
    ///   vL = v − ω·halfTrack     vR = v + ω·halfTrack
    ///   wheel ω = v_wheel / radius
    /// Left/right axis sign differs because the URDF flips the right wheels
    /// 180° about X; flip <see cref="leftWheelSign"/>/<see cref="rightWheelSign"/>
    /// if a robot drives the wrong way (same convention as the IK delta sign).
    /// </summary>
    [DisallowMultipleComponent]
    public class MobileBaseController : MonoBehaviour
    {
        [Header("Binding (auto-filled on Awake if empty)")]
        public string[] leftWheelNames =
            { "front_left_wheel_link", "rear_left_wheel_link" };
        public string[] rightWheelNames =
            { "front_right_wheel_link", "rear_right_wheel_link" };
        public ArticulationBody[] leftWheels;
        public ArticulationBody[] rightWheels;
        [Tooltip("Root articulation body (base_link). Must NOT be immovable.")]
        public ArticulationBody root;

        [Header("Geometry")]
        public float wheelRadius = 0.16459f;
        [Tooltip("Centre-to-wheel distance (half the track width).")]
        public float halfTrack = 0.29153f;

        [Header("Speed")]
        [Tooltip("Max forward speed at 100% (m/s).")]
        public float maxDriveSpeed = 1.5f;
        [Tooltip("Max in-place yaw rate at 100% (deg/s).")]
        public float maxTurnSpeed = 90f;
        [Range(1f, 100f)] public float speedPercent = 50f;

        [Header("Smoothing (anti-lurch)")]
        [Tooltip("Forward acceleration limit (m/s²). Ramps the command so the " +
                 "chassis doesn't lurch and lift its wheels on start/stop.")]
        public float maxLinearAccel = 1.6f;
        [Tooltip("Yaw acceleration limit (deg/s²).")]
        public float maxAngularAccel = 220f;
        [Tooltip("Turn direction sign. Flip if turning — manual AND waypoint — " +
                 "goes the wrong way (causes the route follower to spin in place).")]
        public float turnSign = 1f;

        [Header("Drive tuning")]
        [Tooltip("Velocity-loop damping on each wheel drive (acts as the motor).")]
        public float wheelDamping = 3.0e4f;
        [Tooltip("Max torque each wheel motor may apply (N*m).")]
        public float wheelForceLimit = 1.0e4f;
        [Tooltip("Sign applied to LEFT wheel targets. Flip if it drives wrong.")]
        public float leftWheelSign = 1f;
        [Tooltip("Sign applied to RIGHT wheel targets (right wheels are flipped " +
                 "180° in the URDF, so this defaults to -1).")]
        public float rightWheelSign = -1f;
        [Tooltip("Static/dynamic friction of the wheel-ground contact.")]
        public float groundFriction = 1.6f;
        [Tooltip("Per-body solver iterations for the base + wheels.")]
        public int solverIterations = 20;

        // --- runtime state -------------------------------------------------
        private float _cmdForward;   // -1..1
        private float _cmdTurn;      // -1..1 (+ = turn left / CCW seen from above)
        private float _appliedV;     // m/s, ramped toward the commanded speed
        private float _appliedW;     // rad/s, ramped toward the commanded yaw rate
        private bool _eStop;
        private bool _ready;
        private PhysicsMaterial _wheelMat;

        public bool EmergencyStopped => _eStop;

        /// <summary>
        /// Live transform of the physical base (the root ArticulationBody =
        /// base_link). Physics drives THIS, not the importer container the
        /// MobileBaseController sits on — read pose/heading from here.
        /// </summary>
        public Transform Body => root != null ? root.transform : transform;

        /// <summary>World planar velocity of the base (m/s), y stripped.</summary>
        public Vector3 PlanarVelocity
        {
            get
            {
                if (root == null) return Vector3.zero;
                var v = root.linearVelocity; v.y = 0f; return v;
            }
        }

        /// <summary>Current world planar speed of the base (m/s).</summary>
        public float MeasuredSpeed => PlanarVelocity.magnitude;

        // Auto-learned body-local "drive forward" axis. The URDF/import axis
        // convention is ambiguous (the base may drive along −Z, +X, …), so we
        // learn the true forward from the measured velocity the first time the
        // base actually moves. The waypoint follower steers by this.
        private Vector3 _fwdLocal = Vector3.forward;
        private bool _fwdLearned;

        /// <summary>True once the drive-forward axis has been learned.</summary>
        public bool ForwardLearned => _fwdLearned;

        /// <summary>
        /// World planar direction the base moves when commanded forward
        /// (auto-learned from motion; falls back to Body.forward until learned).
        /// </summary>
        public Vector3 DriveForward
        {
            get
            {
                var b = Body;
                Vector3 f = b.TransformDirection(_fwdLearned ? _fwdLocal
                                                             : Vector3.forward);
                f.y = 0f;
                return f.sqrMagnitude > 1e-6f ? f.normalized : b.forward;
            }
        }

        // ===================================================================
        private void Awake()
        {
            if (!HasWheels()) AutoBind();
            ConfigureBase();
            ConfigureWheels();
            _ready = HasWheels() && root != null;
            if (!_ready)
                Debug.LogError("[MobileBaseController] Could not resolve wheels/root. " +
                               "Check the wheel link names and that this sits on the " +
                               "robot root.", this);
        }

        private bool HasWheels() =>
            leftWheels != null && rightWheels != null &&
            leftWheels.Length > 0 && rightWheels.Length > 0 &&
            System.Array.TrueForAll(leftWheels, w => w != null) &&
            System.Array.TrueForAll(rightWheels, w => w != null);

        /// <summary>Resolve wheels by link name and the root by isRoot.</summary>
        public void AutoBind()
        {
            var byName = new Dictionary<string, ArticulationBody>();
            foreach (var ab in GetComponentsInChildren<ArticulationBody>(true))
            {
                if (ab.isRoot) root = ab;
                if (!byName.ContainsKey(ab.name)) byName[ab.name] = ab;
            }

            leftWheels = Resolve(leftWheelNames, byName);
            rightWheels = Resolve(rightWheelNames, byName);
        }

        private static ArticulationBody[] Resolve(
            string[] names, Dictionary<string, ArticulationBody> byName)
        {
            var list = new List<ArticulationBody>();
            foreach (var n in names)
                if (byName.TryGetValue(n, out var ab)) list.Add(ab);
            return list.ToArray();
        }

        private void ConfigureBase()
        {
            if (root == null) return;
            root.immovable = false;
            root.useGravity = true;
            root.solverIterations = Mathf.Max(root.solverIterations, solverIterations);
        }

        private void ConfigureWheels()
        {
            _wheelMat = new PhysicsMaterial("WheelGround")
            {
                dynamicFriction = groundFriction,
                staticFriction = groundFriction,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Maximum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };

            foreach (var w in AllWheels())
            {
                if (w == null) continue;
                w.useGravity = true;
                w.solverIterations = Mathf.Max(w.solverIterations, solverIterations);

                // Velocity-control mode: zero stiffness, high damping = a motor
                // that holds a target angular velocity.
                var d = w.xDrive;
                d.stiffness = 0f;
                d.damping = wheelDamping;
                d.forceLimit = wheelForceLimit;
                d.targetVelocity = 0f;
                w.xDrive = d;

                // High-friction contact so the wheels grip instead of slipping.
                foreach (var col in w.GetComponentsInChildren<Collider>(true))
                    col.sharedMaterial = _wheelMat;
            }
        }

        private IEnumerable<ArticulationBody> AllWheels()
        {
            if (leftWheels != null) foreach (var w in leftWheels) yield return w;
            if (rightWheels != null) foreach (var w in rightWheels) yield return w;
        }

        // ===================================================================
        //  Public API (pendant + waypoint follower)
        // ===================================================================

        /// <summary>Set normalised drive command. forward/turn in [-1, 1].</summary>
        public void SetDrive(float forward, float turn)
        {
            if (_eStop) { _cmdForward = _cmdTurn = 0f; return; }
            _cmdForward = Mathf.Clamp(forward, -1f, 1f);
            _cmdTurn = Mathf.Clamp(turn, -1f, 1f);
        }

        public void Stop() { _cmdForward = 0f; _cmdTurn = 0f; }

        public void SetSpeedPercent(float pct) =>
            speedPercent = Mathf.Clamp(pct, 1f, 100f);

        public void EngageEmergencyStop()
        {
            _eStop = true;
            _cmdForward = _cmdTurn = 0f;
            _appliedV = _appliedW = 0f;
            ApplyWheelTargets(0f, 0f);
        }

        public void ResetEmergencyStop() => _eStop = false;

        // ===================================================================
        private void FixedUpdate()
        {
            if (!_ready) return;

            float dt = Time.fixedDeltaTime;
            float scale = speedPercent / 100f;

            // Target body velocities (0 while e-stopped).
            float tgtV = _eStop ? 0f : _cmdForward * maxDriveSpeed * scale;
            float tgtW = _eStop ? 0f
                : _cmdTurn * maxTurnSpeed * scale * Mathf.Deg2Rad * turnSign;

            // Ramp toward them so the wheels don't slam to speed (which lurches
            // the chassis and lifts wheels).
            _appliedV = Mathf.MoveTowards(_appliedV, tgtV, maxLinearAccel * dt);
            _appliedW = Mathf.MoveTowards(_appliedW, tgtW,
                maxAngularAccel * Mathf.Deg2Rad * dt);

            float vL = _appliedV - _appliedW * halfTrack;   // m/s, left wheels
            float vR = _appliedV + _appliedW * halfTrack;   // m/s, right wheels

            // m/s → wheel angular velocity (deg/s)
            float wlDeg = (vL / Mathf.Max(0.001f, wheelRadius)) * Mathf.Rad2Deg;
            float wrDeg = (vR / Mathf.Max(0.001f, wheelRadius)) * Mathf.Rad2Deg;

            ApplyWheelTargets(wlDeg, wrDeg);
            LearnForward();
        }

        // Learn the true "drive forward" body axis from the measured velocity
        // whenever the base is being driven forward/back and actually moving.
        private void LearnForward()
        {
            if (root == null || Mathf.Abs(_appliedV) < 0.05f) return;
            Vector3 v = root.linearVelocity; v.y = 0f;
            if (v.sqrMagnitude < 0.0025f) return;            // need > 0.05 m/s
            Vector3 worldFwd = v.normalized * Mathf.Sign(_appliedV);
            Vector3 local = Body.InverseTransformDirection(worldFwd);
            local.y = 0f;
            if (local.sqrMagnitude < 1e-6f) return;
            local.Normalize();
            _fwdLocal = _fwdLearned ? Vector3.Slerp(_fwdLocal, local, 0.15f)
                                    : local;
            _fwdLearned = true;
        }

        private void ApplyWheelTargets(float leftDeg, float rightDeg)
        {
            if (leftWheels != null)
                foreach (var w in leftWheels)
                    SetWheelVelocity(w, leftDeg * leftWheelSign);
            if (rightWheels != null)
                foreach (var w in rightWheels)
                    SetWheelVelocity(w, rightDeg * rightWheelSign);
        }

        private static void SetWheelVelocity(ArticulationBody w, float degPerSec)
        {
            if (w == null) return;
            var d = w.xDrive;
            d.targetVelocity = degPerSec;
            w.xDrive = d;
        }
    }
}
