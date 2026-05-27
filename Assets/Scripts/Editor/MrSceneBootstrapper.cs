using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using VRInteraction.Rig;

namespace VRInteraction.Editor
{
    /// <summary>
    /// Adds the AR Foundation components needed for Quest passthrough (MR) on top
    /// of the XR rig built by <c>UR3 → Setup XR Rig</c>.
    ///
    /// Quest passthrough cannot be done with the bare OpenXR plugin (the runtime
    /// only supports the OPAQUE environment blend mode). It requires the
    /// <c>XR_FB_passthrough</c> extension, surfaced through the
    /// <b>Unity OpenXR: Meta</b> package via AR Foundation:
    ///   • <see cref="ARSession"/>        — drives the AR subsystems.
    ///   • <see cref="ARCameraManager"/>  — the passthrough camera (on XR Camera).
    ///   • <see cref="ARPlaneManager"/>   — real-surface detection (on XR Origin).
    ///   • <see cref="ARRaycastManager"/> — ray-cast against real surfaces.
    ///
    /// Prerequisites (manual, once, in the Editor):
    ///   1. Project Settings → XR Plug-in Management → OpenXR → enable the
    ///      <b>Meta Quest</b> feature group and these features (Standalone tab for
    ///      Quest Link, Android tab for an APK):
    ///        • Meta Quest: Camera (Passthrough)
    ///        • Meta Quest: Plane
    ///        • Meta Quest: Raycast
    ///        • Meta Quest: Session
    ///   2. For an on-device APK: grant the <c>com.oculus.permission.USE_SCENE</c>
    ///      permission and run Space Setup on the headset. Over Quest Link the
    ///      registry keys AllowPassthrough / AllowSpatialData enable the same.
    /// </summary>
    public static class MrSceneBootstrapper
    {
        [MenuItem("UR3/Setup MR (AR Foundation)", priority = 32)]
        public static void SetupMr()
        {
            var mgr = Object.FindAnyObjectByType<RigModeManager>();
            if (mgr == null)
            {
                Debug.LogWarning("[MR Setup] No RigModeManager in scene. " +
                                 "Run UR3 → Setup XR Rig first.");
                return;
            }
            if (mgr.xrOriginGO == null || mgr.xrCamera == null)
            {
                Debug.LogWarning("[MR Setup] RigModeManager has no XR Origin / XR " +
                                 "Camera. Re-run UR3 → Setup XR Rig first.");
                return;
            }

            // ---- AR Session ---------------------------------------------------
            var session = Object.FindAnyObjectByType<ARSession>();
            if (session == null)
            {
                var go = new GameObject("AR Session", typeof(ARSession));
                session = go.GetComponent<ARSession>();
                Debug.Log("[MR Setup] Created AR Session.");
            }

            // ---- AR Camera (passthrough) — on the XR Camera -------------------
            var camGo = mgr.xrCamera.gameObject;
            var camMgr = camGo.GetComponent<ARCameraManager>();
            if (camMgr == null) camMgr = camGo.AddComponent<ARCameraManager>();

            // ---- Plane + Raycast managers — on the XR Origin ------------------
            var originGo = mgr.xrOriginGO;
            var planeMgr = originGo.GetComponent<ARPlaneManager>();
            if (planeMgr == null) planeMgr = originGo.AddComponent<ARPlaneManager>();
            planeMgr.requestedDetectionMode =
                PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;

            var rayMgr = originGo.GetComponent<ARRaycastManager>();
            if (rayMgr == null) rayMgr = originGo.AddComponent<ARRaycastManager>();

            // ---- Wire RigModeManager so it runs AR only in MR ----------------
            mgr.arCameraManager = camMgr;
            mgr.arPlaneManager = planeMgr;

            // Start disabled; RigModeManager turns them on when entering MR.
            camMgr.enabled = false;
            planeMgr.enabled = false;

            EditorSceneManager.MarkAllScenesDirty();
            Debug.Log("[MR Setup] ✓ AR Foundation components added (Session, Camera, " +
                      "Plane, Raycast). Enable the Meta Quest OpenXR features, then " +
                      "UR3 → Mode → MR Passthrough and save the scene.");
        }

        [MenuItem("UR3/Setup MR (AR Foundation)", true)]
        private static bool SetupMrValidate() =>
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().IsValid();
    }
}
