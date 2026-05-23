using UnityEngine;

namespace VRInteraction.UI
{
    /// <summary>
    /// Rotates this transform every frame so its local +Z (the front of a
    /// uGUI world-space canvas) points toward the main camera. Result:
    /// menus and panels always face the operator no matter where they
    /// walk around.
    /// </summary>
    [DefaultExecutionOrder(1000)]   // after gameplay logic; before render
    public class Billboard : MonoBehaviour
    {
        [Tooltip("Constrain rotation to the world Y axis so the panel " +
                 "stays upright (typical for VR). Disable for full lookAt.")]
        public bool yAxisOnly = true;

        private Camera _cam;

        private void LateUpdate()
        {
            if (_cam == null || !_cam.isActiveAndEnabled)
                _cam = Camera.main;
            if (_cam == null) return;

            Vector3 toCam = _cam.transform.position - transform.position;
            if (yAxisOnly) toCam.y = 0f;
            if (toCam.sqrMagnitude < 1e-8f) return;

            transform.rotation = Quaternion.LookRotation(-toCam, Vector3.up);
        }
    }
}
