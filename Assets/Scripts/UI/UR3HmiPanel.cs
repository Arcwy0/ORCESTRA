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
    /// Virtual UR teach pendant. World-space canvas built procedurally so
    /// the same panel works on PC and in VR. Has two modes:
    ///   • <b>JOINTS</b> — 6 sliders + jog buttons (original behaviour).
    ///   • <b>TCP</b>    — ±X / ±Y / ±Z jog buttons; each tick computes a
    ///                     new target TCP and runs <see cref="CcdIkSolver"/>
    ///                     for it, then pushes the resulting joint angles
    ///                     through the controller.
    /// A header collapse button shrinks the panel to just the title bar.
    /// A <see cref="Billboard"/> component keeps the whole pendant facing
    /// the operator from any angle.
    /// </summary>
    public class UR3HmiPanel : MonoBehaviour
    {
        [Tooltip("Robot to control. Auto-found if left empty.")]
        public UR3JointController controller;

        [Header("World placement")]
        public Vector3 panelWorldPosition = new Vector3(0.8f, 1.2f, 0.4f);
        public Vector3 panelWorldEuler = new Vector3(0f, 90f, 0f);
        public float panelWidth = 900f;
        public float panelHeight = 760f;
        public float worldScale = 0.0016f;

        [Header("TCP mode")]
        [Tooltip("TCP jog speed at 100% speed (m/s).")]
        public float tcpJogSpeed = 0.10f;

        private static readonly Color ActiveTabColor =
            new Color(0.30f, 0.62f, 0.95f, 1f);
        private static readonly Color InactiveTabColor =
            new Color(0.16f, 0.30f, 0.50f, 1f);
        private const float HeaderH = 64f;

        private enum Mode { Joints, Tcp }
        private Mode _mode = Mode.Joints;

        private Font _font;

        // structural
        private RectTransform _canvasRt;
        private GameObject _body;
        private GameObject _jointsView, _tcpView;
        private Button _jointsTab, _tcpTab, _collapseBtn;
        private bool _collapsed;
        private bool _built;

        // joints widgets
        private readonly Slider[] _sliders = new Slider[6];
        private readonly Text[] _values = new Text[6];

        // shared
        private Slider _speedSlider;
        private Text _speedLabel;
        private Text _statusLabel;
        private Button _stopButton;

        // tcp widgets
        private Transform _tcp;
        private readonly Text[] _tcpValues = new Text[3];
        private readonly int[] _tcpJogDir = new int[3];     // -1 / 0 / +1

        // canvas ref kept so we can defer worldCamera assignment until the
        // main camera is available (null at Start in some VR rigs)
        private Canvas _canvas;

        private void Start()
        {
            if (controller == null)
                controller = FindAnyObjectByType<UR3JointController>();
            if (controller == null)
            {
                Debug.LogError("[UR3HmiPanel] No UR3JointController in scene.",
                    this);
                return;
            }

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

            EnsureEventSystem();
            BuildUi();
            _tcp = CcdIkSolver.FindTcp(controller.transform);
            Debug.Log($"[UR3HmiPanel] TCP resolved to '{_tcp.name}'" +
                      $"  world pos = {_tcp.position}", _tcp);
            ApplyMode();
            _built = true;
        }

        // ------------------------------------------------------------------
        private void Update()
        {
            if (!_built || controller == null) return;

            // World-space canvas needs an event camera; Camera.main may be
            // null at Start() in VR rigs that initialise the camera later.
            if (_canvas != null && _canvas.worldCamera == null && Camera.main != null)
            {
                _canvas.worldCamera = Camera.main;
                Debug.Log("[UR3HmiPanel] Event camera assigned late: " +
                          Camera.main.name);
            }

            // Joint read-outs are always live so the operator can see the
            // arm's state regardless of which mode is active.
            for (int i = 0; i < controller.JointCount; i++)
            {
                float measured = controller.GetMeasuredDeg(i);
                if (_values[i] != null)
                    _values[i].text = $"{measured,7:0.0}°";
                if (_sliders[i] != null)
                    _sliders[i].SetValueWithoutNotify(controller.GetGoalDeg(i));
            }

            if (_speedSlider != null && _speedLabel != null)
                _speedLabel.text = $"Speed  {controller.speedPercent:0}%";

            if (_statusLabel != null)
            {
                bool es = controller.EmergencyStopped;
                _statusLabel.text = es
                    ? "STATE:  PROTECTIVE STOP"
                    : "STATE:  NORMAL";
                _statusLabel.color = es
                    ? new Color(1f, 0.4f, 0.3f)
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

            if (_mode == Mode.Tcp) UpdateTcpJog();
        }

        // While a TCP arrow is held we ask for a small world-space move this
        // frame, convert it to joint increments with a read-only Jacobian
        // step (no physics-state writes — that was what made the arm sag),
        // and command the joints relative to their MEASURED pose so there is
        // no runaway if the drives momentarily lag.
        private void UpdateTcpJog()
        {
            if (_tcp == null) return;

            Vector3 p = _tcp.position;
            if (_tcpValues[0] != null) _tcpValues[0].text = $"X  {p.x,7:0.000} m";
            if (_tcpValues[1] != null) _tcpValues[1].text = $"Y  {p.y,7:0.000} m";
            if (_tcpValues[2] != null) _tcpValues[2].text = $"Z  {p.z,7:0.000} m";

            if (controller.EmergencyStopped) return;

            Vector3 move = Vector3.zero;
            float step = tcpJogSpeed * (controller.speedPercent / 100f)
                         * Time.deltaTime;
            if (_tcpJogDir[0] != 0) move.x = _tcpJogDir[0] * step;
            if (_tcpJogDir[1] != 0) move.y = _tcpJogDir[1] * step;
            if (_tcpJogDir[2] != 0) move.z = _tcpJogDir[2] * step;
            if (move.sqrMagnitude < 1e-12f) return;

            var dq = CcdIkSolver.TcpJogDeltasDeg(controller, _tcp, move);

            // Accumulate the per-frame increments onto the COMMANDED goal,
            // never onto the MEASURED pose. The drives are heavily damped, so
            // the measured angle trails the command by far more than one
            // frame's dq (≈0.4° lag vs ≈0.008° dq). Adding dq to the lagging
            // measured value put the new goal *behind* the current command,
            // so the ramp drove every joint backwards and the arm sank on any
            // press. Building on the goal advances it monotonically at the
            // requested speed. The lead clamp keeps the goal from winding up
            // far ahead of the arm if a joint ever stalls (anti-runaway).
            const float maxLeadDeg = 5f;
            for (int j = 0; j < 6; j++)
            {
                float target = controller.GetGoalDeg(j) + dq[j];
                float measured = controller.GetMeasuredDeg(j);
                target = Mathf.Clamp(target,
                    measured - maxLeadDeg, measured + maxLeadDeg);
                controller.SetGoalDeg(j, target);
            }
        }

        // ------------------------------------------------------------------
        //  UI construction
        // ------------------------------------------------------------------
        private void BuildUi()
        {
            var canvasGo = new GameObject("UR3_TeachPendant_Canvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            canvasGo.AddComponent<Billboard>();   // always face operator

            _canvas = canvasGo.GetComponent<Canvas>();
            var canvas = _canvas;
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;   // may be null in VR; retried in Update

            _canvasRt = canvas.GetComponent<RectTransform>();
            _canvasRt.sizeDelta = new Vector2(panelWidth, panelHeight);
            // Pivot at top so collapsing shrinks the panel downward — the
            // header stays where the operator put it.
            _canvasRt.pivot = new Vector2(0.5f, 1f);
            float halfWorld = panelHeight * worldScale * 0.5f;
            _canvasRt.position = panelWorldPosition + new Vector3(0, halfWorld, 0);
            _canvasRt.rotation = Quaternion.Euler(panelWorldEuler);
            _canvasRt.localScale = Vector3.one * worldScale;

            // ---- body (everything except header) -------------------------
            // Sibling order = render order; body first, header last → header
            // draws on top of body.
            _body = new GameObject("Body", typeof(RectTransform));
            _body.transform.SetParent(_canvasRt, false);
            var bodyRt = _body.GetComponent<RectTransform>();
            Stretch(bodyRt);

            var bg = NewImage("BG", bodyRt,
                new Color(0.10f, 0.11f, 0.13f, 0.96f));
            Stretch(bg.rectTransform);

            BuildJointsView(bodyRt);
            BuildTcpView(bodyRt);
            BuildSpeedAndCommands(bodyRt);

            // ---- header (always visible) ---------------------------------
            BuildHeader(_canvasRt);
        }

        private void BuildHeader(RectTransform parent)
        {
            var header = NewImage("Header", parent,
                new Color(0.16f, 0.34f, 0.52f, 1f));
            var hRt = header.rectTransform;
            hRt.anchorMin = new Vector2(0, 1);
            hRt.anchorMax = new Vector2(1, 1);
            hRt.pivot = new Vector2(0.5f, 1f);
            hRt.sizeDelta = new Vector2(0, HeaderH);
            hRt.anchoredPosition = Vector2.zero;

            var title = NewText("Title", header.rectTransform,
                "UR3  —  MOVE", 22, TextAnchor.MiddleLeft);
            Anchor(title.rectTransform, 0, 0, 0, 1,
                new Vector2(20, 0), new Vector2(220, 0));

            _jointsTab = NewButton("JointsTab", header.rectTransform,
                "JOINTS", 20, ActiveTabColor);
            Anchor(_jointsTab.GetComponent<RectTransform>(), 0, 0, 0, 1,
                new Vector2(240, 10), new Vector2(410, -10));
            _jointsTab.onClick.AddListener(() => SetMode(Mode.Joints));

            _tcpTab = NewButton("TcpTab", header.rectTransform,
                "TCP", 20, InactiveTabColor);
            Anchor(_tcpTab.GetComponent<RectTransform>(), 0, 0, 0, 1,
                new Vector2(420, 10), new Vector2(590, -10));
            _tcpTab.onClick.AddListener(() => SetMode(Mode.Tcp));

            _collapseBtn = NewButton("Collapse", header.rectTransform,
                "▴", 22, new Color(0.10f, 0.20f, 0.34f, 1f));
            var cRt = _collapseBtn.GetComponent<RectTransform>();
            cRt.anchorMin = new Vector2(1, 1);
            cRt.anchorMax = new Vector2(1, 1);
            cRt.pivot = new Vector2(1, 1);
            cRt.sizeDelta = new Vector2(60, 48);
            cRt.anchoredPosition = new Vector2(-16, -8);
            _collapseBtn.onClick.AddListener(ToggleCollapse);
        }

        private void BuildJointsView(RectTransform parent)
        {
            _jointsView = new GameObject("JointsView", typeof(RectTransform));
            _jointsView.transform.SetParent(parent, false);
            var jvRt = _jointsView.GetComponent<RectTransform>();
            Stretch(jvRt);

            float top = -88f;
            const float rowH = 78f;

            for (int i = 0; i < 6; i++)
            {
                int idx = i;
                float lo = controller.GetLowerLimitDeg(i);
                float hi = controller.GetUpperLimitDeg(i);

                var row = NewImage($"Row{i}", jvRt,
                    new Color(0.16f, 0.17f, 0.20f, 1f));
                RowRect(row.rectTransform, top, rowH - 8);

                var name = NewText($"Name{i}", row.rectTransform,
                    UR3JointController.JointDisplayNames[i], 22,
                    TextAnchor.MiddleLeft);
                Anchor(name.rectTransform, 0, 0, 0, 1,
                    new Vector2(16, 0), new Vector2(150, 0));

                var minus = NewButton($"Minus{i}", row.rectTransform,
                    "−", 30, new Color(0.30f, 0.32f, 0.38f, 1f));
                Anchor(minus.GetComponent<RectTransform>(), 0, 0, 0, 1,
                    new Vector2(160, 8), new Vector2(220, -8));
                AddHold(minus, () => controller.StartJog(idx, -1),
                                () => controller.StopJog(idx));

                var slider = NewSlider($"Slider{i}",
                    row.rectTransform, lo, hi, controller.GetGoalDeg(i));
                Anchor(slider.GetComponent<RectTransform>(), 0, 0, 0, 1,
                    new Vector2(232, 18), new Vector2(660, -18));
                slider.onValueChanged.AddListener(
                    v => controller.SetGoalDeg(idx, v));
                _sliders[i] = slider;

                var plus = NewButton($"Plus{i}", row.rectTransform, "+", 30,
                    new Color(0.30f, 0.32f, 0.38f, 1f));
                Anchor(plus.GetComponent<RectTransform>(), 0, 0, 0, 1,
                    new Vector2(672, 8), new Vector2(732, -8));
                AddHold(plus, () => controller.StartJog(idx, +1),
                               () => controller.StopJog(idx));

                var val = NewText($"Val{i}", row.rectTransform, "0.0°", 22,
                    TextAnchor.MiddleRight);
                Anchor(val.rectTransform, 0, 0, 0, 1,
                    new Vector2(744, 0), new Vector2(884, 0));
                _values[i] = val;

                top -= rowH;
            }
        }

        // Arrow pad in the style of the UR "Move" screen: a vertical Y
        // up/down pair on the left, an X/Z ground-plane diamond on the
        // right, plus live X/Y/Z read-outs along the bottom.
        private void BuildTcpView(RectTransform parent)
        {
            _tcpView = new GameObject("TcpView", typeof(RectTransform));
            _tcpView.transform.SetParent(parent, false);
            var tvRt = _tcpView.GetComponent<RectTransform>();
            Stretch(tvRt);

            var arrow = new Color(0.26f, 0.55f, 0.85f, 1f);

            var caption = NewText("TcpCaption", tvRt,
                "TCP JOG  —  hold an arrow (world frame)", 20,
                TextAnchor.MiddleLeft);
            PlaceTopLeft(caption.rectTransform, 24, 74, 760, 30);

            // --- vertical translation (Y), left block --------------------
            var yLbl = NewText("YLbl", tvRt, "UP / DOWN", 16,
                TextAnchor.MiddleCenter);
            PlaceTopLeft(yLbl.rectTransform, 40, 124, 200, 24);
            MakeArrow(tvRt, "Yplus", "▲", arrow, 85, 152, 1, +1);
            MakeArrow(tvRt, "Yminus", "▼", arrow, 85, 292, 1, -1);

            // --- horizontal plane (X left/right, Z fwd/back) diamond -----
            var planeLbl = NewText("PlaneLbl", tvRt, "FORWARD / SIDE", 16,
                TextAnchor.MiddleCenter);
            PlaceTopLeft(planeLbl.rectTransform, 470, 266, 170, 24);
            MakeArrow(tvRt, "Zplus", "▲", arrow, 495, 152, 2, +1);  // forward
            MakeArrow(tvRt, "Xminus", "◄", arrow, 372, 270, 0, -1); // left
            MakeArrow(tvRt, "Xplus", "►", arrow, 618, 270, 0, +1);  // right
            MakeArrow(tvRt, "Zminus", "▼", arrow, 495, 388, 2, -1); // back

            // --- live read-outs ------------------------------------------
            _tcpValues[0] = NewText("ValX", tvRt, "X  0.000 m", 22,
                TextAnchor.MiddleLeft);
            PlaceTopLeft(_tcpValues[0].rectTransform, 40, 524, 280, 36);
            _tcpValues[1] = NewText("ValY", tvRt, "Y  0.000 m", 22,
                TextAnchor.MiddleLeft);
            PlaceTopLeft(_tcpValues[1].rectTransform, 330, 524, 280, 36);
            _tcpValues[2] = NewText("ValZ", tvRt, "Z  0.000 m", 22,
                TextAnchor.MiddleLeft);
            PlaceTopLeft(_tcpValues[2].rectTransform, 620, 524, 260, 36);
        }

        // Press-and-hold arrow button that jogs one TCP axis while held.
        private void MakeArrow(RectTransform parent, string name, string glyph,
            Color color, float x, float yFromTop, int axis, int sign)
        {
            var b = NewButton(name, parent, glyph, 40, color);
            PlaceTopLeft(b.GetComponent<RectTransform>(), x, yFromTop, 110, 110);
            AddHold(b, () => _tcpJogDir[axis] = sign, () => _tcpJogDir[axis] = 0);
        }

        private void BuildSpeedAndCommands(RectTransform parent)
        {
            // Fixed vertical positions — same regardless of which view is
            // active above. (TCP view leaves a gap; joints view fills it.)
            const float rowH = 78f;
            float top = -88f - 6 * rowH - 12f;

            var speedRow = NewImage("SpeedRow", parent,
                new Color(0.16f, 0.17f, 0.20f, 1f));
            RowRect(speedRow.rectTransform, top, rowH - 8);
            _speedLabel = NewText("SpeedLbl", speedRow.rectTransform,
                "Speed  25%", 22, TextAnchor.MiddleLeft);
            Anchor(_speedLabel.rectTransform, 0, 0, 0, 1,
                new Vector2(16, 0), new Vector2(220, 0));
            _speedSlider = NewSlider("SpeedSlider", speedRow.rectTransform,
                1f, 100f, controller.speedPercent);
            Anchor(_speedSlider.GetComponent<RectTransform>(), 0, 0, 0, 1,
                new Vector2(232, 18), new Vector2(732, -18));
            _speedSlider.onValueChanged.AddListener(
                v => controller.SetSpeedPercent(v));

            top -= rowH + 4f;
            var home = NewButton("HomeBtn", parent, "HOME", 22,
                new Color(0.22f, 0.40f, 0.30f, 1f));
            CmdRect(home.GetComponent<RectTransform>(), top, 0);
            home.onClick.AddListener(() => controller.GoHome());

            var zero = NewButton("ZeroBtn", parent, "ZERO", 22,
                new Color(0.22f, 0.35f, 0.45f, 1f));
            CmdRect(zero.GetComponent<RectTransform>(), top, 1);
            zero.onClick.AddListener(() => controller.GoHome());

            _stopButton = NewButton("StopBtn", parent, "STOP", 24,
                new Color(0.75f, 0.15f, 0.15f, 1f));
            CmdRect(_stopButton.GetComponent<RectTransform>(), top, 2);
            _stopButton.onClick.AddListener(ToggleStop);

            var reset = NewButton("ResetBtn", parent, "RESET", 22,
                new Color(0.35f, 0.35f, 0.40f, 1f));
            CmdRect(reset.GetComponent<RectTransform>(), top, 3);
            reset.onClick.AddListener(() => controller.ResetEmergencyStop());

            _statusLabel = NewText("Status", parent, "STATE:  NORMAL", 20,
                TextAnchor.MiddleCenter);
            var sRt = _statusLabel.rectTransform;
            sRt.anchorMin = new Vector2(0, 0);
            sRt.anchorMax = new Vector2(1, 0);
            sRt.pivot = new Vector2(0.5f, 0f);
            sRt.anchoredPosition = new Vector2(0, 8);
            sRt.sizeDelta = new Vector2(-32, 30);
        }

        // ----------------------------------------------------- mode toggle
        private void SetMode(Mode m)
        {
            _mode = m;
            ApplyMode();
        }

        private void ApplyMode()
        {
            if (_jointsView != null) _jointsView.SetActive(_mode == Mode.Joints);
            if (_tcpView != null) _tcpView.SetActive(_mode == Mode.Tcp);
            if (_jointsTab != null)
                _jointsTab.GetComponent<Image>().color =
                    _mode == Mode.Joints ? ActiveTabColor : InactiveTabColor;
            if (_tcpTab != null)
                _tcpTab.GetComponent<Image>().color =
                    _mode == Mode.Tcp ? ActiveTabColor : InactiveTabColor;
            // Drop any in-flight TCP jog when leaving TCP mode.
            if (_mode != Mode.Tcp)
                for (int a = 0; a < 3; a++) _tcpJogDir[a] = 0;
            // Entering TCP mode: re-sync the controller goal to the live pose
            // so the first jog accumulates from where the arm actually is and
            // the lead-clamp can't snap a stale goal into a jolt.
            else if (controller != null)
                controller.SnapStateToMeasured();
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
            if (_collapsed)
                for (int a = 0; a < 3; a++) _tcpJogDir[a] = 0;
        }

        private void ToggleStop()
        {
            if (controller.EmergencyStopped) controller.ResetEmergencyStop();
            else controller.EngageEmergencyStop();
        }

        // ------------------------------------------------------------------
        //  uGUI helpers
        // ------------------------------------------------------------------
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
            var go = new GameObject(n,
                typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);

            var bg = NewImage("Background", go.transform,
                new Color(0.08f, 0.08f, 0.09f, 1f));
            Stretch(bg.rectTransform, 0, 0, 0, 0);
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
                new Color(0.30f, 0.62f, 0.95f, 1f));
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
            var d = new EventTrigger.Entry
                { eventID = EventTriggerType.PointerDown };
            d.callback.AddListener(_ => onDown());
            var u = new EventTrigger.Entry
                { eventID = EventTriggerType.PointerUp };
            u.callback.AddListener(_ => onUp());
            var x = new EventTrigger.Entry
                { eventID = EventTriggerType.PointerExit };
            x.callback.AddListener(_ => onUp());
            trg.triggers.Add(d);
            trg.triggers.Add(u);
            trg.triggers.Add(x);
        }

        // ---- layout shortcuts -------------------------------------------
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

        // Place a rect a fixed distance (px) from the panel's top-left.
        private static void PlaceTopLeft(RectTransform rt, float x,
            float yFromTop, float w, float h)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
        }

        private void RowRect(RectTransform rt, float top, float height)
        {
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(16, 0);
            rt.offsetMax = new Vector2(-16, 0);
            rt.anchoredPosition = new Vector2(0, top);
            rt.sizeDelta = new Vector2(-32, height);
        }

        private void CmdRect(RectTransform rt, float top, int col)
        {
            float pad = 16f, gap = 12f;
            float usable = panelWidth - pad * 2 - gap * 3;
            float w = usable / 4f;
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w, 64);
            rt.anchoredPosition = new Vector2(pad + col * (w + gap), top);
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
