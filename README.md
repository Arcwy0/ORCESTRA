# VR Interaction — UR3 Digital Twin (Stage 1)

Immersive no-code robot programming on a digital twin. **Stage 1** goal: load
the UR3 manipulator from URDF into a Unity scene and drive it with a virtual
replica of the UR teach pendant, tested on PC (VR/MR comes later).

- **Unity:** 6000.4.x (Unity 6)
- **Render pipeline:** URP
- **Robot source:** `ros-industrial/universal_robot` (`noetic-devel`), UR3

---

## What is already in the repo

| Path | Purpose |
|---|---|
| `Assets/RobotAssets/ur3.urdf` | Flattened UR3 URDF (DAE visuals) generated from the official xacro/YAML params |
| `Assets/RobotAssets/ur3_stl.urdf` | Fallback URDF (STL visuals) if the importer rejects `.dae` |

> **URDF location matters.** The file lives in `Assets/RobotAssets/` (the
> *parent* of `ur_description`), NOT inside `ur_description`. The Unity URDF
> Importer resolves `package://ur_description/...` as
> `<urdf-folder>/ur_description/...`; placing the URDF inside `ur_description`
> produces a doubled `ur_description/ur_description` path and the import fails.
| `Assets/Scripts/Robot/UR3JointController.cs` | Drives the 6 revolute `ArticulationBody` joints (deg, speed-limited) |
| `Assets/Scripts/UI/UR3HmiPanel.cs` | World-space teach-pendant replica (sliders, jog ±, HOME/ZERO/STOP/RESET, speed) |
| `Assets/Scripts/Rig/DesktopFlyCamera.cs` | PC stand-in for the headset (RMB look, WASD move) |
| `Assets/Scripts/Editor/UR3SceneBootstrapper.cs` | Scene assembly menus: `UR3 → Build Demo Scene` / `Fix Imported Robot` / `Setup Placement Scene` |
| `Assets/Scripts/Placement/RobotCatalog.cs` | ScriptableObject catalog of placeable robots (extensible) |
| `Assets/Scripts/Placement/RobotPlacementController.cs` | Catalog → ghost aim → fine-tune XYZ → spawn robot + per-robot pendant |
| `Assets/Scripts/Placement/GhostBuilder.cs` | Translucent physics-free placement preview |
| `Assets/Scripts/Placement/PlacementPointer.cs` | Pointer abstraction (PC mouse now, XR controller later) |
| `Assets/Scripts/UI/UiKit.cs` | Shared world-space uGUI builder |

The `com.unity.robotics.urdf-importer` package is already added in
`Packages/manifest.json`.

---

## Setup steps (in the Unity Editor)

> Git must be on PATH (it is — git-lfs 3.7 detected). Unity needs it to fetch
> the URDF Importer git package.

### 1. Resolve packages
Switch back to the Unity Editor window. It will detect the manifest change and
download **URDF Importer**, then recompile. Wait until the spinner stops and
the **Console** has no errors.

### 2. Import the UR3 from URDF
1. In the Project window open `Assets/RobotAssets/` (URDF is here, NOT in
   `ur_description`).
2. **Right-click `ur3.urdf` → "Import Robot from Selected URDF file"**.
3. In the import dialog:
   - **Axis Type:** `Y Axis`
   - **Mesh Decomposer:** `VHACD`
   - (Newer importer versions have no "Use Articulation Bodies" toggle —
     Articulation Bodies are used by default.)
4. Click **Import URDF**. A `ur3_robot` object appears in the Hierarchy.

> If import errors out on the `.dae` meshes, repeat with **`ur3_stl.urdf`**
> instead — same robot, simpler visuals.

### 3. Build the demo scene
Top menu: **`UR3 → Build Demo Scene`**.
This adds a ground, light, fly-camera, attaches `UR3JointController` to the
robot (auto-binds the 6 joints, pins the base), spawns the teach-pendant
panel, and saves the scene to `Assets/Scenes/UR3_Demo.unity`.

### 4. Test in simulation
Press **Play**:
- **Sliders** — set a target angle per joint (limits come from the URDF).
- **− / +** — press-and-hold jog at the set speed.
- **Speed** — global speed scale (teach-pendant style).
- **HOME / ZERO** — go to all-zeros pose.
- **STOP** — protective stop (freezes motion); **RESET** clears it.
- **Camera** — hold right mouse to look, `WASD` move, `Q/E` down/up,
  `Shift` sprint, wheel changes speed.

---

## Stage 1.5 — Robot placement

After the robot works in the demo scene, convert to the placement workflow:

1. With the fixed UR3 in the scene, run **`UR3 → Setup Placement Scene`**.
   This saves the robot as `Assets/RobotAssets/Prefabs/UR3.prefab`, creates
   `RobotCatalog.asset`, clears the scene robot/pendant, and adds the
   placement controller. Scene starts empty.
2. **Play** → click **`▣ ROBOT CATALOG`** → pick a model.
3. Point at the floor (mouse ray + laser) → **click** to drop the base.
   - A translucent **reach disc** (≈ UR3 0.5 m) is drawn around the base so
     you can judge whether the arm can cover the work area.
   - The ghost turns **red** if its footprint overlaps an already-placed
     robot (a warning — confirming is still allowed).
4. Tune **X / Y / Z** *and* **Yaw** in the fine-tune window (−−/−/+/++
   steps; yaw defaults to 5° / 45° steps).
5. **CONFIRM** → the real robot spawns and its own teach pendant appears
   beside it. Repeat to place more robots.
6. **Move a placed robot:** in the idle state, **click any placed robot** —
   it re-opens the fine-tune window so you can re-position / re-orient it
   (its pendant follows). Esc leaves it untouched.

The pointer is abstracted (`PlacementPointer`): swapping the PC mouse for an
XR controller ray later does not touch the placement logic.

> **Spawn-position note.** Immovable `ArticulationBody` roots ignore plain
> transform writes once the physics scene has baked them. Robots are
> therefore instantiated *already positioned* and re-asserted via
> `ArticulationBody.TeleportRoot()` on the next physics step.

## Stage 1.6 — Waypoint scenarios

Teach a TCP path the placed robot will follow.

> **Re-run `UR3 → Setup Placement Scene` once** after pulling this stage.
> It now also refreshes the saved UR3 prefab so the new stiffer drive
> settings (and solver iterations) propagate. The `WaypointController` is
> added next to the placement controller. Robots placed at runtime are
> auto-tagged.

1. **Play** → click **`◎ WAYPOINTS`** (toolbar under the catalog bar).
2. **Choose a robot:** hovering an entry in the list makes that robot in
   the scene glow; click to confirm it.
3. Its **reach volume** appears as a translucent hollow sphere centred at
   the shoulder (UR3: 152 mm above base, 500 mm radius per UR datasheet —
   override `reachRadius` / `reachCenterLocal` on the catalog entry for
   other arms). A marker rides your pointer:
   - **−/+ Height** sets the work-plane the marker slides on (3D points,
     not just the floor).
   - **Click** drops a point. If it is outside the reach sphere it is
     **rejected** and an error toast shows for ~1 s.
   - The next marker appears automatically; **UNDO LAST** removes the most
     recent point; **DONE — BUILD SPLINE** finishes.
4. A Catmull-Rom **spline** is drawn **from the current TCP, through every
   dropped point** — the operator never has to teach the start.
   - **EDIT POINTS** → back to placing (add / undo more).
   - **RUN SIMULATION** → see playback below.

### Playback model
RUN runs an **offline CCD inverse-kinematics solve in one frame** over the
sampled spline, producing one committed joint configuration per sample.
The joint controller is then put into **external control** mode (its own
ramp loop sits out) and the waypoint tool writes joint drive targets
directly at `playbackRate` samples/sec, blending between adjacent
configurations. Effects:
- The arm tracks the spline **rigidly**, not via an online servo that
  oscillates around it.
- It **stops exactly at the last point** — playback ends when the index
  hits the last config; `SnapStateToMeasured()` then re-syncs the
  controller so it does not drag the arm anywhere afterward.

The IK still reads live joint frames (no DH tables — works for any
imported arm). If a different arm consistently drives *away* from the
path, its joint-axis sign convention is inverted — flip the sign on
`deltaDeg` in `SolveJointPath`.

The waypoint and placement tools share a one-line mode lock (`AppState`)
so a click is never consumed by both.

## Stage 1.7 — Pendant polish

Three usability upgrades to the teach pendant:

1. **TCP mode.** Tabs at the top of the panel switch between
   **JOINTS** (the original 6-slider/jog screen) and **TCP**, an
   arrow pad like the real UR "Move" screen: a vertical ▲/▼ pair for
   up/down (Y) and an ◄ ► / ▲ ▼ diamond for the horizontal X-Z plane.
   Hold an arrow to jog; live X/Y/Z read-outs sit along the bottom.

   Jog uses a **resolved-rate (damped-least-squares Jacobian) step**
   (`CcdIkSolver.TcpJogDeltasDeg`): each frame the held arrows request a
   small world move, the Jacobian `dq = Jᵀ(JJᵀ+λ²I)⁻¹·move` converts it
   to joint increments, and the joints are commanded relative to their
   measured pose. This **only reads** the joint frames — earlier I tried
   a per-frame CCD that called `ArticulationBody.SetJointPositions` every
   frame, which corrupts the live solver state and made the arm sag
   instead of track. Speed follows `tcpJogSpeed * speedPercent`.

2. **Collapse / expand.** A `▴`/`▾` button in the header shrinks the
   pendant to just the title bar, freeing up the operator's view of
   the scene. Click again to restore. Canvas pivot is set to its top
   edge so the header does not move when the body folds away.

3. **Billboarding.** Every world-space canvas built through
   `UiKit.WorldCanvas` (placement toolbar, catalog, fine-tune, waypoint
   panels) and every teach pendant now carries a `Billboard` component.
   In `LateUpdate` it sets `transform.rotation` to face the main camera
   (constrained to the world Y axis by default, so panels stay
   upright). Operators can move around the cell and the menus always
   read forward.

The IK behind TCP mode and the offline waypoint trajectory now share
one implementation in `VRInteraction.Robot.CcdIkSolver` — `SolveOnce`
for single-target jog, `SolveBatch` for the full spline pre-solve.

## Stage 2 — Quest 3 setup

### Prerequisites (one-time, on this PC)

| What | Where |
|---|---|
| **Android Build Support** (+ NDK/JDK) | Unity Hub → Installs → your Unity version → Add modules |
| **Meta Quest Link** app | [meta.com/quest/setup](https://www.meta.com/quest/setup/) — install on PC |
| **Developer Mode** on headset | Meta mobile app → your headset → Developer Mode ON |

---

### A. Add XR packages (already done if you pulled this branch)

`Packages/manifest.json` already contains:
```
com.unity.xr.management          4.5.0
com.unity.xr.openxr              1.13.1
com.unity.xr.interaction.toolkit 3.1.1
```
Switch back to the Unity Editor — it will download and compile them automatically.

---

### B. Configure XR Plugin Management

**Edit → Project Settings → XR Plug-in Management** (install the package if prompted)

#### Standalone tab (Monitor icon) — for Quest Link / in-Editor testing
1. Tick **OpenXR**
2. Click the ⚠ warning icon → fix any validation errors
3. Under **OpenXR → Features** enable **Meta Quest Support** (or "Oculus Touch Controller Profile")

#### Android tab (Android robot icon) — for standalone APK
1. Tick **OpenXR**
2. Under **OpenXR → Features** enable **Meta Quest Feature Group** (covers all Quest models)

---

### C. Android Player Settings

**Edit → Project Settings → Player → Android tab:**

| Setting | Value |
|---|---|
| Minimum API Level | Android 10 (API 29) |
| Target Architecture | ARM64 only (untick ARMv7) |
| Scripting Backend | IL2CPP |
| Graphics API | **Vulkan only** (remove OpenGL ES 3.x) |
| Auto Graphics API | Off |
| Internet Access | Required (for Link streaming) |
| Write Permission | External (SD Card) — optional |

---

### D. Build the XR rig in the scene

1. Open your placement scene (`Assets/Scenes/…unity`).
2. Menu: **`UR3 → Setup XR Rig`**  
   This replaces `DesktopFlyCamera` with an **XR Origin** hierarchy and
   wires `XrControllerPointer` (right controller trigger = confirm,
   B button = cancel) to `RobotPlacementController`.
3. **Save the scene** (`Ctrl+S`).

> If Unity shows "UNITY_XR_INTERACTION_TOOLKIT define missing":
> run **`UR3 → Enable XR Scripting Define`** once, wait for recompile,
> then run **`UR3 → Setup XR Rig`** again.

---

### E. Testing via Quest Link (no APK — fastest)

1. Connect Quest 3 via USB-C (or enable **Air Link** in the headset).
2. In the headset, open **Quest Link** and connect to this PC.
3. Back in Unity, make sure **Standalone** platform is selected.
4. Press **Play** in the Editor — the scene streams live to the headset.
5. Right controller **trigger** = place / confirm; **B** = cancel.
6. World-space UI panels work with the controller ray automatically.

---

### F. Build standalone APK (optional)

1. **File → Build Settings** → switch to **Android** (Install Build Support if
   prompted — takes ~5 min).
2. Click **Add Open Scenes**.
3. Click **Build** → save `VR_Interaction.apk`.
4. Sideload via adb:
   ```bash
   adb install -r VR_Interaction.apk
   ```
   (Quest must be connected in developer mode; `adb devices` should list it.)

---

### What works unchanged in VR

| Feature | Status |
|---|---|
| World-space teach pendants | ✅ billboard faces you in VR |
| Robot placement (catalog → ghost → confirm) | ✅ mapped to trigger / B |
| Waypoint path + simulation | ✅ no input changes needed |
| TCP arrow jog | ⚠ arrows render but need UI raycaster calibration (known bug) |

---

## Stage 3 — More robots (KUKA KR600 + Scout V2)

The catalog is now type-aware: each entry is either a **Manipulator** (joint
pendant + TCP waypoints, like UR3) or a **Mobile** base (drive pendant + floor
waypoints). Two new models ship:

| Robot | Kind | Source | Flat URDF |
|---|---|---|---|
| KUKA KR 600 FORTEC (R2830) | Manipulator | `kroshu/kuka_robot_descriptions` | `Assets/RobotAssets/kuka_kr600.urdf` |
| AgileX Scout V2 | Mobile (skid-steer) | `agilexrobotics/scout_ros` | `Assets/RobotAssets/scout_v2.urdf` |

### Adding them (per robot)

1. **Import the URDF.** In the Project window open `Assets/RobotAssets/`,
   right-click `kuka_kr600.urdf` (or `scout_v2.urdf`) →
   **"Import Robot from Selected URDF file"** → Axis Type **Y Axis**,
   Mesh Decomposer **VHACD** → Import. (If the `.dae` visuals fail, the
   meshes are also shipped as `.stl`.)
2. **Register it** with the imported robot in the open scene:
   - **`UR3 → Catalog → Add KUKA KR600 (Manipulator)`**, or
   - **`UR3 → Catalog → Add Scout V2 (Mobile)`**.
   This strips the importer demo scripts, attaches the right controller,
   saves a prefab to `Assets/RobotAssets/Prefabs/`, adds a catalog entry,
   and clears the scene instance. (`Setup Placement Scene` no longer wipes
   the catalog, so order doesn't matter.)
3. **Play** → `▣ ROBOT CATALOG` → the new model is in the list. Place it
   like any other.

### KUKA KR600 — notes
- Huge robot: ~2.83 m reach, ~3.5 m tall. The reach disc / sphere scale
  accordingly.
- Reuses `UR3JointController` (binds the 6 revolute joints by hierarchy
  order — KUKA links are `link_1…link_6`), the teach pendant, and the
  TCP-waypoint scenario tool unchanged.

### Scout V2 — notes (mobile)
- **Physical skid-steer.** `MobileBaseController` velocity-drives the 4
  wheel ArticulationBody joints; the wheels push the (non-immovable,
  gravity-enabled) base via a high-friction contact. Differential drive:
  `vL = v − ω·halfTrack`, `vR = v + ω·halfTrack`.
- **Drive pendant** (`MobileHmiPanel`): a diamond of press-and-hold arrows
  (forward / back / turn-left / turn-right), live speed read-out, speed
  scale, protective STOP.
- **Floor waypoints**: `◎ WAYPOINTS` → pick the Scout → click points on the
  floor → DONE → RUN. A go-to-goal follower turns toward each point, drives
  forward once aligned, advances within `arriveRadius`, stops after the last.
- **Smooth start/stop.** Command ramping (`maxLinearAccel`,
  `maxAngularAccel` on `MobileBaseController`) keeps the chassis from
  lurching and lifting its wheels. Lower them for gentler motion.
- **Tuning (physics needs it).**
  - Drives straight but **backwards** → flip BOTH `leftWheelSign` and
    `rightWheelSign` on `MobileBaseController`.
  - **Spins in place** during a waypoint route (or manual turn goes the
    wrong way) → flip `turnSign` on `MobileBaseController`. (This is the
    usual cause of "it just rotates instead of driving the route".)
  - Follows a route **backwards** → toggle `mobileHeadingInvert` on
    `WaypointController`.
  - Spawn height (`spawnHeightOffset ≈ 0.235`) lifts the chassis so the
    wheels, not the body, rest on the floor.

### Fixing the pink/magenta robots
`.dae` meshes import their materials with the built-in *Standard* shader,
which renders **magenta** under URP. Run **`UR3 → Catalog → Fix Robot
Prefabs (URP + drives)`** — it converts both prefabs' materials to URP/Lit
(and stiffens the KUKA drives) in place. Then delete the already-placed
robots and place them again so the updated prefab is used. (Fresh
`Add KUKA / Add Scout` runs already apply this automatically.)

## Stage 3.1 — Saving & replaying episodes

A taught waypoint path can be saved as an **episode** and replayed later,
even after the robot has been moved.

- **Save:** teach a path → **DONE — BUILD SPLINE** → in REVIEW press
  **SAVE EPISODE**. The episode stores the robot's *recorded start state*
  (manipulator joint angles, or the mobile base pose) plus the waypoints in
  the robot's *base-relative* frame.
- **Load / replay:** toolbar **`▤ EPISODES`** → pick one. The tool rebinds a
  matching robot, **first returns it to the recorded state** (homes the arm's
  joints / teleports the base back), and only then plays the path.
- Because points are stored relative to the base, a relocated manipulator
  carries its path along; the mobile base is sent back to where it was taught
  so the route reproduces exactly.
- Files live as JSON in `Application.persistentDataPath/Episodes/` (works in
  the Editor and in builds), one file per episode.

## Notes / design decisions

- The `noetic-devel` branch ships **xacro**, not flat URDF. `ur3.urdf` was
  generated faithfully from `config/ur3/{default_kinematics,joint_limits,
  physical_parameters,visual_parameters}.yaml` + `urdf/inc/ur_macro.xacro`.
- Joints are position-controlled `ArticulationBody` drives. Gravity on the
  links is disabled by default (`UR3JointController.disableGravity`) so the
  arm holds any pose; turn it off for full dynamics later.
- The HMI is a **world-space** uGUI canvas on purpose — the identical panel
  will work when the XR rig replaces `DesktopFlyCamera`.
- Stage 2 (next): swap the desktop rig for XR Interaction Toolkit + XR Device
  Simulator, then Meta XR SDK for Quest 3 / MR.

## Git / GitHub

`.gitignore` and `.gitattributes` (Git LFS for `*.stl/*.dae/*.fbx/...`) are
in place. When ready to publish:

```bash
git init
git lfs install
git add .gitattributes .gitignore
git add .
git commit -m "Stage 1: UR3 URDF digital twin + teach-pendant HMI"
```
