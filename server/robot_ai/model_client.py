from __future__ import annotations

import base64
import json
import logging
import math
import os
import re
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path
from typing import Optional

from .schemas import (
    Diagnostics,
    ErrorPayload,
    Intent,
    KnownSceneObject,
    PlanIr,
    RobotCommandRequest,
    RobotCommandResponse,
    RobotSnapshot,
    VisualGrounding,
    Waypoint,
)

logger = logging.getLogger("robot_ai.model_client")
_ASR_MODEL = None


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

    response = _repair_or_override_plan(request, response)

    saved_raw, saved_annotated = _maybe_save_images(
        request, image_bytes, response)

    response.diagnostics.gateway_mode = mode
    response.diagnostics.asr_mode = asr_mode
    response.diagnostics.asr_latency_ms = asr_latency_ms
    response.diagnostics.audio_bytes = len(audio_bytes or b"")
    response.diagnostics.transcript_text = transcript
    response.diagnostics.saved_image_path = saved_raw
    response.diagnostics.saved_annotated_image_path = saved_annotated
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
    if values is not None and len(values) >= 3:
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
        "\"world_position_m\": null or number[], \"world_confidence\": number}, "
        "\"plan_ir\": {\"version\": 1, \"kind\": string, \"robot_id\": string, "
        "\"requires_confirmation\": true, \"contact_allowed\": false, "
        "\"min_clearance_m\": number, \"speed_scale\": number, "
        "\"waypoints\": [] or [{\"position_m\": number[], "
        "\"tcp_orientation_xyzw\": null, \"speed_scale\": number, "
        "\"dwell_s\": number}]}, "
        "\"diagnostics\": {\"request_id\": string, \"gateway_mode\": string, "
        "\"gateway_latency_ms\": 0, \"rejection_reason\": string}, "
        "\"error\": null or {\"code\": string, \"message\": string}"
        "}. "
        "Never return intent as a string. Never return visual_grounding or plan_ir "
        "as a list. If the target is not visible, return the same object schema "
        "with error={\"code\":\"not_grounded\",\"message\":\"...\"}. "
        "Do not infer metric 3D coordinates from image pixels alone. If a "
        "referenced object appears in known_scene_objects, you may use that "
        "object's provided position_m as world_position_m. If the command is a "
        "relative TCP motion such as up/down/left/right/forward/back by a "
        "distance, use the selected robot's tcp_position_m and output a waypoint. "
        "Do not reject as out_of_reach from visual judgment; the gateway and "
        "Unity will validate metric reach using reach_center_m/reach_radius_m. "
        "For visible unknown objects, return bbox/preferred_point_px and no "
        "world coordinate. contact_allowed must be false for v1."
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


def _repair_or_override_plan(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> RobotCommandResponse:
    """Use metric scene state for cases where VLM text is not authoritative."""
    if response.diagnostics is None:
        response.diagnostics = Diagnostics()

    relative = _relative_tcp_waypoint(request)
    if relative is not None:
        robot, waypoint, distance_m, direction_name = relative
        response.error = None
        response.intent = Intent(
            robot_id=robot.id,
            task_type="manipulator_reach",
            target_ref="relative_tcp",
            motion_primitive=f"relative_{direction_name}_{distance_m:.3f}m",
        )
        response.visual_grounding = VisualGrounding(
            label="relative_tcp",
            confidence=1.0,
            world_position_m=waypoint,
            world_confidence=1.0,
        )
        response.plan_ir = PlanIr(
            kind="manipulator_reach",
            robot_id=robot.id,
            requires_confirmation=True,
            contact_allowed=False,
            min_clearance_m=0.05,
            speed_scale=0.2,
            waypoints=[Waypoint(position_m=waypoint, speed_scale=0.2)],
        )
        response.spoken_reply = (
            f"Moving the TCP {direction_name} by {distance_m:.2f} meters. "
            "Please confirm the preview."
        )
        return response

    match = _known_object_target(request, response)
    if match is not None:
        robot, obj, waypoint = match
        model_rejected_reach = (
            response.error is not None and
            _mentions_reach(response.error.message)
        )
        missing_plan = (
            response.plan_ir is None or
            response.plan_ir.waypoints is None or
            len(response.plan_ir.waypoints) == 0
        )
        missing_world = (
            response.visual_grounding is None or
            response.visual_grounding.world_position_m is None
        )
        known_object_requested = _label_score(
            _norm(request.command_text),
            _norm(f"{obj.label} {obj.id}"),
        ) > 0
        if model_rejected_reach or missing_plan or missing_world or known_object_requested:
            response.error = None
            response.intent = Intent(
                robot_id=robot.id,
                task_type="manipulator_reach"
                if robot.kind != "mobile" else "mobile_route",
                target_ref=obj.label,
                motion_primitive="object_relative_reach",
            )
            response.visual_grounding = VisualGrounding(
                label=obj.label,
                confidence=max(
                    response.visual_grounding.confidence
                    if response.visual_grounding else 0.0,
                    0.95,
                ),
                bbox_xyxy_px=response.visual_grounding.bbox_xyxy_px
                if response.visual_grounding else [],
                preferred_point_px=response.visual_grounding.preferred_point_px
                if response.visual_grounding else [],
                world_position_m=obj.position_m,
                world_confidence=0.95,
            )
            plan_kind = "mobile_route" if robot.kind == "mobile" else "manipulator_reach"
            response.plan_ir = PlanIr(
                kind=plan_kind,
                robot_id=robot.id,
                requires_confirmation=True,
                contact_allowed=False,
                min_clearance_m=0.05,
                speed_scale=0.2,
                waypoints=[Waypoint(position_m=waypoint, speed_scale=0.2)],
            )
            response.spoken_reply = (
                f"I found the {obj.label} in the Unity scene. "
                "Please confirm the highlighted motion."
            )
        elif response.visual_grounding is not None:
            response.visual_grounding.world_position_m = (
                response.visual_grounding.world_position_m or obj.position_m
            )
            response.visual_grounding.world_confidence = max(
                response.visual_grounding.world_confidence,
                0.9,
            )
    elif response.error is not None and _mentions_reach(response.error.message):
        has_image_grounding = (
            response.visual_grounding is not None and
            (
                len(response.visual_grounding.preferred_point_px or []) >= 2 or
                len(response.visual_grounding.bbox_xyxy_px or []) >= 4
            )
        )
        if has_image_grounding:
            logger.info(
                "clearing model reach rejection because image grounding exists; "
                "Unity will validate metric reach"
            )
            response.error = None
            if response.plan_ir is None:
                response.plan_ir = PlanIr()
            robot = _select_robot(request, response)
            if robot is not None:
                response.plan_ir.robot_id = response.plan_ir.robot_id or robot.id
                response.plan_ir.kind = (
                    "mobile_route" if robot.kind == "mobile"
                    else "manipulator_reach"
                )

    return response


def _relative_tcp_waypoint(
    request: RobotCommandRequest,
) -> Optional[tuple[RobotSnapshot, list[float], float, str]]:
    command = _norm(request.command_text)
    if not any(word in command for word in ("up", "down", "left", "right", "forward", "back")):
        return None
    if not any(word in command for word in ("move", "raise", "lower", "shift", "translate")):
        return None
    if not any(word in command for word in ("gripper", "tcp", "end effector", "tool")):
        return None

    robot = _first_manipulator(request)
    if robot is None:
        return None

    distance_m = _parse_distance_m(command)
    if distance_m is None:
        distance_m = 0.10
    distance_m = min(max(distance_m, 0.005), 0.50)

    direction_name = "up"
    direction = [0.0, 1.0, 0.0]
    if "down" in command or "lower" in command:
        direction_name = "down"
        direction = [0.0, -1.0, 0.0]
    elif "left" in command:
        direction_name = "left"
        direction = [-1.0, 0.0, 0.0]
    elif "right" in command:
        direction_name = "right"
        direction = [1.0, 0.0, 0.0]
    elif "forward" in command:
        direction_name = "forward"
        direction = [0.0, 0.0, 1.0]
    elif "back" in command:
        direction_name = "back"
        direction = [0.0, 0.0, -1.0]

    tcp = _vec3(robot.tcp_position_m)
    waypoint = [
        tcp[0] + direction[0] * distance_m,
        tcp[1] + direction[1] * distance_m,
        tcp[2] + direction[2] * distance_m,
    ]
    if not _within_reach(robot, waypoint, tolerance_m=0.08):
        center = _vec3(robot.reach_center_m)
        radius = max(robot.reach_radius_m * 0.98, 0.05)
        waypoint = _clip_to_reach_sphere(center, waypoint, radius)
    return robot, waypoint, distance_m, direction_name


def _known_object_target(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> Optional[tuple[RobotSnapshot, KnownSceneObject, list[float]]]:
    if not request.known_scene_objects:
        return None

    query_parts = [request.command_text]
    if response.intent is not None:
        query_parts.extend([response.intent.target_ref, response.intent.motion_primitive])
    if response.visual_grounding is not None:
        query_parts.append(response.visual_grounding.label)
    query = _norm(" ".join(part or "" for part in query_parts))

    best_obj = None
    best_score = 0
    for obj in request.known_scene_objects:
        label = _norm(f"{obj.label} {obj.id}")
        score = _label_score(query, label)
        if score > best_score:
            best_score = score
            best_obj = obj

    if best_obj is None or best_score <= 0:
        return None

    robot = _select_robot(request, response)
    if robot is None:
        return None

    target = _vec3(best_obj.position_m)
    if robot.kind == "mobile":
        return robot, best_obj, [target[0], _vec3(robot.root_position_m)[1], target[2]]

    clearance = 0.05
    if len(best_obj.size_m or []) >= 3:
        clearance += max(best_obj.size_m[1] * 0.5, 0.0)
    waypoint = [target[0], target[1] + clearance, target[2]]
    if not _within_reach(robot, waypoint, tolerance_m=0.08):
        center = _vec3(robot.reach_center_m)
        radius = max(robot.reach_radius_m * 0.98, 0.05)
        waypoint = _clip_to_reach_sphere(center, waypoint, radius)
    return robot, best_obj, waypoint


def _select_robot(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> Optional[RobotSnapshot]:
    robot_id = ""
    if response.plan_ir is not None:
        robot_id = response.plan_ir.robot_id or ""
    if not robot_id and response.intent is not None:
        robot_id = response.intent.robot_id or ""
    if robot_id:
        for robot in request.robots:
            if robot.id == robot_id:
                return robot
    return request.robots[0] if request.robots else None


def _first_manipulator(request: RobotCommandRequest) -> Optional[RobotSnapshot]:
    for robot in request.robots:
        if robot.kind != "mobile":
            return robot
    return None


def _parse_distance_m(command: str) -> Optional[float]:
    match = re.search(
        r"(\d+(?:[\.,]\d+)?)\s*(centimeters?|centimetres?|cm|meters?|metres?|m)\b",
        command,
    )
    if not match:
        return None
    value = float(match.group(1).replace(",", "."))
    unit = match.group(2)
    if unit in ("cm", "centimeter", "centimeters", "centimetre", "centimetres"):
        return value / 100.0
    return value


def _within_reach(
    robot: RobotSnapshot,
    point: list[float],
    tolerance_m: float = 0.0,
) -> bool:
    if robot.kind == "mobile" or robot.reach_radius_m <= 0:
        return True
    center = _vec3(robot.reach_center_m)
    return _distance(center, point) <= robot.reach_radius_m + tolerance_m


def _clip_to_reach_sphere(
    center: list[float],
    point: list[float],
    radius: float,
) -> list[float]:
    dx = point[0] - center[0]
    dy = point[1] - center[1]
    dz = point[2] - center[2]
    dist = math.sqrt(dx * dx + dy * dy + dz * dz)
    if dist <= radius or dist < 1e-6:
        return point
    scale = radius / dist
    return [center[0] + dx * scale, center[1] + dy * scale, center[2] + dz * scale]


def _distance(a: list[float], b: list[float]) -> float:
    return math.sqrt(
        (a[0] - b[0]) ** 2 +
        (a[1] - b[1]) ** 2 +
        (a[2] - b[2]) ** 2
    )


def _label_score(query: str, label: str) -> int:
    if not query or not label:
        return 0
    if label in query or query in label:
        return len(label)
    score = 0
    for token in label.split():
        if len(token) >= 3 and token in query:
            score += len(token)
    return score


def _mentions_reach(message: str) -> bool:
    msg = _norm(message)
    return "reach" in msg or "workspace" in msg or "too far" in msg


def _norm(value: str) -> str:
    return re.sub(r"[^a-z0-9 ]+", " ", (value or "").lower()).strip()


def _transcribe_audio(audio_bytes: bytes, asr_mode: str) -> str:
    if asr_mode in ("", "disabled", "none"):
        return ""
    if asr_mode == "mock":
        return "Move the gripper to the mock target."
    if asr_mode == "faster_whisper":
        return _faster_whisper_transcription(audio_bytes)
    if asr_mode == "openai_compatible":
        return _openai_compatible_transcription(audio_bytes)
    raise RuntimeError(f"Unsupported ROBOT_AI_ASR_MODE: {asr_mode}")


def transcribe_audio_bytes(audio_bytes: bytes) -> tuple[str, Diagnostics]:
    asr_mode = os.getenv("ROBOT_AI_ASR_MODE", "disabled").strip().lower()
    start = time.perf_counter()
    transcript = _transcribe_audio(audio_bytes, asr_mode)
    diagnostics = Diagnostics(
        asr_mode=asr_mode,
        asr_latency_ms=(time.perf_counter() - start) * 1000.0,
        audio_bytes=len(audio_bytes or b""),
        transcript_text=transcript,
    )
    return transcript, diagnostics


def _faster_whisper_transcription(audio_bytes: bytes) -> str:
    global _ASR_MODEL
    if _ASR_MODEL is None:
        try:
            from faster_whisper import WhisperModel
        except ModuleNotFoundError as exc:
            raise RuntimeError(
                "faster-whisper is not installed in the gateway container."
            ) from exc

        model_dir = os.getenv(
            "ROBOT_AI_ASR_MODEL_DIR",
            "/models/asr/faster-whisper-base.en",
        )
        device = os.getenv("ROBOT_AI_ASR_DEVICE", "cpu")
        compute_type = os.getenv("ROBOT_AI_ASR_COMPUTE_TYPE", "int8")
        logger.info(
            "loading faster-whisper model dir=%s device=%s compute_type=%s",
            model_dir,
            device,
            compute_type,
        )
        _ASR_MODEL = WhisperModel(
            model_dir,
            device=device,
            compute_type=compute_type,
        )

    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as f:
        wav_path = f.name
        f.write(audio_bytes)
    try:
        segments, _info = _ASR_MODEL.transcribe(
            wav_path,
            language=os.getenv("ROBOT_AI_ASR_LANGUAGE", "en"),
            beam_size=int(os.getenv("ROBOT_AI_ASR_BEAM_SIZE", "3")),
            vad_filter=os.getenv("ROBOT_AI_ASR_VAD_FILTER", "1") not in ("0", "false"),
        )
        return " ".join(seg.text.strip() for seg in segments).strip()
    finally:
        try:
            os.unlink(wav_path)
        except OSError:
            pass


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


def _maybe_save_images(
    request: RobotCommandRequest,
    image_bytes: Optional[bytes],
    response: RobotCommandResponse,
) -> tuple[str, str]:
    if not image_bytes:
        return "", ""
    if os.getenv("ROBOT_AI_SAVE_IMAGES", "1").strip().lower() in (
        "0",
        "false",
        "no",
        "disabled",
    ):
        return "", ""

    out_dir = Path(os.getenv(
        "ROBOT_AI_OUTPUT_DIR",
        "outputs/robot_ai",
    ))
    out_dir.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    request_id = _safe_file_stem(request.session_id or uuid.uuid4().hex)[:16]
    prefix = f"{stamp}_{request_id}"
    raw_path = out_dir / f"{prefix}_raw.png"
    annotated_path = out_dir / f"{prefix}_annotated.png"
    raw_path.write_bytes(image_bytes)

    try:
        _write_annotated_image(image_bytes, response, annotated_path)
        return str(raw_path), str(annotated_path)
    except Exception:
        logger.exception("failed to write annotated image")
        return str(raw_path), ""


def _write_annotated_image(
    image_bytes: bytes,
    response: RobotCommandResponse,
    annotated_path: Path,
) -> None:
    try:
        from PIL import Image, ImageDraw
    except ModuleNotFoundError as exc:
        raise RuntimeError("Pillow is required for image annotation.") from exc

    with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as f:
        tmp_path = f.name
        f.write(image_bytes)
    try:
        image = Image.open(tmp_path).convert("RGB")
    finally:
        try:
            os.unlink(tmp_path)
        except OSError:
            pass

    draw = ImageDraw.Draw(image)
    grounding = response.visual_grounding
    if grounding is not None:
        bbox = grounding.bbox_xyxy_px or []
        if len(bbox) >= 4:
            x1, y1, x2, y2 = [float(v) for v in bbox[:4]]
            draw.rectangle((x1, y1, x2, y2), outline=(255, 40, 40), width=4)
        point = grounding.preferred_point_px or []
        if len(point) >= 2:
            x, y = float(point[0]), float(point[1])
            r = 9
            draw.line((x - r, y, x + r, y), fill=(40, 255, 40), width=3)
            draw.line((x, y - r, x, y + r), fill=(40, 255, 40), width=3)
        label = grounding.label or ""
        if label:
            draw.text((10, 10), label, fill=(255, 255, 0))
    image.save(annotated_path)


def _safe_file_stem(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", value)


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
