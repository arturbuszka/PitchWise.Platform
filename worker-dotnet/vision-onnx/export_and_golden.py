"""Export a YOLO11 (.pt) model to ONNX and dump a golden detection reference.

Run from the pitchwise repo root, inside worker/.venv:

    worker/.venv/Scripts/python.exe worker-dotnet/vision-onnx/export_and_golden.py \
        --model worker/yolo11n.pt --video mundial.mp4 --imgsz 640 --frames 0,25,50

Outputs (next to this script):
    <model-stem>.onnx           the exported ONNX model (opset 12, fixed imgsz)
    golden_<model-stem>.json    ultralytics' own detections on the chosen frames,
                                in ORIGINAL-image xyxy pixel coords, used as the
                                parity oracle for the .NET Yolo11OnnxDetector.

The .NET detector must reproduce these boxes/classes/scores (within tolerance)
on the same frames, feeding the SAME exported .onnx. That proves the C# pre/post
-processing matches ultralytics before we build anything on top of it.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent


def parse_frames(spec: str) -> list[int]:
    return [int(x) for x in spec.split(",") if x.strip()]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True, help="path to YOLO11 .pt")
    ap.add_argument("--video", required=True, help="video to sample golden frames from")
    ap.add_argument("--imgsz", type=int, default=640, help="ONNX input size (square)")
    ap.add_argument("--frames", default="0,25,50", help="comma-separated frame indices")
    ap.add_argument("--conf", type=float, default=0.25, help="ultralytics conf threshold")
    ap.add_argument("--iou", type=float, default=0.7, help="ultralytics NMS IoU")
    args = ap.parse_args()

    from ultralytics import YOLO
    import cv2

    model_path = Path(args.model)
    stem = model_path.stem
    yolo = YOLO(str(model_path))
    print(f"Loaded {model_path} — classes: {yolo.names}")

    # --- 1. Export to ONNX (fixed square imgsz, opset 12, no NMS baked in so the
    #        .NET side owns post-processing exactly like the Python detector). ---
    onnx_path = yolo.export(
        format="onnx",
        imgsz=args.imgsz,
        opset=12,
        # dynamic batch axis so the .NET detector can run N frames in one inference
        # (GPU batching). Batch=1 still works, so live + parity are unaffected.
        dynamic=True,
        simplify=True,
        nms=False,
    )
    onnx_dest = HERE / f"{stem}.onnx"
    Path(onnx_path).replace(onnx_dest)
    print(f"Exported ONNX -> {onnx_dest}")

    # --- 2. Golden reference: run ultralytics on the chosen frames and record the
    #        resulting boxes in original-image pixel coords. ---
    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        raise SystemExit(f"Cannot open video: {args.video}")

    want = set(parse_frames(args.frames))
    max_idx = max(want)
    golden: dict = {
        "model": str(model_path),
        "onnx": onnx_dest.name,
        "imgsz": args.imgsz,
        "conf": args.conf,
        "iou": args.iou,
        "names": {int(k): v for k, v in yolo.names.items()},
        "frames": {},
    }

    idx = 0
    while idx <= max_idx:
        ok, frame = cap.read()
        if not ok:
            break
        if idx in want:
            h, w = frame.shape[:2]
            res = yolo.predict(
                frame, imgsz=args.imgsz, conf=args.conf, iou=args.iou,
                device="cpu", verbose=False,
            )[0]
            dets = []
            for b in res.boxes:
                x1, y1, x2, y2 = (float(v) for v in b.xyxy[0].tolist())
                dets.append({
                    "cls": int(b.cls[0]),
                    "name": yolo.names[int(b.cls[0])],
                    "conf": round(float(b.conf[0]), 4),
                    "xyxy": [round(x1, 2), round(y1, 2), round(x2, 2), round(y2, 2)],
                })
            golden["frames"][str(idx)] = {"width": w, "height": h, "detections": dets}
            print(f"frame {idx}: {len(dets)} detections ({w}x{h})")
        idx += 1
    cap.release()

    golden_dest = HERE / f"golden_{stem}.json"
    golden_dest.write_text(json.dumps(golden, indent=2))
    print(f"Wrote golden reference -> {golden_dest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
