namespace PitchWise.Broadcast;

/// <summary>Settings for the broadcast service, from the environment.</summary>
public sealed class BroadcastSettings
{
    public string DatabaseConnection { get; set; } = "Host=localhost;Port=5432;Database=pitchwise;Username=pitchwise;Password=pitchwise";
    public string MediaMtxApiUrl { get; set; } = "http://localhost:9997";
    /// <summary>MediaMTX's RTSP listener, which is only reachable from inside the deployment.</summary>
    public string MediaMtxRtspUrl { get; set; } = "rtsp://localhost:8554";
    public string MediaMtxReadSecret { get; set; } = "";
    public string OutputEncryptionKey { get; set; } = "";
    /// <summary>Allows destinations on private addresses, which is only for testing against a local server.</summary>
    public bool AllowPrivateTargets { get; set; }
    public string FfmpegPath { get; set; } = "ffmpeg";

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// How long ffmpeg may send nothing before it is killed: a hung destination never makes it exit. This
    /// must exceed MediaMTX's 10 s read timeout, so that a frozen encoder ends as a lost signal instead.
    /// </summary>
    public TimeSpan StallLimit { get; set; } = TimeSpan.FromSeconds(15);
    /// <summary>How long an output must run for its earlier failures to be forgotten.</summary>
    public TimeSpan SteadyAfter { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>The wait before each retry. An output gives up when this runs out.</summary>
    public TimeSpan[] RetryDelays { get; set; } =
        new[] { 2, 4, 8, 16, 32, 60, 60, 60, 60, 60 }.Select(s => TimeSpan.FromSeconds(s)).ToArray();

    public static BroadcastSettings FromEnvironment()
    {
        var settings = new BroadcastSettings();
        settings.DatabaseConnection = Env("DATABASE_CONNECTION") ?? settings.DatabaseConnection;
        settings.MediaMtxApiUrl = Env("MEDIAMTX_API_URL") ?? settings.MediaMtxApiUrl;
        settings.MediaMtxRtspUrl = Env("MEDIAMTX_RTSP_URL") ?? settings.MediaMtxRtspUrl;
        settings.MediaMtxReadSecret = Env("MEDIAMTX_READ_SECRET") ?? settings.MediaMtxReadSecret;
        settings.OutputEncryptionKey = Env("OUTPUT_ENCRYPTION_KEY") ?? settings.OutputEncryptionKey;
        settings.AllowPrivateTargets = Env("OUTPUTS_ALLOW_PRIVATE_TARGETS") == "true";
        settings.FfmpegPath = Env("FFMPEG_PATH") ?? settings.FfmpegPath;
        return settings;
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);
}
