namespace PitchWise.Worker;

/// <summary>Worker settings, from appsettings "Worker" and the environment.</summary>
public sealed class WorkerSettings
{
    /// <summary>Seconds of footage before and after an event in a highlight clip.</summary>
    public double ClipPreSeconds { get; set; } = 6.0;
    public double ClipPostSeconds { get; set; } = 4.0;

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>How long a recording's files must go unchanged before they count as finished.</summary>
    public double RecordingSettleSeconds { get; set; } = 3.0;
    /// <summary>How long to wait for that. Longer means an encoder is somehow still being recorded.</summary>
    public double RecordingSettleLimitSeconds { get; set; } = 60.0;

    /// <summary>Pause after a Redis error before polling again.</summary>
    public int BrpopTimeoutSeconds { get; set; } = 5;
}
