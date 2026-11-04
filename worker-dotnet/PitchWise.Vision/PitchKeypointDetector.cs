using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// Detects the pitch's fixed keypoints (corners, box lines, circle) from a frame, using a
/// YOLO-pose ONNX model trained on the <see cref="PitchModel"/> template. The output — one
/// (x, y, confidence) per template keypoint, in original-image pixels — is the moving half of
/// the pixel↔pitch correspondences that <see cref="PitchRegistrar"/> turns into a homography.
///
/// It is the third ONNX model in the vision layer and follows the pattern the other two set:
/// same DirectML→CPU session with transparent fallback and shared <see cref="Yolo11OnnxDetector.LogProvider"/>
/// (tagged PITCH), same aspect-preserving letterbox, same inverse mapping back to original pixels.
/// The post-processing differs — a pose model emits keypoints, not boxes — so this is a separate
/// class rather than a mode on the detector.
///
/// <b>Model output layout (ultralytics YOLOv8/11-pose, no NMS baked in):</b>
/// <c>[batch, 4 + 1 + K*3, anchors]</c> — 4 box (cx,cy,w,h) + 1 class score (the single "pitch"
/// class) + K keypoints × (x, y, visibility), all in net-input pixel space. We take the single
/// highest-scoring anchor (there is one pitch) and read its K keypoints.
/// </summary>
public sealed class PitchKeypointDetector : IDisposable
{
    /// <summary>One detected pitch keypoint, index-aligned to <see cref="PitchModel.Keypoints"/>.</summary>
    public readonly record struct Keypoint(double X, double Y, double Confidence);

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _imgsz;

    public PitchKeypointDetector(string modelPath, int imgsz = 640,
        string executionProvider = "dml", int deviceId = 0)
    {
        _session = CreateSession(modelPath, executionProvider, deviceId);
        _inputName = _session.InputMetadata.Keys.First();
        _imgsz = imgsz;
    }

    // Byte-for-byte the detector's session policy, tagged PITCH so the provider log shows whether
    // this third model got the GPU or fell back.
    private static InferenceSession CreateSession(string modelPath, string ep, int deviceId)
    {
        if (string.Equals(ep, "dml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var so = new SessionOptions
                {
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                so.AppendExecutionProvider_DML(deviceId);
                var session = new InferenceSession(modelPath, so);
                Yolo11OnnxDetector.LogProvider($"DirectML GPU (device {deviceId}) — ACTIVE", "PITCH");
                return session;
            }
            catch (Exception ex)
            {
                Yolo11OnnxDetector.LogProvider(
                    $"DirectML init FAILED ({ex.GetType().Name}: {ex.Message}) — falling back to CPU", "PITCH");
            }
        }
        var cpuSession = new InferenceSession(modelPath);
        Yolo11OnnxDetector.LogProvider("CPU execution provider — ACTIVE", "PITCH");
        return cpuSession;
    }

    /// <summary>Detects the pitch keypoints in one BGR frame. Returns one entry per template
    /// keypoint (length <see cref="PitchModel.Count"/>) in original-image pixels; a keypoint the
    /// model could not place carries a low confidence for the registrar to filter out.</summary>
    public IReadOnlyList<Keypoint> Detect(Mat frameBgr)
    {
        LetterboxInfo lb = Letterbox(frameBgr, _imgsz);
        try
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, _imgsz, _imgsz });
            WriteChw(lb.Padded, tensor);

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
            Tensor<float> output = results.First().AsTensor<float>();   // [1, 4+1+K*3, anchors]

            return Decode(output, lb);
        }
        finally
        {
            lb.Padded.Dispose();
        }
    }

    private static IReadOnlyList<Keypoint> Decode(Tensor<float> output, LetterboxInfo lb)
    {
        int channels = output.Dimensions[1];
        int anchors = output.Dimensions[2];

        // channels = 4 (box) + numClasses + K*3. The pitch model has one class, so
        // K = (channels - 5) / 3. Guard against a model whose head does not match.
        int k = (channels - 5) / 3;
        if (k <= 0 || (channels - 5) % 3 != 0)
            throw new InvalidOperationException(
                $"Pitch model head has {channels} channels; expected 4+1+K*3 for a single-class pose model.");

        // One pitch per frame: pick the anchor with the highest class score (channel index 4).
        int best = 0;
        float bestScore = float.NegativeInfinity;
        for (int a = 0; a < anchors; a++)
        {
            float s = output[0, 4, a];
            if (s > bestScore) { bestScore = s; best = a; }
        }

        var kps = new Keypoint[k];
        int kpBase = 5;   // keypoints start after 4 box + 1 class channels
        for (int i = 0; i < k; i++)
        {
            double nx = output[0, kpBase + i * 3 + 0, best];
            double ny = output[0, kpBase + i * 3 + 1, best];
            double vis = output[0, kpBase + i * 3 + 2, best];
            // Net space -> original pixels: undo the centered pad, then the resize ratio. Same
            // inverse mapping the detector applies to box corners.
            double x = (nx - lb.PadLeft) / lb.Ratio;
            double y = (ny - lb.PadTop) / lb.Ratio;
            kps[i] = new Keypoint(x, y, vis);
        }
        return kps;
    }

    private readonly record struct LetterboxInfo(Mat Padded, double Ratio, int PadLeft, int PadTop);

    /// <summary>Aspect-preserving resize into a square imgsz canvas with centered grey-114 pad —
    /// identical to <see cref="Yolo11OnnxDetector"/>'s letterbox, so keypoints and boxes share the
    /// exact same net-space→pixel mapping.</summary>
    private static LetterboxInfo Letterbox(Mat src, int imgsz)
    {
        double ratio = Math.Min(imgsz / (double)src.Width, imgsz / (double)src.Height);
        int resizedW = (int)Math.Round(src.Width * ratio);
        int resizedH = (int)Math.Round(src.Height * ratio);
        double dw = (imgsz - resizedW) / 2.0;
        double dh = (imgsz - resizedH) / 2.0;
        int left = (int)Math.Round(dw - 0.1);
        int top = (int)Math.Round(dh - 0.1);

        var canvas = new Mat(imgsz, imgsz, src.Type(), Scalar.All(114));
        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(resizedW, resizedH), interpolation: InterpolationFlags.Linear);
        using (var roi = new Mat(canvas, new Rect(left, top, resizedW, resizedH)))
            resized.CopyTo(roi);
        return new LetterboxInfo(canvas, ratio, left, top);
    }

    private static void WriteChw(Mat padded, DenseTensor<float> tensor)
    {
        int plane = padded.Height * padded.Width;
        padded.GetArray(out Vec3b[] px);
        Span<float> dst = tensor.Buffer.Span;
        const float inv255 = 1f / 255f;
        for (int i = 0; i < plane; i++)
        {
            Vec3b p = px[i];   // B,G,R
            dst[0 * plane + i] = p.Item2 * inv255;
            dst[1 * plane + i] = p.Item1 * inv255;
            dst[2 * plane + i] = p.Item0 * inv255;
        }
    }

    public void Dispose() => _session.Dispose();
}
