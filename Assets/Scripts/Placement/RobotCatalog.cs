using System.Collections.Generic;
using UnityEngine;

namespace VRInteraction.Placement
{
    /// <summary>
    /// Catalog of placeable digital-twin robots. Currently one entry (UR3);
    /// add more entries (other manipulators, mobile platforms, humanoids)
    /// without touching the placement code.
    /// </summary>
    [CreateAssetMenu(fileName = "RobotCatalog",
        menuName = "VR Interaction/Robot Catalog")]
    public class RobotCatalog : ScriptableObject
    {
        [System.Serializable]
        public class Entry
        {
            [Tooltip("Shown in the catalog menu.")]
            public string displayName = "UR3";

            [Tooltip("Manipulator = fixed arm (joint pendant + TCP waypoints). " +
                     "Mobile = wheeled platform (drive pendant + floor waypoints).")]
            public RobotKind kind = RobotKind.Manipulator;

            [Tooltip("Physics prefab instantiated when the operator confirms " +
                     "placement (ArticulationBody chain + matching controller).")]
            public GameObject prefab;

            [Tooltip("Where the pendant panel appears, relative to the robot " +
                     "base (robot local space).")]
            public Vector3 hmiLocalOffset = new Vector3(0.55f, 1.15f, 0.0f);

            [Tooltip("Pendant panel rotation (euler), robot local space.")]
            public Vector3 hmiLocalEuler = new Vector3(0f, -90f, 0f);

            [Tooltip("Vertical offset (m) added when spawning so the robot " +
                     "rests on the floor. 0 for arms whose base sits on the " +
                     "ground; for a mobile base it is wheel radius + axle drop " +
                     "(Scout V2 ≈ 0.235) so the wheels — not the chassis — " +
                     "touch the floor.")]
            public float spawnHeightOffset = 0f;

            // ---- Manipulator-only: spherical reach envelope ----------------
            [Tooltip("[Manipulator] Max reach (m). UR3 = 0.5, UR5 ≈ 0.85, " +
                     "UR10 ≈ 1.30, KUKA KR600 R2830 ≈ 2.83.")]
            public float reachRadius = 0.5f;

            [Tooltip("[Manipulator] Centre of the spherical reach envelope in " +
                     "the robot LOCAL frame (≈ shoulder height). UR3 ≈ 0.152, " +
                     "KUKA KR600 ≈ 1.045.")]
            public Vector3 reachCenterLocal = new Vector3(0f, 0.152f, 0f);

            // ---- Mobile-only: skid-steer drive parameters ------------------
            [Tooltip("[Mobile] Max forward speed (m/s). Scout V2 ≈ 1.5.")]
            public float driveSpeed = 1.5f;

            [Tooltip("[Mobile] Max yaw rate (deg/s) when turning in place. " +
                     "Scout V2 ≈ 90.")]
            public float turnSpeed = 90f;

            [Tooltip("[Mobile] Drive wheel radius (m). Scout V2 = 0.16459.")]
            public float wheelRadius = 0.16459f;

            [Tooltip("[Mobile] Half the track width — centre to a wheel (m). " +
                     "Scout V2 ≈ 0.29153.")]
            public float halfTrack = 0.29153f;

            [Tooltip("[Mobile] Link names of the LEFT-side drive wheels.")]
            public string[] leftWheelNames =
                { "front_left_wheel_link", "rear_left_wheel_link" };

            [Tooltip("[Mobile] Link names of the RIGHT-side drive wheels.")]
            public string[] rightWheelNames =
                { "front_right_wheel_link", "rear_right_wheel_link" };
        }

        public List<Entry> entries = new List<Entry>();

        public Entry Get(int index) =>
            (index >= 0 && index < entries.Count) ? entries[index] : null;
    }
}
