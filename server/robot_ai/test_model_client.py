import os
import base64
import io
import json
import tempfile
import urllib.error
from pathlib import Path
import unittest

try:
    import pydantic  # noqa: F401
    HAS_PYDANTIC = True
except ModuleNotFoundError:
    HAS_PYDANTIC = False

if HAS_PYDANTIC:
    from server.robot_ai import model_client
    from server.robot_ai.model_client import (
        make_response,
        _saved_image_display_path,
        _repair_or_override_plan,
        _normalize_visual_grounding_coordinates,
    )
    from server.robot_ai.schemas import (
        CameraSnapshot,
        ErrorPayload,
        KnownSceneObject,
        Intent,
        PlanIr,
        RobotCommandRequest,
        RobotCommandResponse,
        RobotSnapshot,
        VisualGrounding,
        Waypoint,
    )


@unittest.skipUnless(
    HAS_PYDANTIC,
    "pydantic is not installed; install server/robot_ai/requirements.txt",
)
class RobotAiGatewayTests(unittest.TestCase):
    def setUp(self):
        self._old_mode = os.environ.get("ROBOT_AI_MODE")
        self._old_asr_mode = os.environ.get("ROBOT_AI_ASR_MODE")
        os.environ["ROBOT_AI_MODE"] = "mock"
        os.environ["ROBOT_AI_ASR_MODE"] = "disabled"

    def tearDown(self):
        if self._old_mode is None:
            os.environ.pop("ROBOT_AI_MODE", None)
        else:
            os.environ["ROBOT_AI_MODE"] = self._old_mode
        if self._old_asr_mode is None:
            os.environ.pop("ROBOT_AI_ASR_MODE", None)
        else:
            os.environ["ROBOT_AI_ASR_MODE"] = self._old_asr_mode

    def test_mock_manipulator_response_has_reachable_world_target(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="move to cup",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=1280, height=720),
            robots=[
                RobotSnapshot(
                    id="UR3_1",
                    kind="manipulator",
                    reach_center_m=[0.0, 0.2, 0.0],
                    reach_radius_m=0.6,
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.plan_ir.robot_id, "UR3_1")
        self.assertEqual(response.plan_ir.kind, "manipulator_reach")
        self.assertFalse(response.plan_ir.contact_allowed)
        self.assertGreaterEqual(response.visual_grounding.world_confidence, 0.6)
        self.assertEqual(len(response.visual_grounding.world_position_m), 3)
        self.assertAlmostEqual(response.visual_grounding.world_position_m[1], 0.2)

    def test_mock_mobile_response_uses_mobile_route(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="drive to chair",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="Scout_1",
                    kind="mobile",
                    root_position_m=[1.0, 0.0, 2.0],
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertEqual(response.plan_ir.robot_id, "Scout_1")
        self.assertEqual(response.plan_ir.kind, "mobile_route")
        self.assertEqual(response.visual_grounding.world_position_m, [1.7, 0.0, 2.0])

    def test_disabled_asr_records_audio_diagnostics_without_transcript(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="typed command",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
        )

        response = make_response(request, image_bytes=None, audio_bytes=b"RIFF")

        self.assertEqual(response.diagnostics.asr_mode, "disabled")
        self.assertEqual(response.diagnostics.audio_bytes, 4)
        self.assertEqual(response.diagnostics.transcript_text, "")

    def test_image_hash_diagnostic_tracks_uploaded_bytes(self):
        old_save_images = os.environ.get("ROBOT_AI_SAVE_IMAGES")
        old_save_traces = os.environ.get("ROBOT_AI_SAVE_TRACES")
        try:
            os.environ["ROBOT_AI_SAVE_IMAGES"] = "0"
            os.environ["ROBOT_AI_SAVE_TRACES"] = "0"
            request = RobotCommandRequest(
                session_id="s",
                command_text="move to target",
                image_source="quest_passthrough_camera",
                camera=CameraSnapshot(width=640, height=480),
            )

            response = make_response(request, image_bytes=b"abc")

            self.assertEqual(response.diagnostics.image_sha256_12,
                             "ba7816bf8f01")
        finally:
            if old_save_images is None:
                os.environ.pop("ROBOT_AI_SAVE_IMAGES", None)
            else:
                os.environ["ROBOT_AI_SAVE_IMAGES"] = old_save_images
            if old_save_traces is None:
                os.environ.pop("ROBOT_AI_SAVE_TRACES", None)
            else:
                os.environ["ROBOT_AI_SAVE_TRACES"] = old_save_traces

    def test_output_artifact_write_failure_does_not_fail_request(self):
        old_output_dir = os.environ.get("ROBOT_AI_OUTPUT_DIR")
        old_save_images = os.environ.get("ROBOT_AI_SAVE_IMAGES")
        old_save_traces = os.environ.get("ROBOT_AI_SAVE_TRACES")
        with tempfile.TemporaryDirectory() as tmp:
            blocked_parent = Path(tmp) / "blocked"
            blocked_parent.write_text("not a directory", encoding="utf-8")
            try:
                os.environ["ROBOT_AI_OUTPUT_DIR"] = str(
                    blocked_parent / "robot_ai"
                )
                os.environ["ROBOT_AI_SAVE_IMAGES"] = "1"
                os.environ["ROBOT_AI_SAVE_TRACES"] = "1"
                request = RobotCommandRequest(
                    session_id="s",
                    command_text="move to target",
                    image_source="quest_passthrough_camera",
                    camera=CameraSnapshot(width=640, height=480),
                )

                response = make_response(request, image_bytes=b"abc")

                self.assertIsNone(response.error)
                self.assertEqual(response.diagnostics.saved_image_path, "")
                self.assertEqual(
                    response.diagnostics.saved_annotated_image_path, "")
                self.assertEqual(response.diagnostics.saved_trace_path, "")
                self.assertEqual(response.diagnostics.image_sha256_12,
                                 "ba7816bf8f01")
            finally:
                if old_output_dir is None:
                    os.environ.pop("ROBOT_AI_OUTPUT_DIR", None)
                else:
                    os.environ["ROBOT_AI_OUTPUT_DIR"] = old_output_dir
                if old_save_images is None:
                    os.environ.pop("ROBOT_AI_SAVE_IMAGES", None)
                else:
                    os.environ["ROBOT_AI_SAVE_IMAGES"] = old_save_images
                if old_save_traces is None:
                    os.environ.pop("ROBOT_AI_SAVE_TRACES", None)
                else:
                    os.environ["ROBOT_AI_SAVE_TRACES"] = old_save_traces

    def test_mock_asr_replaces_command_text_with_transcript(self):
        os.environ["ROBOT_AI_ASR_MODE"] = "mock"
        request = RobotCommandRequest(
            session_id="s",
            command_text="",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
        )

        response = make_response(request, image_bytes=None, audio_bytes=b"RIFF")

        self.assertEqual(response.diagnostics.asr_mode, "mock")
        self.assertEqual(
            response.diagnostics.transcript_text,
            "Move the gripper to the mock target.",
        )

    def test_known_red_cube_uses_scene_object_world_target(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper to the red cube.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.2, 0.3, 0.0],
                    reach_center_m=[0.0, 0.18, 0.0],
                    reach_radius_m=0.75,
                )
            ],
            known_scene_objects=[
                KnownSceneObject(
                    id="VLM_Target_RedCube",
                    label="red cube",
                    position_m=[0.35, 0.05, 0.0],
                    size_m=[0.1, 0.1, 0.1],
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.intent.target_ref, "red cube")
        self.assertEqual(response.visual_grounding.world_position_m,
                         [0.35, 0.05, 0.0])
        self.assertEqual(response.plan_ir.kind, "manipulator_reach")
        self.assertEqual(len(response.plan_ir.waypoints), 1)
        self.assertAlmostEqual(
            response.plan_ir.waypoints[0].position_m[1],
            0.15,
            places=4,
        )

    def test_ordered_known_objects_create_manipulator_waypoint_sequence(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text=(
                "First move the gripper to the red cube, then to the blue sphere."
            ),
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.2, 0.3, 0.0],
                    reach_center_m=[0.0, 0.18, 0.0],
                    reach_radius_m=0.9,
                )
            ],
            known_scene_objects=[
                KnownSceneObject(
                    id="red_cube",
                    label="red cube",
                    position_m=[0.35, 0.05, 0.0],
                    size_m=[0.1, 0.1, 0.1],
                ),
                KnownSceneObject(
                    id="blue_sphere",
                    label="blue sphere",
                    position_m=[0.15, 0.08, 0.22],
                    size_m=[0.08, 0.08, 0.08],
                ),
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.plan_ir.kind, "manipulator_reach")
        self.assertEqual(len(response.plan_ir.waypoints), 2)
        self.assertEqual(len(response.visual_groundings), 2)
        self.assertEqual(response.visual_groundings[0].label, "red cube")
        self.assertEqual(response.visual_groundings[1].label, "blue sphere")
        self.assertAlmostEqual(
            response.plan_ir.waypoints[0].position_m[0],
            0.35,
            places=4,
        )
        self.assertAlmostEqual(
            response.plan_ir.waypoints[1].position_m[2],
            0.22,
            places=4,
        )

    def test_ordered_known_objects_create_mobile_route_sequence(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Drive to the package, then to the delivery zone.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="Scout_1",
                    kind="mobile",
                    root_position_m=[0.0, 0.03, 0.0],
                )
            ],
            known_scene_objects=[
                KnownSceneObject(
                    id="package",
                    label="package",
                    position_m=[1.0, 0.2, 0.4],
                    size_m=[0.2, 0.2, 0.2],
                ),
                KnownSceneObject(
                    id="delivery_zone",
                    label="delivery zone",
                    position_m=[2.5, 0.0, -0.5],
                    size_m=[0.5, 0.01, 0.5],
                ),
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.plan_ir.kind, "mobile_route")
        self.assertEqual(len(response.plan_ir.waypoints), 2)
        self.assertEqual(
            response.plan_ir.waypoints[0].position_m,
            [1.0, 0.03, 0.4],
        )
        self.assertEqual(
            response.plan_ir.waypoints[1].position_m,
            [2.5, 0.03, -0.5],
        )

    def test_ordered_known_objects_resolve_ordinal_table_label(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text=(
                "First move the gripper to the cube on the table, "
                "then to the center of the second table."
            ),
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.0, 0.2, 0.0],
                    reach_center_m=[0.0, 0.2, 0.0],
                    reach_radius_m=2.0,
                )
            ],
            known_scene_objects=[
                KnownSceneObject(
                    id="cube",
                    label="cube",
                    position_m=[0.3, 0.05, 0.0],
                    size_m=[0.1, 0.1, 0.1],
                ),
                KnownSceneObject(
                    id="table_1",
                    label="table 1",
                    position_m=[0.4, 0.0, 0.0],
                    size_m=[0.8, 0.05, 0.8],
                ),
                KnownSceneObject(
                    id="table_2",
                    label="table 2",
                    position_m=[0.9, 0.0, 0.2],
                    size_m=[0.8, 0.05, 0.8],
                ),
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(len(response.plan_ir.waypoints), 2)
        self.assertEqual(response.visual_groundings[0].label, "cube")
        self.assertEqual(response.visual_groundings[1].label, "table 2")
        self.assertAlmostEqual(
            response.plan_ir.waypoints[1].position_m[0],
            0.9,
            places=4,
        )

    def test_known_blue_cube_overrides_wrong_vlm_pixel_and_waypoint(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper to the blue cube.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=1707, height=875),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[-0.0557, 0.1051, 0.5974],
                    reach_center_m=[-0.25, 0.23, 1.05],
                    reach_radius_m=0.75,
                )
            ],
            known_scene_objects=[
                KnownSceneObject(
                    id="auto_blue_sphere",
                    label="blue sphere",
                    position_m=[0.18, 0.11, 0.86],
                    size_m=[0.1, 0.1, 0.1],
                ),
                KnownSceneObject(
                    id="auto_blue_cube",
                    label="blue cube",
                    position_m=[0.10, 0.14, 1.05],
                    size_m=[0.12, 0.12, 0.12],
                ),
            ],
        )
        response = RobotCommandResponse(
            spoken_reply="I am moving the gripper to the blue cube.",
            intent=Intent(
                robot_id="UR3_TestRobot",
                task_type="move_to",
                target_ref="blue cube",
                motion_primitive="move_to",
            ),
            visual_grounding=VisualGrounding(
                label="blue cube",
                confidence=0.98,
                bbox_xyxy_px=[442, 588, 482, 638],
                preferred_point_px=[462, 613],
                world_position_m=None,
                world_confidence=0.0,
            ),
            plan_ir=PlanIr(
                kind="move_to",
                robot_id="UR3_TestRobot",
                waypoints=[
                    Waypoint(position_m=[-0.0557, 0.1051, 0.5974]),
                ],
            ),
        )

        repaired = _repair_or_override_plan(request, response)

        self.assertIsNone(repaired.error)
        self.assertEqual(repaired.intent.target_ref, "blue cube")
        self.assertEqual(
            repaired.visual_grounding.world_position_m,
            [0.10, 0.14, 1.05],
        )
        self.assertEqual(repaired.visual_grounding.bbox_xyxy_px, [])
        self.assertEqual(repaired.visual_grounding.preferred_point_px, [])
        waypoint = repaired.plan_ir.waypoints[0].position_m
        self.assertAlmostEqual(waypoint[0], 0.10, places=4)
        self.assertAlmostEqual(waypoint[1], 0.25, places=4)
        self.assertAlmostEqual(waypoint[2], 1.05, places=4)

    def test_qwen_1000_grounding_coordinates_scale_to_image_pixels(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        try:
            os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = "qwen_1000"
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move the gripper to the blue cube.",
                image_source="unity_screenshot",
                camera=CameraSnapshot(width=1707, height=875),
            )
            response = RobotCommandResponse(
                visual_grounding=VisualGrounding(
                    label="blue cube",
                    confidence=0.98,
                    bbox_xyxy_px=[442, 588, 482, 638],
                    preferred_point_px=[462, 613],
                ),
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertAlmostEqual(
                response.visual_grounding.bbox_xyxy_px[0],
                442 / 1000 * 1707,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.bbox_xyxy_px[1],
                588 / 1000 * 875,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[0],
                462 / 1000 * 1707,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[1],
                613 / 1000 * 875,
                places=4,
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format

    def test_qwen_1000_grounding_coordinates_scale_all_targets(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        try:
            os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = "qwen_1000"
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move to the cube, then the table center.",
                image_source="unity_screenshot",
                camera=CameraSnapshot(width=1000, height=500),
            )
            response = RobotCommandResponse(
                visual_groundings=[
                    VisualGrounding(
                        label="cube",
                        confidence=0.95,
                        bbox_xyxy_px=[100, 200, 300, 400],
                        preferred_point_px=[200, 300],
                    ),
                    VisualGrounding(
                        label="table center",
                        confidence=0.90,
                        bbox_xyxy_px=[500, 100, 800, 300],
                        preferred_point_px=[650, 200],
                    ),
                ],
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertEqual(len(response.visual_groundings), 2)
            self.assertEqual(response.visual_grounding.label, "cube")
            self.assertAlmostEqual(
                response.visual_groundings[0].bbox_xyxy_px[1],
                100.0,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_groundings[1].preferred_point_px[1],
                100.0,
                places=4,
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format

    def test_default_coordinate_format_scales_qwen_grid(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        try:
            os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move the gripper to the yellow ball.",
                image_source="quest_passthrough_camera",
                camera=CameraSnapshot(width=1280, height=960),
            )
            response = RobotCommandResponse(
                visual_grounding=VisualGrounding(
                    label="yellow ball",
                    confidence=0.98,
                    bbox_xyxy_px=[582, 432, 642, 508],
                    preferred_point_px=[612, 470],
                ),
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertAlmostEqual(
                response.visual_grounding.bbox_xyxy_px[0],
                582 / 1000 * 1280,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.bbox_xyxy_px[1],
                432 / 1000 * 960,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[0],
                612 / 1000 * 1280,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[1],
                470 / 1000 * 960,
                places=4,
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format

    def test_auto_coordinate_format_scales_ambiguous_qwen_grounding(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        old_model = os.environ.get("ROBOT_AI_MODEL")
        try:
            os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = "auto"
            os.environ["ROBOT_AI_MODEL"] = "Qwen/Qwen3-VL-8B-Instruct-FP8"
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move the gripper to the yellow ball.",
                image_source="quest_passthrough_camera",
                camera=CameraSnapshot(width=1280, height=960),
            )
            response = RobotCommandResponse(
                visual_grounding=VisualGrounding(
                    label="yellow ball",
                    confidence=0.98,
                    bbox_xyxy_px=[582, 432, 642, 508],
                    preferred_point_px=[612, 470],
                ),
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertAlmostEqual(
                response.visual_grounding.bbox_xyxy_px[0],
                744.96,
                places=4,
            )
            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[1],
                451.2,
                places=4,
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format
            if old_model is None:
                os.environ.pop("ROBOT_AI_MODEL", None)
            else:
                os.environ["ROBOT_AI_MODEL"] = old_model

    def test_auto_coordinate_format_uses_default_qwen_model(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        old_model = os.environ.get("ROBOT_AI_MODEL")
        try:
            os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = "auto"
            os.environ.pop("ROBOT_AI_MODEL", None)
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move the gripper to the yellow ball.",
                image_source="quest_passthrough_camera",
                camera=CameraSnapshot(width=1280, height=960),
            )
            response = RobotCommandResponse(
                visual_grounding=VisualGrounding(
                    label="yellow ball",
                    confidence=0.98,
                    bbox_xyxy_px=[582, 432, 642, 508],
                    preferred_point_px=[612, 470],
                ),
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertAlmostEqual(
                response.visual_grounding.preferred_point_px[0],
                783.36,
                places=4,
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format
            if old_model is None:
                os.environ.pop("ROBOT_AI_MODEL", None)
            else:
                os.environ["ROBOT_AI_MODEL"] = old_model

    def test_auto_coordinate_format_preserves_non_qwen_pixel_grounding(self):
        old_format = os.environ.get("ROBOT_AI_VLM_COORD_FORMAT")
        old_model = os.environ.get("ROBOT_AI_MODEL")
        try:
            os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = "auto"
            os.environ["ROBOT_AI_MODEL"] = "pixel-grounding-model"
            request = RobotCommandRequest(
                session_id="s",
                command_text="Move the gripper to the controller.",
                image_source="quest_passthrough_camera",
                camera=CameraSnapshot(width=1280, height=960),
            )
            response = RobotCommandResponse(
                visual_grounding=VisualGrounding(
                    label="controller",
                    confidence=0.88,
                    bbox_xyxy_px=[742, 404, 832, 470],
                    preferred_point_px=[787, 437],
                ),
            )

            _normalize_visual_grounding_coordinates(request, response)

            self.assertEqual(
                response.visual_grounding.bbox_xyxy_px,
                [742.0, 404.0, 832.0, 470.0],
            )
            self.assertEqual(
                response.visual_grounding.preferred_point_px,
                [787.0, 437.0],
            )
        finally:
            if old_format is None:
                os.environ.pop("ROBOT_AI_VLM_COORD_FORMAT", None)
            else:
                os.environ["ROBOT_AI_VLM_COORD_FORMAT"] = old_format
            if old_model is None:
                os.environ.pop("ROBOT_AI_MODEL", None)
            else:
                os.environ["ROBOT_AI_MODEL"] = old_model

    def test_not_grounded_full_frame_placeholder_remains_rejected(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper to the controller.",
            image_source="quest_passthrough_camera",
            camera=CameraSnapshot(width=1280, height=960),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.0, 0.2, 0.5],
                    reach_center_m=[0.0, 0.2, 0.5],
                    reach_radius_m=0.65,
                )
            ],
        )
        response = RobotCommandResponse(
            spoken_reply="I cannot locate the controller.",
            intent=Intent(
                robot_id="UR3_TestRobot",
                task_type="move_to",
                target_ref="controller",
                motion_primitive="move_to",
            ),
            visual_grounding=VisualGrounding(
                label="controller",
                confidence=0.0,
                bbox_xyxy_px=[0, 0, 1280, 960],
                preferred_point_px=[640, 480],
                world_position_m=None,
                world_confidence=0.0,
            ),
            plan_ir=PlanIr(
                kind="move_to",
                robot_id="UR3_TestRobot",
                waypoints=[],
            ),
            error=ErrorPayload(
                code="not_grounded",
                message="The target 'controller' is not visible.",
            ),
        )

        repaired = _repair_or_override_plan(request, response)

        self.assertIsNotNone(repaired.error)
        self.assertEqual(repaired.error.code, "not_grounded")
        self.assertEqual(repaired.plan_ir.waypoints, [])

    def test_unknown_object_grounding_clears_untrusted_model_waypoints(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper to the blue cube.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=1707, height=875),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[-0.0557, 0.1051, 0.5974],
                    reach_center_m=[-0.25, 0.23, 1.05],
                    reach_radius_m=0.75,
                )
            ],
        )
        response = RobotCommandResponse(
            intent=Intent(
                robot_id="UR3_TestRobot",
                task_type="move_to",
                target_ref="blue cube",
                motion_primitive="move_to",
            ),
            visual_grounding=VisualGrounding(
                label="blue cube",
                confidence=0.98,
                bbox_xyxy_px=[754, 514, 823, 558],
                preferred_point_px=[789, 536],
                world_position_m=None,
                world_confidence=0.0,
            ),
            plan_ir=PlanIr(
                kind="move_to",
                robot_id="UR3_TestRobot",
                waypoints=[
                    Waypoint(position_m=[-0.0557, 0.1051, 0.5974]),
                ],
            ),
        )

        repaired = _repair_or_override_plan(request, response)

        self.assertIsNone(repaired.error)
        self.assertEqual(repaired.plan_ir.kind, "manipulator_reach")
        self.assertEqual(repaired.plan_ir.robot_id, "UR3_TestRobot")
        self.assertEqual(repaired.plan_ir.waypoints, [])
        self.assertEqual(repaired.intent.task_type, "manipulator_reach")

    def test_relative_vertical_motion_uses_tcp_position(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper vertically up by 30 centimeters.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.2, 0.3, 0.0],
                    reach_center_m=[0.0, 0.18, 0.0],
                    reach_radius_m=0.75,
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.intent.target_ref, "relative_tcp")
        self.assertEqual(response.plan_ir.kind, "manipulator_reach")
        self.assertAlmostEqual(
            response.plan_ir.waypoints[0].position_m[1],
            0.6,
            places=4,
        )

    def test_relative_decimal_meter_motion_preserves_fraction(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text="Move the gripper up by 0.2 meters.",
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    tcp_position_m=[0.2, 0.3, 0.0],
                    reach_center_m=[0.0, 0.18, 0.0],
                    reach_radius_m=0.75,
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.intent.target_ref, "relative_tcp")
        self.assertIn("0.200m", response.intent.motion_primitive)
        self.assertAlmostEqual(
            response.plan_ir.waypoints[0].position_m[1],
            0.5,
            places=4,
        )

    def test_horizontal_circle_around_base_uses_geometric_primitive(self):
        request = RobotCommandRequest(
            session_id="s",
            command_text=(
                "Move the gripper in a horizontal circle of 0.1 m "
                "radius around the base."
            ),
            image_source="unity_screenshot",
            camera=CameraSnapshot(width=640, height=480),
            robots=[
                RobotSnapshot(
                    id="UR3_TestRobot",
                    kind="manipulator",
                    root_position_m=[-0.25, 0.05, 1.05],
                    tcp_position_m=[0.0, 0.25, 1.05],
                    reach_center_m=[-0.25, 0.23, 1.05],
                    reach_radius_m=0.75,
                )
            ],
        )

        response = make_response(request, image_bytes=None)

        self.assertIsNone(response.error)
        self.assertEqual(response.intent.task_type, "geometric_primitive")
        self.assertEqual(response.plan_ir.kind, "geometric_primitive")
        self.assertEqual(response.plan_ir.robot_id, "UR3_TestRobot")
        self.assertGreaterEqual(len(response.plan_ir.waypoints), 24)
        first = response.plan_ir.waypoints[0].position_m
        self.assertAlmostEqual(first[0], -0.15, places=4)
        self.assertAlmostEqual(first[1], 0.25, places=4)
        self.assertAlmostEqual(first[2], 1.05, places=4)

    def test_saved_image_path_uses_host_label_when_configured(self):
        old_output_dir = os.environ.get("ROBOT_AI_OUTPUT_DIR")
        old_host_label = os.environ.get("ROBOT_AI_OUTPUT_DIR_HOST_LABEL")
        try:
            os.environ["ROBOT_AI_OUTPUT_DIR"] = "/workspace/outputs/robot_ai"
            os.environ["ROBOT_AI_OUTPUT_DIR_HOST_LABEL"] = (
                "/media/imit-learn/orcestra_robot_ai/outputs/robot_ai"
            )

            path = _saved_image_display_path(
                Path("/workspace/outputs/robot_ai/20260622_test_raw.png"))

            self.assertEqual(
                path,
                "/media/imit-learn/orcestra_robot_ai/outputs/robot_ai/"
                "20260622_test_raw.png",
            )
        finally:
            if old_output_dir is None:
                os.environ.pop("ROBOT_AI_OUTPUT_DIR", None)
            else:
                os.environ["ROBOT_AI_OUTPUT_DIR"] = old_output_dir
            if old_host_label is None:
                os.environ.pop("ROBOT_AI_OUTPUT_DIR_HOST_LABEL", None)
            else:
                os.environ["ROBOT_AI_OUTPUT_DIR_HOST_LABEL"] = old_host_label

    def test_openai_payload_uses_multi_waypoint_token_budget_by_default(self):
        old_mode = os.environ.get("ROBOT_AI_MODE")
        old_max_tokens = os.environ.get("ROBOT_AI_MAX_TOKENS")
        old_urlopen = model_client.urllib.request.urlopen
        captured = {}

        class FakeResponse:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

            def read(self):
                content = json.dumps({
                    "spoken_reply": "ok",
                    "intent": {},
                    "visual_grounding": {},
                    "visual_groundings": [],
                    "plan_ir": {},
                    "diagnostics": {},
                    "error": None,
                })
                return json.dumps({
                    "choices": [
                        {
                            "message": {"content": content},
                            "finish_reason": "stop",
                        }
                    ]
                }).encode("utf-8")

        def fake_urlopen(req, timeout):
            captured["payload"] = json.loads(req.data.decode("utf-8"))
            return FakeResponse()

        try:
            os.environ["ROBOT_AI_MODE"] = "openai_compatible"
            os.environ.pop("ROBOT_AI_MAX_TOKENS", None)
            model_client.urllib.request.urlopen = fake_urlopen
            request = RobotCommandRequest(
                session_id="token-budget",
                command_text=(
                    "First move the gripper to the cube, then to the table."
                ),
                image_source="unity_screenshot",
                camera=CameraSnapshot(width=640, height=480),
            )

            make_response(request, image_bytes=None)

            self.assertGreaterEqual(captured["payload"]["max_tokens"], 2048)
        finally:
            model_client.urllib.request.urlopen = old_urlopen
            if old_mode is None:
                os.environ.pop("ROBOT_AI_MODE", None)
            else:
                os.environ["ROBOT_AI_MODE"] = old_mode
            if old_max_tokens is None:
                os.environ.pop("ROBOT_AI_MAX_TOKENS", None)
            else:
                os.environ["ROBOT_AI_MAX_TOKENS"] = old_max_tokens

    def test_openai_http_error_includes_response_body(self):
        old_mode = os.environ.get("ROBOT_AI_MODE")
        old_urlopen = model_client.urllib.request.urlopen

        def fake_urlopen(req, timeout):
            raise urllib.error.HTTPError(
                req.full_url,
                400,
                "Bad Request",
                hdrs=None,
                fp=io.BytesIO(b'{"error":"image tokens exceed limit"}'),
            )

        try:
            os.environ["ROBOT_AI_MODE"] = "openai_compatible"
            model_client.urllib.request.urlopen = fake_urlopen
            request = RobotCommandRequest(
                session_id="http-error",
                command_text="Move the gripper to the cube.",
                image_source="unity_screenshot",
                camera=CameraSnapshot(width=640, height=480),
            )

            with self.assertRaisesRegex(
                RuntimeError, "image tokens exceed limit"
            ):
                make_response(request, image_bytes=None)
        finally:
            model_client.urllib.request.urlopen = old_urlopen
            if old_mode is None:
                os.environ.pop("ROBOT_AI_MODE", None)
            else:
                os.environ["ROBOT_AI_MODE"] = old_mode

    def test_openai_payload_resizes_large_image_before_model(self):
        old_mode = os.environ.get("ROBOT_AI_MODE")
        old_max_side = os.environ.get("ROBOT_AI_VLM_IMAGE_MAX_SIDE")
        old_urlopen = model_client.urllib.request.urlopen
        captured = {}

        class FakeResponse:
            def __enter__(self):
                return self

            def __exit__(self, exc_type, exc, tb):
                return False

            def read(self):
                content = json.dumps({
                    "spoken_reply": "ok",
                    "intent": {},
                    "visual_grounding": {},
                    "visual_groundings": [],
                    "plan_ir": {},
                    "diagnostics": {},
                    "error": None,
                })
                return json.dumps({
                    "choices": [{"message": {"content": content}}]
                }).encode("utf-8")

        def fake_urlopen(req, timeout):
            captured["payload"] = json.loads(req.data.decode("utf-8"))
            return FakeResponse()

        try:
            from PIL import Image

            image = Image.new("RGB", (1400, 1000), (20, 40, 80))
            source = io.BytesIO()
            image.save(source, format="PNG")
            os.environ["ROBOT_AI_MODE"] = "openai_compatible"
            os.environ["ROBOT_AI_VLM_IMAGE_MAX_SIDE"] = "256"
            model_client.urllib.request.urlopen = fake_urlopen
            request = RobotCommandRequest(
                session_id="resize-image",
                command_text="Move the gripper to the cube.",
                image_source="unity_screenshot",
                camera=CameraSnapshot(width=1400, height=1000),
            )

            make_response(request, image_bytes=source.getvalue())

            content = captured["payload"]["messages"][1]["content"]
            image_url = content[1]["image_url"]["url"]
            encoded = image_url.split(",", 1)[1]
            with Image.open(io.BytesIO(base64.b64decode(encoded))) as resized:
                self.assertLessEqual(max(resized.size), 256)
                self.assertEqual(resized.size, (256, 183))
        finally:
            model_client.urllib.request.urlopen = old_urlopen
            if old_mode is None:
                os.environ.pop("ROBOT_AI_MODE", None)
            else:
                os.environ["ROBOT_AI_MODE"] = old_mode
            if old_max_side is None:
                os.environ.pop("ROBOT_AI_VLM_IMAGE_MAX_SIDE", None)
            else:
                os.environ["ROBOT_AI_VLM_IMAGE_MAX_SIDE"] = old_max_side

    def test_trace_file_records_request_and_final_response(self):
        old_save_traces = os.environ.get("ROBOT_AI_SAVE_TRACES")
        old_output_dir = os.environ.get("ROBOT_AI_OUTPUT_DIR")
        try:
            with tempfile.TemporaryDirectory() as tmp:
                os.environ["ROBOT_AI_SAVE_TRACES"] = "1"
                os.environ["ROBOT_AI_OUTPUT_DIR"] = tmp
                request = RobotCommandRequest(
                    session_id="trace-test",
                    command_text="Move the gripper up by 0.2 meters.",
                    image_source="unity_screenshot",
                    camera=CameraSnapshot(width=640, height=480),
                    robots=[
                        RobotSnapshot(
                            id="UR3_TestRobot",
                            kind="manipulator",
                            tcp_position_m=[0.2, 0.3, 0.0],
                            reach_center_m=[0.0, 0.18, 0.0],
                            reach_radius_m=0.75,
                        )
                    ],
                )

                response = make_response(request, image_bytes=None)

                trace_path = Path(response.diagnostics.saved_trace_path)
                self.assertTrue(trace_path.exists())
                payload = json.loads(trace_path.read_text(encoding="utf-8"))
                self.assertEqual(
                    payload["request"]["command_text"],
                    "Move the gripper up by 0.2 meters.",
                )
                self.assertIn("raw_model_output", payload)
                self.assertIn("response_before_repair", payload)
                self.assertEqual(
                    payload["final_response"]["plan_ir"]["kind"],
                    "manipulator_reach",
                )
                self.assertIn(
                    "0.200m",
                    payload["final_response"]["intent"]["motion_primitive"],
                )
        finally:
            if old_save_traces is None:
                os.environ.pop("ROBOT_AI_SAVE_TRACES", None)
            else:
                os.environ["ROBOT_AI_SAVE_TRACES"] = old_save_traces
            if old_output_dir is None:
                os.environ.pop("ROBOT_AI_OUTPUT_DIR", None)
            else:
                os.environ["ROBOT_AI_OUTPUT_DIR"] = old_output_dir


if __name__ == "__main__":
    unittest.main()
