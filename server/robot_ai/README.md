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

The gateway forwards WAV bytes and replaces `command_text` with the transcript
when ASR returns non-empty text. It still does not download or load ASR model
weights; run the ASR runtime separately on the GPU server.
