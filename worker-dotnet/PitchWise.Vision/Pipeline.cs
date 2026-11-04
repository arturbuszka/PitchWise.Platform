namespace PitchWise.Vision;

/// <summary>Result of a full recording analysis.</summary>
public sealed class PipelineResult
{
    public double? DurationSeconds { get; init; }
    public double? Fps { get; init; }
    public IReadOnlyList<DetectedEvent> Events { get; init; } = Array.Empty<DetectedEvent>();
    public int FramesProcessed { get; init; }
}

/// <summary>
/// Vision orchestration: detection → tracking → events.
/// analyze_video. Pure domain function — no DB/HTTP dependencies.
/// </summary>
public static class Pipeline
{
    /// <param name="videoPath">Path to the recording.</param>
    /// <param name="modelPath">Exported YOLO11 .onnx.</param>
    /// <param name="classNames">model class-id → raw name.</param>
    /// <param name="frameStride">Process every Nth frame.</param>
    /// <param name="imgsz">Model input size (must match the export).</param>
    /// <param name="eventConfig">Event heuristics tuning (defaults if null).</param>
    /// <param name="onProgress">Progress callback (0..1) for UI/queue.</param>
    /// <param name="startSeconds">Analyse only from this timestamp (segment start). 0 = beginning.</param>
    /// <param name="endSeconds">Analyse only up to this timestamp (segment end). null = end of file.</param>
    /// <remarks>Events keep ABSOLUTE timestamps relative to the whole video, so a segmented
    /// run and a whole-file run place events at the same match time.</remarks>
    public static PipelineResult AnalyzeVideo(
        string videoPath,
        string modelPath,
        IReadOnlyDictionary<int, string> classNames,
        int frameStride = 5,
        int imgsz = 640,
        EventConfig? eventConfig = null,
        Action<double>? onProgress = null,
        double startSeconds = 0.0,
        double? endSeconds = null)
    {
        (double? duration, double? fps) = FfmpegTools.ProbeVideo(videoPath);

        int frameRate = (int)Math.Round(fps is > 0 ? fps.Value : 25.0);
        using var detector = new Detector(
            modelPath, classNames, frameRate: frameRate,
            frameStride: frameStride, imgsz: imgsz);

        // Progress is reported 0..1 over the requested [start, end] window (or whole file).
        double segStart = Math.Max(0.0, startSeconds);
        double segEnd = endSeconds ?? duration ?? 0.0;
        double segSpan = segEnd > segStart ? segEnd - segStart : 0.0;

        var frames = new List<FrameResult>();
        foreach (FrameResult fr in detector.Run(videoPath, startSeconds, endSeconds))
        {
            frames.Add(fr);
            if (onProgress is not null && segSpan > 0 && fr.TimestampSeconds > segStart)
                onProgress(Math.Min(0.95, (fr.TimestampSeconds - segStart) / segSpan));
        }

        List<DetectedEvent> events = Events.Detect(frames, eventConfig);

        onProgress?.Invoke(1.0);

        return new PipelineResult
        {
            DurationSeconds = duration,
            Fps = fps,
            Events = events,
            FramesProcessed = frames.Count,
        };
    }
}
