"""Export a torchreid OSNet person-Re-ID model to ONNX + dump a golden embedding reference.

Companion to export_and_golden.py (YOLO). Produces the appearance model consumed by the
.NET OsNetOnnxEmbedder, plus a parity oracle so the C# preprocessing can be proven to match
torchreid before PlayerReId is built on top of it.

Run from the pitchwise repo root, inside a venv with torch + torchreid installed:

    pip install torch torchreid onnx
    python worker-dotnet/vision-onnx/export_reid_onnx.py \
        --arch osnet_x0_25 --crops crop1.jpg crop2.jpg

Outputs (next to this script):
    <arch>.onnx              exported ONNX model (opset 12, dynamic batch axis)
    golden_reid_<arch>.json  torchreid embeddings for the given crops, L2-normalized, used
                             as the parity oracle for OsNetOnnxEmbedder.

The .NET embedder must reproduce these vectors (cosine >= ~0.999) feeding the SAME .onnx.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

HERE = Path(__file__).resolve().parent

# torchreid person-Re-ID input geometry + ImageNet normalization (its default eval transform).
# MUST match OsNetOnnxEmbedder (InputH=256, InputW=128, ImageNet mean/std).
INPUT_H = 256
INPUT_W = 128
MEAN = [0.485, 0.456, 0.406]
STD = [0.229, 0.224, 0.225]


def preprocess(img_bgr, np, cv2):
    """BGR uint8 HxWx3 -> 1x3x256x128 float32, RGB, /255, ImageNet-normalized.
    Mirrors OsNetOnnxEmbedder.WriteChw exactly (same resize + normalization)."""
    resized = cv2.resize(img_bgr, (INPUT_W, INPUT_H), interpolation=cv2.INTER_LINEAR)
    rgb = cv2.cvtColor(resized, cv2.COLOR_BGR2RGB).astype("float32") / 255.0
    for c in range(3):
        rgb[:, :, c] = (rgb[:, :, c] - MEAN[c]) / STD[c]
    chw = rgb.transpose(2, 0, 1)[None, ...]  # 1x3xHxW
    return np.ascontiguousarray(chw)


def l2_normalize(vec, np):
    n = float(np.linalg.norm(vec))
    return vec if n <= 1e-12 else vec / n


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--arch", default="osnet_x0_25", help="torchreid model name")
    ap.add_argument("--weights", default=None,
                    help="optional path to fine-tuned weights; omit for ImageNet-pretrained")
    ap.add_argument("--crops", nargs="*", default=[],
                    help="player-crop images to embed as the golden reference")
    args = ap.parse_args()

    # numpy/cv2 are only needed for the optional golden reference (--crops); keep them out
    # of the export-only path so a plain `--arch ...` run works with just torch+torchreid+onnx.
    import torch
    import torchreid

    # --- 1. Build the model (feature extractor, no classifier head) and export to ONNX. ---
    model = torchreid.models.build_model(
        name=args.arch, num_classes=1, pretrained=args.weights is None,
    )
    if args.weights:
        torchreid.utils.load_pretrained_weights(model, args.weights)
    model.eval()
    # torchreid returns features (not logits) in eval() mode.

    onnx_dest = HERE / f"{args.arch}.onnx"
    dummy = torch.randn(1, 3, INPUT_H, INPUT_W)
    torch.onnx.export(
        model, dummy, str(onnx_dest),
        input_names=["images"], output_names=["features"],
        # dynamic batch axis so the .NET embedder can batch all player crops of a frame
        # in one inference (batch=1 still works, so parity is unaffected).
        dynamic_axes={"images": {0: "batch"}, "features": {0: "batch"}},
        opset_version=12,
    )
    print(f"Exported ONNX -> {onnx_dest}")

    if not args.crops:
        print("No --crops given; skipped golden reference (export-only run).")
        return 0

    # --- 2. Golden reference: torchreid embeddings for the given crops (L2-normalized). ---
    try:
        import numpy as np
        import cv2
    except ModuleNotFoundError as exc:
        raise SystemExit(
            f"--crops needs numpy + opencv-python for the golden reference ({exc.name} missing). "
            "Install them, or omit --crops to export the ONNX model only."
        )

    golden: dict = {"arch": args.arch, "onnx": onnx_dest.name, "input": [INPUT_H, INPUT_W],
                    "mean": MEAN, "std": STD, "crops": {}}
    for cpath in args.crops:
        img = cv2.imread(cpath)
        if img is None:
            raise SystemExit(f"Cannot read crop: {cpath}")
        inp = torch.from_numpy(preprocess(img, np, cv2))
        with torch.no_grad():
            feat = model(inp).cpu().numpy()[0]
        feat = l2_normalize(feat, np)
        golden["crops"][Path(cpath).name] = [round(float(x), 6) for x in feat.tolist()]
        print(f"crop {cpath}: {feat.shape[0]}-d embedding")

    golden_dest = HERE / f"golden_reid_{args.arch}.json"
    golden_dest.write_text(json.dumps(golden, indent=2))
    print(f"Wrote golden reference -> {golden_dest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
