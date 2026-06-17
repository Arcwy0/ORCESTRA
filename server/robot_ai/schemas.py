from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, Field


class CameraSnapshot(BaseModel):
    world_from_camera: List[float] = Field(default_factory=list)
    projection: List[float] = Field(default_factory=list)
    width: int = 0
    height: int = 0


class RobotSnapshot(BaseModel):
    id: str
    kind: str
    root_position_m: List[float] = Field(default_factory=list)
    root_rotation_xyzw: List[float] = Field(default_factory=list)
    tcp_position_m: List[float] = Field(default_factory=list)
    reach_center_m: List[float] = Field(default_factory=list)
    reach_radius_m: float = 0.0
    joint_deg: List[float] = Field(default_factory=list)


class PlaneSnapshot(BaseModel):
    id: str
    center_m: List[float] = Field(default_factory=list)
    normal: List[float] = Field(default_factory=list)
    extent_m: List[float] = Field(default_factory=list)


class KnownSceneObject(BaseModel):
    id: str
    label: str
    position_m: List[float] = Field(default_factory=list)
    size_m: List[float] = Field(default_factory=list)


class RobotCommandRequest(BaseModel):
    session_id: str
    command_text: str
    image_source: str
    audio_format: str = ""
    audio_sample_rate_hz: int = 0
    camera: CameraSnapshot
    robots: List[RobotSnapshot] = Field(default_factory=list)
    mr_planes: List[PlaneSnapshot] = Field(default_factory=list)
    known_scene_objects: List[KnownSceneObject] = Field(default_factory=list)


class Intent(BaseModel):
    robot_id: str = ""
    task_type: str = ""
    target_ref: str = ""
    motion_primitive: str = ""


class VisualGrounding(BaseModel):
    label: str = ""
    confidence: float = 0.0
    bbox_xyxy_px: List[float] = Field(default_factory=list)
    preferred_point_px: List[float] = Field(default_factory=list)
    world_position_m: Optional[List[float]] = None
    world_confidence: float = 0.0


class Waypoint(BaseModel):
    position_m: List[float] = Field(default_factory=list)
    tcp_orientation_xyzw: Optional[List[float]] = None
    speed_scale: float = 0.25
    dwell_s: float = 0.0


class PlanIr(BaseModel):
    version: int = 1
    kind: str = ""
    robot_id: str = ""
    requires_confirmation: bool = True
    contact_allowed: bool = False
    min_clearance_m: float = 0.05
    speed_scale: float = 0.25
    waypoints: List[Waypoint] = Field(default_factory=list)


class ErrorPayload(BaseModel):
    code: str
    message: str


class Diagnostics(BaseModel):
    request_id: str = ""
    gateway_mode: str = ""
    gateway_latency_ms: float = 0.0
    asr_mode: str = ""
    asr_latency_ms: float = 0.0
    audio_bytes: int = 0
    transcript_text: str = ""
    rejection_reason: str = ""


class RobotCommandResponse(BaseModel):
    spoken_reply: str = ""
    intent: Intent = Field(default_factory=Intent)
    visual_grounding: VisualGrounding = Field(default_factory=VisualGrounding)
    plan_ir: PlanIr = Field(default_factory=PlanIr)
    diagnostics: Diagnostics = Field(default_factory=Diagnostics)
    error: Optional[ErrorPayload] = None
