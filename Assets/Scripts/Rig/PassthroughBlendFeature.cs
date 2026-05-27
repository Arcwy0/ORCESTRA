using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.NativeTypes;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR.Features;
#endif

namespace VRInteraction.Rig
{
    /// <summary>
    /// Custom OpenXR feature that flips the runtime <b>environment blend mode</b>
    /// between OPAQUE (full VR) and ALPHA_BLEND (Quest passthrough / MR).
    ///
    /// When the blend mode is ALPHA_BLEND and the active camera clears to a
    /// transparent colour, the Quest compositor shows the real-world camera
    /// feed wherever nothing virtual is drawn — that is passthrough MR, with no
    /// extra packages required (it rides on the installed OpenXR plugin).
    ///
    /// <b>Enable this feature</b> in
    /// <i>Project Settings → XR Plug-in Management → OpenXR</i> for both the
    /// Standalone and Android tabs, then drive it from
    /// <see cref="RigModeManager"/> (<see cref="SetPassthrough"/>).
    ///
    /// NOTE: passthrough over Quest Link also requires the desktop Meta Quest
    /// Link app's beta toggle "Pass-through over Meta Quest Link".
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName = "VRInteraction Passthrough Blend",
        FeatureId = featureId,
        Version = "1.0.0",
        Company = "VRInteraction",
        Desc = "Switches the OpenXR environment blend mode to ALPHA_BLEND so " +
               "Quest passthrough shows through transparent camera regions (MR).",
        BuildTargetGroups = new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })]
#endif
    public class PassthroughBlendFeature : OpenXRFeature
    {
        public const string featureId = "com.vrinteraction.passthroughblend";

        // Desired state survives session restarts; the actual native call can
        // only be made once a session is running, so we defer if needed.
        private static bool s_desiredPassthrough;
        private static bool s_sessionRunning;

        /// <summary>True if the OpenXR session is up and the feature is active.</summary>
        public static bool SessionRunning => s_sessionRunning;

        /// <summary>
        /// Request passthrough (ALPHA_BLEND) or full VR (OPAQUE). Safe to call
        /// any time — applied immediately if a session is running, otherwise
        /// remembered and applied when the session begins.
        /// </summary>
        public static void SetPassthrough(bool on)
        {
            s_desiredPassthrough = on;
            if (s_sessionRunning) Apply();
        }

        private static void Apply()
        {
            // SetEnvironmentBlendMode is a protected static of OpenXRFeature;
            // accessible here because this class derives from it.
            SetEnvironmentBlendMode(s_desiredPassthrough
                ? XrEnvironmentBlendMode.AlphaBlend
                : XrEnvironmentBlendMode.Opaque);
        }

        protected override void OnSessionBegin(ulong xrSession)
        {
            s_sessionRunning = true;
            Apply();   // re-assert the desired blend mode for this session
        }

        protected override void OnSessionEnd(ulong xrSession)
        {
            s_sessionRunning = false;
        }
    }
}
