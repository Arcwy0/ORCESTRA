import os
import unittest

try:
    import pydantic  # noqa: F401
    HAS_PYDANTIC = True
except ModuleNotFoundError:
    HAS_PYDANTIC = False

if HAS_PYDANTIC:
    from server.robot_ai.model_client import make_response
    from server.robot_ai.schemas import (
        CameraSnapshot,
        RobotCommandRequest,
        RobotSnapshot,
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


if __name__ == "__main__":
    unittest.main()
