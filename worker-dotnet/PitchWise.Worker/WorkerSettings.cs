namespace PitchWise.Worker;

/// <summary>Worker settings, from appsettings "Worker" and the environment.</summary>
public sealed class WorkerSettings
{
    /// <summary>Seconds of footage before and after an event in a highlight clip.</summary>
    public double ClipPreSeconds { get; set; } = 6.0;
    public double ClipPostSeconds { get; set; } = 4.0;

    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Pause after a Redis error before polling again.</summary>
    public int BrpopTimeoutSeconds { get; set; } = 5;
}
