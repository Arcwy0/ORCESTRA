using UnityEngine;
using UnityEngine.XR;
using VRInteraction.Placement;

namespace VRInteraction.Rig
{
    /// <summary>
    /// XR controller implementation of <see cref="PlacementPointer"/>.
    ///
    /// Reads the ray from the right controller's Transform (set by the XR rig)
    /// and maps trigger→confirm, secondary-button (B)→cancel.
    ///
    /// Uses only <c>UnityEngine.XR.InputDevices</c> (always available) —
    /// no XR Interaction Toolkit dependency so this compiles even before XRI
    /// is fully configured.
    ///
    /// Attach this to the same GameObject as <see cref="RobotPlacementController"/>
    /// after running <b>UR3 → Setup XR Rig</b> from the menu.
    /// </summary>
    public class XrControllerPointer : PlacementPointer
    {
        [Tooltip("Which controller acts as the placement ray (usually right hand).")]
        public XRNode hand = XRNode.RightHand;

        [Tooltip("Transform at the tip of the ray (XR Ray Interactor's rayOriginTransform).")]
        public Transform rayOrigin;

        // The XR controller is held in 3D, so tools place points at the
        // controller tip rather than on a screen-projected work-plane.
        public override bool IsSpatial => true;

        // ----- edge detection ------------------------------------------------
        // Edges are computed ONCE per frame in Update and cached. Reading them
        // (ConfirmPressedThisFrame / CancelPressedThisFrame) must NOT consume
        // the event: several tools poll the same pointer each frame (the
        // placement controller polls even while idle), and a consume-on-read
        // design let whichever ran first eat the press so the others — e.g. the
        // waypoint tool — never saw it.
        private bool _prevTrigger;
        private bool _prevCancel;
        private bool _confirmEdge;
        private bool _cancelEdge;

        private void Update()
        {
            // ---- trigger → confirm ----------------------------------------
            var dev = InputDevices.GetDeviceAtXRNode(hand);
            bool trig = false;
            dev.TryGetFeatureValue(CommonUsages.triggerButton, out trig);
            if (!trig)
            {
                float axis = 0f;
                if (dev.TryGetFeatureValue(CommonUsages.trigger, out axis))
                    trig = axis > 0.7f;
            }
            _confirmEdge = trig && !_prevTrigger;
            _prevTrigger = trig;

            // ---- B button → cancel ----------------------------------------
            var rdev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool cancel = false;
            rdev.TryGetFeatureValue(CommonUsages.secondaryButton, out cancel);
            _cancelEdge = cancel && !_prevCancel;
            _prevCancel = cancel;
        }

        // -----------------------------------------------------------------------
        public override Ray GetRay()
        {
            if (rayOrigin != null)
                return new Ray(rayOrigin.position, rayOrigin.forward);

            // Fallback: camera centre ray (useful in Editor simulation)
            var cam = Camera.main;
            if (cam != null)
                return new Ray(cam.transform.position, cam.transform.forward);

            return new Ray(Vector3.zero, Vector3.forward);
        }

        // Non-consuming reads: return the cached per-frame edge so every tool
        // that polls this frame observes the same press.
        public override bool ConfirmPressedThisFrame() =>
            !IsOverUi() && _confirmEdge;

        public override bool CancelPressedThisFrame() => _cancelEdge;

        // Right thumbstick Y → push the held point farther / pull it nearer
        // along the ray. Dead-zoned so a resting stick doesn't drift the point.
        public override float DepthAxis()
        {
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out var axis) &&
                Mathf.Abs(axis.y) > 0.15f)
                return axis.y;
            return 0f;
        }
    }
}
