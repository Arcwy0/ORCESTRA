using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using VRInteraction.Robot;
using VRInteraction.UI;

namespace VRInteraction.Placement
{
    /// <summary>
    /// Operator flow for placing a digital twin:
    ///   Idle → open catalog → pick model → aim ghost on the floor →
    ///   click to drop → fine-tune X/Y/Z/Yaw → confirm →
    ///   real robot spawns + its own teach pendant appears next to it.
    /// Multiple robots can be placed; clicking a placed robot in Idle
    /// re-opens fine-tune so it can be moved/rotated again.
    /// </summary>
    public class RobotPlacementController : MonoBehaviour
    {
        public RobotCatalog catalog;
        [Tooltip("World Y of the floor the ghost slides on.")]
        public float groundY = 0f;
        [Tooltip("Fine-tune nudge sizes in metres.")]
        public float smallStep = 0.01f;
        public float bigStep = 0.10f;
        [Tooltip("Fine-tune yaw nudge sizes in degrees.")]
        public float yawSmallStep = 5f;
        public float yawBigStep = 45f;

        private static readonly Color GhostCyan =
            new Color(0.30f, 0.85f, 1f, 0.35f);
        private static readonly Color GhostRed =
            new Color(1.00f, 0.32f, 0.26f, 0.42f);

        private enum State { Idle, Catalog, Aiming, FineTune }
        private State _state = State.Idle;

        private PlacementPointer _pointer;
        private LineRenderer _laser;

        private int _selected = -1;
        private GameObject _ghost;
        private GameObject _reach;
        private Vector3 _ghostPos;
        private float _ghostYaw;
        private float _spawnOffset;     // vertical lift so wheels/base rest on floor
        private int _placedCount;

        // One record per robot the operator has placed.
        private class Placed
        {
            public GameObject robot;
            public GameObject pendant;
            public int entryIndex;
            public Vector3 pos;
            public float yaw;
        }
        private readonly List<Placed> _placed = new List<Placed>();
        private Placed _relocating;   // non-null while moving an existing robot

        // UI
        private GameObject _catalogPanel;
        private GameObject _finePanel;
        private Text _hint;
        private Text _xVal, _yVal, _zVal, _yawVal;

        /// <summary>
        /// Replace the active pointer at runtime (called by RigModeManager when
        /// switching between Desktop and XR mode).
        /// </summary>
        public void SetPointer(PlacementPointer p) => _pointer = p;

        private void Start()
        {
            _pointer = FindAnyObjectByType<PlacementPointer>();
            if (_pointer == null)
            {
                var camGo = Camera.main != null ? Camera.main.gameObject : gameObject;
                _pointer = camGo.AddComponent<DesktopMousePointer>();
            }

            var g = GameObject.Find("Ground");
            if (g != null) groundY = g.transform.position.y;

            UiKit.EnsureEventSystem();
            BuildUi();
            CreateLaser();
            SetState(State.Idle);
        }

        private void Update()
        {
            if (_pointer == null) return;

            if (_state == State.Idle)
            {
                UpdateLaser(Vector3.zero, Vector3.zero, false);
                if (_pointer.ConfirmPressedThisFrame())
                    TrySelectPlacedForRelocate();
                return;
            }

            if (_state == State.Aiming)
            {
                if (TryGroundPoint(out var p))
                {
                    _ghostPos = p;
                    ApplyGhostTransform();
                    UpdateLaser(_pointer.GetRay().origin, p, true);
                }
                else UpdateLaser(Vector3.zero, Vector3.zero, false);

                if (_pointer.ConfirmPressedThisFrame())
                    EnterFineTune();
                else if (_pointer.CancelPressedThisFrame())
                    CancelPlacement();
            }
            else
            {
                UpdateLaser(Vector3.zero, Vector3.zero, false);
                if (_state == State.FineTune &&
                    _pointer.CancelPressedThisFrame())
                    CancelPlacement();
            }

            if (_state == State.Aiming || _state == State.FineTune)
            {
                UpdateReach();
                GhostBuilder.SetTint(_ghost,
                    GhostOverlaps() ? GhostRed : GhostCyan);
            }
        }

        // ---------------------------------------------------------------- flow
        private void OpenCatalog()
        {
            if (AppState.Mode == AppMode.Waypoints)
            {
                SetHint("Finish the waypoint task first.");
                return;
            }
            if (catalog == null || catalog.entries.Count == 0)
            {
                SetHint("Catalog is empty.");
                return;
            }
            SetState(State.Catalog);
        }

        private void SelectModel(int idx)
        {
            _selected = idx;
            _relocating = null;
            _ghostYaw = 0f;
            var entry = catalog.Get(idx);
            if (entry == null || entry.prefab == null)
            {
                SetHint("This catalog entry has no prefab.");
                return;
            }
            _spawnOffset = entry.spawnHeightOffset;
            DestroyGhost();
            _ghost = GhostBuilder.CreateGhost(entry.prefab, GhostCyan);
            _ghostPos = new Vector3(0, groundY, 0);
            ApplyGhostTransform();
            if (entry.kind != RobotKind.Mobile) BuildReach(entry);
            SetState(State.Aiming);
        }

        private void EnterFineTune()
        {
            SetState(State.FineTune);
            RefreshFineValues();
        }

        private void Nudge(int axis, float delta)
        {
            if (_state != State.FineTune) return;
            if (axis == 0) _ghostPos.x += delta;
            if (axis == 1) _ghostPos.y += delta;
            if (axis == 2) _ghostPos.z += delta;
            ApplyGhostTransform();
            RefreshFineValues();
        }

        private void NudgeYaw(float delta)
        {
            if (_state != State.FineTune) return;
            _ghostYaw = Mathf.Repeat(_ghostYaw + delta, 360f);
            ApplyGhostTransform();
            RefreshFineValues();
        }

        private void ApplyGhostTransform()
        {
            if (_ghost != null)
                _ghost.transform.SetPositionAndRotation(
                    _ghostPos + Vector3.up * _spawnOffset,
                    Quaternion.Euler(0f, _ghostYaw, 0f));
        }

        // -------------------------------------------------------------- confirm
        private void ConfirmPlacement()
        {
            var entry = catalog.Get(_selected);
            if (entry == null || entry.prefab == null) { CancelPlacement(); return; }

            var rot = Quaternion.Euler(0f, _ghostYaw, 0f);
            // Lift so wheels (mobile) / base (arm) rest on the floor.
            var spawnPos = _ghostPos + Vector3.up * _spawnOffset;

            if (_relocating != null)
            {
                // Move an already-placed robot to the new pose.
                var p = _relocating;
                PlaceArticulation(p.robot, spawnPos, rot);
                StartCoroutine(DeferredSnap(p.robot, spawnPos, rot));

                if (p.pendant != null) Destroy(p.pendant);
                p.pendant = SpawnPendant(p.robot, entry);
                p.pos = _ghostPos;
                p.yaw = _ghostYaw;

                _relocating = null;
                CleanupAfterPlace();
                SetHint($"Moved {p.robot.name}.");
                return;
            }

            // Fresh placement. Instantiate ALREADY positioned so the
            // ArticulationBody chain builds at the correct world pose, then
            // re-assert the root pose (immovable AB roots ignore plain
            // transform writes once the physics scene has baked them).
            var robot = Instantiate(entry.prefab, spawnPos, rot);
            robot.name = $"{entry.displayName}_{_placedCount + 1}";
            PlaceArticulation(robot, spawnPos, rot);
            StartCoroutine(DeferredSnap(robot, spawnPos, rot));

            // Tag it so the waypoint / scenario tools can find it.
            var info = robot.GetComponent<PlacedRobot>();
            if (info == null) info = robot.AddComponent<PlacedRobot>();
            info.kind = entry.kind;
            info.displayName = robot.name;
            info.reachRadius = entry.reachRadius;
            info.reachCenterLocal = entry.reachCenterLocal;

            var pendant = SpawnPendant(robot, entry);
            _placed.Add(new Placed
            {
                robot = robot,
                pendant = pendant,
                entryIndex = _selected,
                pos = _ghostPos,
                yaw = _ghostYaw
            });
            _placedCount++;

            CleanupAfterPlace();
            SetHint($"Placed {robot.name}. Open the catalog to add another, " +
                    "or click a placed robot to move it.");
        }

        private void CleanupAfterPlace()
        {
            DestroyGhost();
            DestroyReach();
            _selected = -1;
            _ghostYaw = 0f;
            SetState(State.Idle);
        }

        private void CancelPlacement()
        {
            _relocating = null;          // existing robot was never moved
            DestroyGhost();
            DestroyReach();
            _selected = -1;
            _ghostYaw = 0f;
            SetState(State.Idle);
        }

        // ------------------------------------------------------------ relocate
        private void TrySelectPlacedForRelocate()
        {
            if (AppState.Mode != AppMode.Idle) return;   // waypoint tool busy
            if (_placed.Count == 0) return;
            var hits = Physics.RaycastAll(_pointer.GetRay(), 100f);
            foreach (var h in hits)
            {
                foreach (var p in _placed)
                {
                    if (p.robot == null) continue;
                    if (h.transform == p.robot.transform ||
                        h.transform.IsChildOf(p.robot.transform))
                    {
                        BeginRelocate(p);
                        return;
                    }
                }
            }
        }

        private void BeginRelocate(Placed p)
        {
            var entry = catalog.Get(p.entryIndex);
            if (entry == null || entry.prefab == null) return;

            _relocating = p;
            _selected = p.entryIndex;
            _ghostPos = p.pos;
            _ghostYaw = p.yaw;
            _spawnOffset = entry.spawnHeightOffset;

            DestroyGhost();
            _ghost = GhostBuilder.CreateGhost(entry.prefab, GhostCyan);
            ApplyGhostTransform();
            if (entry.kind != RobotKind.Mobile) BuildReach(entry);

            SetState(State.FineTune);
            RefreshFineValues();
            SetHint($"Moving {p.robot.name}. Adjust X / Y / Z / Yaw, " +
                    "then CONFIRM. Esc = leave it where it is.");
        }

        // ----------------------------------------------------- articulation
        private static void PlaceArticulation(
            GameObject robot, Vector3 pos, Quaternion rot)
        {
            robot.transform.SetPositionAndRotation(pos, rot);
            var rootAb = System.Array.Find(
                robot.GetComponentsInChildren<ArticulationBody>(true),
                a => a.isRoot);
            if (rootAb != null) rootAb.TeleportRoot(pos, rot);
        }

        // The articulation is only registered with the physics scene on the
        // next simulation step; re-assert the pose then so an immovable root
        // does not snap back to where it was instantiated.
        private IEnumerator DeferredSnap(
            GameObject robot, Vector3 pos, Quaternion rot)
        {
            yield return new WaitForFixedUpdate();
            if (robot != null) PlaceArticulation(robot, pos, rot);
        }

        private GameObject SpawnPendant(GameObject robot, RobotCatalog.Entry entry)
        {
            var hmiGo = new GameObject($"{robot.name}_Pendant");
            Vector3 pos = robot.transform.TransformPoint(entry.hmiLocalOffset);
            Vector3 euler = (robot.transform.rotation *
                             Quaternion.Euler(entry.hmiLocalEuler)).eulerAngles;

            if (entry.kind == RobotKind.Mobile)
            {
                var panel = hmiGo.AddComponent<UI.MobileHmiPanel>();
                panel.controller = robot.GetComponentInChildren<MobileBaseController>();
                panel.panelWorldPosition = pos;
                panel.panelWorldEuler = euler;
            }
            else
            {
                var panel = hmiGo.AddComponent<UI.UR3HmiPanel>();
                panel.controller = robot.GetComponentInChildren<UR3JointController>();
                panel.panelWorldPosition = pos;
                panel.panelWorldEuler = euler;
            }
            return hmiGo;
        }

        // --------------------------------------------------- reach envelope
        private void BuildReach(RobotCatalog.Entry entry)
        {
            DestroyReach();
            float r = Mathf.Max(0.05f, entry.reachRadius);
            _reach = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _reach.name = "ReachEnvelope";
            var col = _reach.GetComponent<Collider>();
            if (col != null) Destroy(col);
            // Cylinder primitive: radius 0.5, height 2 at scale 1.
            _reach.transform.localScale = new Vector3(r * 2f, 0.0015f, r * 2f);
            var mr = _reach.GetComponent<MeshRenderer>();
            mr.sharedMaterial = GhostBuilder.TransparentMaterial(
                new Color(0.25f, 0.70f, 1f, 0.10f));
            mr.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            UpdateReach();
        }

        private void UpdateReach()
        {
            if (_reach != null)
                _reach.transform.position = new Vector3(
                    _ghostPos.x, groundY + 0.002f, _ghostPos.z);
        }

        private void DestroyReach()
        {
            if (_reach != null) Destroy(_reach);
            _reach = null;
        }

        // --------------------------------------------------- overlap warning
        private bool GhostOverlaps()
        {
            if (_ghost == null) return false;

            Bounds b = default;
            bool any = false;
            foreach (var r in _ghost.GetComponentsInChildren<Renderer>())
            {
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!any) return false;

            var hits = Physics.OverlapBox(
                b.center, b.extents * 0.92f, Quaternion.identity);
            foreach (var h in hits)
            {
                if (h == null) continue;
                foreach (var p in _placed)
                {
                    if (p.robot == null) continue;
                    if (_relocating != null && p == _relocating) continue;
                    if (h.transform == p.robot.transform ||
                        h.transform.IsChildOf(p.robot.transform))
                        return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- util
        private bool TryGroundPoint(out Vector3 point)
        {
            var ray = _pointer.GetRay();
            var plane = new Plane(Vector3.up, new Vector3(0, groundY, 0));
            if (plane.Raycast(ray, out float enter) && enter > 0f)
            {
                point = ray.GetPoint(enter);
                return true;
            }
            point = Vector3.zero;
            return false;
        }

        private void DestroyGhost()
        {
            if (_ghost != null) Destroy(_ghost);
            _ghost = null;
        }

        private void SetState(State s)
        {
            _state = s;
            if (_catalogPanel != null)
                _catalogPanel.SetActive(s == State.Catalog);
            if (_finePanel != null)
                _finePanel.SetActive(s == State.FineTune);

            // Claim / release the shared interaction lock (do not clobber the
            // waypoint tool if it currently owns it).
            if (s == State.Idle)
            {
                if (AppState.Mode == AppMode.Placement)
                    AppState.Mode = AppMode.Idle;
            }
            else AppState.Mode = AppMode.Placement;

            switch (s)
            {
                case State.Idle:
                    SetHint("Open the Robot Catalog to place a manipulator " +
                            "— or click a placed robot to move it.");
                    break;
                case State.Catalog:
                    SetHint("Pick a model from the catalog.");
                    break;
                case State.Aiming:
                    SetHint("Point at the floor and CLICK to drop the base. " +
                            "Esc = cancel.");
                    break;
                case State.FineTune:
                    SetHint("Fine-tune X / Y / Z / Yaw, then CONFIRM. " +
                            "Esc = cancel.");
                    break;
            }
        }

        private void SetHint(string s) { if (_hint != null) _hint.text = s; }

        private void RefreshFineValues()
        {
            if (_xVal != null) _xVal.text = $"X  {_ghostPos.x,7:0.000} m";
            if (_yVal != null) _yVal.text = $"Y  {_ghostPos.y,7:0.000} m";
            if (_zVal != null) _zVal.text = $"Z  {_ghostPos.z,7:0.000} m";
            if (_yawVal != null) _yawVal.text = $"YAW {_ghostYaw,6:0.0} °";
        }

        // ---------------------------------------------------------------- laser
        private void CreateLaser()
        {
            _laser = gameObject.AddComponent<LineRenderer>();
            _laser.widthMultiplier = 0.006f;
            _laser.positionCount = 2;
            _laser.useWorldSpace = true;
            var sh = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Sprites/Default");
            _laser.material = new Material(sh);
            _laser.startColor = new Color(0.3f, 0.9f, 1f, 0.9f);
            _laser.endColor = new Color(0.3f, 0.9f, 1f, 0.2f);
            _laser.enabled = false;
        }

        private void UpdateLaser(Vector3 a, Vector3 b, bool on)
        {
            if (_laser == null) return;
            _laser.enabled = on;
            if (on)
            {
                _laser.SetPosition(0, a);
                _laser.SetPosition(1, b);
            }
        }

        // ---------------------------------------------------------------- UI
        private void BuildUi()
        {
            // Persistent toolbar with the catalog button + hint line.
            var bar = UiKit.WorldCanvas("Placement_Toolbar", transform,
                new Vector3(0f, 0.9f, 1.4f), new Vector3(0, 180, 0),
                new Vector2(900, 150), 0.0016f);
            UiKit.Panel(bar.transform, new Color(0.10f, 0.11f, 0.13f, 0.92f));
            var open = UiKit.Button("OpenCatalog", bar.transform,
                "▣  ROBOT CATALOG", 26,
                new Color(0.16f, 0.34f, 0.52f, 1f), OpenCatalog);
            UiKit.Box(UiKit.Rt(open), 16, 16, 360, 60);
            _hint = UiKit.Text("Hint", bar.transform, "", 20,
                TextAnchor.MiddleLeft);
            UiKit.Box(UiKit.Rt(_hint), 16, 86, 868, 48);

            BuildCatalogPanel();
            BuildFinePanel();
        }

        private void BuildCatalogPanel()
        {
            var c = UiKit.WorldCanvas("Placement_Catalog", transform,
                new Vector3(-0.7f, 1.3f, 1.4f), new Vector3(0, 180, 0),
                new Vector2(620, 560), 0.0016f);
            _catalogPanel = c.gameObject;
            UiKit.Panel(c.transform, new Color(0.10f, 0.11f, 0.13f, 0.96f));

            var header = UiKit.Image("Header", c.transform,
                new Color(0.16f, 0.34f, 0.52f, 1f));
            UiKit.TopRow(UiKit.Rt(header), 0, 56, 0);
            var title = UiKit.Text("Title", header.transform,
                "  ROBOT CATALOG", 24, TextAnchor.MiddleLeft);
            UiKit.Stretch(title.rectTransform, 16, 0, 0, 0);

            float y = 76f;
            if (catalog != null)
            {
                for (int i = 0; i < catalog.entries.Count; i++)
                {
                    int idx = i;
                    var e = catalog.entries[i];
                    var b = UiKit.Button($"Model{i}", c.transform,
                        e.displayName, 24,
                        new Color(0.20f, 0.22f, 0.26f, 1f),
                        () => SelectModel(idx));
                    UiKit.TopRow(UiKit.Rt(b), y, 64);
                    y += 76f;
                }
            }

            var close = UiKit.Button("Close", c.transform, "CLOSE", 22,
                new Color(0.35f, 0.35f, 0.40f, 1f),
                () => SetState(State.Idle));
            UiKit.TopRow(UiKit.Rt(close), 480, 60);
        }

        private void BuildFinePanel()
        {
            var c = UiKit.WorldCanvas("Placement_FineTune", transform,
                new Vector3(0.75f, 1.3f, 1.4f), new Vector3(0, 180, 0),
                new Vector2(720, 620), 0.0016f);
            _finePanel = c.gameObject;
            UiKit.Panel(c.transform, new Color(0.10f, 0.11f, 0.13f, 0.96f));

            var header = UiKit.Image("Header", c.transform,
                new Color(0.16f, 0.34f, 0.52f, 1f));
            UiKit.TopRow(UiKit.Rt(header), 0, 56, 0);
            var title = UiKit.Text("Title", header.transform,
                "  ADJUST BASE  (X / Y / Z / YAW)", 22,
                TextAnchor.MiddleLeft);
            UiKit.Stretch(title.rectTransform, 16, 0, 0, 0);

            _xVal = StepRow(c.transform, 76f, "X",
                smallStep, bigStep, d => Nudge(0, d));
            _yVal = StepRow(c.transform, 168f, "Y",
                smallStep, bigStep, d => Nudge(1, d));
            _zVal = StepRow(c.transform, 260f, "Z",
                smallStep, bigStep, d => Nudge(2, d));
            _yawVal = StepRow(c.transform, 352f, "YAW",
                yawSmallStep, yawBigStep, NudgeYaw);

            var confirm = UiKit.Button("Confirm", c.transform, "CONFIRM", 24,
                new Color(0.20f, 0.45f, 0.28f, 1f), ConfirmPlacement);
            UiKit.Box(UiKit.Rt(confirm), 24, 470, 320, 70);
            var cancel = UiKit.Button("Cancel", c.transform, "CANCEL", 24,
                new Color(0.55f, 0.18f, 0.18f, 1f), CancelPlacement);
            UiKit.Box(UiKit.Rt(cancel), 376, 470, 320, 70);
        }

        private Text StepRow(Transform parent, float y, string id,
            float small, float big, System.Action<float> nudge)
        {
            var row = UiKit.Image($"Row_{id}", parent,
                new Color(0.16f, 0.17f, 0.20f, 1f));
            UiKit.TopRow(UiKit.Rt(row), y, 80);

            var val = UiKit.Text("Val", row.transform, "", 24,
                TextAnchor.MiddleCenter);
            UiKit.Box(UiKit.Rt(val), 250, 22, 220, 40);

            var c1 = new Color(0.30f, 0.32f, 0.38f, 1f);
            var bMinus = UiKit.Button("--", row.transform, "−−", 26, c1,
                () => nudge(-big));
            UiKit.Box(UiKit.Rt(bMinus), 16, 12, 90, 56);
            var minus = UiKit.Button("-", row.transform, "−", 26, c1,
                () => nudge(-small));
            UiKit.Box(UiKit.Rt(minus), 114, 12, 90, 56);
            var plus = UiKit.Button("+", row.transform, "+", 26, c1,
                () => nudge(small));
            UiKit.Box(UiKit.Rt(plus), 484, 12, 90, 56);
            var bPlus = UiKit.Button("++", row.transform, "++", 26, c1,
                () => nudge(big));
            UiKit.Box(UiKit.Rt(bPlus), 582, 12, 90, 56);
            return val;
        }
    }
}
