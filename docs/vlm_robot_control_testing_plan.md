# MR VLM Robot Control Testing Plan

## Goals

Validate the v1 digital-twin control pipeline before connecting real Qwen3-VL inference:

- Unity can build a robot/scene snapshot.
- Unity can capture an image and send a typed command request.
- Mock server or local mock client returns a typed plan.
- Unity grounds, validates, previews, and executes only after confirmation.
- UR3, KUKA KR600, and Scout V2 all exercise their intended execution path.
- No model weights are downloaded during tests.

Full Unity setup and object/component attachment instructions are in
`docs/unity_ai_robot_control_setup_guide.md`.

## Test Matrix

| Area | Mode | Target | Expected Result |
|---|---|---|---|
| Unity client mock | Editor | UR3 | Preview appears, confirm moves TCP to reachable standoff |
| Unity client mock | Editor | KUKA | Same manipulator schema works with KUKA prefab |
| Unity client mock | Editor | Scout | Preview route appears, confirm drives floor route |
| FastAPI mock | Windows/server | `/v1/robot/command` | Returns schema-valid plan without model download |
| FastAPI OpenAI-compatible | GPU server | vLLM/SGLang endpoint | Returns schema-valid JSON through gateway |
| Screenshot grounding | Editor/Quest | Center-screen object/plane | Pixel point raycasts to collider or floor plane |
| Passthrough fallback | Quest/Editor | Passthrough selected | Falls back to screenshot when API unavailable |
| Safety rejection | Editor | Bad response | Rejects missing robot, wrong kind, out-of-reach, NaN, contact plan |

## Unity Editor Smoke Test

1. Open `C:/Arcwy/1Important/Unity/ORCESTRA` in Unity `6000.4.10f1`.
2. Open `Assets/Scenes/UR3_Demo.unity`.
3. Run `UR3 -> Setup AI Robot Control`.
4. Enter Play Mode.
5. Place at least one robot through the existing robot catalog.
6. In the AI panel, keep mock mode enabled and image source `UnityScreenshot`.
7. Send: `Move the gripper to the cup on the table.`
8. Expected:
   - Status changes to mock plan ready.
   - Yellow preview marker/path appears.
   - `CONFIRM` starts motion.
   - `CANCEL` clears preview and leaves the robot stopped.

## Server Mock Test

Run from the repo root after installing `server/robot_ai/requirements.txt` in the target Python environment:

```powershell
$env:ROBOT_AI_MODE="mock"
python -m uvicorn server.robot_ai.main:app --host 127.0.0.1 --port 8080
```

Then point `RobotAiController.serverUrl` to:

```text
http://127.0.0.1:8080/v1/robot/command
```

Disable `useLocalMock` on the Unity `RobotAiController`.

Expected:

- Unity receives the same typed mock plan through HTTP.
- Server logs show no model loading and no model download.

Run the server unit tests:

```powershell
python -m unittest server.robot_ai.test_model_client
```

If `pydantic` is not installed locally, this exits cleanly with skipped tests.
After installing `server/robot_ai/requirements.txt`, expected result is passing
mock-response and ASR-diagnostics tests.

## GPU Server Integration Test

On the RTX 4090 server:

1. Start Qwen3-VL using vLLM or SGLang with an OpenAI-compatible `/v1/chat/completions` endpoint.
2. Start the FastAPI gateway:

```powershell
$env:ROBOT_AI_MODE="openai_compatible"
$env:ROBOT_AI_BASE_URL="http://127.0.0.1:8000/v1"
$env:ROBOT_AI_MODEL="Qwen/Qwen3-VL-8B-Instruct"
python -m uvicorn server.robot_ai.main:app --host 0.0.0.0 --port 8080
```

3. Point Unity to `http://<server-ip>:8080/v1/robot/command`.

Expected:

- Gateway returns JSON conforming to `RobotCommandResponse`.
- If model output is invalid, gateway returns typed `model_error`.
- Unity does not execute invalid or unsafe plans.

## Validation Cases

Create or mock responses for these cases:

- `contact_allowed=true`: rejected.
- `robot_id` not placed: rejected.
- `kind=manipulator_reach` for Scout: rejected through robot-kind validation or execution mismatch.
- `kind=mobile_route` for UR3/KUKA: rejected through robot-kind validation.
- Waypoint outside manipulator reach: rejected.
- Waypoint contains `NaN` or infinity: rejected.
- Waypoint position has fewer than three coordinates: rejected.
- Grounding confidence `<0.6`: rejected.
- Empty waypoint list with no bbox/keypoint/world position: rejected.
- Valid world-position target near reach center: accepted and previewed.

Run the added Unity EditMode tests from the Unity Test Runner:

```text
Assets/Tests/EditMode/Editor/RobotAiModelTests.cs
```

These cover pure helper behavior: vector packing, finite checks, and JSON
round trip for AI response models.

## Quest Manual Test

1. Build standalone APK.
2. Run on Quest 3/3S.
3. Verify AI panel is visible and clickable with XR ray.
4. Use `UnityScreenshot` source first.
5. Place UR3/KUKA/Scout and repeat the Editor smoke tests.
6. Switch source to `QuestPassthroughCamera`.
7. Expected for current implementation:
   - Status reports passthrough unavailable.
   - Fallback screenshot capture is used.
   - Request still completes in mock or server mode.

## Quest Speech Test

1. Keep `AiAsrBackendMode.ServerGateway` for first end-to-end testing.
2. In the AI panel, press `REC`; speak a short command; press `REC` again.
3. Press `SEND`.
4. Expected:
   - A WAV buffer is captured at 16 kHz mono.
   - Audio is attached to the next server request as `audio/wav`.
   - Android TextToSpeech speaks `spoken_reply` on Quest.
   - In Editor, TTS logs the text instead of speaking.
5. Switch `AiAsrBackendMode.OnDeviceWhisperSentis` only after assigning the
   required model assets from `docs/quest_asr_tts_model_bundle.md`.
6. Expected without assigned assets:
   - The UI reports that Whisper log-mel, encoder, decoder, and vocab assets
     must be assigned.
7. Expected after assigning assets:
   - Sentis ASR returns a transcript or a concrete tensor/runtime error.
   - If a tensor/runtime error appears, compare the exported model input/output
     names and logits axis with `SentisWhisperAsrBackend`.

## Windows Editor Speech Test

For local Unity Editor testing without Quest or server ASR:

1. Use Windows Editor.
2. Set `Use Local Mock = true`.
3. Keep `AiAsrBackendMode.ServerGateway` or set
   `AiAsrBackendMode.WindowsDictation`.
4. Press `REC`, speak into the PC microphone, press `REC` again.
5. Expected:
   - status mentions Windows dictation;
   - the command text field changes to the recognized text.

If `Use Local Mock = false`, `ServerGateway` does not transcribe locally; it
records WAV and sends it to the configured server on `SEND`.

If Windows reports speech recognition is unsupported, enable
`useEditorMockAsrFallback` or set `AiAsrBackendMode.EditorMock`. This validates
the Unity command flow with `editorMockTranscript`; it is not real ASR.

## Quest Speech Latency Measurements

For each ASR backend, log:

- Recording duration: 3 s, 5 s, 10 s.
- WAV size in bytes.
- ASR time from stop-recording to transcript.
- End-to-end time from `SEND` to plan preview.
- TTS start latency after `spoken_reply`.
- Quest FPS or visible frame drops during ASR.
- Peak memory if available from Unity profiler.

Target v1 thresholds:

- Recording stop to server request attach: `<100 ms`.
- Android TTS start after response: `<500 ms`.
- Server ASR + VLM gateway response: `[RESULT TBD]`, measured on RTX 4090.
- On-device ASR target: `<2 s` for a 5 s command clip if Whisper tiny.en is
  feasible; otherwise use server ASR or native plugin.

## Metrics to Log Later

- Time from command submit to preview.
- Time from confirm to motion start.
- Grounding confidence and source.
- Gateway mode and gateway latency from `diagnostics`.
- Accepted/rejected plan count.
- Rejection reason.
- Robot type and task type.
- Execution completion/failure.
- ASR backend, TTS backend, WAV duration, WAV bytes.

## Known Gaps

- Real Meta Passthrough Camera API integration is not implemented.
- Server gateway can forward audio to an external OpenAI-compatible ASR
  endpoint, but it does not load ASR weights itself.
- Android TTS is implemented as the Quest v1 speech-output backend.
- On-device Whisper/Sentis ASR has backend plumbing and greedy decoding, but it
  still requires chosen model assets and Quest shape/latency validation.
- Collision validation is basic and should be expanded before automatic mode.
- The current gateway extracts a JSON object from wrapped model text/code fences,
  but does not repair malformed JSON; invalid JSON returns a typed model error.
