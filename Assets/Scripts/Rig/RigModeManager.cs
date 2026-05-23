using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using VRInteraction.Placement;

namespace VRInteraction.Rig
{
    /// <summary>
    /// Single-component toggle between Desktop (mouse + fly-camera) and
    /// XR (Quest headset + controller ray) modes.
    ///
    /// Two independent cameras co-exist in the scene:
    ///   • <see cref="desktopCamera"/> — on the DesktopFlyCamera GO, always at
    ///     scene root.  Active and tagged "MainCamera" in Desktop mode.
    ///   • <see cref="xrCamera"/>     — under XR Origin / Camera Offset, driven
    ///     by TrackedPoseDriver.  Active and tagged "MainCamera" in XR mode.
    ///
    /// Camera.main always returns the correct camera because the "MainCamera"
    /// tag is swapped between them in <see cref="Apply"/>.
    ///
    /// Press <b>F12</b> at runtime to flip modes without leaving Play mode.
    /// In the Editor use <b>UR3 → Mode → Desktop (PC)</b> / <b>XR (Quest)</b>.
    /// </summary>
    public class RigModeManager : MonoBehaviour
    {
        public enum RigMode { Desktop, XR }

        [Header("Starting mode")]
        public RigMode startMode = RigMode.Desktop;

        [Header("Desktop rig")]
        [Tooltip("Camera component that lives on the DesktopFlyCamera GO.")]
        public Camera desktopCamera;
        [Tooltip("DesktopMousePointer component (on the placement-controller GO).")]
        public DesktopMousePointer desktopPointer;

        [Header("XR rig")]
        [Tooltip("XR Origin root GameObject (created by UR3 → Setup XR Rig).")]
        public GameObject xrOriginGO;
        [Tooltip("Dedicated XR Camera inside Camera Offset (NOT the desktop camera).")]
        public Camera xrCamera;
        [Tooltip("XrControllerPointer component (on the placement-controller GO).")]
        public XrControllerPointer xrPointer;

        [Header("Systems to notify")]
        [Tooltip("Placement controller whose pointer reference will be swapped.")]
        public RobotPlacementController placementController;

        // -----------------------------------------------------------------------
        private RigMode _current;

        private void Awake() => Apply(startMode, forceRefresh: true);

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null && Keyboard.current.f12Key.wasPressedThisFrame)
                Toggle();
#else
            if (Input.GetKeyDown(KeyCode.F12))
                Toggle();
#endif
        }

        // -----------------------------------------------------------------------
        /// <summary>Toggle between Desktop and XR at runtime (F12).</summary>
        public void Toggle() =>
            Apply(_current == RigMode.Desktop ? RigMode.XR : RigMode.Desktop);

        /// <summary>Switch to a specific mode programmatically.</summary>
        public void Apply(RigMode mode, bool forceRefresh = false)
        {
            if (!forceRefresh && mode == _current) return;
            _current = mode;
            bool isDesktop = mode == RigMode.Desktop;

            // ---- Desktop camera ------------------------------------------------
            // Self-heal: if the reference was lost (e.g. after a re-run of
            // Setup XR Rig), find the camera that has DesktopFlyCamera on it.
            if (desktopCamera == null)
            {
                var dfc = FindAnyObjectByType<DesktopFlyCamera>(
                    FindObjectsInactive.Include);
                if (dfc != null) desktopCamera = dfc.GetComponent<Camera>();
                if (desktopCamera == null)
                    desktopCamera = FindAnyObjectByType<Camera>(
                        FindObjectsInactive.Include);
                if (desktopCamera != null)
                    Debug.LogWarning("[RigMode] desktopCamera was null — auto-recovered: "
                                     + desktopCamera.name);
            }

            if (desktopCamera != null)
            {
                desktopCamera.gameObject.SetActive(isDesktop);
                // Camera.main uses the "MainCamera" tag — swap it so any code
                // that calls Camera.main gets the correct, active camera.
                desktopCamera.tag = isDesktop ? "MainCamera" : "Untagged";
            }
            else if (isDesktop)
            {
                Debug.LogError("[RigMode] No desktop camera found! " +
                               "Run UR3 → Setup XR Rig again to rebuild the rig.");
            }

            // ---- XR rig --------------------------------------------------------
            // XR Origin (and the XR Camera inside it) lives separately.
            if (xrOriginGO != null) xrOriginGO.SetActive(!isDesktop);
            if (xrCamera   != null) xrCamera.tag = isDesktop ? "Untagged" : "MainCamera";

            // ---- Pointer -------------------------------------------------------
            PlacementPointer active = isDesktop
                ? (PlacementPointer)desktopPointer
                : (PlacementPointer)xrPointer;

            if (placementController != null && active != null)
                placementController.SetPointer(active);

            Debug.Log($"[RigMode] → {mode}   Camera.main = {Camera.main?.name ?? "null"}");
        }

        // -----------------------------------------------------------------------
        // Inspector helper: bakes the default mode into the scene (Editor only).
        public void BakeMode(RigMode mode)
        {
            startMode = mode;
            Apply(mode, forceRefresh: true);
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
#endif
        }
    }
}
