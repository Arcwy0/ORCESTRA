using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using UnityEngine.EventSystems;

namespace VRInteraction.Placement
{
    /// <summary>
    /// Abstraction over the "pointing device". On PC this is the mouse ray
    /// from the camera; later an XR controller can subclass this so the
    /// placement flow stays unchanged.
    /// </summary>
    public abstract class PlacementPointer : MonoBehaviour
    {
        public abstract Ray GetRay();
        public abstract bool ConfirmPressedThisFrame();
        public abstract bool CancelPressedThisFrame();

        /// <summary>True while the pointer is hovering a uGUI element.</summary>
        public bool IsOverUi()
        {
            var es = EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }
    }

    /// <summary>PC stand-in: ray from the main camera through the mouse.</summary>
    public class DesktopMousePointer : PlacementPointer
    {
        private Camera _cam;

        private Camera Cam
        {
            get
            {
                if (_cam == null) _cam = Camera.main;
                return _cam;
            }
        }

        public override Ray GetRay()
        {
            if (Cam == null) return new Ray(Vector3.zero, Vector3.forward);
#if ENABLE_INPUT_SYSTEM
            Vector2 p = Mouse.current != null
                ? Mouse.current.position.ReadValue()
                : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
#else
            Vector2 p = Input.mousePosition;
#endif
            return Cam.ScreenPointToRay(p);
        }

        public override bool ConfirmPressedThisFrame()
        {
            if (IsOverUi()) return false;
#if ENABLE_INPUT_SYSTEM
            return Mouse.current != null &&
                   Mouse.current.leftButton.wasPressedThisFrame;
#else
            return Input.GetMouseButtonDown(0);
#endif
        }

        public override bool CancelPressedThisFrame()
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null &&
                   Keyboard.current.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }
    }
}
