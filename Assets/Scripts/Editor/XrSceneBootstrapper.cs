using UnityEngine;
using UnityEditor;
using UnityEngine.EventSystems;
using VRInteraction.Placement;
using VRInteraction.Rig;
using RigModeManager = VRInteraction.Rig.RigModeManager;

#if UNITY_XR_INTERACTION_TOOLKIT
using Unity.XR.CoreUtils;

using UnityEngine.XR.Interaction.Toolkit.UI;
#endif

namespace VRInteraction.Editor
{
    /// <summary>
    /// Builds the XR Origin camera/controller rig for Quest 3 testing.
    ///
    /// Prerequisites (must be done in the Unity Editor before running this):
    ///   1. <b>Edit → Project Settings → XR Plugin Management</b> →
    ///      install, enable <b>OpenXR</b> for <em>Standalone</em> AND <em>Android</em>.
    ///   2. In the OpenXR settings add <b>Meta Quest Feature Group</b>
    ///      under each platform.
    ///   3. (Android) Switch platform to Android, set min API 29,
    ///      Scripting Backend IL2CPP, Target Architecture ARM64.
    ///
    /// This menu item does the scene-assembly work so you don't have to drag
    /// prefabs manually. Run it on the open placement scene.
    /// </summary>
    public static class XrSceneBootstrapper
    {
        // -----------------------------------------------------------------------
        [MenuItem("UR3/Setup XR Rig", priority = 30)]
        public static void SetupXrRig()
        {
            // ---- 0. Clean up any previous XR Rig run -------------------------
            //
            // Rescue ALL cameras inside XR Origin before destroying it.
            // We do NOT filter by name — any previous run may have created
            // cameras with different names ("XR Camera", "Main Camera", etc.)
            // and we must not lose them.
            var oldOrigin = GameObject.Find("XR Origin");
            Camera rescuedDesktopCam = null;
            if (oldOrigin != null)
            {
                var camsInOrigin = oldOrigin.GetComponentsInChildren<Camera>(true);
                foreach (var cam in camsInOrigin)
                {
                    // Only rescue cameras that were the desktop cam (have
                    // DesktopFlyCamera) or were tagged MainCamera.  Pure "XR Camera"
                    // objects (no DesktopFlyCamera, no MainCamera tag) are discarded.
                    bool isDesktopCam = cam.GetComponent<DesktopFlyCamera>() != null
                                     || cam.CompareTag("MainCamera")
                                     || cam.gameObject.name == "Main Camera";
                    if (isDesktopCam)
                    {
                        cam.transform.SetParent(null, worldPositionStays: true);
                        cam.tag = "MainCamera";
                        cam.gameObject.SetActive(true);
                        rescuedDesktopCam = cam;
                        Debug.Log($"[XR Setup] Rescued desktop camera '{cam.name}' from old XR Origin.");
                    }
                }
                Object.DestroyImmediate(oldOrigin);
                Debug.Log("[XR Setup] Removed old XR Origin.");
            }

            // Remove stale RigModeManager from previous run
            var oldMgr = Object.FindAnyObjectByType<RigModeManager>();
            if (oldMgr != null) Object.DestroyImmediate(oldMgr);

            // ---- 1. Ensure a desktop camera exists and DesktopFlyCamera is on it
            var existing = Object.FindAnyObjectByType<DesktopFlyCamera>(
                FindObjectsInactive.Include);
            if (existing != null)
            {
                existing.enabled = true;
                existing.gameObject.SetActive(true);
                Debug.Log("[XR Setup] DesktopFlyCamera re-enabled.");
            }

            // If no camera survived, create a fresh one so the scene is usable
            if (Camera.main == null && rescuedDesktopCam == null)
            {
                var camGo = new GameObject("Main Camera", typeof(Camera),
                                           typeof(DesktopFlyCamera));
                camGo.tag = "MainCamera";
                camGo.transform.SetPositionAndRotation(
                    new Vector3(-1.2f, 1.4f, -1.2f),
                    Quaternion.Euler(15f, 45f, 0f));
                camGo.GetComponent<Camera>().nearClipPlane = 0.02f;
                rescuedDesktopCam = camGo.GetComponent<Camera>();
                Debug.Log("[XR Setup] Created a fresh Main Camera (none survived cleanup).");
            }

            // ---- 2. Build XR Origin hierarchy ---------------------------------
            //
            // Hierarchy:
            //   XR Origin
            //     Camera Offset
            //       Main Camera  (TrackedPoseDriver via XRI — added below)
            //     Left Controller
            //     Right Controller   ← XrControllerPointer ray will use this
            //
            var xrOriginGO = new GameObject("XR Origin");

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(xrOriginGO.transform, false);
            cameraOffset.transform.localPosition = Vector3.up * 1.36f; // floor-level offset

            // Create a DEDICATED XR camera — do NOT move the existing desktop
            // Main Camera.  Both cameras coexist; RigModeManager swaps the
            // "MainCamera" tag between them so Camera.main is always correct.
            var xrCamGO = new GameObject("XR Camera");
            xrCamGO.transform.SetParent(cameraOffset.transform, false);
            xrCamGO.transform.localPosition = Vector3.zero;
            var xrCam = xrCamGO.AddComponent<Camera>();
            xrCamGO.tag = "Untagged"; // RigModeManager assigns "MainCamera" in XR mode

            var leftCtrl = new GameObject("Left Controller");
            leftCtrl.transform.SetParent(xrOriginGO.transform, false);

            var rightCtrl = new GameObject("Right Controller");
            rightCtrl.transform.SetParent(xrOriginGO.transform, false);

            // ---- 3. Add XR components if XRI package is installed -------------
#if UNITY_XR_INTERACTION_TOOLKIT
            // XR Origin — references the dedicated XR camera (not the desktop one)
            var xrOriginComp = xrOriginGO.AddComponent<XROrigin>();
            xrOriginComp.CameraFloorOffsetObject = cameraOffset;
            xrOriginComp.Camera = xrCam;

            // Add TrackedPoseDriver to the XR camera so head tracking works.
            // CRITICAL: it must be bound to the HMD center-eye pose, otherwise the
            // driver reads nothing and the world appears locked to the head.
            ConfigureTrackedPose(
                xrCamGO,
                "<XRHMD>/centerEyePosition",
                "<XRHMD>/centerEyeRotation",
                "HMD");

            // Right controller: ray interactor for placement + UI
            var rayInteractor = rightCtrl.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactors.XRRayInteractor>();
            var lineVisual = rightCtrl.AddComponent<UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals.XRInteractorLineVisual>();
            lineVisual.lineWidth = 0.005f;

            // Stereo-correct line material so the ray is visible in BOTH eyes.
            // The built-in line material is not single-pass-stereo aware and only
            // renders in the left eye under URP + XR.
            var lr = rightCtrl.GetComponent<LineRenderer>();
            if (lr != null) lr.sharedMaterial = GetOrCreateRayMaterial();

            // Drive UI clicks and select with the right-hand trigger.
            rayInteractor.uiPressInput = MakeTriggerReader("Right UI Press");
            rayInteractor.selectInput  = MakeTriggerReader("Right Select");

            // Make both controllers follow the physical Touch controllers.
            // Right controller uses the POINTER (aim) pose so the ray points where
            // you aim. The device/grip pose would make the ray shoot sideways out
            // of the controller body (vertically), offset from the controller.
            ConfigureTrackedPose(leftCtrl,
                "<XRController>{LeftHand}/devicePosition",
                "<XRController>{LeftHand}/deviceRotation", "LeftHand");
            ConfigureTrackedPose(rightCtrl,
                "<XRController>{RightHand}/pointerPosition",
                "<XRController>{RightHand}/pointerRotation", "RightHand");

            // Small visible markers so the controllers can be seen in-headset.
            AddControllerMarker(leftCtrl);
            AddControllerMarker(rightCtrl);

            // XR UI Input Module — replaces Standalone/InputSystem module
            var es = Object.FindAnyObjectByType<EventSystem>();
            if (es != null)
            {
                var old = es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                if (old != null) Object.DestroyImmediate(old);

                if (es.GetComponent<XRUIInputModule>() == null)
                    es.gameObject.AddComponent<XRUIInputModule>();
                Debug.Log("[XR Setup] XRUIInputModule added to EventSystem.");
            }
            else
            {
                Debug.LogWarning("[XR Setup] No EventSystem in scene — UI clicks may not work.");
            }

            Debug.Log("[XR Setup] XROrigin, XRRayInteractor, XRUIInputModule added.");
#else
            Debug.LogWarning(
                "[XR Setup] XR Interaction Toolkit not yet installed or the " +
                "UNITY_XR_INTERACTION_TOOLKIT scripting define is missing.\n" +
                "After the package resolves, re-run UR3 → Setup XR Rig.\n" +
                "XR Origin hierarchy was created — add XROrigin + XRRayInteractor manually.");
#endif

            // ---- 4. Wire XrControllerPointer to the placement controller ------
            var placement = Object.FindAnyObjectByType<RobotPlacementController>();
            XrControllerPointer xrPtr = null;
            DesktopMousePointer desktopPtr = null;

            if (placement != null)
            {
                // Keep DesktopMousePointer — RigModeManager will swap between them
                desktopPtr = placement.GetComponent<DesktopMousePointer>();

                xrPtr = placement.GetComponent<XrControllerPointer>();
                if (xrPtr == null) xrPtr = placement.gameObject.AddComponent<XrControllerPointer>();
                xrPtr.rayOrigin = rightCtrl.transform;

                Debug.Log("[XR Setup] XrControllerPointer attached to PlacementController.");
            }
            else
            {
                Debug.LogWarning("[XR Setup] No RobotPlacementController in scene — " +
                                 "add RigModeManager references manually.");
            }

            // ---- 5. Add / configure RigModeManager ---------------------------
            var mgrGO = placement != null ? placement.gameObject : xrOriginGO;
            var mgr = mgrGO.GetComponent<RigModeManager>();
            if (mgr == null) mgr = mgrGO.AddComponent<RigModeManager>();

            // Desktop camera: prefer Camera.main; fall back to what we rescued above
            var desktopCam = Camera.main ?? rescuedDesktopCam;
            var dfc = Object.FindAnyObjectByType<DesktopFlyCamera>(
                FindObjectsInactive.Include);

            mgr.desktopCamera    = desktopCam;
            mgr.xrCamera         = xrCam;
            mgr.xrOriginGO       = xrOriginGO;
            mgr.desktopPointer   = desktopPtr;
            mgr.xrPointer        = xrPtr;
            mgr.placementController = placement;
            mgr.startMode        = RigModeManager.RigMode.Desktop;  // safe default

            // Re-enable the desktop fly camera if it was disabled earlier
            if (dfc != null) dfc.enabled = true;

            Debug.Log("[XR Setup] RigModeManager configured. " +
                      "Switch via UR3 → Mode → Desktop/XR or press F12 at runtime.");

            // ---- 6. Mark scene dirty -----------------------------------------
            UnityEditor.SceneManagement.EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[XR Setup] ✓ XR Rig setup complete. Save the scene (Ctrl+S).");
        }

        // -----------------------------------------------------------------------
        /// <summary>
        /// Adds a <see cref="UnityEngine.InputSystem.XR.TrackedPoseDriver"/> bound to
        /// the given OpenXR pose paths. Inline (singleton) input actions are used so
        /// the rig is self-contained and the actions auto-enable at runtime.
        /// </summary>
        private static void ConfigureTrackedPose(
            GameObject go, string posBinding, string rotBinding, string label)
        {
            var tpd = go.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            if (tpd == null) tpd = go.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();

            var posAction = new UnityEngine.InputSystem.InputAction(
                $"{label} Position", UnityEngine.InputSystem.InputActionType.Value,
                posBinding, expectedControlType: "Vector3");
            var rotAction = new UnityEngine.InputSystem.InputAction(
                $"{label} Rotation", UnityEngine.InputSystem.InputActionType.Value,
                rotBinding, expectedControlType: "Quaternion");

            tpd.positionInput = new UnityEngine.InputSystem.InputActionProperty(posAction);
            tpd.rotationInput = new UnityEngine.InputSystem.InputActionProperty(rotAction);
            tpd.trackingType  = UnityEngine.InputSystem.XR.TrackedPoseDriver.TrackingType.RotationAndPosition;
            tpd.updateType    = UnityEngine.InputSystem.XR.TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;

            Debug.Log($"[XR Setup] TrackedPoseDriver on '{go.name}' bound to {label} pose.");
        }

        /// <summary>
        /// Builds an <see cref="UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader"/>
        /// in embedded-action mode bound to the right-hand trigger (button + axis).
        /// </summary>
        private static UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader
            MakeTriggerReader(string label)
        {
            var reader = new UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader(label);
            reader.inputSourceMode =
                UnityEngine.XR.Interaction.Toolkit.Inputs.Readers.XRInputButtonReader.InputSourceMode.InputAction;
            reader.inputActionPerformed = new UnityEngine.InputSystem.InputAction(
                label, UnityEngine.InputSystem.InputActionType.Button,
                "<XRController>{RightHand}/triggerPressed");
            reader.inputActionValue = new UnityEngine.InputSystem.InputAction(
                $"{label} Value", UnityEngine.InputSystem.InputActionType.Value,
                "<XRController>{RightHand}/trigger", expectedControlType: "Axis");
            return reader;
        }

        /// <summary>
        /// Loads (or creates) a URP-unlit material with GPU instancing enabled so
        /// the ray LineRenderer renders correctly in both eyes (single-pass stereo).
        /// </summary>
        private static Material GetOrCreateRayMaterial()
        {
            const string path = "Assets/XR/RayLine.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "RayLine" };
            mat.enableInstancing = true;

            var color = new Color(0.25f, 0.8f, 1f);
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            else mat.color = color;

            if (!AssetDatabase.IsValidFolder("Assets/XR"))
                AssetDatabase.CreateFolder("Assets", "XR");
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();
            Debug.Log($"[XR Setup] Created stereo-safe ray material at {path}.");
            return mat;
        }

        /// <summary>Adds a small non-colliding cube so a controller is visible in VR.</summary>
        private static void AddControllerMarker(GameObject controller)
        {
            if (controller.transform.Find("Marker") != null) return;

            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "Marker";
            marker.transform.SetParent(controller.transform, false);
            marker.transform.localScale = Vector3.one * 0.04f;

            // Remove the collider so the marker never blocks the placement ray.
            var col = marker.GetComponent<Collider>();
            if (col != null) Object.DestroyImmediate(col);
        }

        // -----------------------------------------------------------------------
        [MenuItem("UR3/Setup XR Rig", true)]
        private static bool SetupXrRigValidate() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().IsValid();

        // -----------------------------------------------------------------------
        [MenuItem("UR3/Mode/Desktop (PC)", priority = 40)]
        public static void SwitchToDesktop() => SetRigMode(RigModeManager.RigMode.Desktop);

        [MenuItem("UR3/Mode/XR (Quest)", priority = 41)]
        public static void SwitchToXR() => SetRigMode(RigModeManager.RigMode.XR);

        [MenuItem("UR3/Mode/VR Headset (no passthrough)", priority = 50)]
        public static void SwitchToVR() => SetPassthroughMode(false);

        [MenuItem("UR3/Mode/MR Passthrough", priority = 51)]
        public static void SwitchToMR() => SetPassthroughMode(true);

        private static void SetRigMode(RigModeManager.RigMode mode)
        {
            var mgr = Object.FindAnyObjectByType<RigModeManager>();
            if (mgr == null)
            {
                Debug.LogWarning("[RigMode] No RigModeManager in scene. " +
                                 "Run UR3 → Setup XR Rig first.");
                return;
            }
            mgr.BakeMode(mode);
            Debug.Log($"[RigMode] Scene baked to {mode} mode. Save (Ctrl+S).");
        }

        // Bakes the VR/MR choice. Forces XR mode too (passthrough only applies
        // in the headset rig), so picking MR is a one-click "go to passthrough".
        private static void SetPassthroughMode(bool passthrough)
        {
            var mgr = Object.FindAnyObjectByType<RigModeManager>();
            if (mgr == null)
            {
                Debug.LogWarning("[RigMode] No RigModeManager in scene. " +
                                 "Run UR3 → Setup XR Rig first.");
                return;
            }
            mgr.BakeMode(RigModeManager.RigMode.XR);
            mgr.BakePassthrough(passthrough);
            Debug.Log($"[RigMode] Scene baked to {(passthrough ? "MR passthrough" : "VR")} " +
                      "mode. Save (Ctrl+S). Enable the 'VRInteraction Passthrough " +
                      "Blend' OpenXR feature if you haven't yet.");
        }

        // -----------------------------------------------------------------------
        /// <summary>
        /// Adds the UNITY_XR_INTERACTION_TOOLKIT scripting define so the XR
        /// code path compiles once the package is installed. Run once after
        /// the packages resolve.
        /// </summary>
        [MenuItem("UR3/Enable XR Scripting Define", priority = 31)]
        public static void AddXrDefine()
        {
            const string define = "UNITY_XR_INTERACTION_TOOLKIT";
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            PlayerSettings.GetScriptingDefineSymbolsForGroup(group, out string[] existing);

            foreach (var d in existing)
                if (d == define) { Debug.Log($"[XR] '{define}' already set."); return; }

            var list = new System.Collections.Generic.List<string>(existing) { define };
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, list.ToArray());
            Debug.Log($"[XR] Added scripting define '{define}'. Recompiling…");
        }
    }
}
