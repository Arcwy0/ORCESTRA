import os
import json
import tempfile
from pathlib import Path
import unittest

try:
    import pydantic  # noqa: F401
    HAS_PYDANTIC = True
except ModuleNotFoundError:
    HAS_PYDANTIC = False

if HAS_PYDANTIC:
    from server.robot_ai.model_client import (
        make_response,
        _saved_image_display_path,
        _repair_or_override_plan,
        _normalize_visual_grounding_coordinates,
    )
    from server.robot_ai.schemas import (
        CameraSnapshot,
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
