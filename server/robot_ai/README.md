# Robot AI Gateway

Thin FastAPI gateway for Unity robot commands.

This service does not download or load model weights. In `mock` mode it returns
deterministic plans for Unity development. In `openai_compatible` mode it
forwards prompts to a separate model runtime such as vLLM or SGLang serving
Qwen3-VL on the GPU server.

## Run in mock mode

```powershell
uv run uvicorn server.robot_ai.main:app --host 0.0.0.0 --port 8080
```

Equivalent without `uv`, inside an environment that already has dependencies:

```powershell
python -m uvicorn server.robot_ai.main:app --host 0.0.0.0 --port 8080
```

## Environment

```text
ROBOT_AI_MODE=mock
ROBOT_AI_BASE_URL=http://localhost:8000/v1
ROBOT_AI_MODEL=Qwen/Qwen3-VL-8B-Instruct
ROBOT_AI_API_KEY=
ROBOT_AI_ASR_MODE=disabled
ROBOT_AI_ASR_BASE_URL=http://localhost:8001/v1
ROBOT_AI_ASR_MODEL=whisper-tiny.en
ROBOT_AI_ASR_API_KEY=
ROBOT_AI_SAVE_IMAGES=1
ROBOT_AI_OUTPUT_DIR=/workspace/outputs/robot_ai
```

Use `ROBOT_AI_MODE=openai_compatible` only on the server where vLLM/SGLang or
another OpenAI-compatible runtime is already running.

## Audio

`/v1/robot/command` accepts an optional `audio` multipart file and request
metadata fields `audio_format` / `audio_sample_rate_hz`.

ASR is disabled by default:

```text
ROBOT_AI_ASR_MODE=disabled
```

For gateway tests without a model:

```text
ROBOT_AI_ASR_MODE=mock
```

For a real server-side ASR service exposing an OpenAI-compatible
`/audio/transcriptions` endpoint:

```text
ROBOT_AI_ASR_MODE=openai_compatible
ROBOT_AI_ASR_BASE_URL=http://127.0.0.1:8001/v1
ROBOT_AI_ASR_MODEL=whisper-tiny.en
```

For local faster-whisper ASR:

```text
ROBOT_AI_ASR_MODE=faster_whisper
ROBOT_AI_ASR_MODEL_DIR=/models/asr/faster-whisper-base.en
ROBOT_AI_ASR_DEVICE=cpu
ROBOT_AI_ASR_COMPUTE_TYPE=int8
```

The gateway forwards WAV bytes and replaces `command_text` with the transcript
when ASR returns non-empty text. It still does not download or load ASR model
weights during normal startup; download weights with the deploy downloader.

`/v1/audio/transcribe` and `/v1/audio/transcribe_json` transcribe audio without
also sending a VLM request. Unity uses this endpoint when recording stops so the
menu text shows the actual command before `SEND`.

## TTS

TTS is not run in this gateway. The server returns `spoken_reply`; Unity speaks
that text locally through Android TextToSpeech or the Unity-side Piper native
plugin.

## Images

With `ROBOT_AI_SAVE_IMAGES=1`, the gateway writes:

```text
*_raw.png
*_annotated.png
```

under `ROBOT_AI_OUTPUT_DIR`.
When launched with `server/deploy/docker-compose.yml`, that container path is
bind-mounted from `ROBOT_AI_OUTPUT_DIR_HOST` on the Docker host.

With `ROBOT_AI_SAVE_TRACES=1`, the gateway also writes `*_trace.json` files
containing the request, raw VLM output, parsed pre-repair response, and final
response sent to Unity.
