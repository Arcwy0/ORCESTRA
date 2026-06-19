# ORCESTRA Robot AI Server

This folder contains the server-side part of the ORCESTRA VLM robot-control
pipeline.

- `robot_ai/`: FastAPI gateway used by Unity.
- `deploy/`: Docker Compose deployment for Qwen3-VL through vLLM plus the
  FastAPI gateway.

The gateway does not contain model weights and does not download a model during
normal startup. Model weights are expected to live on the GPU server and are
mounted into the vLLM container.

## Recommended Model

For an RTX 4090 24 GB server, start with:

```text
Qwen/Qwen3-VL-8B-Instruct-FP8
```

Use `Qwen/Qwen3-VL-4B-Instruct` if the 8B FP8 model is too slow or does not fit
with the desired context length.

## GPU Server Setup

On the Linux GPU server:

```bash
cd /workspace/orcestra_robot_ai/server/deploy
cp .env.example .env
```

Edit `.env`:

```text
MODEL_DIR=/workspace/models/qwen3-vl-8b-instruct-fp8
HF_CACHE_DIR=/workspace/hf_cache
VLM_MODEL_ID=Qwen/Qwen3-VL-8B-Instruct-FP8
VLM_MODEL_NAME=Qwen/Qwen3-VL-8B-Instruct-FP8
```

Optional Hugging Face authentication goes only in the local `.env` file:

```text
HF_TOKEN=
```

Do not commit `.env`.

Download model files into `MODEL_DIR`:

```bash
docker compose --profile download run --rm downloader
```

Start vLLM and the gateway:

```bash
docker compose up --build
```

Health checks:

```bash
curl http://127.0.0.1:8000/v1/models
curl http://127.0.0.1:8080/health
```

## Unity Connection

Unity should call the FastAPI gateway on port `8080`, not vLLM on port `8000`.

Recommended first setup in `RobotAiController`:

```text
Use Local Mock = false
Server Url = http://<server-ip>:8080/v1/robot/command
Image Source Mode = UnityScreenshot
Asr Backend Mode = ServerGateway
```

The Unity client defaults to JSON transport and automatically maps
`/v1/robot/command` to `/v1/robot/command_json`. Keep the inspector URL as
`/v1/robot/command` unless you are debugging the raw endpoint.

For a local Windows Unity Editor session over SSH/Tailscale, tunnel the gateway:

```powershell
ssh -N -L 18080:127.0.0.1:8080 <server-user>@<server-tailscale-or-lan-ip>
```

Then set:

```text
Server Url = http://127.0.0.1:18080/v1/robot/command
```

If Unity reports `Insecure connection not allowed`, enable HTTP for development
in Unity Player Settings or use the committed project setting
`insecureHttpOption = 2`. For production builds, put the gateway behind HTTPS.

## Smoke Tests

From `server/deploy` on the GPU server:

```bash
python3 scripts/smoke_vllm.py
python3 scripts/smoke_gateway.py
```

Expected result:

- `smoke_vllm.py` returns a JSON completion from vLLM.
- `smoke_gateway.py` returns a schema-compatible `RobotCommandResponse`.

## Endpoint Summary

- `GET /health`: gateway health.
- `POST /v1/robot/command`: multipart form with `request_json`, optional PNG,
  optional WAV.
- `POST /v1/robot/command_json`: JSON body with request plus base64 PNG/WAV.

Current Unity tests should use `/v1/robot/command_json` through the client
default JSON transport because it is more reliable in Unity Editor and Quest
builds.

## Server ASR

Download the recommended ASR weights on the GPU server:

```bash
cd /workspace/orcestra_robot_ai/server/deploy
docker compose --profile download-speech run --rm speech-downloader
```

This writes:

```text
/workspace/models/asr/faster-whisper-base.en
```

Enable server ASR in `.env`:

```text
ROBOT_AI_ASR_MODE=faster_whisper
ROBOT_AI_ASR_MODEL_DIR_HOST=/workspace/models/asr/faster-whisper-base.en
ROBOT_AI_ASR_DEVICE=cpu
ROBOT_AI_ASR_COMPUTE_TYPE=int8
```

Unity `ServerGateway` ASR now calls `/v1/audio/transcribe_json` immediately
after recording stops and writes the transcript into the command input field.

TTS is intentionally not server-side. Unity speaks `spoken_reply` locally.
For packaged model-based TTS, put the Piper voice under
`Assets/StreamingAssets/TTS/piper-en_US-lessac-medium` and set
`Tts Backend Mode = PiperNativePlugin` in `RobotAiController`.

## Saved Images

The gateway saves every received screenshot by default:

```text
/workspace/outputs/robot_ai/*_raw.png
/workspace/outputs/robot_ai/*_annotated.png
```

The annotated image includes returned bounding boxes and preferred image points
when the VLM provides them. Paths are also returned in response diagnostics:

```text
diagnostics.saved_image_path
diagnostics.saved_annotated_image_path
```
