from __future__ import annotations

import json
import os
import uuid
import base64
import urllib.request


# Small valid PNG. It is intentionally trivial; this smoke test validates the
# transport/schema path, not object grounding quality.
PNG_1X1 = (
    b"\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01"
    b"\x00\x00\x00\x01\x08\x02\x00\x00\x00\x90wS\xde\x00"
    b"\x00\x00\x0cIDAT\x08\xd7c\xf8\xcf\xc0\x00\x00\x03"
    b"\x01\x01\x00\x18\xdd\x8d\xb0\x00\x00\x00\x00IEND\xaeB`\x82"
)


def main() -> None:
    url = os.getenv(
        "ROBOT_AI_GATEWAY_URL",
        "http://127.0.0.1:8080/v1/robot/command_json",
    )
    request_json = {
        "session_id": "smoke",
        "command_text": "Move the gripper to the cup on the table.",
        "image_source": "unity_screenshot",
        "audio_format": "",
        "audio_sample_rate_hz": 0,
        "camera": {
            "world_from_camera": [],
            "projection": [],
            "width": 1280,
            "height": 720,
        },
        "robots": [
            {
                "id": "UR3_1",
                "kind": "manipulator",
                "root_position_m": [0.0, 0.0, 0.0],
                "root_rotation_xyzw": [0.0, 0.0, 0.0, 1.0],
                "tcp_position_m": [0.2, 0.2, 0.2],
                "reach_center_m": [0.0, 0.2, 0.0],
                "reach_radius_m": 0.6,
                "joint_deg": [],
            }
        ],
        "mr_planes": [],
        "known_scene_objects": [],
    }
    if url.endswith("/command_json"):
        payload = {
            "request": request_json,
            "image_png_base64": base64.b64encode(PNG_1X1).decode("ascii"),
            "audio_wav_base64": "",
        }
        body = json.dumps(payload).encode("utf-8")
        content_type = "application/json"
    else:
        body, content_type = multipart_body(
            {"request_json": json.dumps(request_json)},
            {"image": ("screenshot.png", "image/png", PNG_1X1)},
        )
    req = urllib.request.Request(
        url,
        data=body,
        headers={"Content-Type": content_type},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=180) as response:
        print(response.read().decode("utf-8"))


def multipart_body(
    fields: dict[str, str],
    files: dict[str, tuple[str, str, bytes]],
) -> tuple[bytes, str]:
    boundary = "----robot-ai-smoke-" + uuid.uuid4().hex
    chunks: list[bytes] = []
    for name, value in fields.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode(),
                f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode(),
                value.encode(),
                b"\r\n",
            ]
        )
    for name, (filename, content_type, data) in files.items():
        chunks.extend(
            [
                f"--{boundary}\r\n".encode(),
                (
                    f'Content-Disposition: form-data; name="{name}"; '
                    f'filename="{filename}"\r\n'
                ).encode(),
                f"Content-Type: {content_type}\r\n\r\n".encode(),
                data,
                b"\r\n",
            ]
        )
    chunks.append(f"--{boundary}--\r\n".encode())
    return b"".join(chunks), f"multipart/form-data; boundary={boundary}"


if __name__ == "__main__":
    main()
