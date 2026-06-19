from __future__ import annotations

import os
from pathlib import Path

from huggingface_hub import snapshot_download


def main() -> None:
    asr_model_id = os.getenv("ROBOT_AI_ASR_MODEL_ID", "Systran/faster-whisper-base.en")
    asr_dir = Path(os.getenv("ROBOT_AI_ASR_MODEL_DIR", "/models/asr/faster-whisper-base.en"))

    print(f"Downloading ASR model {asr_model_id} -> {asr_dir}")
    asr_dir.mkdir(parents=True, exist_ok=True)
    snapshot_download(
        repo_id=asr_model_id,
        local_dir=str(asr_dir),
        local_dir_use_symlinks=False,
    )

    print("ASR model download complete.")


if __name__ == "__main__":
    main()
