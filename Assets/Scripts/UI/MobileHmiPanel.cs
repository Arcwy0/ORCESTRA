using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using VRInteraction.Robot;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

namespace VRInteraction.UI
{
    /// <summary>
    /// Drive pendant for a wheeled mobile base (Scout V2). World-space canvas
    /// built procedurally — same panel works on PC and in VR. A diamond of
    /// press-and-hold arrows (forward / back / turn-left / turn-right), a speed
    /// scale, a live speed read-out and a protective STOP. Mirrors the layout
    /// language of <see cref="UR3HmiPanel"/> but commands a
    /// <see cref="MobileBaseController"/> in velocity space instead of joints.
    /// </summary>
    public class MobileHmiPanel : MonoBehaviour
    {
        [Tooltip("Mobile base to control. Auto-found if left empty.")]
        public MobileBaseController controller;

        [Header("World placement")]
        public Vector3 panelWorldPosition = new Vector3(0.8f, 1.2f, 0.4f);
        public Vector3 panelWorldEuler = new Vector3(0f, 90f, 0f);
        public float panelWidth = 680f;
        public float panelHeight = 740f;
        public float worldScale = 0.0016f;

        private const float HeaderH = 64f;

        private Font _font;

        // structural
        private RectTransform _canvasRt;
        private GameObject _body;
        private Button _collapseBtn;
        private bool _collapsed;
        private bool _built;
        private Canvas _canvas;

        // widgets
        private Slider _speedSlider;
        private Text _speedLabel;
        private Text _statusLabel;
        private Text _speedReadout;
        private Button _stopButton;

        // drive command, set by the held arrows (-1 / 0 / +1)
        private int _driveForward;
        private int _driveTurn;     // + = turn left (CCW)

        // ===================================================================
        private void Start()
        {
            if (controller == null)
                controller = FindAnyObjectByType<MobileBaseController>();
            if (controller == null)
            {
                Debug.LogError("[MobileHmiPanel] No MobileBaseController in scene.",
                    this);
                return;
            }

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            EnsureEventSystem();
            BuildUi();
            _built = true;
        }

        private void Update()
        {
            if (!_built || controller == null) return;

            if (_canvas != null && _canvas.worldCamera == null && Camera.main != null)
                _canvas.worldCamera = Camera.main;

            // Push the held command to the base every frame — UNLESS a waypoint
            // route is running, in which case the follower owns the base and we
            // must not fight it with our (idle) 0,0 command.
            if (Placement.AppState.Mode != Placement.AppMode.Waypoints)
                controller.SetDrive(_driveForward, _driveTurn);

            if (_speedSlider != null && _speedLabel != null)
                _speedLabel.text = $"Speed  {controller.speedPercent:0}%";

            if (_speedReadout != null)
                _speedReadout.text = $"{controller.MeasuredSpeed:0.00}\nm/s";

            if (_statusLabel != null)
            {
                bool es = controller.EmergencyStopped;
                _statusLabel.text = es ? "STATE:  PROTECTIVE STOP"
                                       : "STATE:  NORMAL";
                _statusLabel.color = es ? new Color(1f, 0.4f, 0.3f)
                                        : new Color(0.5f, 1f, 0.6f);
                if (_stopButton != null)
                {
                    var img = _stopButton.GetComponent<Image>();
                    img.color = es ? new Color(0.45f, 0.12f, 0.12f)
                                    : new Color(0.75f, 0.15f, 0.15f);
                    _stopButton.GetComponentInChildren<Text>().text =
                        es ? "RESET" : "STOP";
                }
            }
        }

        // ------------------------------------------------------------------
        //  UI construction
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            var canvasGo = new GameObject("Scout_DrivePendant_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.AddComponent<Billboard>();

            _canvas = canvasGo.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = Camera.main;

            _canvasRt = _canvas.GetComponent<RectTransform>();
            _canvasRt.sizeDelta = new Vector2(panelWidth, panelHeight);
            _canvasRt.pivot = new Vector2(0.5f, 1f);
            float halfWorld = panelHeight * worldScale * 0.5f;
            _canvasRt.position = panelWorldPosition + new Vector3(0, halfWorld, 0);
            _canvasRt.rotation = Quaternion.Euler(panelWorldEuler);
            _canvasRt.localScale = Vector3.one * worldScale;

            // body
            _body = new GameObject("Body", typeof(RectTransform));
            _body.transform.SetParent(_canvasRt, false);
            var bodyRt = _body.GetComponent<RectTransform>();
            Stretch(bodyRt);
            var bg = NewImage("BG", bodyRt, new Color(0.10f, 0.11f, 0.13f, 0.96f));
            Stretch(bg.rectTransform);

            BuildDrivePad(bodyRt);
            BuildSpeedAndCommands(bodyRt);

            BuildHeader(_canvasRt);
        }

        private void BuildHeader(RectTransform parent)
        {
            var header = NewImage("Header", parent,
                new Color(0.18f, 0.40f, 0.30f, 1f));
            var hRt = header.rectTransform;
            hRt.anchorMin = new Vector2(0, 1);
            hRt.anchorMax = new Vector2(1, 1);
            hRt.pivot = new Vector2(0.5f, 1f);
            hRt.sizeDelta = new Vector2(0, HeaderH);
            hRt.anchoredPosition = Vector2.zero;

            var title = NewText("Title", header.rectTransform,
                "SCOUT V2  —  DRIVE", 22, TextAnchor.MiddleLeft);
            Anchor(title.rectTransform, 0, 0, 0, 1,
                new Vector2(20, 0), new Vector2(360, 0));

            _collapseBtn = NewButton("Collapse", header.rectTransform,
                "▴", 22, new Color(0.10f, 0.26f, 0.18f, 1f));
            var cRt = _collapseBtn.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(1, 1);
            cRt.anchorMax = new Vector2(1, 1);
            cRt.pivot = new Vector2(1, 1);
            cRt.sizeDelta = new Vector2(60, 48);
            cRt.anchoredPosition = new Vector2(-16, -8);
            _collapseBtn.onClick.AddListener(ToggleCollapse);
        }

        // Diamond drive pad: ▲ forward, ▼ back, ◄ turn left, ► turn right,
        // with a live speed read-out in the centre.
        private void BuildDrivePad(RectTransform parent)
        {
            var caption = NewText("Caption", parent,
                "DRIVE  —  hold an arrow", 20, TextAnchor.MiddleLeft);
            PlaceTopLeft(caption.rectTransform, 24, 74, 600, 30);

            var fwd = new Color(0.24f, 0.55f, 0.40f, 1f);
            var turn = new Color(0.26f, 0.45f, 0.62f, 1f);

            // Geometry: arrows 120×120, centre column at x = (W-120)/2.
            float cx = (panelWidth - 120f) / 2f;
            MakeHold(parent, "Forward", "▲", fwd, cx, 120, () => _driveForward = +1,
                     () => _driveForward = 0);
            MakeHold(parent, "Back", "▼", fwd, cx, 400, () => _driveForward = -1,
                     () => _driveForward = 0);
            MakeHold(parent, "Left", "◄", turn, cx - 150, 260, () => _driveTurn = +1,
                     () => _driveTurn = 0);
            MakeHold(parent, "Right", "►", turn, cx + 150, 260, () => _driveTurn = -1,
                     () => _driveTurn = 0);

            // centre live speed read-out
            _speedReadout = NewText("SpeedReadout", parent, "0.00\nm/s", 22,
                TextAnchor.MiddleCenter);
            PlaceTopLeft(_speedReadout.rectTransform, cx, 260, 120, 120);
        }

        private void BuildSpeedAndCommands(RectTransform parent)
        {
            // Three distinct horizontal bands below the drive pad — no overlap.
            // Speed row (label left, slider right).
            var speedRow = NewImage("SpeedRow", parent,
                new Color(0.16f, 0.17f, 0.20f, 1f));
            PlaceTopLeft(speedRow.rectTransform, 16, 540, panelWidth - 32, 64);
            _speedLabel = NewText("SpeedLbl", speedRow.rectTransform,
                "Speed  50%", 22, TextAnchor.MiddleLeft);
            Anchor(_speedLabel.rectTransform, 0, 0, 0, 1,
                new Vector2(16, 0), new Vector2(220, 0));
            _speedSlider = NewSlider("SpeedSlider", speedRow.rectTransform,
                1f, 100f, controller.speedPercent);
            // anchorMax.x MUST be 1 (stretch). Using 0 here gave the slider a
            // negative width earlier — it rendered but couldn't be dragged.
            Anchor(_speedSlider.GetComponent<RectTransform>(), 0, 0, 1, 1,
                new Vector2(220, 16), new Vector2(-24, -16));
            _speedSlider.onValueChanged.AddListener(v => controller.SetSpeedPercent(v));

            // Protective STOP (latching).
            _stopButton = NewButton("StopBtn", parent, "STOP", 24,
                new Color(0.75f, 0.15f, 0.15f, 1f));
            PlaceTopLeft(_stopButton.GetComponent<RectTransform>(),
                16, 616, panelWidth - 32, 56);
            _stopButton.onClick.AddListener(ToggleStop);

            // Status line.
            _statusLabel = NewText("Status", parent, "STATE:  NORMAL", 20,
                TextAnchor.MiddleCenter);
            PlaceTopLeft(_statusLabel.rectTransform, 16, 684, panelWidth - 32, 30);
        }

        // ------------------------------------------------------- collapse
        private void ToggleCollapse()
        {
            _collapsed = !_collapsed;
            if (_body != null) _body.SetActive(!_collapsed);
            _canvasRt.sizeDelta = new Vector2(
                panelWidth, _collapsed ? HeaderH : panelHeight);
            if (_collapseBtn != null)
                _collapseBtn.GetComponentInChildren<Text>().text =
                    _collapsed ? "▾" : "▴";
            if (_collapsed) { _driveForward = 0; _driveTurn = 0; }
        }

        private void ToggleStop()
        {
            if (controller.EmergencyStopped) controller.ResetEmergencyStop();
            else controller.EngageEmergencyStop();
        }

        // ------------------------------------------------------------------
        //  uGUI helpers (mirrors UR3HmiPanel)
        // ------------------------------------------------------------------
        private void MakeHold(RectTransform parent, string name, string glyph,
            Color color, float x, float yFromTop,
            System.Action onDown, System.Action onUp)
        {
            var b = NewButton(name, parent, glyph, 40, color);
            PlaceTopLeft(b.GetComponent<RectTransform>(), x, yFromTop, 120, 120);
            AddHold(b, onDown, onUp);
        }

        private Image NewImage(string n, Transform parent, Color c)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = c;
            return img;
        }

        private Text NewText(string n, Transform parent, string txt, int size,
            TextAnchor anchor)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = _font;
            t.text = txt;
            t.fontSize = size;
            t.alignment = anchor;
            t.color = new Color(0.92f, 0.94f, 0.97f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
        }

        private Button NewButton(string n, Transform parent, string label,
            int size, Color color)
        {
            var go = new GameObject(n,
                typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            var t = NewText("Label", go.transform, label, size,
                TextAnchor.MiddleCenter);
            Stretch(t.rectTransform);
            return btn;
        }

        private Slider NewSlider(string n, Transform parent, float min,
            float max, float value)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);

            var bg = NewImage("Background", go.transform,
                new Color(0.08f, 0.08f, 0.09f, 1f));
            Stretch(bg.rectTransform);
            bg.rectTransform.anchorMin = new Vector2(0, 0.35f);
            bg.rectTransform.anchorMax = new Vector2(1, 0.65f);

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(go.transform, false);
            var faRt = fillArea.GetComponent<RectTransform>();
            faRt.anchorMin = new Vector2(0, 0.35f);
            faRt.anchorMax = new Vector2(1, 0.65f);
            faRt.offsetMin = Vector2.zero;
            faRt.offsetMax = Vector2.zero;
            var fill = NewImage("Fill", fillArea.transform,
                new Color(0.30f, 0.78f, 0.55f, 1f));
            fill.rectTransform.sizeDelta = Vector2.zero;

            var handleArea = new GameObject("Handle Slide Area",
                typeof(RectTransform));
            handleArea.transform.SetParent(go.transform, false);
            var haRt = handleArea.GetComponent<RectTransform>();
            haRt.anchorMin = Vector2.zero;
            haRt.anchorMax = Vector2.one;
            haRt.offsetMin = Vector2.zero;
            haRt.offsetMax = Vector2.zero;
            var handle = NewImage("Handle", handleArea.transform,
                new Color(0.85f, 0.88f, 0.93f, 1f));
            handle.rectTransform.sizeDelta = new Vector2(22, 0);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = false;
            slider.SetValueWithoutNotify(value);
            return slider;
        }

        private static void AddHold(Button b, System.Action onDown,
            System.Action onUp)
        {
            var trg = b.gameObject.AddComponent<EventTrigger>();
            var d = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            d.callback.AddListener(_ => onDown());
            var u = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            u.callback.AddListener(_ => onUp());
            var x = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
            x.callback.AddListener(_ => onUp());
            trg.triggers.Add(d);
            trg.triggers.Add(u);
            trg.triggers.Add(x);
        }

        private static void Stretch(RectTransform rt, float l = 0,
            float r = 0, float t = 0, float b = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(l, b);
            rt.offsetMax = new Vector2(-r, -t);
        }

        private static void Anchor(RectTransform rt,
            float aMinX, float aMinY, float aMaxX, float aMaxY,
            Vector2 offMin, Vector2 offMax)
        {
            rt.anchorMin = new Vector2(aMinX, aMinY);
            rt.anchorMax = new Vector2(aMaxX, aMaxY);
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
        }

        private static void PlaceTopLeft(RectTransform rt, float x,
            float yFromTop, float w, float h)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
        }

        private static void EnsureEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
#if ENABLE_INPUT_SYSTEM
            go.AddComponent<InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
