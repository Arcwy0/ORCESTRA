using UnityEngine;

namespace VRInteraction.UI
{
    /// <summary>
    /// Keeps a world-space panel floating in front of the operator, offset a
    /// little to the right and above the gaze centre, so it's always reachable
    /// without covering the middle of the view. Position only — pair it with a
    /// <see cref="Billboard"/> (added by UiKit) to also keep it facing the
    /// operator.
    ///
    /// Runs just before <see cref="Billboard"/> so the billboard rotation uses
    /// the updated position in the same frame.
    /// </summary>
    [DefaultExecutionOrder(999)]
    public class FollowOperator : MonoBehaviour
    {
        [Tooltip("Distance in front of the operator, in metres.")]
        public float distance = 0.55f;
        [Tooltip("Offset to the right of the gaze centre, in metres.")]
        public float rightOffset = 0.18f;
        [Tooltip("Offset above the gaze centre, in metres.")]
        public float upOffset = 0.12f;
        [Tooltip("Higher = snappier follow, lower = smoother/laggier.")]
        public float smooth = 8f;

        private Camera _cam;
        private bool _snap;

        private void OnEnable() => _snap = true;   // jump to place on first show

        private void LateUpdate()
        {
            if (_cam == null || !_cam.isActiveAndEnabled)
                _cam = Camera.main;
            if (_cam == null) return;

            var t = _cam.transform;
            Vector3 target = t.position
                           + t.forward * distance
                           + t.right * rightOffset
                           + t.up * upOffset;

            if (_snap)
            {
                transform.position = target;
                _snap = false;
            }
            else
            {
                float k = 1f - Mathf.Exp(-smooth * Time.deltaTime);
                transform.position = Vector3.Lerp(transform.position, target, k);
            }
        }
    }
}
