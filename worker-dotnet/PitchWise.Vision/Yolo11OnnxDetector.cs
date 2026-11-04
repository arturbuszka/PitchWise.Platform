using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace PitchWise.Vision;

/// <summary>
/// YOLO11 object detector running an ultralytics-exported ONNX model. Reproduces
/// ultralytics' pre/post-processing so detections match ultralytics itself; PitchWise.Vision.ParityTest checks this.
///
/// Key differences from the YOLOX detector in ByteTrack.NET.Sample:
///   - preprocessing is <c>/255</c> only (NO ImageNet mean/std),
///   - output is <c>[1, 4+numClasses, numAnchors]</c> — TRANSPOSED, NO objectness,
///     boxes already cx,cy,w,h in net-input pixel space (no grid/stride/exp decode),
///   - letterbox pads to a SQUARE (imgsz x imgsz) with grey 114.
/// </summary>
public sealed class Yolo11OnnxDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _imgsz;
    private readonly float _scoreThresh;
    private readonly float _nmsThresh;
    private readonly IReadOnlyDictionary<int, string> _classNames;
    private readonly Func<string, string?> _mapClass;

    /// <param name="modelPath">Path to the exported YOLO11 .onnx model.</param>
    /// <param name="classNames">model class-id → raw class name (from golden/names).</param>
    /// <param name="mapClass">Maps a raw class name to an <see cref="ObjectClass"/> or null to drop.</param>
    /// <param name="imgsz">Square model input size (default 640, must match export).</param>
    /// <param name="scoreThresh">Min class score to keep (ultralytics conf default 0.25).</param>
    /// <param name="nmsThresh">IoU threshold for NMS (ultralytics iou default 0.7).</param>
    /// <param name="executionProvider">"dml" (DirectML GPU, default) or "cpu". On any DML
    /// failure we transparently fall back to CPU so the worker never crashes.</param>
    /// <param name="deviceId">GPU adapter index for DirectML (default 0).</param>
    public Yolo11OnnxDetector(
        string modelPath,
        IReadOnlyDictionary<int, string> classNames,
        Func<string, string?> mapClass,
        int imgsz = 640,
        float scoreThresh = 0.25f,
        float nmsThresh = 0.7f,
        string executionProvider = "dml",
        int deviceId = 0)
    {
        _session = CreateSession(modelPath, executionProvider, deviceId);
        _inputName = _session.InputMetadata.Keys.First();
        _classNames = classNames;
        _mapClass = mapClass;
        _imgsz = imgsz;
        _scoreThresh = scoreThresh;
        _nmsThresh = nmsThresh;
    }

    // Builds the inference session on the requested provider. DirectML runs on any DX12
    // GPU without CUDA; on ANY failure (no GPU, missing DLL, unsupported op) we fall back
    // to a plain CPU session so the worker keeps running. Every outcome is logged loudly
    // (console banner + a onnx_provider.log file) so it's never a mystery which one ran.
    private static InferenceSession CreateSession(string modelPath, string ep, int deviceId)
    {
        if (string.Equals(ep, "dml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var so = new SessionOptions
                {
                    // Both are required by the DirectML execution provider.
                    EnableMemoryPattern = false,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                };
                so.AppendExecutionProvider_DML(deviceId);
                var session = new InferenceSession(modelPath, so);
                LogProvider($"DirectML GPU (device {deviceId}) — ACTIVE", "YOLO");
                return session;
            }
            catch (Exception ex)
            {
                LogProvider($"DirectML init FAILED ({ex.GetType().Name}: {ex.Message}) — falling back to CPU", "YOLO");
            }
        }
        var cpuSession = new InferenceSession(modelPath);
        LogProvider("CPU execution provider — ACTIVE", "YOLO");
        return cpuSession;
    }

    // Loud, unmissable log of which ONNX provider actually ran: a console banner AND a
    // onnx_provider.log file in the working dir (survives even when the worker runs as a
    // background job whose stdout isn't visible). Shared with OsNetOnnxEmbedder so the Re-ID
    // model's provider is just as visible; <paramref name="model"/> tags which one it is.
    internal static void LogProvider(string message, string model = "YOLO")
    {
        // Providers compiled into this ORT build (proves the DirectML DLL is present).
        string available;
        try { available = string.Join(", ", OrtEnv.Instance().GetAvailableProviders()); }
        catch { available = "n/a"; }
        string line = $"[ONNX] {DateTime.Now:HH:mm:ss} [{model}] {message} | available=[{available}]";
        string banner = new string('=', 60);
        Console.WriteLine($"\n{banner}\n{line}\n{banner}\n");
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "onnx_provider.log"),
                line + Environment.NewLine);
        }
        catch { /* logging must never break inference */ }
    }

    /// <summary>Runs detection on one BGR frame. Boxes are in original-image coords.
    /// Thin wrapper over <see cref="DetectBatch"/> so single-frame (live) and batched
    /// (analysis) paths share ONE code path — automatic parity, one thing to maintain.</summary>
    public IReadOnlyList<Detection> Detect(Mat frameBgr) => DetectBatch(new[] { frameBgr })[0];

    /// <summary>
    /// Runs detection on N frames in a single ONNX inference. Requires a model exported
    /// with a dynamic batch axis for N>1 (a fixed-batch model only accepts N==1). Returns
    /// per-frame detections in input order. Boxes are in each frame's original-image coords.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Detection>> DetectBatch(IReadOnlyList<Mat> framesBgr)
    {
        int n = framesBgr.Count;
        if (n == 0) return Array.Empty<IReadOnlyList<Detection>>();

        var lbs = new LetterboxInfo[n];
        try
        {
            // Letterbox every frame and pack into one [N,3,imgsz,imgsz] tensor.
            var tensor = new DenseTensor<float>(new[] { n, 3, _imgsz, _imgsz });
            for (int i = 0; i < n; i++)
            {
                lbs[i] = Letterbox(framesBgr[i], _imgsz);
                WriteChw(lbs[i].Padded, tensor, i);
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor),
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
            Tensor<float> output = results.First().AsTensor<float>();  // [N, 4+numClasses, numAnchors]

            var perFrame = new List<IReadOnlyList<Detection>>(n);
            for (int i = 0; i < n; i++)
                perFrame.Add(PostprocessOne(output, i, lbs[i]));
            return perFrame;
        }
        finally
        {
            for (int i = 0; i < n; i++)
                lbs[i].Padded?.Dispose();
        }
    }

    private readonly record struct LetterboxInfo(Mat Padded, double Ratio, int PadLeft, int PadTop);

    /// <summary>
    /// Aspect-preserving resize into a square imgsz canvas with CENTERED grey-114
    /// padding — matches ultralytics <c>LetterBox(center=True, auto=False)</c>. To map
    /// a net-space coord back: subtract the pad offset, then divide by ratio.
    /// </summary>
    private static LetterboxInfo Letterbox(Mat src, int imgsz)
    {
        double ratio = Math.Min(imgsz / (double)src.Width, imgsz / (double)src.Height);
        int resizedW = (int)Math.Round(src.Width * ratio);
        int resizedH = (int)Math.Round(src.Height * ratio);

        // Centered padding, same rounding as ultralytics get_params.
        double dw = (imgsz - resizedW) / 2.0;
        double dh = (imgsz - resizedH) / 2.0;
        int left = (int)Math.Round(dw - 0.1);
        int top = (int)Math.Round(dh - 0.1);

        var canvas = new Mat(imgsz, imgsz, src.Type(), Scalar.All(114));
        using var resized = new Mat();
        Cv2.Resize(src, resized, new Size(resizedW, resizedH), interpolation: InterpolationFlags.Linear);
        using (var roi = new Mat(canvas, new Rect(left, top, resizedW, resizedH)))
        {
            resized.CopyTo(roi);
        }
        return new LetterboxInfo(canvas, ratio, left, top);
    }

    /// <summary>
    /// Writes one letterboxed BGR frame into slot <paramref name="n"/> of a batched CHW
    /// tensor [N,3,H,W]: BGR→RGB, /255. Writes straight into the tensor's contiguous
    /// backing buffer via <see cref="Span{T}"/> with precomputed plane offsets — one linear
    /// pass, no multi-dimensional indexer, no per-element bounds-check. This is the hot path
    /// (called once per processed frame); the old per-pixel indexer version starved the GPU.
    /// </summary>
    private static void WriteChw(Mat padded, DenseTensor<float> tensor, int n)
    {
        int h = padded.Height, w = padded.Width;
        int plane = h * w;

        // Pull pixels once (single managed copy) instead of indexing the Mat per element.
        padded.GetArray(out Vec3b[] px);

        Span<float> dst = tensor.Buffer.Span;
        int rBase = (n * 3 + 0) * plane;   // R plane
        int gBase = (n * 3 + 1) * plane;   // G plane
        int bBase = (n * 3 + 2) * plane;   // B plane
        const float inv255 = 1f / 255f;

        for (int i = 0; i < plane; i++)
        {
            Vec3b p = px[i];               // Item0=B, Item1=G, Item2=R
            dst[rBase + i] = p.Item2 * inv255;
            dst[gBase + i] = p.Item1 * inv255;
            dst[bBase + i] = p.Item0 * inv255;
        }
    }

    /// <summary>
    /// Decodes YOLO11 output <c>[1, 4+numClasses, numAnchors]</c> (transposed, no
    /// objectness) into original-image boxes, applies score threshold + per-class NMS.
    /// </summary>
    private IReadOnlyList<Detection> PostprocessOne(Tensor<float> output, int n, LetterboxInfo lb)
    {
        double ratio = lb.Ratio;
        int channels = output.Dimensions[1];       // 4 + numClasses
        int numAnchors = output.Dimensions[2];
        int numClasses = channels - 4;

        var candidates = new List<(double[] tlbr, float score, int cls)>();

        for (int i = 0; i < numAnchors; i++)
        {
            // Best class over this anchor (no objectness in YOLO11).
            int bestCls = 0;
            float bestScore = 0f;
            for (int c = 0; c < numClasses; c++)
            {
                float s = output[n, 4 + c, i];
                if (s > bestScore)
                {
                    bestScore = s;
                    bestCls = c;
                }
            }

            if (bestScore < _scoreThresh) continue;

            // Boxes are cx,cy,w,h in net-input pixel space — no grid/stride/exp decode.
            double cx = output[n, 0, i];
            double cy = output[n, 1, i];
            double bw = output[n, 2, i];
            double bh = output[n, 3, i];

            // center→corner in net space, subtract the centered pad, then divide by
            // ratio to map back to the original image.
            double x1 = (cx - bw / 2.0 - lb.PadLeft) / ratio;
            double y1 = (cy - bh / 2.0 - lb.PadTop) / ratio;
            double x2 = (cx + bw / 2.0 - lb.PadLeft) / ratio;
            double y2 = (cy + bh / 2.0 - lb.PadTop) / ratio;

            candidates.Add((new[] { x1, y1, x2, y2 }, bestScore, bestCls));
        }

        return NonMaxSuppression(candidates);
    }

    /// <summary>Greedy per-class NMS; maps surviving classes to our categories.</summary>
    private IReadOnlyList<Detection> NonMaxSuppression(
        List<(double[] tlbr, float score, int cls)> candidates)
    {
        var kept = new List<Detection>();

        foreach (int cls in candidates.Select(c => c.cls).Distinct())
        {
            var byClass = candidates
                .Where(c => c.cls == cls)
                .OrderByDescending(c => c.score)
                .ToList();

            var suppressed = new bool[byClass.Count];
            for (int i = 0; i < byClass.Count; i++)
            {
                if (suppressed[i]) continue;

                (double[] boxI, float scoreI, int clsI) = byClass[i];

                string rawName = _classNames.TryGetValue(clsI, out string? n) ? n : "";
                string? mapped = _mapClass(rawName);
                if (mapped is not null)
                {
                    kept.Add(new Detection(
                        mapped, boxI[0], boxI[1], boxI[2], boxI[3], scoreI));
                }

                for (int j = i + 1; j < byClass.Count; j++)
                {
                    if (suppressed[j]) continue;
                    if (Iou(boxI, byClass[j].tlbr) > _nmsThresh) suppressed[j] = true;
                }
            }
        }

        return kept;
    }

    private static double Iou(double[] a, double[] b)
    {
        double ix1 = Math.Max(a[0], b[0]);
        double iy1 = Math.Max(a[1], b[1]);
        double ix2 = Math.Min(a[2], b[2]);
        double iy2 = Math.Min(a[3], b[3]);
        double iw = Math.Max(0.0, ix2 - ix1);
        double ih = Math.Max(0.0, iy2 - iy1);
        double inter = iw * ih;
        double areaA = Math.Max(0.0, a[2] - a[0]) * Math.Max(0.0, a[3] - a[1]);
        double areaB = Math.Max(0.0, b[2] - b[0]) * Math.Max(0.0, b[3] - b[1]);
        double union = areaA + areaB - inter;
        return union <= 0.0 ? 0.0 : inter / union;
    }

    public void Dispose() => _session.Dispose();
}
