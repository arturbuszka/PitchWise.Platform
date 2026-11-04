namespace PitchWise.Data.Models;

// EF Core entities. Table and column names (snake_case) are set in AppDbContext.

public class AnalysisSession
{
    public int Id { get; set; }
    public int TenantId { get; set; }
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

// A customer of the platform. Everything a customer creates belongs to one tenant.
public class Tenant
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Only the hash is stored, so a database leak does not expose working keys.
public class ApiKey
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string KeyHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// One live channel: an encoder publishes to it over RTMP and viewers watch it over HLS.
public class LiveStream
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StreamKeyHash { get; set; } = string.Empty;
    public LiveStreamStatus Status { get; set; } = LiveStreamStatus.Idle;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
}
