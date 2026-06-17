#!/usr/bin/env sh
set -eu

echo "Host GPU:"
nvidia-smi

echo
echo "Docker GPU:"
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
