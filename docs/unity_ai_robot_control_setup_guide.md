# Unity AI Robot Control Setup Guide

This guide describes the current implementation, how to attach/configure it in
Unity, and how to validate the VLM/ASR/TTS robot-control pipeline.

Project assumptions:

- Unity: `6000.4.10f1`.
- Target device: Meta Quest 3/3S standalone APK.
- First target: digital twins only.
- Robots: UR3, KUKA KR600, Scout V2.
- Server VLM/ASR runtime: RTX 4090 machine, separate from Unity.
- Local model weights are not included in this repository.

## Implemented Components

Unity code:

- `Assets/Scripts/AI/RobotAiController.cs`
  Main runtime component. Builds the world-space UI, records audio, captures an
  image, sends requests, validates responses, previews plans, and executes after
  confirmation.
- `Assets/Scripts/AI/AiCommandClient.cs`
  Multipart HTTP client. Sends `request_json`, PNG image, and optional WAV audio.
  Also has a Unity-local mock mode.
- `Assets/Scripts/AI/AiSceneSnapshotBuilder.cs`
  Serializes placed robot state, camera matrices, MR planes, and known scene
  objects. Explicit object markers are optional; saturated primitive props are
  also included as test-scene hints.
- `Assets/Scripts/AI/AiImageCaptureProviders.cs`
  Provides Unity screenshot capture and a guarded Quest-passthrough fallback.
- `Assets/Scripts/AI/AiGroundingService.cs`
  Converts VLM grounding output into Unity world waypoints. For unknown objects,
  it treats the VLM bbox/preferred point as 2D evidence. Unity screenshots lift
  this evidence through physics/MR scene colliders and can fall back to the
  floor plane for VR test scenes. Quest passthrough camera frames require MRUK
  environment raycast depth and fail explicitly if depth is unavailable.
- `Assets/Scripts/AI/AiGroundingDebugImageWriter.cs`
  Saves a client-side PNG overlay with VLM 2D evidence and the final Unity
  waypoint projected back into the captured image.
- `Assets/Scripts/AI/AiPlanValidator.cs`
  Rejects unsafe or malformed plans.
- `Assets/Scripts/AI/AiPlanPreview.cs`
  Draws target/path preview.
- `Assets/Scripts/AI/AiMotionExecutor.cs`
  Sends confirmed plans to existing digital-twin controllers.
- `Assets/Scripts/AI/Speech/*`
  Microphone WAV recording, WAV encoding/decoding, Android TTS, Sentis Whisper
  ASR backend, and speech backend interfaces.
- `Assets/Scripts/Editor/AiSceneBootstrapper.cs`
  Adds the Unity menu item `UR3/Setup AI Robot Control`.

Server code:

- `server/robot_ai/main.py`
  FastAPI endpoint `/v1/robot/command`.
- `server/robot_ai/model_client.py`
  Mock response, OpenAI-compatible VLM forwarding, and optional
  OpenAI-compatible ASR forwarding.
- `server/robot_ai/schemas.py`
  Pydantic request/response schema.

Docs:

- `docs/vlm_robot_control_plan.md`
- `docs/vlm_robot_control_testing_plan.md`
- `docs/quest_asr_tts_model_bundle.md`

## Package Setup

`Packages/manifest.json` includes:

```json
"com.unity.ai.inference": "2.6.0"
```

and version define:

```json
{
  "name": "com.unity.ai.inference",
  "expression": "2.6.0",
  "define": "UNITY_AI_INFERENCE"
}
```

First Unity action:

1. Open `C:/Arcwy/1Important/Unity/ORCESTRA` in Unity `6000.4.10f1`.
2. Let Package Manager resolve packages.
3. Confirm there are no compiler errors.
4. If `Unity.InferenceEngine` API errors appear, fix
   `Assets/Scripts/AI/Speech/SentisWhisperAsrBackend.cs` first.

## Scene Setup: Recommended Path

Use the bootstrapper:

1. Open the target scene, for example `Assets/Scenes/UR3_Demo.unity`.
2. In the Unity menu, run:

```text
UR3 -> Setup AI Robot Control
```

Expected result:

- A new GameObject named `AI_RobotControl` is created if it does not already
  exist.
- It has `RobotAiController`.
- At runtime, `RobotAiController` automatically adds:
  - `AiCommandClient`
  - `AiPlanPreview`
  - `AiMotionExecutor`
- The AI panel is created as a world-space UI child at Play Mode startup.

## Clean VR VLM Test Scene

For VLM testing without MR/passthrough and without the placement/waypoint UI
occluding the camera, use the dedicated test-scene builder:

```text
UR3 -> Build VR VLM Test Scene
```

It creates and saves:

```text
Assets/Scenes/VR_VLM_Test.unity
```

Scene contents:

- clean VR-only floor and lighting;
- one pre-placed UR3 digital twin named `UR3_TestRobot`;
- one visible target cube named `VLM_Target_RedCube`;
- `AiKnownSceneObjectMarker` on the cube with label `red cube`;
- `AI_RobotControl` configured for JSON transport and server URL
  `http://127.0.0.1:18080/v1/robot/command`;
- XR rig baked to VR mode, no MR passthrough requirement.

Before pressing Play, keep the SSH tunnel open if you are using the server:

```powershell
ssh -N -L 18080:127.0.0.1:8080 <server-user>@<server-tailscale-or-lan-ip>
```

First commands to test:

```text
Move the gripper to the red cube.
Move the gripper in a circle in the vertical plane.
```

The red cube is included in the Unity scene snapshot as a known object, so
Unity can ground it by label even if the VLM omits a precise image point.

To test the unknown-object path, remove or disable `AiKnownSceneObjectMarker`
from a target object and use a command such as:

```text
Move the gripper to the blue cube.
```

Expected:

- the server annotated image draws the bbox around the object;
- the trace shows object coordinates in original image pixels in
  `response_after_coordinate_normalization`; `ROBOT_AI_VLM_COORD_FORMAT=auto`
  only rescales Qwen 0-1000 coordinates when they cannot already be valid image
  pixels;
- `final_response.plan_ir.waypoints` is empty for the unknown object;
- Unity logs that it grounded the image bbox through a collider and then
  previews a waypoint above the hit point.

## Scene Setup: Manual Path

If you do not use the menu item:

1. Create an empty GameObject:

```text
AI_RobotControl
```

2. Add component:

```text
RobotAiController
```

3. Optional: add these components manually, otherwise the controller adds them
   in `Awake()`:

```text
AiCommandClient
AiPlanPreview
AiMotionExecutor
```

4. Place the GameObject near the user rig only if you want to tune panel
   location. The current panel is created at:

```text
local position: (0, 1.18, 1.45)
local rotation: (0, 180, 0)
canvas size: 900 x 330
scale: 0.0014
```

## Robot Setup Requirements

Use the existing robot-placement workflow. The AI system discovers placed robots
through existing placement/runtime state.

Each robot digital twin should have the existing project components it already
uses:

- UR3/KUKA manipulator:
  - `PlacedRobot`
  - manipulator joint/controller stack
  - reachable TCP or reach-center information exposed through existing robot
    scripts
- Scout V2 mobile base:
  - `PlacedRobot`
  - `MobileBaseController`

The AI executor expects:

- manipulator plans use `kind = "manipulator_reach"`;
- mobile plans use `kind = "mobile_route"`;
- coordinates are Unity world meters;
- rotations are quaternions `[x, y, z, w]`;
- contact is disabled for v1.

## RobotAiController Inspector Fields

Server:

- `Command Client`: leave empty unless you added one manually.
- `Use Local Mock`: enabled for first Editor tests.
- `Server Url`: `http://127.0.0.1:8080/v1/robot/command` for local server, or
  `http://<server-ip>:8080/v1/robot/command` for RTX server.

Input:

- `Image Source Mode`:
  - `UnityScreenshot`: default, use for Editor and first Quest tests.
  - `QuestPassthroughCamera`: sends the raw Quest headset camera frame only
    when Meta MRUK v81+ is installed and `ORCESTRA_META_PCA` is enabled in
    scripting define symbols. If that integration is absent, the UI logs a
    concrete warning and falls back to `UnityScreenshot`.
- `Debug Command`: default text placed in the AI panel input field.

Speech:

- `Enable Speech`: enables `REC` and TTS.
- `Asr Backend Mode`:
  - `ServerGateway`: records WAV, calls the server ASR endpoint immediately
    after recording stops, and writes the transcript into the command field.
    If ASR fails, the WAV is kept and sent with the next request as fallback.
  - `OnDeviceWhisperSentis`: transcribes on Quest/Unity through Sentis Whisper.
  - `NativePlugin`: reserved for future sherpa-onnx/native Android plugin.
  - `WindowsDictation`: Windows Editor/standalone local dictation backend.
  - `EditorMock`: deterministic Editor transcript for testing UI flow without
    real ASR.
- In Windows Editor with `Use Local Mock = true` and
  `Asr Backend Mode = ServerGateway`, the controller automatically uses
  `WindowsDictation` so local REC testing updates the text field without a
  server. If Windows reports speech recognition is unsupported, it falls back
  to `EditorMock` when `Use Editor Mock Asr Fallback` is enabled.
- `Editor Mock Transcript`: text inserted by `EditorMock`; default is
  `test test test`.
- `Tts Backend Mode`:
  - `AndroidTextToSpeech`: current Quest output backend.
  - `PiperNativePlugin`: Unity-side packaged Piper voice under
    `Assets/StreamingAssets/TTS/piper-en_US-lessac-medium`; falls back to
    Android TextToSpeech if the native plugin is absent.
  - other enum values currently fall back to Android TTS.
- `Record Sample Rate Hz`: keep `16000`.
- `Max Record Seconds`: default `10`.
- `Speech Model Bundle`: metadata for the selected ASR bundle.

Sentis Whisper fields, visible when `UNITY_AI_INFERENCE` is defined:

- `Whisper Log Mel`
- `Whisper Encoder`
- `Whisper Decoder`
- `Whisper Vocab Json`

Assign these only after adding the exported model assets.

## Whisper tiny.en Asset Setup

Selected v1 ASR bundle:

```text
openai/whisper-tiny.en
```

Expected Unity asset layout:

```text
Assets/StreamingAssets/ASR/whisper-tiny-en/LogMelSpectro.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/AudioEncoder_Tiny.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/AudioDecoder_Tiny.sentis
Assets/StreamingAssets/ASR/whisper-tiny-en/vocab.json
```

After copying assets:

1. Select `AI_RobotControl`.
2. Set `Asr Backend Mode = OnDeviceWhisperSentis`.
3. Assign the four fields:
   - `Whisper Log Mel`
   - `Whisper Encoder`
   - `Whisper Decoder`
   - `Whisper Vocab Json`
4. Enter Play Mode.
5. Press `REC`, speak a short English command, press `REC` again.
6. Expected:
   - AI status changes to `Running on-device ASR...`.
   - Then status becomes `ASR: <transcript>`, or a concrete tensor/runtime
     error.

If a tensor error appears, inspect:

- log-mel model input shape;
- encoder model input/output shape;
- decoder input order;
- decoder output logits axis;
- whether `Functional.ArgMax(outputs[0], 2)` matches the exported decoder.

## Local Mock Validation

Use this first. It does not need a server or model weights.

1. Open a scene.
2. Run `UR3 -> Setup AI Robot Control`.
3. Select `AI_RobotControl`.
4. Set:

```text
Use Local Mock = true
Image Source Mode = UnityScreenshot
Asr Backend Mode = ServerGateway
Tts Backend Mode = AndroidTextToSpeech
```

5. Enter Play Mode.
6. Place a robot using the existing placement UI.
7. In the AI panel, send:

```text
Move the gripper to the cup on the table.
```

Expected:

- screenshot capture completes;
- mock plan is generated locally;
- preview marker/path appears;
- `CONFIRM` starts motion;
- `CANCEL` clears pending plan and stops executor state.

Local Editor speech test:

1. Keep `Use Local Mock = true`.
2. Keep `Asr Backend Mode = ServerGateway`, set it explicitly to
   `WindowsDictation`, or set `EditorMock` when Windows speech recognition is
   unavailable.
3. Press `REC`.
4. Say a short command into the Windows microphone.
5. Press `REC` again.
6. Expected:
   - status reports Windows dictation;
   - the text field changes to the recognized command.

If the text does not change:

- confirm Windows microphone permission is enabled for desktop apps;
- confirm Windows online speech recognition/dictation is available;
- check Unity Console for `Windows dictation failed` or
  `Windows dictation did not return text`;
- set `Asr Backend Mode = EditorMock` to validate the Unity flow without real
  speech recognition.

Repeat with:

```text
Drive the Scout to the chair.
```

Expected for Scout:

- plan kind is `mobile_route`;
- preview route appears;
- confirm calls mobile-base execution path.

## FastAPI Mock Server Validation

Use this to validate Unity HTTP/multipart networking without VLM model loading.

From repo root on Windows PowerShell:

```powershell
$env:ROBOT_AI_MODE="mock"
$env:ROBOT_AI_ASR_MODE="disabled"
uv run python -m uvicorn server.robot_ai.main:app --host 127.0.0.1 --port 8080
```

If you are in another Python environment that already has requirements:

```powershell
python -m uvicorn server.robot_ai.main:app --host 127.0.0.1 --port 8080
```

Unity setup:

```text
Use Local Mock = false
Server Url = http://127.0.0.1:8080/v1/robot/command
```

Expected:

- Unity posts multipart request with JSON and image.
- Server returns mock response.
- Unity previews and executes after confirmation.
- Server does not load or download any model weights.

## Server-Side ASR Validation

Use this when audio should be transcribed on the RTX server.

Gateway environment:

```powershell
$env:ROBOT_AI_MODE="mock"
$env:ROBOT_AI_ASR_MODE="mock"
uv run python -m uvicorn server.robot_ai.main:app --host 127.0.0.1 --port 8080
```

Unity setup:

```text
Use Local Mock = false
Asr Backend Mode = ServerGateway
Server Url = http://127.0.0.1:8080/v1/robot/command
```

Manual test:

1. Clear the text field.
2. Press `REC`.
3. Speak anything.
4. Press `REC`.
5. Press `SEND`.

Expected:

- Unity sends WAV with the request.
- Gateway mock ASR replaces empty command text with:

```text
Move the gripper to the mock target.
```

- Response diagnostics include:
  - `asr_mode = mock`
  - nonzero `audio_bytes`
  - `transcript_text`

For real ASR, run a separate OpenAI-compatible ASR service and set:

```powershell
$env:ROBOT_AI_ASR_MODE="openai_compatible"
$env:ROBOT_AI_ASR_BASE_URL="http://127.0.0.1:8001/v1"
$env:ROBOT_AI_ASR_MODEL="whisper-tiny.en"
```

## Qwen3-VL Server Validation

FastAPI is the robot gateway, not the model runtime. Run Qwen3-VL separately
through vLLM/SGLang or another OpenAI-compatible server.

Gateway environment on the RTX server:

```powershell
$env:ROBOT_AI_MODE="openai_compatible"
$env:ROBOT_AI_BASE_URL="http://127.0.0.1:8000/v1"
$env:ROBOT_AI_MODEL="Qwen/Qwen3-VL-8B-Instruct"
$env:ROBOT_AI_VLM_COORD_FORMAT="auto"
$env:ROBOT_AI_ASR_MODE="disabled"
python -m uvicorn server.robot_ai.main:app --host 0.0.0.0 --port 8080
```

Unity setup:

```text
Use Local Mock = false
Server Url = http://<server-ip>:8080/v1/robot/command
Image Source Mode = UnityScreenshot
```

Expected:

- Gateway sends Unity scene snapshot and screenshot to the model endpoint.
- Model returns schema-compatible JSON.
- Unity rejects malformed/unsafe responses.
- Unity does not execute until `CONFIRM`.

## Quest APK Validation

Build settings:

- Platform: Android.
- Target device: Quest 3/3S.
- XR setup: existing Meta/OpenXR project settings.
- Microphone permission must be available for speech recording.

First Quest test:

1. Use `UnityScreenshot` source.
2. Use `Use Local Mock = true`.
3. Build and run.
4. Place UR3, KUKA, and Scout in separate runs.
5. Send text commands.
6. Confirm preview/execution.

Speech output test:

1. Keep `Tts Backend Mode = AndroidTextToSpeech`.
2. Trigger a mock plan.
3. Expected: Quest speaks the `spoken_reply`.

Speech input test with server ASR:

1. Set `Asr Backend Mode = ServerGateway`.
2. Set `Use Local Mock = false`. This disables the Windows Editor dictation
   fallback and sends WAV to the server.
3. Point `Server Url` to reachable server IP.
4. Press `REC`, speak, press `REC`.
5. Expected: Unity calls `/v1/audio/transcribe_json` and the command text field
   changes to the transcript.
6. Press `SEND`.
7. Expected: VLM request uses the visible transcript text.

Server `.env` for local ASR:

```text
ROBOT_AI_ASR_MODE=faster_whisper
ROBOT_AI_ASR_MODEL_DIR_HOST=/workspace/models/asr/faster-whisper-base.en
ROBOT_AI_ASR_DEVICE=cpu
ROBOT_AI_ASR_COMPUTE_TYPE=int8
```

Download the model:

```bash
cd /workspace/orcestra_robot_ai/server/deploy
docker compose --profile download-speech run --rm speech-downloader
```

Unity-side local TTS:

TTS is not configured on the server. Unity owns TTS.

For packaged model-based TTS in Unity:

```powershell
.\tools\download_unity_tts_model.ps1
```

This writes:

```text
Assets/StreamingAssets/TTS/piper-en_US-lessac-medium/en_US-lessac-medium.onnx
Assets/StreamingAssets/TTS/piper-en_US-lessac-medium/en_US-lessac-medium.onnx.json
```

Then set:

```text
Tts Backend Mode = PiperNativePlugin
```

Runtime synthesis requires the Android Piper native plugin. Until that plugin
is present, use `AndroidTextToSpeech` for working Quest audio.

Saved screenshots:

```text
server/deploy/outputs/robot_ai/*_raw.png
server/deploy/outputs/robot_ai/*_annotated.png
server/deploy/outputs/robot_ai/*_trace.json
```

Set `ROBOT_AI_OUTPUT_DIR_HOST` in `server/deploy/.env` to use a different
host-visible mounted folder.

Use `*_trace.json` to compare the exact VLM text against the final plan:
`raw_model_output` is the model JSON, `response_before_repair` is the parsed
model response after coordinate conversion, and
`final_response.plan_ir.waypoints` is what Unity executed.

For Qwen3-VL, bbox/preferred point values in `raw_model_output` are expected in
a 0-1000 image grid. The gateway writes
`response_after_coordinate_normalization` so you can verify conversion to actual
screenshot pixels before Unity receives the response.

Speech input test with Sentis ASR:

1. Copy and assign Whisper tiny.en assets.
2. Set `Asr Backend Mode = OnDeviceWhisperSentis`.
3. Press `REC`, speak a short English command, press `REC`.
4. Expected: transcript appears in the text field.
5. Measure latency for 3 s, 5 s, and 10 s clips.

## Unity Test Runner Validation

Run EditMode tests:

```text
Assets/Tests/EditMode/Editor/RobotAiModelTests.cs
```

Expected coverage:

- vector packing/unpacking;
- finite coordinate checks;
- response JSON round trip;
- validator rejection cases;
- WAV encode/decode;
- selected speech bundle metadata.

## Python Server Validation

Syntax check:

```powershell
uv run python -m py_compile server\robot_ai\main.py server\robot_ai\model_client.py server\robot_ai\schemas.py server\robot_ai\test_model_client.py
```

Unit tests:

```powershell
uv run python -m unittest server.robot_ai.test_model_client
```

If local dependencies are not installed, tests may skip because `pydantic` is
missing. After installing `server/robot_ai/requirements.txt`, expected result is
passing tests.

## Safety Validation

Create or mock server responses for:

- `contact_allowed = true`: must reject.
- `grounding confidence < 0.6`: must reject.
- missing `robot_id`: must reject.
- wrong plan kind for robot type: must reject or fail execution safely.
- NaN/infinite coordinates: must reject.
- waypoint outside reach radius: must reject.
- empty waypoint and no world grounding: must reject.

The digital twin must never move from a server response until:

1. grounding succeeds;
2. validation succeeds;
3. preview is shown;
4. user presses `CONFIRM`.

## Current Known Limitations

- Real Meta Passthrough Camera frame access requires Meta MRUK v81+,
  `PassthroughCameraAccess`, `horizonos.permission.HEADSET_CAMERA`, and the
  `ORCESTRA_META_PCA` scripting define. Without those, the app deliberately
  falls back to Unity screenshot capture.
- Unknown real-world object height/depth in MR requires
  `com.oculus.permission.USE_SCENE` and MRUK `EnvironmentRaycastManager`, or
  another geometry provider with colliders. Without such geometry, Unity can
  only fall back to a floor-plane point from the image ray.
- Sentis Whisper is implemented but not validated against exported Quest assets.
- Server-side real ASR requires an external ASR runtime.
- Qwen3-VL requires an external vLLM/SGLang/OpenAI-compatible runtime.
- Collision checking is still basic and should be expanded before automatic
  mode.
- Real robot control is intentionally out of scope for this implementation.

## Minimal Acceptance Checklist

Before using this in experiments:

- Unity compiles cleanly.
- EditMode tests pass.
- Local mock command previews and executes for UR3.
- Local mock command previews and executes for KUKA.
- Local mock command previews and executes for Scout.
- FastAPI mock server works over HTTP.
- Quest APK records WAV.
- Quest APK speaks Android TTS reply.
- Server ASR or Sentis ASR produces a transcript.
- Unsafe mock responses are rejected.
- Confirmation is required before every execution.
