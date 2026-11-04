namespace PitchWise.Live;

/// <summary>Live-server settings. Includes the vision settings.</summary>
public sealed class LiveSettings
{
    /// <summary>Directory where live sessions write HLS segments (served by this server).</summary>
    public string HlsBaseDir { get; set; } =
        Environment.GetEnvironmentVariable("LIVE_HLS_DIR")
        ?? Path.Combine(Path.GetTempPath(), "live_hls");

    /// <summary>Exported YOLO11 .onnx + its .names.json sidecar (shared detector).</summary>
    public string YoloModelPath { get; set; } = "models/football.onnx";
    public string? YoloNamesPath { get; set; }
    public int LiveImgsz { get; set; } = 640;

    /// <summary>ONNX execution provider: "dml" (DirectML GPU, default) or "cpu".
    /// Falls back to CPU automatically if DirectML can't initialise.</summary>
    public string OnnxExecutionProvider { get; set; } = "dml";
    public int OnnxDeviceId { get; set; }

    /// <summary>"passthrough" (raw frames) | "detect" (YOLO + overlay). Default detect.</summary>
    public string LivePipelineMode { get; set; } = "detect";

    /// <summary>Cap encoded width; source wider than this is downscaled.</summary>
    public int MaxWidth { get; set; } = 1280;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string YtDlpPath { get; set; } = "yt-dlp";

    // LLM (OpenAI-compatible chat) for tactical tips. Empty key → tips disabled.
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "gpt-4o-mini";

    public string WebOrigin { get; set; } = "http://localhost:3000";
    public string WebOriginAlt { get; set; } = "http://localhost:3001";

    public static LiveSettings FromEnvironment(IConfiguration config)
    {
        var s = new LiveSettings();
        config.GetSection("Live").Bind(s);
        string? Env(string k) => Environment.GetEnvironmentVariable(k);
        s.YoloModelPath = Env("YOLO_MODEL_PATH") ?? s.YoloModelPath;
        s.YoloNamesPath = Env("YOLO_NAMES_PATH") ?? s.YoloNamesPath;
        if (int.TryParse(Env("LIVE_IMGSZ"), out int isz)) s.LiveImgsz = isz;
        s.OnnxExecutionProvider = Env("ONNX_EP") ?? s.OnnxExecutionProvider;
        if (int.TryParse(Env("ONNX_DEVICE_ID"), out int did)) s.OnnxDeviceId = did;
        s.LivePipelineMode = Env("LIVE_PIPELINE_MODE") ?? s.LivePipelineMode;
        if (int.TryParse(Env("MAX_WIDTH"), out int mw)) s.MaxWidth = mw;
        s.FfmpegPath = Env("FFMPEG_PATH") ?? s.FfmpegPath;
        s.YtDlpPath = Env("YT_DLP_PATH") ?? s.YtDlpPath;
        s.LlmBaseUrl = Env("LLM_BASE_URL") ?? s.LlmBaseUrl;
        s.LlmApiKey = Env("LLM_API_KEY") ?? s.LlmApiKey;
        s.LlmModel = Env("LLM_MODEL") ?? s.LlmModel;
        s.WebOrigin = Env("WEB_ORIGIN") ?? s.WebOrigin;
        s.WebOriginAlt = Env("WEB_ORIGIN_ALT") ?? s.WebOrigin;
        return s;
    }
}
