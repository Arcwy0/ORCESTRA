using System.Collections.Generic;
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

        [Header("Mixed Reality (passthrough)")]
        [Tooltip("Start XR in passthrough (MR) instead of full VR.")]
        public bool startPassthrough = false;
        [Tooltip("Virtual environment hidden in passthrough so the real room " +
                 "shows through (e.g. Ground). Auto-finds 'Ground' if left empty.")]
        public GameObject[] passthroughHide;
        [Tooltip("Floor-relative tracking so the virtual floor (Y=0) lines up " +
                 "with the real floor. Recommended for MR.")]
        public bool floorTracking = true;
        [Tooltip("AR Foundation ARCameraManager (drives Quest passthrough). " +
                 "Wired by UR3 → Setup MR. Runs only in MR.")]
        public Behaviour arCameraManager;
        [Tooltip("AR Foundation ARPlaneManager (real-surface detection). " +
                 "Wired by UR3 → Setup MR. Runs only in MR.")]
        public Behaviour arPlaneManager;

        [Header("Systems to notify")]
        [Tooltip("Placement controller whose pointer reference will be swapped.")]
        public RobotPlacementController placementController;

        // -----------------------------------------------------------------------
        private RigMode _current;
        private bool _passthrough;
        private bool _prevLeftToggle;

        private void Awake()
        {
            _passthrough = startPassthrough;
            RequestScenePermissions();
            EnsurePassthroughHideList();
            ConfigureFloorTracking();
            Apply(startMode, forceRefresh: true);
        }

        private void Update()
        {
            bool toggleMode = false;   // F12 → Desktop / XR
            bool togglePass = false;   // F11 → VR / MR
#if ENABLE_INPUT_SYSTEM
            if (Keyboard.current != null)
            {
                if (Keyboard.current.f12Key.wasPressedThisFrame) toggleMode = true;
                if (Keyboard.current.f11Key.wasPressedThisFrame) togglePass = true;
            }
#else
            if (Input.GetKeyDown(KeyCode.F12)) toggleMode = true;
            if (Input.GetKeyDown(KeyCode.F11)) togglePass = true;
#endif
            // Left-controller Y button also flips VR/MR (usable in-headset).
            togglePass |= LeftTogglePressedThisFrame();

            if (toggleMode) Toggle();
            if (togglePass && _current == RigMode.XR) SetPassthrough(!_passthrough);
        }

        // Edge-detected left-hand secondary (Y) button via the legacy XR input
        // subsystem — no XRI dependency, mirrors XrControllerPointer's approach.
        private bool LeftTogglePressedThisFrame()
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(
                UnityEngine.XR.XRNode.LeftHand);
            bool cur = false;
            dev.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out cur);
            bool pressed = cur && !_prevLeftToggle;
            _prevLeftToggle = cur;
            return pressed;
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

            // ---- Desktop pointer self-heal -------------------------------------
            // Setup XR Rig removes the DesktopMousePointer from the scene, so
            // the inspector slot goes null. Auto-find it (search inactive objects
            // too) or, failing that, add one to this GameObject so Desktop mode
            // always has a working mouse pointer. Only heal in Desktop mode so
            // XR mode never accidentally picks up a stray DesktopMousePointer.
            if (isDesktop && desktopPointer == null)
            {
                desktopPointer = FindAnyObjectByType<DesktopMousePointer>(
                    FindObjectsInactive.Include);
                if (desktopPointer == null)
                    desktopPointer = gameObject.AddComponent<DesktopMousePointer>();
                if (desktopPointer != null)
                    Debug.LogWarning("[RigMode] desktopPointer was null — " +
                                     "auto-recovered: " + desktopPointer.name);
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

            // World-space UI canvases cache worldCamera at build time. A panel
            // built while the XR camera was MainCamera keeps raycasting through
            // the now-disabled XR camera after switching to Desktop, so mouse
            // clicks miss every button. Re-point every canvas at the camera that
            // actually renders this mode.
            RefreshCanvasCameras();

            // Re-apply passthrough for the new mode (no-op / VR background in
            // Desktop, real passthrough only while in XR).
            ApplyPassthrough();

            Debug.Log($"[RigMode] → {mode}   Camera.main = {Camera.main?.name ?? "null"}");
        }

        // Keep every world-space UI canvas pointing at the camera that is
        // actually rendering the current mode. GraphicRaycaster builds its
        // screen ray from canvas.worldCamera, so a stale reference to the
        // inactive camera makes mouse (and controller) clicks land nowhere.
        private void RefreshCanvasCameras()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            var canvases = FindObjectsByType<Canvas>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var c in canvases)
                if (c.renderMode == RenderMode.WorldSpace && c.worldCamera != cam)
                    c.worldCamera = cam;
        }

        // ---------------------------------------------------------- passthrough
        /// <summary>Switch between full VR (false) and passthrough MR (true).</summary>
        public void SetPassthrough(bool on)
        {
            _passthrough = on;
            ApplyPassthrough();
        }

        /// <summary>Flip VR ↔ MR at runtime.</summary>
        public void TogglePassthrough() => SetPassthrough(!_passthrough);

        private void ApplyPassthrough()
        {
            // Passthrough only makes sense while the headset rig is active.
            bool show = _passthrough && _current == RigMode.XR;

            // Camera background: transparent so the real room shows through;
            // skybox in plain VR.
            if (xrCamera != null)
            {
                xrCamera.clearFlags = show
                    ? CameraClearFlags.SolidColor
                    : CameraClearFlags.Skybox;
                if (show) xrCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }

            // Hide the virtual environment (ground/skybox props) in MR.
            EnsurePassthroughHideList();
            if (passthroughHide != null)
                foreach (var go in passthroughHide)
                    if (go != null) go.SetActive(!show);

            // AR Foundation drives Quest passthrough (XR_FB_passthrough composition
            // layer) and real-surface detection. Run them only in MR so plain VR
            // isn't paying for passthrough/plane tracking. The transparent camera
            // above lets the passthrough layer show through.
            if (arCameraManager != null) arCameraManager.enabled = show;
            if (arPlaneManager  != null) arPlaneManager.enabled  = show;

            // In MR the virtual Ground is hidden (above), which also removes the
            // collider dynamic robots rested on — a wheeled base would fall
            // through the floor. Provide an invisible physics floor at groundY
            // that exists only in MR.
            EnsureMrFloor(show);

            Debug.Log($"[RigMode] Passthrough {(show ? "ON (MR)" : "OFF (VR)")}.");
        }

        // Invisible physics floor used only in MR (the virtual Ground — and its
        // collider — is hidden so passthrough shows through). Sits with its top
        // surface at the same height robots are placed on (the Ground's Y, 0 by
        // default), so a wheeled base rests on it instead of falling.
        private GameObject _mrFloor;
        private void EnsureMrFloor(bool active)
        {
            if (active && _mrFloor == null)
            {
                float y = 0f;
                var ground = GameObject.Find("Ground");
                if (ground != null) y = ground.transform.position.y;

                _mrFloor = new GameObject("MR_FloorCollider");
                var bc = _mrFloor.AddComponent<BoxCollider>();
                bc.size = new Vector3(60f, 0.1f, 60f);
                // Box top surface lands exactly at y.
                _mrFloor.transform.position = new Vector3(0f, y - 0.05f, 0f);
            }
            if (_mrFloor != null) _mrFloor.SetActive(active);
        }

        // USE_SCENE / USE_ANCHOR_API are declared in the manifest by the Meta
        // OpenXR features, but they are RUNTIME permissions: until the user
        // grants them the scene/plane provider returns nothing, so no surfaces
        // are detected. Request them on launch and restart plane tracking once
        // granted. (The user must also have run Space Setup on the headset.)
        private void RequestScenePermissions()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var perms = new[]
            {
                "com.oculus.permission.USE_SCENE",
                "com.oculus.permission.USE_ANCHOR_API",
            };
            var need = new List<string>();
            foreach (var p in perms)
                if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(p))
                    need.Add(p);
            if (need.Count == 0) return;

            var cb = new UnityEngine.Android.PermissionCallbacks();
            // Re-run passthrough setup so the plane manager re-queries the scene
            // now that we're allowed to read it.
            cb.PermissionGranted += _ => ApplyPassthrough();
            UnityEngine.Android.Permission.RequestUserPermissions(
                need.ToArray(), cb);
#endif
        }

        // If the hide list wasn't wired in the inspector, fall back to the
        // scene's "Ground" plane so MR works out of the box.
        private void EnsurePassthroughHideList()
        {
            if (passthroughHide != null && passthroughHide.Length > 0) return;
            var ground = GameObject.Find("Ground");
            passthroughHide = ground != null
                ? new[] { ground }
                : new GameObject[0];
        }

        // Switch the XR Origin to floor-relative tracking so the virtual floor
        // (Y=0, where Ground sits and robots are placed) matches the real floor.
        private void ConfigureFloorTracking()
        {
#if UNITY_XR_INTERACTION_TOOLKIT
            if (!floorTracking || xrOriginGO == null) return;
            var origin = xrOriginGO.GetComponent<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null)
            {
                origin.RequestedTrackingOriginMode =
                    Unity.XR.CoreUtils.XROrigin.TrackingOriginMode.Floor;
                origin.CameraYOffset = 0f;
                Debug.Log("[RigMode] XR Origin set to floor tracking for MR.");
            }
#endif
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

        // Inspector helper: bakes the default VR/MR choice into the scene.
        public void BakePassthrough(bool on)
        {
            startPassthrough = on;
            SetPassthrough(on);
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
#endif
        }
    }
}
