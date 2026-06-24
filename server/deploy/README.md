# ORCESTRA Robot-AI Server Deployment

This package runs:

- `vllm`: OpenAI-compatible Qwen3-VL server.
- `gateway`: FastAPI robot gateway used by Unity.
- `downloader`: one-shot model download container.

Recommended first model for RTX 4090 24 GB:

```text
Qwen/Qwen3-VL-8B-Instruct-FP8
```

The model is downloaded locally into `MODEL_DIR`, then mounted read-only into
the vLLM container as `/models/qwen3-vl`.

## 1. Server Prerequisites

On the Linux GPU server:

```bash
nvidia-smi
docker --version
docker compose version
```

Check Docker GPU access:

```bash
sh scripts/check_gpu.sh
```

If `nvidia-smi` works on the host but fails inside Docker, install/configure
NVIDIA Container Toolkit before continuing.

## 2. Unpack

Copy the zip to the server, then:

```bash
mkdir -p /workspace/orcestra_robot_ai
unzip orcestra_robot_ai_server_bundle.zip -d /workspace/orcestra_robot_ai
cd /workspace/orcestra_robot_ai/server/deploy
```

## 3. Configure

```bash
cp .env.example .env
```

Edit `.env`:

```text
MODEL_DIR=/workspace/models/qwen3-vl-8b-instruct-fp8
HF_CACHE_DIR=/workspace/hf_cache
VLM_MODEL_ID=Qwen/Qwen3-VL-8B-Instruct-FP8
VLM_MODEL_NAME=Qwen/Qwen3-VL-8B-Instruct-FP8
VLLM_MAX_MODEL_LEN=8192
VLLM_GPU_MEMORY_UTILIZATION=0.88
```

Set `HF_TOKEN` only if Hugging Face requires it for download/rate limits. Do not
commit `.env`.

For Qwen3-VL, keep:

```text
ROBOT_AI_VLM_COORD_FORMAT=qwen_1000
ROBOT_AI_VLM_IMAGE_MAX_SIDE=896
```

Qwen3-VL grounding uses a 0-1000 relative image grid. The gateway scales that
grid to the submitted `request.camera.width`/`request.camera.height` before it
saves annotated images and before Unity lifts the point to 3D. `auto` treats
Qwen models as `qwen_1000`; use `pixel` only for a model that always returns
true image pixels.
The gateway still saves the original request image; `ROBOT_AI_VLM_IMAGE_MAX_SIDE`
only controls the resized copy sent into vLLM.

## 4. Download Model Locally

This uses a temporary downloader container and writes into `MODEL_DIR`:

```bash
docker compose --profile download run --rm downloader
```

If you see:

```text
Warning: `huggingface-cli` is deprecated and no longer works. Use `hf` instead.
```

you are using an old copy of this deployment file. The downloader command
should use `hf download`, not `huggingface-cli download`.

Check:

```bash
ls -lah "$MODEL_DIR"
```

You should see model config/tokenizer/safetensors files.

## 5. Start vLLM + Gateway

```bash
docker compose up --build
```

In another terminal:

```bash
curl http://127.0.0.1:8000/v1/models
curl http://127.0.0.1:8080/health
```

If 8B-FP8 does not fit or vLLM fails on FP8 kernels, reduce:

```text
VLLM_MAX_MODEL_LEN=4096
VLLM_GPU_MEMORY_UTILIZATION=0.82
```

If it still fails, switch to:

```text
VLM_MODEL_ID=Qwen/Qwen3-VL-4B-Instruct
VLM_MODEL_NAME=Qwen/Qwen3-VL-4B-Instruct
MODEL_DIR=/workspace/models/qwen3-vl-4b-instruct
```

then rerun the downloader.

## 6. Smoke Test vLLM

```bash
python3 scripts/smoke_vllm.py
```

Expected: JSON response from `/v1/chat/completions`.

## 7. Smoke Test Robot Gateway

```bash
python3 scripts/smoke_gateway.py
```

Expected: gateway returns a `RobotCommandResponse` JSON object. If model output
is not strict JSON, the gateway returns `model_error`; then tune the system
prompt or model generation settings.

## 8. Connect Unity

In Unity `AI_RobotControl` / `RobotAiController`:

```text
Use Local Mock = false
Server Url = http://<server-ip>:8080/v1/robot/command
Image Source Mode = UnityScreenshot
Asr Backend Mode = ServerGateway
```

For first VLM test, use text input in the Unity panel. Keep audio ASR disabled
until visual grounding is working.

## 9. ASR

The compose stack does not run a speech-to-text model by default.

Default:

```text
ROBOT_AI_ASR_MODE=disabled
```

For gateway-only tests:

```text
ROBOT_AI_ASR_MODE=mock
```

For real ASR, run a separate OpenAI-compatible `/audio/transcriptions` service
and set:

```text
ROBOT_AI_ASR_MODE=openai_compatible
ROBOT_AI_ASR_BASE_URL=http://<asr-host>:<port>/v1
ROBOT_AI_ASR_MODEL=<model-name>
```

Recommended local server ASR:

```bash
docker compose --profile download-speech run --rm speech-downloader
```

Then set in `.env`:

```text
ROBOT_AI_ASR_MODE=faster_whisper
ROBOT_AI_ASR_MODEL_DIR_HOST=/workspace/models/asr/faster-whisper-base.en
ROBOT_AI_ASR_DEVICE=cpu
ROBOT_AI_ASR_COMPUTE_TYPE=int8
```

Unity calls `/v1/audio/transcribe_json` when recording stops, so the menu text
changes before the VLM request is sent.

## 10. TTS

TTS is Unity-side, not server-side. The gateway returns only `spoken_reply`.
Use either Android TextToSpeech on Quest or package a Piper voice in:

```text
Assets/StreamingAssets/TTS/piper-en_US-lessac-medium
```

Then set Unity `RobotAiController -> Tts Backend Mode` to
`PiperNativePlugin`.

## 11. Saved Screenshots and VLM Traces

By default:

```text
ROBOT_AI_SAVE_IMAGES=1
ROBOT_AI_SAVE_TRACES=1
ROBOT_AI_OUTPUT_DIR_HOST=./outputs/robot_ai
```

The host directory above is bind-mounted to the gateway container at
`/workspace/outputs/robot_ai`. With the default relative path, run commands from
`server/deploy` and the gateway writes raw and annotated images on the Docker
host here:

```text
server/deploy/outputs/robot_ai/*_raw.png
server/deploy/outputs/robot_ai/*_annotated.png
server/deploy/outputs/robot_ai/*_trace.json
```

Use an absolute host path if you want the screenshots elsewhere, for example:

```text
ROBOT_AI_OUTPUT_DIR_HOST=/media/imit-learn/ISR_2T3/VR_October/orcestra_robot_ai/outputs/robot_ai
```

Open `*_trace.json` to validate model numbers. The important fields are:

```text
request.command_text
raw_model_output
response_after_coordinate_normalization
response_before_repair
final_response.intent.motion_primitive
final_response.plan_ir.waypoints
```

`raw_model_output` is the exact VLM JSON text. `response_before_repair` is that
JSON after schema parsing and image-coordinate conversion. For Qwen, compare
`raw_model_output` against `response_after_coordinate_normalization` to verify
0-1000 boxes were scaled to screenshot pixels. `final_response` is what Unity
actually previews and executes after deterministic gateway repairs such as
decimal distance parsing, known-object grounding, unknown-object image
grounding handoff, and circle waypoint generation.

## 12. Useful Commands

Stop:

```bash
docker compose down
```

Logs:

```bash
docker compose logs -f vllm
docker compose logs -f gateway
```

Restart gateway only:

```bash
docker compose up --build gateway
```

Restart vLLM after changing `.env`:

```bash
docker compose up --force-recreate vllm gateway
```

## 13. Ports

- vLLM OpenAI API: `8000`
- FastAPI robot gateway: `8080`

Unity should talk only to the gateway on `8080`, not directly to vLLM.

The current Unity client defaults to JSON transport. In the inspector, keep:

```text
Server Url = http://<server-ip>:8080/v1/robot/command
```

The client automatically posts to `/v1/robot/command_json` when JSON transport
is enabled. This avoids Unity multipart upload edge cases in Editor and Quest
tests.

For Windows Unity Editor testing through SSH or Tailscale, use a local tunnel:

```powershell
ssh -N -L 18080:127.0.0.1:8080 <server-user>@<server-tailscale-or-lan-ip>
```

Then set:

```text
Server Url = http://127.0.0.1:18080/v1/robot/command
```

## 14. Source Notes

vLLM exposes an OpenAI-compatible server and supports Docker deployment. The
Qwen model cards provide vLLM/SGLang launch examples. The gateway is intentionally
thin and forwards to vLLM through `/v1/chat/completions`.
