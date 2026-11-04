namespace PitchWise.Worker;

/// <summary>
/// Vision-specific worker settings (the API's AppSettings covers DB/Redis/dirs).
/// Vision settings.
/// </summary>
public sealed class WorkerSettings
{
    /// <summary>Path to the exported YOLO11 .onnx model (football or yolo11n fallback).</summary>
    public string YoloModelPath { get; set; } = "models/football.onnx";

    /// <summary>Sidecar JSON with {classId: name}. Defaults to "&lt;model&gt;.names.json".
    /// Produced by export_and_golden.py (the golden's "names") or written by hand.</summary>
    public string? YoloNamesPath { get; set; }

    /// <summary>Path to the exported OSNet person-Re-ID .onnx model (see export_reid_onnx.py).
    /// When set, players get a stable <see cref="Vision.Detection.PlayerId"/> across track-id
    /// switches and a time-on-pitch report is produced. Null/empty disables Re-ID.</summary>
    public string? ReidModelPath { get; set; }

    /// <summary>Path to the exported pitch-keypoint YOLO-pose .onnx model (see
    /// export_pitch_onnx.py). When set, each frame is registered to pitch metres and the engine
    /// emits possession/pass events; null/empty leaves the engine in normalized coordinates
    /// (no distance-derived events). Follows the same optional-model pattern as Re-ID.</summary>
    public string? PitchModelPath { get; set; }

    /// <summary>Model input size — must match the export (default 640).</summary>
    public int Imgsz { get; set; } = 640;

    /// <summary>ONNX execution provider: "dml" (DirectML GPU, default) or "cpu".
    /// Falls back to CPU automatically if DirectML can't initialise.</summary>
    public string OnnxExecutionProvider { get; set; } = "dml";
    public int OnnxDeviceId { get; set; }

    /// <summary>Process every Nth frame.</summary>
    public int FrameStride { get; set; } = 3;

    /// <summary>Burn detection boxes into a persistent annotated MP4 per segment and
    /// stitch them when the video finishes. Off => behaves like plain analysis.</summary>
    public bool RenderAnnotated { get; set; } = true;

    /// <summary>Per-event clip extraction. Off by default.</summary>
    public bool GenerateClips { get; set; }
    public double ClipPreSeconds { get; set; } = 6.0;
    public double ClipPostSeconds { get; set; } = 4.0;

    /// <summary>ffmpeg / ffprobe binaries (PATH lookup by default).</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>BRPOP block timeout (server-side), seconds.</summary>
    public int BrpopTimeoutSeconds { get; set; } = 5;
}
