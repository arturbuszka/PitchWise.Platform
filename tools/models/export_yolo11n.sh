#!/usr/bin/env bash
# Exports yolo11n to ONNX, the COCO model the stack runs with by default. Python is needed only here.
# Uses CPU-only torch, which is much smaller and enough for an export. The venv stays in
# ~/.cache/pitchwise/export-venv so re-runs are quick; delete it when you are done.
# Usage: tools/models/export_yolo11n.sh
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
venv="$HOME/.cache/pitchwise/export-venv"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ ! -x "$venv/bin/yolo" ]; then
    python3 -m venv "$venv"
    "$venv/bin/pip" install -q torch torchvision --index-url https://download.pytorch.org/whl/cpu
    "$venv/bin/pip" install -q ultralytics onnx onnxslim onnxruntime
fi

(cd "$work" && "$venv/bin/yolo" export model=yolo11n.pt format=onnx imgsz=640 opset=17 simplify=True dynamic=True)
cp "$work/yolo11n.onnx" "$repo/worker-dotnet/vision-onnx/yolo11n.onnx"
echo "Exported worker-dotnet/vision-onnx/yolo11n.onnx"
