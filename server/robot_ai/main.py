from __future__ import annotations

import base64
import json
import logging
import time
from typing import Optional

from fastapi import FastAPI, File, Form, UploadFile
from pydantic import BaseModel, Field

from .model_client import make_response, transcribe_audio_bytes
from .schemas import (
    Diagnostics,
    ErrorPayload,
    RobotCommandRequest,
    RobotCommandResponse,
)

app = FastAPI(title="Robot AI Gateway")
logger = logging.getLogger("robot_ai.gateway")


class RobotCommandJsonPayload(BaseModel):
    request: RobotCommandRequest
    image_png_base64: str = ""
    audio_wav_base64: str = ""


class AudioTranscribeJsonPayload(BaseModel):
    audio_wav_base64: str = ""


class AudioTranscribeResponse(BaseModel):
    text: str = ""
    diagnostics: Diagnostics = Field(default_factory=Diagnostics)
    error: Optional[ErrorPayload] = None


@app.get("/health")
def health() -> dict[str, str]:
    return {"status": "ok"}


@app.post("/v1/robot/command", response_model=RobotCommandResponse)
async def robot_command(
    request_json: str = Form(...),
    image: Optional[UploadFile] = File(None),
    audio: Optional[UploadFile] = File(None),
) -> RobotCommandResponse:
    start = time.perf_counter()
    logger.info("robot_command received")
    try:
        request = RobotCommandRequest.model_validate_json(request_json)
    except Exception as exc:
        logger.exception("bad request_json")
        return _error("bad_request", f"Invalid request_json: {exc}")

    image_bytes = await image.read() if image is not None else None
    audio_bytes = await audio.read() if audio is not None else None
    logger.info(
        "request parsed session=%s image_source=%s command_len=%d robots=%d "
        "image_bytes=%d audio_bytes=%d",
        request.session_id,
        request.image_source,
        len(request.command_text or ""),
        len(request.robots or []),
        len(image_bytes or b""),
        len(audio_bytes or b""),
    )
    try:
        response = make_response(request, image_bytes, audio_bytes)
        if response.diagnostics is None:
            response.diagnostics = Diagnostics()
        response.diagnostics.request_id = request.session_id
        response.diagnostics.audio_bytes = len(audio_bytes or b"")
        response.diagnostics.gateway_latency_ms = (
            time.perf_counter() - start
        ) * 1000.0
        logger.info(
            "robot_command completed session=%s latency_ms=%.1f",
            request.session_id,
            response.diagnostics.gateway_latency_ms,
        )
        return response
    except Exception as exc:
        logger.exception("robot_command failed")
        return _error("model_error", str(exc))


@app.post("/v1/robot/command_json", response_model=RobotCommandResponse)
async def robot_command_json(
    payload: RobotCommandJsonPayload,
) -> RobotCommandResponse:
    start = time.perf_counter()
    request = payload.request
    logger.info("robot_command_json received")
    try:
        image_bytes = _decode_optional_base64(payload.image_png_base64)
        audio_bytes = _decode_optional_base64(payload.audio_wav_base64)
        logger.info(
            "json request parsed session=%s image_source=%s command_len=%d "
            "robots=%d image_bytes=%d audio_bytes=%d",
            request.session_id,
            request.image_source,
            len(request.command_text or ""),
            len(request.robots or []),
            len(image_bytes or b""),
            len(audio_bytes or b""),
        )
        response = make_response(request, image_bytes, audio_bytes)
        if response.diagnostics is None:
            response.diagnostics = Diagnostics()
        response.diagnostics.request_id = request.session_id
        response.diagnostics.audio_bytes = len(audio_bytes or b"")
        response.diagnostics.gateway_latency_ms = (
            time.perf_counter() - start
        ) * 1000.0
        logger.info(
            "robot_command_json completed session=%s latency_ms=%.1f",
            request.session_id,
            response.diagnostics.gateway_latency_ms,
        )
        return response
    except Exception as exc:
        logger.exception("robot_command_json failed")
        return _error("model_error", str(exc))


@app.post("/v1/audio/transcribe", response_model=AudioTranscribeResponse)
async def audio_transcribe(
    audio: Optional[UploadFile] = File(None),
) -> AudioTranscribeResponse:
    if audio is None:
        return _transcribe_error("bad_request", "Missing audio file.")
    audio_bytes = await audio.read()
    return _transcribe(audio_bytes)


@app.post("/v1/audio/transcribe_json", response_model=AudioTranscribeResponse)
async def audio_transcribe_json(
    payload: AudioTranscribeJsonPayload,
) -> AudioTranscribeResponse:
    try:
        audio_bytes = _decode_optional_base64(payload.audio_wav_base64)
    except Exception as exc:
        return _transcribe_error("bad_request", f"Invalid base64 audio: {exc}")
    if not audio_bytes:
        return _transcribe_error("bad_request", "Missing audio_wav_base64.")
    return _transcribe(audio_bytes)


def _transcribe(audio_bytes: bytes) -> AudioTranscribeResponse:
    try:
        text, diagnostics = transcribe_audio_bytes(audio_bytes)
        return AudioTranscribeResponse(text=text, diagnostics=diagnostics)
    except Exception as exc:
        logger.exception("audio transcription failed")
        return _transcribe_error("asr_error", str(exc))


def _decode_optional_base64(value: str) -> Optional[bytes]:
    if not value:
        return None
    return base64.b64decode(value)


def _error(code: str, message: str) -> RobotCommandResponse:
    return RobotCommandResponse(
        spoken_reply="I could not create a safe robot plan.",
        error=ErrorPayload(code=code, message=message),
    )


def _transcribe_error(code: str, message: str) -> AudioTranscribeResponse:
    return AudioTranscribeResponse(
        error=ErrorPayload(code=code, message=message),
    )


@app.post("/v1/robot/mock", response_model=RobotCommandResponse)
async def robot_mock(request: RobotCommandRequest) -> RobotCommandResponse:
    return make_response(request, None)
