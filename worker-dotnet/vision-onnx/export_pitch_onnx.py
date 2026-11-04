"""Export a YOLO pose model that detects the 32 soccer-pitch keypoints to ONNX, and dump a
golden reference of its keypoint predictions.

This mirrors export_and_golden.py (which handles the detection model). The difference: a pose
model outputs, per instance, a set of (x, y, visibility) keypoints instead of just a box. We
detect the single "pitch" instance and record its 32 keypoints in ORIGINAL-image pixel coords —
the parity oracle the .NET PitchKeypointDetector must reproduce before anything is built on top.

Getting the model (Roboflow, football-field-detection):
    - Roboflow Universe -> football-field-detection-f07vi -> a YOLOv8-pose model trained on 32
      pitch keypoints. Download the .pt weights (needs a Roboflow account/API key).
    - Or `pip install roboflow` and pull it via the SDK, then point --model at the .pt.

Run from the pitchwise repo root, inside the vision-onnx venv:

    worker-dotnet/vision-onnx/.venv/Scripts/python.exe \
        worker-dotnet/vision-onnx/export_pitch_onnx.py \
        --model football-field.pt --video test_mecz.mp4 --imgsz 640 --frames 1300,1350,1400

Outputs (next to this script):
    <model-stem>.onnx            exported ONNX (opset 12, dynamic batch, no NMS baked in)
    golden_<model-stem>.json     ultralytics' own keypoints on the chosen frames, in
                                 original-image (x, y, conf) pixel coords.
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
    ap.add_argument("--model", required=True, help="path to the YOLO pose .pt (32 pitch keypoints)")
    ap.add_argument("--video", required=True, help="video to sample golden frames from")
    ap.add_argument("--imgsz", type=int, default=640, help="ONNX input size (square)")
    ap.add_argument("--frames", default="0,25,50", help="comma-separated frame indices")
    ap.add_argument("--conf", type=float, default=0.25, help="ultralytics conf threshold")
    args = ap.parse_args()

    from ultralytics import YOLO
    import cv2

    model_path = Path(args.model)
    stem = model_path.stem
    yolo = YOLO(str(model_path))
    print(f"Loaded {model_path}")
    # A pose model exposes the keypoint count; surface it so a mismatch with PitchModel (32) is
    # caught here rather than as garbage homographies later.
    kpt_shape = getattr(getattr(yolo, "model", None), "kpt_shape", None)
    print(f"keypoint shape: {kpt_shape}  (expected [32, 2] or [32, 3])")

    # --- 1. Export to ONNX. Same flags as the detector export so the .NET side owns
    #        pre/post-processing identically: fixed square imgsz, opset 12, dynamic batch,
    #        no NMS baked in. ---
    onnx_path = yolo.export(
        format="onnx",
        imgsz=args.imgsz,
        opset=12,
        dynamic=True,
        simplify=True,
        nms=False,
    )
    onnx_dest = HERE / f"{stem}.onnx"
    Path(onnx_path).replace(onnx_dest)
    print(f"Exported ONNX -> {onnx_dest}")

    # --- 2. Golden: run ultralytics on the chosen frames, record the pitch instance's
    #        keypoints in original-image pixel coords. ---
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
        "kpt_shape": list(kpt_shape) if kpt_shape is not None else None,
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
                frame, imgsz=args.imgsz, conf=args.conf, device="cpu", verbose=False,
            )[0]

            # Take the highest-confidence instance (there is one pitch). kpts: [N, K, 2 or 3].
            instances = []
            if res.keypoints is not None and len(res.keypoints) > 0:
                xy = res.keypoints.xy.cpu().numpy()          # [N, K, 2] in original pixels
                conf = (res.keypoints.conf.cpu().numpy()      # [N, K] visibility, if present
                        if res.keypoints.conf is not None else None)
                box_conf = (res.boxes.conf.cpu().numpy()
                            if res.boxes is not None and len(res.boxes) else None)
                best = int(box_conf.argmax()) if box_conf is not None else 0
                pts = []
                for k in range(xy.shape[1]):
                    x, y = float(xy[best, k, 0]), float(xy[best, k, 1])
                    c = float(conf[best, k]) if conf is not None else 1.0
                    pts.append({"i": k, "x": round(x, 2), "y": round(y, 2), "conf": round(c, 4)})
                instances = pts

            golden["frames"][str(idx)] = {"width": w, "height": h, "keypoints": instances}
            print(f"frame {idx}: {len(instances)} keypoints ({w}x{h})")
        idx += 1
    cap.release()

    golden_dest = HERE / f"golden_{stem}.json"
    golden_dest.write_text(json.dumps(golden, indent=2))
    print(f"Wrote golden reference -> {golden_dest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
