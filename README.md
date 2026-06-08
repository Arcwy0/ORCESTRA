# VR Interaction — UR3 / KUKA / Scout Digital Twins

Immersive no-code robot programming on digital twins. Place industrial-arm and
mobile-base twins in a Unity scene, teach a TCP path or a floor route by hand
in VR / Mixed Reality on a Meta Quest 3, then play it back rigidly. Runs as a
**standalone Quest APK** — once installed, the PC is not needed.

| Item | Value |
|---|---|
| Unity | 6000.x (Unity 6, URP) |
| Target device | Meta Quest 3 / 3S (Quest 2 / Pro also fine) |
| Modes | Desktop (mouse), VR (full immersion), **MR (passthrough)** |
| Robots out of the box | UR3 (manipulator), KUKA KR600 R2830 (manipulator), Scout V2 (mobile, skid-steer) |

---

## Quick start

### 1. Clone the repo

```bash
git lfs install
git clone https://github.com/Ivashka513/ORCESTRA.git
cd ORCESTRA
```

Git LFS is required — the `.stl` / `.dae` / `.fbx` robot meshes are stored as
LFS objects (~70 MB total after cleanup).

### 2. Open in Unity Hub

Add the cloned folder as a project. Unity 6 (any 6000.x) downloads the
packages from `Packages/manifest.json` on first open — wait until the spinner
in the bottom-right stops and the **Console** has no errors.

The URDF importer and Meta OpenXR packages are pinned in the manifest, so
nothing else needs to be installed by hand.

### 3. Open the demo scene

`Assets/Scenes/UR3_Demo.unity` is the working scene. Hit **Play** to drive the
UR3 with the teach pendant from the PC. To play in MR on the headset see
[Running on the Quest](#running-on-the-quest) below.

---

## What's in the repo

```
Assets/
├── RobotAssets/
│   ├── ur3.urdf, ur3_stl.urdf, kuka_kr600.urdf, scout_v2.urdf  (flat URDFs)
│   ├── ur_description/        (UR3 meshes + xacro source)
│   ├── kuka_fortec_description/   (kr600_r2830 meshes + xacro)
│   ├── scout_description/     (Scout V2 meshes + xacro)
│   ├── Prefabs/               (UR3.prefab, KUKA_KR600.prefab, Scout_V2.prefab,
│   │                           shared URP materials)
│   └── RobotCatalog.asset     (ScriptableObject listing placeable robots)
├── Scenes/UR3_Demo.unity      (the working scene)
└── Scripts/
    ├── Editor/                (UR3 → … menu commands: scene setup, MR setup,
    │                           catalog add/fix)
    ├── Placement/             (robot catalog UI, ghost preview, surface
    │                           detection, fine-tune, delete)
    ├── Rig/                   (Desktop fly-cam, XR controller pointer,
    │                           RigModeManager — Desktop ↔ VR ↔ MR + scene
    │                           permission, MR floor collider, passthrough)
    ├── Robot/                 (UR3JointController, MobileBaseController,
    │                           CcdIkSolver)
    ├── UI/                    (Teach pendants, Billboard, FollowOperator)
    └── Waypoints/             (TCP path teach + spline + episode save/load)
```

Only one variant per robot ships (UR3 / KR600 R2830 / Scout V2). The original
ROS packages contained ~15 UR sizes and 7 KUKA Fortec variants — they were
trimmed since the catalog never used them.

---

## Modes & controls

The project has three runtime modes, switched at any time:

| Mode | Camera | Input | Toggle |
|---|---|---|---|
| Desktop | mouse-look fly-camera | mouse + keyboard | `F12` |
| VR | XR Origin (Quest) | right-controller ray | `F12` |
| MR (passthrough) | XR Origin + real-world camera composited | right-controller ray | `F11` or **left-controller Y** |

`RigModeManager` swaps the active camera (`Camera.main` tag follows) and the
active `PlacementPointer` (mouse vs. controller) on every toggle. The UI
panels are world-space so they work unchanged across all three modes.

### Desktop
- **RMB** look · **WASD** move · **Q / E** down / up · **Shift** sprint · wheel: speed
- **LMB** = confirm / drop point · **Esc** = cancel

### VR / MR
- Right controller **trigger** = confirm / drop point
- Right controller **B** = cancel
- Right thumbstick **Y** = push the held waypoint along the ray (XR manipulator-teach only)
- Left controller **Y** = flip VR ↔ MR

---

## Running on the Quest

You have two ways to run the project on the headset.

### A. Quest Link (in-Editor, fast iteration)
1. Connect via USB-C **or** enable **Air Link** in the headset.
2. In the headset, open **Quest Link** and connect to this PC.
3. Back in Unity: **Edit → Project Settings → XR Plug-in Management → Standalone tab → tick OpenXR + Meta Quest Support**.
4. Press **Play** in the Editor. The scene streams to the headset live.

### B. Standalone APK (installed on the headset, no PC after install)

This is the recommended workflow once the project works. The APK runs entirely on the headset; no Link, no PC, no cable.

#### One-time Project Settings (Player → Android tab)

| Setting | Value |
|---|---|
| Company Name | your name / org |
| Product Name | shown on the headset's app icon (e.g. `UR3 MR Teach`) |
| Identification → Override Default Package Name | enable + e.g. `com.yourname.ur3mrteach` |
| Minimum API Level | Android 10 (API 29) or higher |
| Target Architectures | **ARM64 only** (untick ARMv7 — Quest is 64-bit) |
| Scripting Backend | **IL2CPP** |
| Graphics API | **Vulkan** (remove OpenGL ES) |
| Configuration | **Release** for the final APK (not Development Build) |

#### One-time XR Plug-in Management (Project Settings → XR Plug-in Management → Android tab)

Enable **OpenXR**, then under **OpenXR → Features** make sure these Meta Quest features are ON (already configured in this repo, but verify after first open):

- Meta Quest Support
- Meta Quest: Camera (Passthrough)
- Meta Quest: Planes  (provider type = **Spatial Entity**)
- Meta Quest: Raycasts
- Meta Quest: Session
- Meta Quest: Anchors

#### Build the APK

1. **File → Build Profiles** → select **Android** (install Build Support if prompted).
2. **Add Open Scenes** (`UR3_Demo`).
3. Click **Build And Run** to install on the connected headset immediately, **or** **Build** to save a `.apk` file you can hand off.

#### Install the .apk on any Quest (without Unity)

Pick one of these — the device must have Developer Mode enabled in the Meta mobile app:

- **Meta Quest Developer Hub** — drag the `.apk` onto the device's app list.
- **SideQuest** — Install APK File.
- **adb** — `adb install -r path/to/app.apk` (Quest connected via USB-C, `adb devices` must list it).

After install, put on the headset → `Library → Apps → filter "Unknown Sources"` → the app appears with its Product Name. **From here the PC is no longer needed.**

#### First-launch device setup (one-time, per Quest)

For Mixed-Reality surface detection (placing robots on real tables) to work you need:

1. **Run Space Setup on the headset** (`Settings → Physical Space → Space Setup`) and label your room — floor, walls, **and any tables / desks** you want to land robots on.
2. **Grant the scene permission** when the dialog appears on first launch — it asks for access to spatial data. Without it, the AR plane subsystem returns zero planes and the ghost falls back to the flat virtual floor.

---

## Workflow

### 1. Place robots
- Toolbar **`▣ ROBOT CATALOG`** → pick a model. A translucent ghost follows the controller ray.
- In **MR**, the ghost lands on the real surface the ray hits (floor, table, desk — anything horizontal Space Setup detected). In Desktop / VR it lands on the virtual floor.
- **Trigger / click** → fine-tune **X / Y / Z / Yaw** in a panel that floats to the right of your gaze.
- **CONFIRM** → the real robot spawns and its own teach pendant appears beside it.
- A translucent **reach disc** shows the manipulator's working area on the surface (catalogue reach × 1.3 — the usable working area is a bit larger than the bare datasheet reach).

### 2. Move or delete a placed robot
- Click any placed robot in Idle to re-open the fine-tune panel.
- Same tweak controls as the first placement, **plus** a red **DELETE ROBOT** button that destroys the robot and its pendant. Hidden during fresh placements.
- **CONFIRM** to re-position, **CANCEL** to leave it untouched.

### 3. Teach a TCP path (manipulator) or a floor route (mobile)
- Toolbar **`◎ WAYPOINTS`** → pick the robot from the list. Its reach sphere appears (manipulators) or the floor becomes the work-plane (mobile).
- A marker rides at the end of the controller ray. **Right thumbstick** pushes the marker closer / farther along the ray so you can drop points far from yourself without physically reaching.
- **Trigger / click** to drop a point. Points outside the reach sphere are rejected with a toast.
- **UNDO LAST** removes the most recent point. **DONE → BUILD SPLINE** finishes — a Catmull-Rom spline is drawn from the robot's current TCP through every point.
- **RUN SIMULATION** plays it: an offline CCD inverse-kinematics solve produces a joint trajectory in one frame, then the controller writes drive targets directly. The arm tracks rigidly and stops exactly at the last point. For mobile, a go-to-goal follower drives between points.
- **SAVE EPISODE** stores the path (robot-relative) + the recorded start joint pose to `Application.persistentDataPath/Episodes/*.json`.

### 4. Replay an episode
- Toolbar **`▤ EPISODES`** → pick a saved file. The robot is first returned to its recorded state (joints homed / base teleported), then the spline plays.

---

## Editor menus

Open the Unity Editor with the demo scene loaded — the **`UR3`** menu hosts every assembly command:

| Command | What it does |
|---|---|
| `Setup XR Rig` | Swap `DesktopFlyCamera` for an `XR Origin` + wire `XrControllerPointer` |
| `Setup MR (AR Foundation)` | Add `ARSession`, `ARCameraManager`, `ARPlaneManager`, `ARRaycastManager` on top of the XR rig |
| `Mode → Desktop / XR / Passthrough` | Bake the scene's starting mode |
| `Setup Placement Scene` | Save the current robot as a prefab, build the catalog, switch the scene to placement workflow |
| `Catalog → Add KUKA KR600` | Import the kuka_kr600 prefab and register it in the catalog |
| `Catalog → Add Scout V2` | Same for the Scout V2 mobile base |
| `Catalog → Fix Robot Prefabs (URP + drives)` | Convert imported `.dae` materials from the magenta Standard shader to URP/Lit and stiffen drive gains |

---

## Notes & design decisions

- **No magic constants — except one.** Manipulator reach radius shown in the placement disc and used to validate waypoints is `entry.reachRadius × PlacedRobot.WorkAreaScale` (1.3). The factor lives on `PlacedRobot.WorkAreaScale`; tune it in code if the working area should match the datasheet exactly.
- **MR floor collider.** Passthrough hides the virtual `Ground` so the real room shows through — which also removes its collider. `RigModeManager.EnsureMrFloor` adds an invisible `BoxCollider` at `groundY` so dynamic robots (Scout) don't fall through. Only active in MR.
- **Multi-root articulations.** UR3's URDF lands with two `isRoot` ArticulationBodies (`base_link_inertia` + a stray fixed `base` frame, both children of an AB-less `base_link`). `RobotPlacementController.PlaceArticulation` teleports **every** root to its post-move world pose — single-root robots (KUKA) keep working.
- **Scene permission is async.** `RigModeManager.RequestScenePermissions` requests `com.oculus.permission.USE_SCENE` and `USE_ANCHOR_API` at launch. On grant, `ApplyPassthrough()` is re-run so the plane subsystem re-queries the scene.
- **Robots are instantiated already positioned**, and `ArticulationBody.TeleportRoot()` is re-asserted on the next physics step — immovable AB roots ignore plain transform writes once the physics scene has baked them.
- **Pointer is abstract** (`PlacementPointer.GetRay/ConfirmPressed/CancelPressed/DepthAxis`). Desktop = mouse over camera ray, XR = right-controller transform + trigger / B / right thumbstick Y. Tools never branch on the input device.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Ghost lands only on virtual floor in MR | Space Setup not run, or scene permission not granted | Run Space Setup; reinstall and grant the permission dialog on first launch |
| Mobile robot falls through the floor in MR | (Already fixed) An older build had no MR floor collider | Rebuild from current `master` |
| UR3 relocate moves only the pendant | (Already fixed) UR3 has two articulation roots | Rebuild from current `master` |
| Robots appear pink / magenta in URP | `.dae` materials imported under the legacy Standard shader | `UR3 → Catalog → Fix Robot Prefabs (URP + drives)` |
| Passthrough shows black instead of the real room | URP HDR / Post-processing enabled on the mobile assets | `Mobile_RPAsset` HDR off; `Mobile_Renderer` post-processing off |
| "UNITY_XR_INTERACTION_TOOLKIT define missing" | XR Interaction Toolkit not yet compiled | `UR3 → Enable XR Scripting Define`, wait for recompile, then redo `Setup XR Rig` |

---

## License & sources

- UR3 URDF/meshes — `ros-industrial/universal_robot` (`noetic-devel`), trimmed to ur3 only
- KUKA KR 600 R2830 — `kroshu/kuka_robot_descriptions`, trimmed to kr600_r2830 only
- AgileX Scout V2 — `agilexrobotics/scout_ros`, trimmed to scout_v2 only

Each upstream package retains its original license inside its folder.
