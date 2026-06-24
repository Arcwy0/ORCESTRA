from __future__ import annotations

import base64
import hashlib
import io
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
from pathlib import Path, PurePosixPath
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

    trace: dict = {
        "mode": mode,
        "asr_mode": asr_mode,
        "audio_bytes": len(audio_bytes or b""),
        "image_sha256_12": _image_sha256_12(image_bytes),
        "transcript_text": transcript,
        "raw_model_output": "",
        "model_response_json": None,
    }

    if mode == "openai_compatible":
        response, model_trace = _openai_compatible_response(
            request, image_bytes)
        trace.update(model_trace)
        _sync_visual_groundings(response)
        _normalize_visual_grounding_coordinates(request, response)
        _clear_unusable_image_grounding(response)
    else:
        response = _mock_response(request)
        _sync_visual_groundings(response)

    trace["response_after_coordinate_normalization"] = response.model_dump()
    trace["response_before_repair"] = response.model_dump()
    response = _repair_or_override_plan(request, response)
    _sync_visual_groundings(response)

    saved_raw, saved_annotated = _maybe_save_images(
        request, image_bytes, response)

    if response.diagnostics is None:
        response.diagnostics = Diagnostics()
    response.diagnostics.gateway_mode = mode
    response.diagnostics.asr_mode = asr_mode
    response.diagnostics.asr_latency_ms = asr_latency_ms
    response.diagnostics.audio_bytes = len(audio_bytes or b"")
    response.diagnostics.transcript_text = transcript
    response.diagnostics.image_sha256_12 = _image_sha256_12(image_bytes)
    response.diagnostics.saved_image_path = saved_raw
    response.diagnostics.saved_annotated_image_path = saved_annotated
    response.diagnostics.saved_trace_path = _maybe_save_trace(
        request, response, trace)
    return response


def _image_sha256_12(image_bytes: Optional[bytes]) -> str:
    if not image_bytes:
        return ""
    return hashlib.sha256(image_bytes).hexdigest()[:12]


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
        visual_groundings=[
            VisualGrounding(
                label="mock target",
                confidence=0.85,
                preferred_point_px=[width * 0.5, height * 0.5],
                world_position_m=world,
                world_confidence=0.8,
            )
        ],
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


def _model_image_max_side() -> int:
    try:
        return int(os.getenv("ROBOT_AI_VLM_IMAGE_MAX_SIDE", "896"))
    except ValueError:
        return 896


def _prepare_model_image(image_bytes: Optional[bytes]) -> Optional[bytes]:
    if not image_bytes:
        return image_bytes

    max_side = _model_image_max_side()
    if max_side <= 0:
        return image_bytes

    try:
        from PIL import Image

        with Image.open(io.BytesIO(image_bytes)) as image:
            width, height = image.size
            largest = max(width, height)
            if largest <= max_side:
                return image_bytes

            scale = max_side / float(largest)
            new_size = (
                max(1, int(round(width * scale))),
                max(1, int(round(height * scale))),
            )
            resampling = getattr(getattr(Image, "Resampling", Image),
                                 "LANCZOS")
            resized = image.convert("RGB").resize(new_size, resampling)
            output = io.BytesIO()
            resized.save(output, format="PNG", optimize=True)
            prepared = output.getvalue()
            logger.info(
                "resized VLM image for model %dx%d -> %dx%d, "
                "bytes %d -> %d",
                width,
                height,
                new_size[0],
                new_size[1],
                len(image_bytes),
                len(prepared),
            )
            return prepared
    except Exception:
        logger.exception("failed to resize VLM image; using original image")
        return image_bytes


def _http_error_body(exc: urllib.error.HTTPError) -> str:
    try:
        body = exc.read()
    except Exception:
        return ""
    if not body:
        return ""
    return body.decode("utf-8", errors="replace").strip()[:4000]


def _openai_compatible_response(
    request: RobotCommandRequest, image_bytes: Optional[bytes]
) -> tuple[RobotCommandResponse, dict]:
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
        "\"visual_groundings\": [] or [{\"label\": string, "
        "\"confidence\": number, \"bbox_xyxy_px\": number[], "
        "\"preferred_point_px\": number[], \"world_position_m\": null or "
        "number[], \"world_confidence\": number}], "
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
        "as a list. visual_grounding is the first target for backward "
        "compatibility; visual_groundings is the ordered list of all target "
        "evidence in execution order. If there is only one target, include it in "
        "both visual_grounding and visual_groundings. For multi-step commands "
        "such as 'first go to A, then go to B', output every target in "
        "visual_groundings in that requested order. If all targets have metric "
        "positions from known_scene_objects or relative TCP math, populate "
        "plan_ir.waypoints in the same order. If any target is a visible unknown "
        "object, return a tight bbox/preferred point for each unknown target in "
        "visual_groundings and leave plan_ir.waypoints empty; Unity will lift "
        "each target to 3D and create the ordered waypoint path. If the target "
        "is not visible, return the same object schema "
        "with error={\"code\":\"not_grounded\",\"message\":\"...\"}. "
        "Do not infer metric 3D coordinates from image pixels alone. If a "
        "referenced object appears in known_scene_objects, you may use that "
        "object's provided position_m as world_position_m. If the command is a "
        "relative TCP motion such as up/down/left/right/forward/back by a "
        "distance, use the selected robot's tcp_position_m and output a waypoint. "
        "Do not reject as out_of_reach from visual judgment; the gateway and "
        "Unity will validate metric reach using reach_center_m/reach_radius_m. "
        "For visible unknown objects, return a tight bbox and preferred point "
        "around the referred object only, in Qwen's 0-1000 relative image "
        "coordinate grid with top-left origin, no world coordinate, and no "
        "waypoints. The gateway will convert those relative image coordinates "
        "to request.camera.width/request.camera.height pixels. Never use a "
        "full-image bbox or center point as a placeholder; if you are unsure, "
        "return empty bbox/preferred_point arrays and a not_grounded error. "
        "Ignore UI panels, labels, "
        "robot links, and the gripper unless the command explicitly refers to "
        "them. contact_allowed must be false for v1."
    )
    user_text = json.dumps(request.model_dump(), ensure_ascii=False)
    content = [{"type": "text", "text": user_text}]
    model_image_bytes = _prepare_model_image(image_bytes)
    if model_image_bytes:
        b64 = base64.b64encode(model_image_bytes).decode("ascii")
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
        "max_tokens": int(os.getenv("ROBOT_AI_MAX_TOKENS", "2048")),
    }
    logger.info(
        "forwarding to model base_url=%s model=%s image_bytes=%d "
        "model_image_bytes=%d timeout_s=%.1f",
        base_url,
        model,
        len(image_bytes or b""),
        len(model_image_bytes or b""),
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
    except urllib.error.HTTPError as exc:
        body = _http_error_body(exc)
        logger.error(
            "model endpoint HTTP %s %s: %s",
            exc.code,
            exc.reason,
            body,
        )
        suffix = f": {body}" if body else ""
        raise RuntimeError(
            f"Model endpoint failed: HTTP {exc.code} {exc.reason}{suffix}"
        ) from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise RuntimeError(f"Model endpoint failed: {exc}") from exc

    text = raw["choices"][0]["message"]["content"]
    if isinstance(text, list):
        text = "".join(part.get("text", "") for part in text)
    text = _extract_json_object(_strip_code_fence(str(text).strip()))
    return _parse_robot_response(text), {
        "raw_model_output": text,
        "model_response_json": raw,
    }


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

    visual_grounding = out.get("visual_grounding")
    visual_groundings = out.get("visual_groundings")
    if isinstance(visual_grounding, list):
        if visual_groundings is None:
            visual_groundings = visual_grounding
        visual_grounding = (
            visual_grounding[0] if visual_grounding and
            isinstance(visual_grounding[0], dict)
            else VisualGrounding().model_dump()
        )
    if not isinstance(visual_grounding, dict):
        visual_grounding = VisualGrounding().model_dump()
    out["visual_grounding"] = visual_grounding

    if isinstance(visual_groundings, dict):
        visual_groundings = [visual_groundings]
    if not isinstance(visual_groundings, list):
        visual_groundings = []
    visual_groundings = [
        item for item in visual_groundings
        if isinstance(item, dict)
    ]
    if not visual_groundings and _grounding_payload_has_content(
        visual_grounding
    ):
        visual_groundings = [visual_grounding]
    if visual_groundings and not _grounding_payload_has_content(
        visual_grounding
    ):
        out["visual_grounding"] = visual_groundings[0]
    out["visual_groundings"] = visual_groundings

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


def _grounding_payload_has_content(payload: dict) -> bool:
    if not isinstance(payload, dict):
        return False
    return bool(
        payload.get("label") or
        payload.get("confidence") or
        payload.get("bbox_xyxy_px") or
        payload.get("preferred_point_px") or
        payload.get("world_position_m") or
        payload.get("world_confidence")
    )


def _sync_visual_groundings(response: RobotCommandResponse) -> None:
    if response is None:
        return
    primary = response.visual_grounding
    sequence = [
        grounding for grounding in (response.visual_groundings or [])
        if _visual_grounding_has_content(grounding)
    ]
    if sequence:
        response.visual_grounding = sequence[0]
        response.visual_groundings = sequence
        return
    if _visual_grounding_has_content(primary):
        response.visual_groundings = [primary]
    else:
        response.visual_groundings = []


def _visual_grounding_has_content(grounding: Optional[VisualGrounding]) -> bool:
    if grounding is None:
        return False
    return bool(
        grounding.label or
        grounding.confidence or
        grounding.bbox_xyxy_px or
        grounding.preferred_point_px or
        grounding.world_position_m or
        grounding.world_confidence
    )


def _iter_visual_groundings(
    response: RobotCommandResponse,
) -> list[VisualGrounding]:
    if response is None:
        return []
    _sync_visual_groundings(response)
    seen: set[tuple] = set()
    result: list[VisualGrounding] = []
    for grounding in [response.visual_grounding] + list(
        response.visual_groundings or []
    ):
        if not _visual_grounding_has_content(grounding):
            continue
        key = _visual_grounding_key(grounding)
        if key in seen:
            continue
        seen.add(key)
        result.append(grounding)
    return result


def _visual_grounding_key(grounding: VisualGrounding) -> tuple:
    return (
        grounding.label,
        tuple(grounding.bbox_xyxy_px or []),
        tuple(grounding.preferred_point_px or []),
        tuple(grounding.world_position_m or []),
    )


def _repair_or_override_plan(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> RobotCommandResponse:
    """Use metric scene state for cases where VLM text is not authoritative."""
    if response.diagnostics is None:
        response.diagnostics = Diagnostics()

    circle = _circle_tcp_waypoints(request)
    if circle is not None:
        robot, waypoints, radius_m, plane_name, center_name = circle
        response.error = None
        response.intent = Intent(
            robot_id=robot.id,
            task_type="geometric_primitive",
            target_ref=center_name,
            motion_primitive=f"{plane_name}_circle_{radius_m:.3f}m",
        )
        response.visual_grounding = VisualGrounding(
            label=center_name,
            confidence=1.0,
            world_position_m=waypoints[0],
            world_confidence=1.0,
        )
        response.plan_ir = PlanIr(
            kind="geometric_primitive",
            robot_id=robot.id,
            requires_confirmation=True,
            contact_allowed=False,
            min_clearance_m=0.05,
            speed_scale=0.15,
            waypoints=[
                Waypoint(position_m=p, speed_scale=0.15)
                for p in waypoints
            ],
        )
        response.spoken_reply = (
            f"Generated a {plane_name} TCP circle with "
            f"{radius_m:.2f} meter radius. Please confirm the preview."
        )
        return response

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

    sequence = _known_object_sequence(request, response)
    if sequence is not None:
        robot, objects, waypoints = sequence
        plan_kind = "mobile_route" if robot.kind == "mobile" else "manipulator_reach"
        labels = [obj.label for obj in objects]
        response.error = None
        response.intent = Intent(
            robot_id=robot.id,
            task_type=plan_kind,
            target_ref=" -> ".join(labels),
            motion_primitive="ordered_object_route",
        )
        response.visual_groundings = [
            VisualGrounding(
                label=obj.label,
                confidence=0.95,
                bbox_xyxy_px=[],
                preferred_point_px=[],
                world_position_m=obj.position_m,
                world_confidence=0.95,
            )
            for obj in objects
        ]
        response.visual_grounding = response.visual_groundings[0]
        response.plan_ir = PlanIr(
            kind=plan_kind,
            robot_id=robot.id,
            requires_confirmation=True,
            contact_allowed=False,
            min_clearance_m=0.05,
            speed_scale=0.2,
            waypoints=[
                Waypoint(position_m=point, speed_scale=0.2)
                for point in waypoints
            ],
        )
        response.spoken_reply = (
            f"Planned {len(waypoints)} ordered waypoints. "
            "Please confirm the highlighted path."
        )
        return response

    match = None if _has_multi_grounding_sequence(response) else (
        _known_object_target(request, response)
    )
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
                bbox_xyxy_px=[],
                preferred_point_px=[],
                world_position_m=obj.position_m,
                world_confidence=0.95,
            )
            response.visual_groundings = [response.visual_grounding]
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
            response.visual_groundings = [response.visual_grounding]
    elif response.error is not None and _mentions_reach(response.error.message):
        if _requires_unity_image_grounding(response):
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

    if _requires_unity_image_grounding(response):
        robot = _select_robot(request, response)
        if robot is not None:
            if response.intent is None:
                response.intent = Intent()
            if response.plan_ir is None:
                response.plan_ir = PlanIr()
            plan_kind = "mobile_route" if robot.kind == "mobile" else "manipulator_reach"
            response.error = None
            response.intent.robot_id = response.intent.robot_id or robot.id
            response.intent.task_type = plan_kind
            response.intent.motion_primitive = (
                response.intent.motion_primitive or "image_grounded_reach"
            )
            response.plan_ir.kind = plan_kind
            response.plan_ir.robot_id = response.plan_ir.robot_id or robot.id
            response.plan_ir.requires_confirmation = True
            response.plan_ir.contact_allowed = False
            response.plan_ir.waypoints = []

    return response


def _normalize_visual_grounding_coordinates(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> None:
    width = request.camera.width if request and request.camera else 0
    height = request.camera.height if request and request.camera else 0
    if width <= 0 or height <= 0:
        return

    for grounding in _iter_visual_groundings(response):
        _normalize_one_visual_grounding(grounding, width, height)


def _normalize_one_visual_grounding(
    grounding: VisualGrounding,
    width: int,
    height: int,
) -> None:
    if grounding is None:
        return

    coord_format = os.getenv(
        "ROBOT_AI_VLM_COORD_FORMAT",
        "qwen_1000",
    ).strip().lower()
    if coord_format in ("pixel", "pixels", "image_pixels"):
        _clamp_grounding_to_image(grounding, width, height)
        return

    scale = coord_format in ("qwen_1000", "qwen", "normalized_1000")
    if coord_format == "auto":
        scale = _auto_should_scale_qwen_1000_coordinates(
            grounding, width, height)
    if scale:
        if grounding.bbox_xyxy_px and len(grounding.bbox_xyxy_px) >= 4:
            grounding.bbox_xyxy_px = [
                grounding.bbox_xyxy_px[0] / 1000.0 * width,
                grounding.bbox_xyxy_px[1] / 1000.0 * height,
                grounding.bbox_xyxy_px[2] / 1000.0 * width,
                grounding.bbox_xyxy_px[3] / 1000.0 * height,
            ]
        if grounding.preferred_point_px and len(grounding.preferred_point_px) >= 2:
            grounding.preferred_point_px = [
                grounding.preferred_point_px[0] / 1000.0 * width,
                grounding.preferred_point_px[1] / 1000.0 * height,
            ]

    _clamp_grounding_to_image(grounding, width, height)


def _auto_should_scale_qwen_1000_coordinates(
    grounding: VisualGrounding,
    width: int,
    height: int,
) -> bool:
    if not _coordinates_fit_qwen_1000_grid(grounding):
        return False

    model = os.getenv(
        "ROBOT_AI_MODEL", "Qwen/Qwen3-VL-8B-Instruct").strip().lower()
    if "qwen" in model:
        return True

    return _looks_like_qwen_1000_coordinates(grounding, width, height)


def _coordinates_fit_qwen_1000_grid(grounding: VisualGrounding) -> bool:
    values = _grounding_coordinate_values(grounding)
    return bool(values) and min(values) >= 0.0 and max(values) <= 1000.0


def _grounding_coordinate_values(grounding: VisualGrounding) -> list[float]:
    values: list[float] = []
    if grounding.bbox_xyxy_px:
        values.extend(float(v) for v in grounding.bbox_xyxy_px[:4])
    if grounding.preferred_point_px:
        values.extend(float(v) for v in grounding.preferred_point_px[:2])
    return values


def _looks_like_qwen_1000_coordinates(
    grounding: VisualGrounding,
    width: int,
    height: int,
) -> bool:
    values: list[float] = []
    x_values: list[float] = []
    y_values: list[float] = []
    if grounding.bbox_xyxy_px:
        bbox = [float(v) for v in grounding.bbox_xyxy_px[:4]]
        values.extend(bbox)
        x_values.extend([bbox[0], bbox[2]])
        y_values.extend([bbox[1], bbox[3]])
    if grounding.preferred_point_px:
        point = [float(v) for v in grounding.preferred_point_px[:2]]
        values.extend(point)
        x_values.append(point[0])
        y_values.append(point[1])
    if not values or min(values) < 0.0 or max(values) > 1000.0:
        return False

    # When all coordinates already fit the actual image extent, prefer pixels.
    # Qwen's 0-1000 grid is only inferred in auto mode when at least one axis
    # coordinate cannot be a valid pixel for the submitted image.
    fits_pixels = (
        all(v <= max(width - 1, 0) for v in x_values) and
        all(v <= max(height - 1, 0) for v in y_values)
    )
    return not fits_pixels


def _clamp_grounding_to_image(
    grounding: VisualGrounding,
    width: int,
    height: int,
) -> None:
    if grounding.bbox_xyxy_px and len(grounding.bbox_xyxy_px) >= 4:
        x1 = _clamp(float(grounding.bbox_xyxy_px[0]), 0.0, max(width - 1, 0))
        y1 = _clamp(float(grounding.bbox_xyxy_px[1]), 0.0, max(height - 1, 0))
        x2 = _clamp(float(grounding.bbox_xyxy_px[2]), 0.0, max(width - 1, 0))
        y2 = _clamp(float(grounding.bbox_xyxy_px[3]), 0.0, max(height - 1, 0))
        grounding.bbox_xyxy_px = [
            min(x1, x2),
            min(y1, y2),
            max(x1, x2),
            max(y1, y2),
        ]
    if grounding.preferred_point_px and len(grounding.preferred_point_px) >= 2:
        grounding.preferred_point_px = [
            _clamp(float(grounding.preferred_point_px[0]), 0.0, max(width - 1, 0)),
            _clamp(float(grounding.preferred_point_px[1]), 0.0, max(height - 1, 0)),
        ]


def _clamp(value: float, lo: float, hi: float) -> float:
    return min(max(value, lo), hi)


def _requires_unity_image_grounding(response: RobotCommandResponse) -> bool:
    for grounding in _iter_visual_groundings(response):
        if (
            grounding.world_position_m is not None and
            grounding.world_confidence >= 0.6
        ):
            continue
        if _has_usable_image_grounding(grounding):
            return True
    return False


def _has_usable_image_grounding(grounding: VisualGrounding) -> bool:
    if grounding is None:
        return False
    has_bbox = (
        grounding.bbox_xyxy_px is not None and
        len(grounding.bbox_xyxy_px) >= 4
    )
    has_point = (
        grounding.preferred_point_px is not None and
        len(grounding.preferred_point_px) >= 2
    )
    if not has_bbox and not has_point:
        return False
    if grounding.confidence <= 0.05 and grounding.world_confidence <= 0.0:
        return False
    return True


def _clear_unusable_image_grounding(response: RobotCommandResponse) -> None:
    for grounding in _iter_visual_groundings(response):
        has_bbox = (
            grounding.bbox_xyxy_px is not None and
            len(grounding.bbox_xyxy_px) >= 4
        )
        has_point = (
            grounding.preferred_point_px is not None and
            len(grounding.preferred_point_px) >= 2
        )
        if not has_bbox and not has_point:
            continue
        if _has_usable_image_grounding(grounding):
            continue
        grounding.bbox_xyxy_px = []
        grounding.preferred_point_px = []


def _circle_tcp_waypoints(
    request: RobotCommandRequest,
) -> Optional[tuple[RobotSnapshot, list[list[float]], float, str, str]]:
    command = _norm(request.command_text)
    raw_command = (request.command_text or "").lower()
    if "circle" not in command:
        return None
    if not any(word in command for word in ("gripper", "tcp", "end effector", "tool")):
        return None

    robot = _first_manipulator(request)
    if robot is None:
        return None

    radius_m = _parse_distance_m(raw_command)
    if radius_m is None:
        radius_m = 0.06
    radius_m = min(max(radius_m, 0.01), 0.50)

    tcp = _vec3(robot.tcp_position_m)
    root = _vec3(robot.root_position_m)
    center_name = "base" if "base" in command else "tcp"
    if center_name == "base":
        center = [root[0], tcp[1], root[2]]
    else:
        center = tcp

    horizontal = "horizontal" in command
    plane_name = "horizontal" if horizontal else "vertical"
    count = 24
    waypoints: list[list[float]] = []
    for i in range(count + 1):
        a = i / count * math.tau
        ca = math.cos(a)
        sa = math.sin(a)
        if horizontal:
            point = [
                center[0] + radius_m * ca,
                center[1],
                center[2] + radius_m * sa,
            ]
        else:
            point = [
                center[0] + radius_m * ca,
                center[1] + radius_m * sa,
                center[2],
            ]
        if not _within_reach(robot, point, tolerance_m=0.08):
            point = _clip_to_reach_sphere(
                _vec3(robot.reach_center_m),
                point,
                max(robot.reach_radius_m * 0.98, 0.05),
            )
        waypoints.append(point)

    return robot, waypoints, radius_m, plane_name, center_name


def _relative_tcp_waypoint(
    request: RobotCommandRequest,
) -> Optional[tuple[RobotSnapshot, list[float], float, str]]:
    command = _norm(request.command_text)
    raw_command = (request.command_text or "").lower()
    if not any(word in command for word in ("up", "down", "left", "right", "forward", "back")):
        return None
    if not any(word in command for word in ("move", "raise", "lower", "shift", "translate")):
        return None
    if not any(word in command for word in ("gripper", "tcp", "end effector", "tool")):
        return None

    robot = _first_manipulator(request)
    if robot is None:
        return None

    distance_m = _parse_distance_m(raw_command)
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
    for grounding in response.visual_groundings or []:
        query_parts.append(grounding.label)
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

    return robot, best_obj, _object_waypoint(robot, best_obj)


def _known_object_sequence(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
) -> Optional[tuple[RobotSnapshot, list[KnownSceneObject], list[list[float]]]]:
    if not request.known_scene_objects:
        return None

    robot = _select_robot(request, response)
    if robot is None:
        return None

    command = _norm(request.command_text)
    if not _looks_like_ordered_command(command):
        return None

    matches: list[tuple[int, int, KnownSceneObject]] = []
    for obj in request.known_scene_objects:
        pos, score = _object_mention_position(command, obj)
        if pos >= 0 and score > 0:
            matches.append((pos, -score, obj))

    if len(matches) < 2:
        return None

    matches.sort(key=lambda item: (item[0], item[1], item[2].id))
    selected: list[KnownSceneObject] = []
    seen: set[str] = set()
    for _pos, _score, obj in matches:
        key = obj.id or obj.label
        if key in seen:
            continue
        seen.add(key)
        selected.append(obj)

    if len(selected) < 2:
        return None

    waypoints = [_object_waypoint(robot, obj) for obj in selected]
    return robot, selected, waypoints


def _object_waypoint(robot: RobotSnapshot, obj: KnownSceneObject) -> list[float]:
    target = _vec3(obj.position_m)
    if robot.kind == "mobile":
        return [target[0], _vec3(robot.root_position_m)[1], target[2]]

    clearance = 0.05
    if len(obj.size_m or []) >= 3:
        clearance += max(obj.size_m[1] * 0.5, 0.0)
    waypoint = [target[0], target[1] + clearance, target[2]]
    if not _within_reach(robot, waypoint, tolerance_m=0.08):
        center = _vec3(robot.reach_center_m)
        radius = max(robot.reach_radius_m * 0.98, 0.05)
        waypoint = _clip_to_reach_sphere(center, waypoint, radius)
    return waypoint


def _looks_like_ordered_command(command: str) -> bool:
    if not command:
        return False
    return any(
        token in command.split()
        for token in ("first", "then", "second", "after", "next")
    ) or " and then " in f" {command} "


def _object_mention_position(
    command: str,
    obj: KnownSceneObject,
) -> tuple[int, int]:
    label = _norm(obj.label)
    obj_id = _norm(obj.id)
    ordinal_pos, ordinal_score = _ordinal_object_mention_position(
        command, label, obj_id
    )
    if ordinal_pos >= 0:
        return ordinal_pos, ordinal_score
    if _object_ordinal_index(label, obj_id) is not None:
        return -1, 0

    for phrase in (label, obj_id):
        if not phrase:
            continue
        pos = command.find(phrase)
        if pos >= 0:
            return pos, len(phrase)

    tokens = [token for token in label.split() if len(token) >= 3]
    if not tokens:
        return -1, 0

    positions: list[int] = []
    score = 0
    for token in tokens:
        pos = command.find(token)
        if pos < 0:
            return -1, 0
        positions.append(pos)
        score += len(token)
    return min(positions), score


def _ordinal_object_mention_position(
    command: str,
    label: str,
    obj_id: str,
) -> tuple[int, int]:
    index = _object_ordinal_index(label, obj_id)
    if index is None:
        return -1, 0

    base_tokens = [
        token for token in label.split()
        if len(token) >= 3 and not token.isdigit() and token not in _ORDINAL_WORDS
    ]
    if not base_tokens:
        base_tokens = [
            token for token in obj_id.split()
            if len(token) >= 3 and not token.isdigit() and token not in _ORDINAL_WORDS
        ]
    if not base_tokens:
        return -1, 0

    ordinal_forms = _ORDINAL_FORMS.get(index, [])
    best_pos = -1
    best_score = 0
    for ordinal in ordinal_forms:
        for base in base_tokens:
            for phrase in (f"{ordinal} {base}", f"{base} {ordinal}"):
                pos = command.find(phrase)
                if pos >= 0:
                    score = len(base) + len(ordinal) + 6
                    if score > best_score:
                        best_pos = pos
                        best_score = score
    return best_pos, best_score


def _object_ordinal_index(label: str, obj_id: str) -> Optional[int]:
    tokens = (label + " " + obj_id).split()
    for token in tokens:
        if token.isdigit():
            return int(token)
        if token in _ORDINAL_WORDS:
            return _ORDINAL_WORDS[token]
    return None


_ORDINAL_WORDS = {
    "first": 1,
    "1st": 1,
    "one": 1,
    "second": 2,
    "2nd": 2,
    "two": 2,
    "third": 3,
    "3rd": 3,
    "three": 3,
    "fourth": 4,
    "4th": 4,
    "four": 4,
}


_ORDINAL_FORMS = {
    1: ["first", "1st", "one", "1"],
    2: ["second", "2nd", "two", "2"],
    3: ["third", "3rd", "three", "3"],
    4: ["fourth", "4th", "four", "4"],
}


def _has_multi_grounding_sequence(response: RobotCommandResponse) -> bool:
    return len(_iter_visual_groundings(response)) > 1


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
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%Y%m%d-%H%M%S")
        request_id = _safe_file_stem(
            request.session_id or uuid.uuid4().hex)[:16]
        image_source = _safe_file_stem(request.image_source or "unknown")[:32]
        prefix = f"{stamp}_{request_id}_{image_source}"
        raw_path = out_dir / f"{prefix}_raw.png"
        annotated_path = out_dir / f"{prefix}_annotated.png"
        raw_path.write_bytes(image_bytes)
    except Exception:
        logger.exception("failed to write raw image artifact to %s", out_dir)
        return "", ""

    try:
        _write_annotated_image(image_bytes, response, annotated_path)
        return _saved_image_display_path(raw_path), _saved_image_display_path(
            annotated_path
        )
    except Exception:
        logger.exception("failed to write annotated image")
        return _saved_image_display_path(raw_path), ""


def _maybe_save_trace(
    request: RobotCommandRequest,
    response: RobotCommandResponse,
    trace: dict,
) -> str:
    if os.getenv("ROBOT_AI_SAVE_TRACES", "1").strip().lower() in (
        "0",
        "false",
        "no",
        "disabled",
    ):
        return ""

    out_dir = Path(os.getenv(
        "ROBOT_AI_OUTPUT_DIR",
        "outputs/robot_ai",
    ))
    try:
        out_dir.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%Y%m%d-%H%M%S")
        request_id = _safe_file_stem(
            request.session_id or uuid.uuid4().hex)[:16]
        path = out_dir / f"{stamp}_{request_id}_trace.json"

        payload = {
            "schema_version": 1,
            "request": request.model_dump(),
            "mode": trace.get("mode", ""),
            "asr_mode": trace.get("asr_mode", ""),
            "audio_bytes": trace.get("audio_bytes", 0),
            "transcript_text": trace.get("transcript_text", ""),
            "raw_model_output": trace.get("raw_model_output", ""),
            "model_response_json": trace.get("model_response_json"),
            "response_after_coordinate_normalization": trace.get(
                "response_after_coordinate_normalization"
            ),
            "response_before_repair": trace.get("response_before_repair"),
            "final_response": response.model_dump(),
        }
        path.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        return _saved_image_display_path(path)
    except Exception:
        logger.exception("failed to write trace artifact to %s", out_dir)
        return ""


def _saved_image_display_path(path: Path) -> str:
    """Return a host-visible path label when Docker bind mount info is known."""
    host_root = os.getenv("ROBOT_AI_OUTPUT_DIR_HOST_LABEL", "").strip()
    if not host_root:
        return str(path)

    container_root = Path(os.getenv(
        "ROBOT_AI_OUTPUT_DIR",
        "outputs/robot_ai",
    ))
    try:
        rel = path.relative_to(container_root)
    except ValueError:
        return str(path)
    if host_root.startswith("/"):
        return str(PurePosixPath(host_root) / rel.as_posix())
    return str(Path(host_root) / rel)


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
    colors = [
        (255, 40, 40),
        (40, 180, 255),
        (255, 180, 40),
        (180, 80, 255),
    ]
    for idx, grounding in enumerate(_iter_visual_groundings(response)):
        color = colors[idx % len(colors)]
        bbox = grounding.bbox_xyxy_px or []
        if len(bbox) >= 4:
            x1, y1, x2, y2 = [float(v) for v in bbox[:4]]
            draw.rectangle((x1, y1, x2, y2), outline=color, width=4)
        point = grounding.preferred_point_px or []
        if len(point) >= 2:
            x, y = float(point[0]), float(point[1])
            r = 9
            draw.line((x - r, y, x + r, y), fill=(40, 255, 40), width=3)
            draw.line((x, y - r, x, y + r), fill=(40, 255, 40), width=3)
        label = grounding.label or ""
        if label:
            draw.text((10, 10 + idx * 18), f"{idx + 1}. {label}", fill=color)
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
