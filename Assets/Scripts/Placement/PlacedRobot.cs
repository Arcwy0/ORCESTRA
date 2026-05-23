using UnityEngine;

namespace VRInteraction.Placement
{
    /// <summary>
    /// Which interaction tool currently owns world clicks. Keeps the
    /// placement tool and the waypoint tool from reacting to the same click.
    /// </summary>
    public enum AppMode { Idle, Placement, Waypoints }

    public static class AppState
    {
        public static AppMode Mode = AppMode.Idle;
    }

    /// <summary>
    /// Distinguishes a fixed-base articulated arm (driven by joint angles +
    /// teach pendant + TCP waypoints) from a wheeled mobile platform (driven
    /// by linear/angular velocity + drive pendant + floor waypoints). The
    /// placement flow and the spawned pendant branch on this.
    /// </summary>
    public enum RobotKind { Manipulator, Mobile }

    /// <summary>
    /// Tag attached to every robot the operator places. Lets other systems
    /// (waypoints, scenarios, …) enumerate placed robots and know each one's
    /// display name + reach radius without depending on placement internals.
    /// </summary>
    public class PlacedRobot : MonoBehaviour
    {
        public RobotKind kind = RobotKind.Manipulator;
        public string displayName = "Robot";

        // Manipulator-only: spherical reach envelope.
        public float reachRadius = 0.5f;
        public Vector3 reachCenterLocal = new Vector3(0f, 0.152f, 0f);
    }
}
