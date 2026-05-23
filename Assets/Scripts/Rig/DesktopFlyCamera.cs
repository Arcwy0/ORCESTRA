using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VRInteraction.Rig
{
    /// <summary>
    /// Simple desktop free-fly camera that stands in for the VR headset until
    /// the XR rig is added. Hold right mouse button to look, WASD to move,
    /// Q/E down/up, mouse wheel to change speed.
    /// </summary>
    public class DesktopFlyCamera : MonoBehaviour
    {
        [Tooltip("Movement speed in m/s.")]
        public float moveSpeed = 2.0f;
        [Tooltip("Mouse look sensitivity.")]
        public float lookSensitivity = 2.0f;
        [Tooltip("Shift speed multiplier.")]
        public float sprintMultiplier = 3.0f;

        private float _yaw;
        private float _pitch;

        private void Start()
        {
            var e = transform.eulerAngles;
            _yaw = e.y;
            _pitch = e.x;
        }

        private void Update()
        {
#if ENABLE_INPUT_SYSTEM
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            bool look = mouse.rightButton.isPressed;
            if (look)
            {
                Vector2 d = mouse.delta.ReadValue() * (lookSensitivity * 0.05f);
                _yaw += d.x;
                _pitch = Mathf.Clamp(_pitch - d.y, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f)
                moveSpeed = Mathf.Clamp(moveSpeed + Mathf.Sign(scroll) * 0.25f, 0.1f, 20f);

            Vector3 m = Vector3.zero;
            if (kb.wKey.isPressed) m += Vector3.forward;
            if (kb.sKey.isPressed) m += Vector3.back;
            if (kb.aKey.isPressed) m += Vector3.left;
            if (kb.dKey.isPressed) m += Vector3.right;
            if (kb.eKey.isPressed) m += Vector3.up;
            if (kb.qKey.isPressed) m += Vector3.down;

            float speed = moveSpeed * (kb.leftShiftKey.isPressed ? sprintMultiplier : 1f);
            transform.Translate(m.normalized * speed * Time.deltaTime, Space.Self);
#else
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * lookSensitivity, -89f, 89f);
                transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
                moveSpeed = Mathf.Clamp(moveSpeed + Mathf.Sign(scroll) * 0.25f, 0.1f, 20f);

            Vector3 m = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) m += Vector3.forward;
            if (Input.GetKey(KeyCode.S)) m += Vector3.back;
            if (Input.GetKey(KeyCode.A)) m += Vector3.left;
            if (Input.GetKey(KeyCode.D)) m += Vector3.right;
            if (Input.GetKey(KeyCode.E)) m += Vector3.up;
            if (Input.GetKey(KeyCode.Q)) m += Vector3.down;

            float speed = moveSpeed * (Input.GetKey(KeyCode.LeftShift) ? sprintMultiplier : 1f);
            transform.Translate(m.normalized * speed * Time.deltaTime, Space.Self);
#endif
        }
    }
}
