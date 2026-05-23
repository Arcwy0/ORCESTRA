using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.Robot
{
    /// <summary>
    /// Cyclic-Coordinate-Descent inverse kinematics for any
    /// <see cref="UR3JointController"/>-driven articulation chain.
    ///
    /// Strategy: mutate the live joint transforms (Transform.RotateAround)
    /// during the CCD sweep, then restore the physics state via the root
    /// ArticulationBody's joint-position snapshot. The whole solve runs
    /// synchronously inside one Unity tick, so no FixedUpdate fires between
    /// iterations and the scratch transforms hold long enough to converge.
    /// Physics never sees the intermediate poses.
    ///
    /// Joint axes are read live from each body's anchor frame, so the
    /// solver is not UR-specific.
    /// </summary>
    public static class CcdIkSolver
    {
        /// <summary>
        /// Resolved-rate TCP jog step (damped least squares). Computes joint
        /// angle increments (deg) that move the TCP by <paramref name="move"/>
        /// (world metres). READS the live joint frames only — no transform
        /// mutation and no <c>SetJointPositions</c> — so it is safe to call
        /// every frame while physics runs (unlike the batch CCD which is a
        /// one-shot offline solve).
        ///
        /// dq = Jᵀ (J Jᵀ + λ²I)⁻¹ · move, with J the 3×6 linear-velocity
        /// Jacobian (column j = axisⱼ × (tcp − pivotⱼ)). The damping λ keeps
        /// it stable through singularities.
        /// </summary>
        public static float[] TcpJogDeltasDeg(
            UR3JointController ctrl, Transform tcp, Vector3 move,
            float damping = 0.08f)
        {
            var dq = new float[6];
            if (ctrl == null || ctrl.joints == null || tcp == null) return dq;

            Vector3 tcpPos = tcp.position;
            var col = new Vector3[6];
            for (int j = 0; j < 6; j++)
            {
                var ab = ctrl.joints[j];
                if (ab == null) { col[j] = Vector3.zero; continue; }
                Vector3 pivot = ab.transform.TransformPoint(ab.anchorPosition);

                // anchorRotation encodes the URDF joint axis as the local
                // X-axis of the anchor frame.  Some URDF importer versions
                // leave anchorRotation as identity and instead bake the axis
                // into the body's transform orientation.  We try the anchor
                // first; if the result is too close to zero after the cross
                // product we try the parent-anchor rotation as a fallback.
                Vector3 axis = (ab.transform.rotation *
                                ab.anchorRotation *
                                Vector3.right).normalized;
                if (axis.sqrMagnitude < 0.5f)       // degenerate — shouldn't happen
                    axis = ab.transform.right;

                col[j] = Vector3.Cross(axis, tcpPos - pivot);
            }

            // Quick sanity: if every column is near-zero the Jacobian is
            // degenerate (wrong TCP or fully-singular pose); log once.
            float jacNorm = 0f;
            for (int j = 0; j < 6; j++) jacNorm += col[j].sqrMagnitude;
            if (jacNorm < 1e-6f)
                Debug.LogWarning("[CcdIkSolver] Jacobian near-zero — check TCP" +
                                 " transform and joint anchorRotations.");

            // A = J Jᵀ + λ²I  (3×3 symmetric)
            float lam2 = damping * damping;
            float a00 = lam2, a01 = 0f, a02 = 0f;
            float a11 = lam2, a12 = 0f, a22 = lam2;
            for (int j = 0; j < 6; j++)
            {
                var c = col[j];
                a00 += c.x * c.x; a01 += c.x * c.y; a02 += c.x * c.z;
                a11 += c.y * c.y; a12 += c.y * c.z; a22 += c.z * c.z;
            }

            if (!SolveSym3(a00, a01, a02, a11, a12, a22, move, out Vector3 x))
                return dq;

            for (int j = 0; j < 6; j++)
                dq[j] = Vector3.Dot(col[j], x) * Mathf.Rad2Deg;
            return dq;
        }

        // Solves the symmetric 3×3 system A x = b. Returns false if singular.
        private static bool SolveSym3(
            float a00, float a01, float a02,
            float a11, float a12, float a22,
            Vector3 b, out Vector3 x)
        {
            float c00 = a11 * a22 - a12 * a12;
            float c01 = a02 * a12 - a01 * a22;
            float c02 = a01 * a12 - a02 * a11;
            float det = a00 * c00 + a01 * c01 + a02 * c02;
            if (Mathf.Abs(det) < 1e-12f) { x = Vector3.zero; return false; }

            float c11 = a00 * a22 - a02 * a02;
            float c12 = a02 * a01 - a00 * a12;
            float c22 = a00 * a11 - a01 * a01;
            float inv = 1f / det;

            x = new Vector3(
                (c00 * b.x + c01 * b.y + c02 * b.z) * inv,
                (c01 * b.x + c11 * b.y + c12 * b.z) * inv,
                (c02 * b.x + c12 * b.y + c22 * b.z) * inv);
            return true;
        }

        /// <summary>
        /// Batch solve: walks a sequential list of TCP targets (e.g. the
        /// samples of a Catmull–Rom spline) and returns one joint
        /// configuration per target. Each solve starts from the *previous*
        /// solved pose, so the trajectory is continuous in joint space.
        /// State is restored at the end.
        /// </summary>
        public static List<float[]> SolveBatch(
            UR3JointController ctrl, Transform tcp, IList<Vector3> targets,
            int iterations = 40, float tol = 0.002f)
        {
            var path = new List<float[]>();
            if (ctrl == null || ctrl.joints == null || tcp == null) return path;
            var root = FindRoot(ctrl);
            if (root == null) return path;

            var savedJointPos = new List<float>();
            root.GetJointPositions(savedJointPos);

            var savedDriveTarget = new float[6];
            var savedLocalRot = new Quaternion[6];
            var savedLocalPos = new Vector3[6];
            for (int j = 0; j < 6; j++)
            {
                var ab = ctrl.joints[j];
                if (ab == null) continue;
                savedDriveTarget[j] = ab.xDrive.target;
                savedLocalRot[j] = ab.transform.localRotation;
                savedLocalPos[j] = ab.transform.localPosition;
            }

            var cur = new float[6];
            for (int j = 0; j < 6; j++) cur[j] = ctrl.GetMeasuredDeg(j);

            foreach (var t in targets)
            {
                IterateCCD(ctrl, tcp, t, iterations, tol, cur);
                path.Add((float[])cur.Clone());
            }

            for (int j = 0; j < 6; j++)
            {
                var ab = ctrl.joints[j];
                if (ab == null) continue;
                ab.transform.localPosition = savedLocalPos[j];
                ab.transform.localRotation = savedLocalRot[j];
            }
            root.SetJointPositions(savedJointPos);
            for (int j = 0; j < 6; j++)
            {
                var ab = ctrl.joints[j];
                if (ab == null) continue;
                var d = ab.xDrive;
                d.target = savedDriveTarget[j];
                ab.xDrive = d;
            }

            return path;
        }

        /// <summary>
        /// Best-effort lookup of the URDF tool frame on a robot subtree.
        /// </summary>
        public static Transform FindTcp(Transform root)
        {
            string[] names = { "tool0", "tcp", "flange", "wrist_3_link" };
            foreach (var n in names)
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (string.Equals(t.name, n,
                            System.StringComparison.OrdinalIgnoreCase))
                        return t;
            Debug.LogWarning("[CcdIkSolver] No TCP frame found (tried tool0/tcp/" +
                             "flange/wrist_3_link). Falling back to robot root — " +
                             "Jacobian will be wrong. Check the URDF hierarchy.", root);
            return root;
        }

        // -- internals -----------------------------------------------------
        private static ArticulationBody FindRoot(UR3JointController ctrl)
        {
            foreach (var ab in ctrl.GetComponentsInChildren<ArticulationBody>(
                         true))
                if (ab.isRoot) return ab;
            return null;
        }

        // Mutates 'cur' AND the joint transforms; does NOT restore.
        private static void IterateCCD(
            UR3JointController ctrl, Transform tcp, Vector3 target,
            int iterations, float tol, float[] cur)
        {
            for (int iter = 0; iter < iterations; iter++)
            {
                float distBefore = Vector3.Distance(tcp.position, target);
                if (distBefore < tol) return;

                for (int j = 5; j >= 0; j--)
                {
                    var ab = ctrl.joints[j];
                    if (ab == null) continue;

                    Vector3 pivot =
                        ab.transform.TransformPoint(ab.anchorPosition);
                    Vector3 axis = (ab.transform.rotation *
                                    ab.anchorRotation *
                                    Vector3.right).normalized;

                    Vector3 a = Vector3.ProjectOnPlane(
                        tcp.position - pivot, axis);
                    Vector3 b = Vector3.ProjectOnPlane(
                        target - pivot, axis);
                    if (a.sqrMagnitude < 1e-8f ||
                        b.sqrMagnitude < 1e-8f) continue;

                    float deltaDeg = Vector3.SignedAngle(a, b, axis);
                    float lo = ctrl.GetLowerLimitDeg(j);
                    float hi = ctrl.GetUpperLimitDeg(j);
                    float wanted = Mathf.Clamp(cur[j] + deltaDeg, lo, hi);
                    deltaDeg = wanted - cur[j];
                    cur[j] = wanted;
                    if (Mathf.Abs(deltaDeg) > 0.0001f)
                        ab.transform.RotateAround(pivot, axis, deltaDeg);
                }

                float distAfter = Vector3.Distance(tcp.position, target);
                if (distBefore - distAfter < 1e-5f) return;
            }
        }
    }
}
