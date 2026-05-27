using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace VRInteraction.UI
{
    /// <summary>
    /// Tiny procedural uGUI builder shared by the world-space panels
    /// (teach pendant, robot catalog, fine-tune window). Keeps every panel
    /// VR-ready: all canvases are world-space.
    /// </summary>
    public static class UiKit
    {
        public static Font Font =>
            Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
            ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

        public static Canvas WorldCanvas(string name, Transform parent,
            Vector3 worldPos, Vector3 worldEuler, Vector2 size, float scale)
        {
            var go = new GameObject(name,
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null) go.transform.SetParent(parent, false);

            // Let XR controller rays click this world-space UI.
            AddXrRaycaster(go);

            // Always face the operator. The initial worldEuler below is
            // overwritten in LateUpdate after the first frame.
            go.AddComponent<Billboard>();

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;

            var rt = canvas.GetComponent<RectTransform>();
            rt.sizeDelta = size;
            rt.position = worldPos;
            rt.rotation = Quaternion.Euler(worldEuler);
            rt.localScale = Vector3.one * scale;
            return canvas;
        }

        public static Image Panel(Transform parent, Color c)
        {
            var img = Image("Panel", parent, c);
            Stretch(img.rectTransform);
            return img;
        }

        public static Image Image(string n, Transform parent, Color c)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            return img;
        }

        public static Text Text(string n, Transform parent, string txt, int size,
            TextAnchor anchor)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = Font;
            t.text = txt;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = new Color(0.92f, 0.94f, 0.97f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        public static Button Button(string n, Transform parent, string label,
            int size, Color color, Action onClick)
        {
            var go = new GameObject(n,
                typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var t = Text("Label", go.transform, label, size, TextAnchor.MiddleCenter);
            Stretch(t.rectTransform);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        public static RectTransform Rt(Component c) =>
            (RectTransform)c.transform;

        public static void Stretch(RectTransform rt, float l = 0, float r = 0,
            float t = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
        }

        /// <summary>Anchor to top, full width, fixed height, offset from top.</summary>
        public static void TopRow(RectTransform rt, float topOffset, float height,
            float sideMargin = 16f)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(sideMargin, 0);
            rt.offsetMax = new Vector2(-sideMargin, 0);
            rt.anchoredPosition = new Vector2(0, -topOffset);
            rt.sizeDelta = new Vector2(-sideMargin * 2, height);
        }

        public static void Box(RectTransform rt, float x, float y,
            float w, float h)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        /// <summary>
        /// Adds a <c>TrackedDeviceGraphicRaycaster</c> to a world-space canvas so
        /// XR controller rays can click it. No-op if XRI isn't present or the
        /// raycaster already exists. Call this for any canvas not built through
        /// <see cref="WorldCanvas"/> (e.g. the robot teach pendants).
        /// </summary>
        public static void AddXrRaycaster(GameObject canvasGo)
        {
#if UNITY_XR_INTERACTION_TOOLKIT
            if (canvasGo == null) return;
            if (canvasGo.GetComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>() == null)
                canvasGo.AddComponent<UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster>();
#endif
        }

        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<EventSystem>() != null)
                return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
