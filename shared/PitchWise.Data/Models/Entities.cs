namespace PitchWise.Data.Models;

// EF Core entities. Table and column names (snake_case) are set in AppDbContext.

public class AnalysisSession
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string Sport { get; set; } = "football";
    public SessionStatus Status { get; set; } = SessionStatus.Draft;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Video
{
    public int Id { get; set; }
    public int AnalysisId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public double? Fps { get; set; }
    public int Order { get; set; }
    // Annotated (boxes burned-in) MP4, stitched from all segment renders. Null until
    // every segment of this video has finished rendering. Served for playback with
    // full rewind (HTTP Range); the raw upload is the fallback.
    public string? AnnotatedFilename { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class VisionJob
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public VisionJobStatus Status { get; set; } = VisionJobStatus.Pending;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

// Whole-match aggregate statistics for one analysed video, produced by the worker's
// MatchStatsTracker. One row per video (upsert on re-analysis). Team-level counts are plain
// columns; the per-player time-on-pitch list is variable-length and stored as JSON.
public class MatchStats
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public int AnalysisId { get; set; }

    // Possession split (of controlled time; A + B ≈ 100 when the engine attributed any).
    public double PossessionPctA { get; set; }
    public double PossessionPctB { get; set; }
    public double ControlledSeconds { get; set; }
    public double LooseSeconds { get; set; }

    // Passing, per team.
    public int PassesA { get; set; }
    public int PassesB { get; set; }
    public int TurnoversA { get; set; }
    public int TurnoversB { get; set; }
    public double PassAccuracyPctA { get; set; }
    public double PassAccuracyPctB { get; set; }

    // JSON array of { player_id, seconds_on_pitch, frames_seen }, descending by time.
    public string TimeOnPitchJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Event
{
    public int Id { get; set; }
    public int AnalysisId { get; set; }
    public int? VideoId { get; set; }
    public EventType Type { get; set; }
    public EventSource Source { get; set; }
    public double TimestampSeconds { get; set; }
    public double? Confidence { get; set; }
    public string? Label { get; set; }
    public string? Note { get; set; }
    public int? PlayerNumber { get; set; }
    public string? PlayerName { get; set; }
    public int? AssistNumber { get; set; }
    public string? AssistName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Clip
{
    public int Id { get; set; }
    public int EventId { get; set; }
    public int VideoId { get; set; }
    public string Filename { get; set; } = string.Empty;
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LiveSession
{
    public string Id { get; set; } = string.Empty;  // UUID4
    public string SourceUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "idle";    // idle | running | stopped
    public string WsUrl { get; set; } = string.Empty;
    public string HlsUrl { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StoppedAt { get; set; }
}

// A highlight reel stitched from clips around a coach-selected set of events.
// Rendered in the background by the worker (ffmpeg concat), then streamed via
// HTTP Range. ShareToken/ShareExpiresAt back the public, time-limited share link.
public class Highlight
{
    public int Id { get; set; }
    public int AnalysisId { get; set; }
    public string Name { get; set; } = string.Empty;
    // CSV of selected event ids (e.g. "12,15,18"). Stored as text for simplicity.
    public string EventIds { get; set; } = string.Empty;
    public HighlightStatus Status { get; set; } = HighlightStatus.Pending;
    public double Progress { get; set; }
    public string? Filename { get; set; }
    public bool HlsReady { get; set; }
    public string? Error { get; set; }
    public string? ShareToken { get; set; }
    public DateTime? ShareExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}
