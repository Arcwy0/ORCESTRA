# Quest 3 Standalone Build Guide

This checklist is for building the Unity 6000.4.10f1 project as a standalone
Quest 3/3S APK.

## What Is Configured

- Android package identity: `com.orcestra.robotai`.
- Product name: `ORCESTRA Robot AI`.
- Android minimum SDK: API 32.
- Android target SDK: automatic.
- Android architecture: ARM64 only.
- Scripting backend: IL2CPP.
- Graphics API: Vulkan.
- OpenXR loader is configured for Android through XR Management.
- Android XR Management automatically loads and starts the OpenXR loader.
- Meta Quest Support is enabled for Quest 3 and Quest 3S.
- Meta Quest camera image support is enabled in OpenXR settings.
- `UR3_Demo` and `VR_VLM_Test` are enabled in Build Settings.
- Internet permission is forced in Player Settings.
- `INTERNET`, `ACCESS_NETWORK_STATE`, and `RECORD_AUDIO` are declared through
  `Assets/Plugins/Android/ORCESTRAAndroidPermissions.androidlib`.
- Runtime microphone permission is requested before recording on Android.
- `UR3 > Build > Build Quest APK` builds enabled scenes to
  `Builds/ORCESTRA_Robot_AI_Quest.apk` when Android is the active platform.

## Unity Build Steps

1. Open the project with Unity `6000.4.10f1`.
2. Install Unity Android Build Support, including SDK/NDK/OpenJDK, if Unity
   prompts for it.
3. Open `Assets/Scenes/UR3_Demo.unity` for the main app or
   `Assets/Scenes/VR_VLM_Test.unity` for the controlled AI test scene.
4. In `AI_RobotControl`, set:
   - `Use Local Mock`: disabled for server tests.
   - `Use Json Transport`: enabled.
   - `Server Url`: `http://<server-ip>:8080/v1/robot/command`.
   - `Timeout Seconds`: `180`.
5. Do not use `127.0.0.1` for a headset build unless the gateway runs on the
   headset. Use the LAN or VPN address reachable from the Quest.
6. Open `File > Build Profiles`.
7. Select Android and switch platform if needed.
8. Confirm `UR3_Demo` and `VR_VLM_Test` are enabled.
9. Use `Build And Run` for a connected headset or `Build` to create an APK.
   Alternatively, after Android is active, run
   `UR3 > Build > Build Quest APK`.

## Device Checks

Before testing AI control on the standalone APK:

- Enable Developer Mode for the headset.
- Confirm `adb devices` lists the Quest when connected by USB.
- Confirm the Quest and VLM server are on the same reachable network.
- From another machine on the same network, verify:

```powershell
Invoke-RestMethod http://<server-ip>:8080/health
```

The headset cannot run this PowerShell command; this only verifies that the
server is reachable from the network.

## First Launch

1. Launch the APK from `Unknown Sources`.
2. Grant microphone permission when pressing `REC` for the first time.
3. If using MR placement, complete Quest Space Setup and grant scene/spatial
   permissions when prompted.
4. Press `REC`, speak a command, press `REC` again, and confirm the command text
   appears in the AI panel.
5. Press `SEND`; the server should receive a `/v1/robot/command_json` request.
6. Confirm the preview only after the waypoint/path looks correct.

## Current Limitation

The `QuestPassthroughCamera` image source is still guarded. If direct Meta
passthrough camera frame access is unavailable, the app falls back to the Unity
screenshot provider. The standalone APK is build-ready, but real-world unknown
object grounding still depends on finishing the device-side passthrough camera
capture spike.
