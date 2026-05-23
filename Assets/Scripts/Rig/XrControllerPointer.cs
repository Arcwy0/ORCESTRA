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

        // ----- edge detection ------------------------------------------------
        private bool _prevTrigger;
        private bool _prevCancel;

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

        public override bool ConfirmPressedThisFrame()
        {
            if (IsOverUi()) return false;

            var dev = InputDevices.GetDeviceAtXRNode(hand);
            bool cur = false;
            dev.TryGetFeatureValue(CommonUsages.triggerButton, out cur);

            // If triggerButton not available try trigger axis threshold
            if (!cur)
            {
                float axis = 0f;
                if (dev.TryGetFeatureValue(CommonUsages.trigger, out axis))
                    cur = axis > 0.7f;
            }

            bool pressed = cur && !_prevTrigger;
            _prevTrigger = cur;
            return pressed;
        }

        public override bool CancelPressedThisFrame()
        {
            // B button (secondaryButton) on the right Touch controller
            var dev = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            bool cur = false;
            dev.TryGetFeatureValue(CommonUsages.secondaryButton, out cur);

            bool pressed = cur && !_prevCancel;
            _prevCancel = cur;
            return pressed;
        }
    }
}
