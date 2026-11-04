using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace PitchWise.Vision;

/// <summary>
/// Person Re-ID appearance embedder running a torchreid-exported OSNet ONNX model.
/// Given a player crop it produces an L2-normalized feature vector; cosine similarity
/// between two vectors reduces to a dot product. Used by <see cref="PlayerReId"/> to
/// merge switched ByteTrack ids back onto one stable player identity.
///
/// Mirrors <see cref="Yolo11OnnxDetector"/>: same ONNX Runtime DirectML session with a
/// transparent CPU fallback, same batched-tensor / span-based CHW hot path. Difference
/// from YOLO preprocessing: crops are resized to a fixed 256x128 (person-Re-ID standard)
/// and normalized with ImageNet mean/std (torchreid's default transform), not /255 only.
/// </summary>
public sealed class OsNetOnnxEmbedder : IDisposable
{
    // torchreid person-Re-ID input geometry + ImageNet normalization (its default eval transform).
    private const int InputH = 256;
    private const int InputW = 128;
    // Per-channel (R,G,B) ImageNet mean/std, matching torchreid Normalize(...).
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    /// <param name="modelPath">Path to the exported OSNet .onnx model (see export_reid_onnx.py).</param>
    /// <param name="executionProvider">"dml" (DirectML GPU, default) or "cpu". On any DML
    /// failure we transparently fall back to CPU, matching the detector.</param>
    /// <param name="deviceId">GPU adapter index for DirectML (default 0).</param>
    public OsNetOnnxEmbedder(string modelPath, string executionProvider = "dml", int deviceId = 0)
    {
        _session = CreateSession(modelPath, executionProvider, deviceId);
        _inputName = _session.InputMetadata.Keys.First();
    }

    // Same DML→CPU fallback policy as Yolo11OnnxDetector so the worker never crashes on a
    // GPU/DLL problem. Logs which provider actually ran (tagged [REID]) via the detector's
    // shared LogProvider, so you can tell whether Re-ID got the GPU or quietly fell back.
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
                Yolo11OnnxDetector.LogProvider($"DirectML GPU (device {deviceId}) — ACTIVE", "REID");
                return session;
            }
            catch (Exception ex)
            {
                Yolo11OnnxDetector.LogProvider(
                    $"DirectML init FAILED ({ex.GetType().Name}: {ex.Message}) — falling back to CPU", "REID");
            }
        }
        var cpuSession = new InferenceSession(modelPath);
        Yolo11OnnxDetector.LogProvider("CPU execution provider — ACTIVE", "REID");
        return cpuSession;
    }

    /// <summary>Embeds one player crop. Thin wrapper over <see cref="EmbedBatch"/> so single
    /// and batched paths share ONE code path.</summary>
    public float[] Embed(Mat frameBgr, Detection box) => EmbedBatch(frameBgr, new[] { box })[0];

    /// <summary>
    /// Embeds N crops (taken from the SAME frame) in one ONNX inference. Requires a model
    /// exported with a dynamic batch axis for N>1. Returns L2-normalized vectors in input
    /// order. Boxes are clamped to the frame; a degenerate (zero-area) box yields a zero
    /// vector rather than throwing.
    /// </summary>
    public IReadOnlyList<float[]> EmbedBatch(Mat frameBgr, IReadOnlyList<Detection> boxes)
    {
        int n = boxes.Count;
        if (n == 0) return Array.Empty<float[]>();

        var crops = new Mat?[n];
        try
        {
            var tensor = new DenseTensor<float>(new[] { n, 3, InputH, InputW });
            for (int i = 0; i < n; i++)
            {
                Rect roi = ClampRoi(boxes[i], frameBgr.Width, frameBgr.Height);
                if (roi.Width <= 0 || roi.Height <= 0)
                    continue;   // leave this slot as zeros; PlayerReId treats it as no-embedding
                using var sub = new Mat(frameBgr, roi);
                var resized = new Mat();
                Cv2.Resize(sub, resized, new Size(InputW, InputH), interpolation: InterpolationFlags.Linear);
                crops[i] = resized;
                WriteChw(resized, tensor, i);
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor),
            };
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
            Tensor<float> output = results.First().AsTensor<float>();   // [N, featDim]
            int featDim = output.Dimensions[1];

            var vectors = new List<float[]>(n);
            for (int i = 0; i < n; i++)
            {
                var v = new float[featDim];
                for (int c = 0; c < featDim; c++) v[c] = output[i, c];
                L2Normalize(v);
                vectors.Add(v);
            }
            return vectors;
        }
        finally
        {
            for (int i = 0; i < n; i++) crops[i]?.Dispose();
        }
    }

    /// <summary>Clamps a detection box to the frame bounds and rounds to an integer Rect.</summary>
    private static Rect ClampRoi(Detection d, int frameW, int frameH)
    {
        int x1 = (int)Math.Floor(Math.Clamp(d.X1, 0, frameW - 1));
        int y1 = (int)Math.Floor(Math.Clamp(d.Y1, 0, frameH - 1));
        int x2 = (int)Math.Ceiling(Math.Clamp(d.X2, 0, frameW));
        int y2 = (int)Math.Ceiling(Math.Clamp(d.Y2, 0, frameH));
        return new Rect(x1, y1, Math.Max(0, x2 - x1), Math.Max(0, y2 - y1));
    }

    /// <summary>
    /// Writes one resized BGR crop into slot <paramref name="n"/> of a batched CHW tensor
    /// [N,3,H,W]: BGR→RGB, /255, then ImageNet (mean/std) normalize. Span-based single pass
    /// with precomputed plane offsets — same hot-path shape as the detector's WriteChw.
    /// </summary>
    private static void WriteChw(Mat crop, DenseTensor<float> tensor, int n)
    {
        int h = crop.Height, w = crop.Width;
        int plane = h * w;

        crop.GetArray(out Vec3b[] px);

        Span<float> dst = tensor.Buffer.Span;
        int rBase = (n * 3 + 0) * plane;   // R plane
        int gBase = (n * 3 + 1) * plane;   // G plane
        int bBase = (n * 3 + 2) * plane;   // B plane
        const float inv255 = 1f / 255f;

        for (int i = 0; i < plane; i++)
        {
            Vec3b p = px[i];               // Item0=B, Item1=G, Item2=R
            dst[rBase + i] = (p.Item2 * inv255 - Mean[0]) / Std[0];
            dst[gBase + i] = (p.Item1 * inv255 - Mean[1]) / Std[1];
            dst[bBase + i] = (p.Item0 * inv255 - Mean[2]) / Std[2];
        }
    }

    /// <summary>In-place L2 normalization so cosine similarity == dot product. A zero
    /// vector (degenerate crop) is left as-is.</summary>
    private static void L2Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        double norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return;
        float inv = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }

    public void Dispose() => _session.Dispose();
}
