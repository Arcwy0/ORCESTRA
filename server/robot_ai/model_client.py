from __future__ import annotations

import base64
import json
import logging
import os
import time
import urllib.error
import urllib.request
import uuid
from typing import Optional

from .schemas import (
    Diagnostics,
    ErrorPayload,
    Intent,
    PlanIr,
    RobotCommandRequest,
    RobotCommandResponse,
    VisualGrounding,
)

logger = logging.getLogger("robot_ai.model_client")


def make_response(
    request: RobotCommandRequest,
    image_bytes: Optional[bytes],
    audio_bytes: Optional[bytes] = None,
) -> RobotCommandResponse:
    mode = os.getenv("ROBOT_AI_MODE", "mock").strip().lower()
    asr_mode = os.getenv("ROBOT_AI_ASR_MODE", "disabled").strip().lower()
    transcript = ""
    asr_latency_ms = 0.0
    if audio_bytes:
        asr_start = time.perf_counter()
        transcript = _transcribe_audio(audio_bytes, asr_mode)
        asr_latency_ms = (time.perf_counter() - asr_start) * 1000.0
        if transcript:
            request = request.model_copy(update={"command_text": transcript})

    if mode == "openai_compatible":
        response = _openai_compatible_response(request, image_bytes)
    else:
        response = _mock_response(request)
    response.diagnostics.gateway_mode = mode
    response.diagnostics.asr_mode = asr_mode
    response.diagnostics.asr_latency_ms = asr_latency_ms
    response.diagnostics.audio_bytes = len(audio_bytes or b"")
    response.diagnostics.transcript_text = transcript
    return response


def _mock_response(request: RobotCommandRequest) -> RobotCommandResponse:
    robot = request.robots[0] if request.robots else None
    robot_id = robot.id if robot else ""
    kind = robot.kind if robot else "manipulator"
    plan_kind = "mobile_route" if kind == "mobile" else "manipulator_reach"
    width = request.camera.width or 1280
    height = request.camera.height or 960
    world = _mock_world_target(request)
    return RobotCommandResponse(
        spoken_reply="Mock plan ready. Please confirm the highlighted motion.",
        intent=Intent(
            robot_id=robot_id,
            task_type=plan_kind,
            target_ref="mock target",
            motion_primitive="approach_standoff",
        ),
        visual_grounding=VisualGrounding(
            label="mock target",
            confidence=0.85,
            preferred_point_px=[width * 0.5, height * 0.5],
            world_position_m=world,
            world_confidence=0.8,
        ),
        plan_ir=PlanIr(
            kind=plan_kind,
            robot_id=robot_id,
            requires_confirmation=True,
            contact_allowed=False,
            min_clearance_m=0.05,
            speed_scale=0.25,
            waypoints=[],
        ),
        diagnostics=Diagnostics(gateway_mode="mock"),
    )


def _mock_world_target(request: RobotCommandRequest) -> list[float]:
    if not request.robots:
        return [0.0, 0.0, 1.0]
    robot = request.robots[0]
    if robot.kind == "mobile":
        root = _vec3(robot.root_position_m)
        return [root[0] + 0.7, root[1], root[2]]
    center = _vec3(robot.reach_center_m)
    offset = min(max(robot.reach_radius_m * 0.35, 0.12), 0.35)
    return [center[0] + offset, center[1], center[2]]


def _vec3(values: list[float]) -> list[float]:
    if len(values) >= 3:
        return [values[0], values[1], values[2]]
    return [0.0, 0.0, 0.0]


def _openai_compatible_response(
    request: RobotCommandRequest, image_bytes: Optional[bytes]
) -> RobotCommandResponse:
    base_url = os.getenv("ROBOT_AI_BASE_URL", "http://localhost:8000/v1").rstrip("/")
    model = os.getenv("ROBOT_AI_MODEL", "Qwen/Qwen3-VL-8B-Instruct")
    api_key = os.getenv("ROBOT_AI_API_KEY", "")
    timeout_s = float(os.getenv("ROBOT_AI_MODEL_TIMEOUT_S", "240"))

    system = (
        "You are a robot digital-twin planning module. Return only one JSON object "
        "and no markdown. The JSON must match this exact top-level schema: "
        "{"
        "\"spoken_reply\": string, "
        "\"intent\": {\"robot_id\": string, \"task_type\": string, "
        "\"target_ref\": string, \"motion_primitive\": string}, "
        "\"visual_grounding\": {\"label\": string, \"confidence\": number, "
        "\"bbox_xyxy_px\": number[], \"preferred_point_px\": number[], "
        "\"world_position_m\": null, \"world_confidence\": number}, "
        "\"plan_ir\": {\"version\": 1, \"kind\": string, \"robot_id\": string, "
        "\"requires_confirmation\": true, \"contact_allowed\": false, "
        "\"min_clearance_m\": number, \"speed_scale\": number, "
        "\"waypoints\": []}, "
        "\"diagnostics\": {\"request_id\": string, \"gateway_mode\": string, "
        "\"gateway_latency_ms\": 0, \"rejection_reason\": string}, "
        "\"error\": null or {\"code\": string, \"message\": string}"
        "}. "
        "Never return intent as a string. Never return visual_grounding or plan_ir "
        "as a list. If the target is not visible, return the same object schema "
        "with error={\"code\":\"not_grounded\",\"message\":\"...\"}. "
        "Do not invent metric 3D coordinates from the image. Prefer bbox/keypoint "
        "grounding and let Unity lift points to world coordinates. contact_allowed "
        "must be false for v1."
    )
    user_text = json.dumps(request.model_dump(), ensure_ascii=False)
    content = [{"type": "text", "text": user_text}]
    if image_bytes:
        b64 = base64.b64encode(image_bytes).decode("ascii")
        content.append(
            {
                "type": "image_url",
                "image_url": {"url": f"data:image/png;base64,{b64}"},
            }
        )

    payload = {
        "model": model,
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": content},
        ],
        "temperature": 0.1,
        "max_tokens": int(os.getenv("ROBOT_AI_MAX_TOKENS", "512")),
    }
    logger.info(
        "forwarding to model base_url=%s model=%s image_bytes=%d timeout_s=%.1f",
        base_url,
        model,
        len(image_bytes or b""),
        timeout_s,
    )
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        f"{base_url}/chat/completions",
        data=data,
        headers={
            "Content-Type": "application/json",
            **({"Authorization": f"Bearer {api_key}"} if api_key else {}),
        },
        method="POST",
    )

    try:
        start = time.perf_counter()
        with urllib.request.urlopen(req, timeout=timeout_s) as resp:
            raw = json.loads(resp.read().decode("utf-8"))
        logger.info(
            "model response received latency_ms=%.1f",
            (time.perf_counter() - start) * 1000.0,
        )
    except (urllib.error.URLError, TimeoutError) as exc:
        raise RuntimeError(f"Model endpoint failed: {exc}") from exc

    text = raw["choices"][0]["message"]["content"]
    if isinstance(text, list):
        text = "".join(part.get("text", "") for part in text)
    text = _extract_json_object(_strip_code_fence(str(text).strip()))
    return _parse_robot_response(text)


def _parse_robot_response(text: str) -> RobotCommandResponse:
    try:
        return RobotCommandResponse.model_validate_json(text)
    except Exception as first_exc:
        try:
            payload = json.loads(text)
            return RobotCommandResponse.model_validate(
                _coerce_robot_response_payload(payload)
            )
        except Exception:
            raise first_exc


def _coerce_robot_response_payload(payload: object) -> dict:
    if not isinstance(payload, dict):
        return {
            "spoken_reply": "I could not create a safe robot plan.",
            "error": {
                "code": "invalid_model_output",
                "message": "Model did not return a JSON object.",
            },
        }

    out = dict(payload)
    if not isinstance(out.get("spoken_reply"), str):
        out["spoken_reply"] = "I could not create a safe robot plan."

    if not isinstance(out.get("intent"), dict):
        intent_text = out.get("intent", "")
        out["intent"] = {
            "robot_id": "",
            "task_type": str(intent_text) if intent_text is not None else "",
            "target_ref": "",
            "motion_primitive": "",
        }

    if not isinstance(out.get("visual_grounding"), dict):
        out["visual_grounding"] = VisualGrounding().model_dump()

    if not isinstance(out.get("plan_ir"), dict):
        out["plan_ir"] = PlanIr().model_dump()

    if not isinstance(out.get("diagnostics"), dict):
        out["diagnostics"] = Diagnostics().model_dump()

    error = out.get("error")
    if isinstance(error, str):
        out["error"] = ErrorPayload(
            code="model_reported_error",
            message=error,
        ).model_dump()
    elif error is not None and not isinstance(error, dict):
        out["error"] = ErrorPayload(
            code="invalid_model_error",
            message=str(error),
        ).model_dump()

    return out


def _transcribe_audio(audio_bytes: bytes, asr_mode: str) -> str:
    if asr_mode in ("", "disabled", "none"):
        return ""
    if asr_mode == "mock":
        return "Move the gripper to the mock target."
    if asr_mode == "openai_compatible":
        return _openai_compatible_transcription(audio_bytes)
    raise RuntimeError(f"Unsupported ROBOT_AI_ASR_MODE: {asr_mode}")


def _openai_compatible_transcription(audio_bytes: bytes) -> str:
    base_url = os.getenv(
        "ROBOT_AI_ASR_BASE_URL",
        os.getenv("ROBOT_AI_BASE_URL", "http://localhost:8000/v1"),
    ).rstrip("/")
    model = os.getenv("ROBOT_AI_ASR_MODEL", "whisper-tiny.en")
    api_key = os.getenv(
        "ROBOT_AI_ASR_API_KEY",
        os.getenv("ROBOT_AI_API_KEY", ""),
    )
    fields = {"model": model, "response_format": "json"}
    files = {
        "file": ("command.wav", "audio/wav", audio_bytes),
    }
    body, content_type = _multipart_body(fields, files)
    req = urllib.request.Request(
        f"{base_url}/audio/transcriptions",
        data=body,
        headers={
            "Content-Type": content_type,
            **({"Authorization": f"Bearer {api_key}"} if api_key else {}),
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            raw = json.loads(resp.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError) as exc:
        raise RuntimeError(f"ASR endpoint failed: {exc}") from exc
    return str(raw.get("text", "")).strip()


def _multipart_body(
    fields: dict[str, str],
    files: dict[str, tuple[str, str, bytes]],
) -> tuple[bytes, str]:
    boundary = "----robot-ai-gateway-" + uuid.uuid4().hex
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode("utf-8"),
                (
                    f'Content-Disposition: form-data; name="{name}"'
                    "\r\n\r\n"
                ).encode("utf-8"),
                str(value).encode("utf-8"),
                b"\r\n",
            ]
        )
    for name, (filename, content_type, data) in files.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode("utf-8"),
                (
                    f'Content-Disposition: form-data; name="{name}"; '
                    f'filename="{filename}"\r\n'
                ).encode("utf-8"),
                f"Content-Type: {content_type}\r\n\r\n".encode("utf-8"),
                data,
                b"\r\n",
            ]
        )
    chunks.append(f"--{boundary}--\r\n".encode("utf-8"))
    return b"".join(chunks), f"multipart/form-data; boundary={boundary}"


def _strip_code_fence(text: str) -> str:
    if text.startswith("```"):
        lines = text.splitlines()
        if lines and lines[0].startswith("```"):
            lines = lines[1:]
        if lines and lines[-1].startswith("```"):
            lines = lines[:-1]
        return "\n".join(lines).strip()
    return text


def _extract_json_object(text: str) -> str:
    if text.startswith("{") and text.endswith("}"):
        return text
    start = text.find("{")
    if start < 0:
        return text
    depth = 0
    in_string = False
    escape = False
    for idx in range(start, len(text)):
        ch = text[idx]
        if escape:
            escape = False
            continue
        if ch == "\\":
            escape = True
            continue
        if ch == '"':
            in_string = not in_string
            continue
        if in_string:
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : idx + 1]
    return text
