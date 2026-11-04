using PitchWise.Data.Models;

namespace PitchWise.Api.Dtos;

// Request and response shapes. JSON field names are set globally to
// snake_case (JsonNamingPolicy in Program.cs), so PascalCase properties map to
// video_count, timestamp_seconds, etc. — exactly what the existing frontend expects.

public record AnalysisCreate(string Name, string? Subtitle = null, string Sport = "football");

public record VideoOut(
    int Id,
    int AnalysisId,
    string Name,
    double? DurationSeconds,
    double? Fps,
    int Order);

public record AnalysisListItem(
    int Id,
    string Name,
    string? Subtitle,
    string Sport,
    SessionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int VideoCount);

public record AnalysisDetail(
    int Id,
    string Name,
    string? Subtitle,
    string Sport,
    SessionStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<VideoOut> Videos);

public record EventOut(
    int Id,
    int AnalysisId,
    int? VideoId,
    EventType Type,
    EventSource Source,
    double TimestampSeconds,
    double? Confidence,
    string? Label,
    string? Note,
    int? PlayerNumber,
    string? PlayerName,
    int? AssistNumber,
    string? AssistName);

public record EventCreate(
    double TimestampSeconds,
    EventType Type = EventType.Manual,
    string? Label = null,
    string? Note = null,
    int? VideoId = null,
    int? PlayerNumber = null,
    string? PlayerName = null,
    int? AssistNumber = null,
    string? AssistName = null);

public record EventTypeConfigOut(
    string Key,
    string Label,
    string Icon,
    string Color,
    string Bg);

// --- Highlights ---

public record CreateHighlightIn(string Name, List<int> EventIds);

public record HighlightOut(
    int Id,
    int AnalysisId,
    string Name,
    HighlightStatus Status,
    double Progress,
    string? Error,
    string? ShareToken,
    DateTime? ShareExpiresAt,
    DateTime CreatedAt,
    DateTime? FinishedAt);

// Returned when creating/refreshing a share link.
public record ShareOut(string Token, string Url, DateTime ExpiresAt);

// Signed, expiring HLS manifest URL (served by the nginx edge, not the API).
public record HlsUrlOut(string Url, DateTime ExpiresAt);

// Public metadata for the share page (no analysis id leaked).
public record SharePublicOut(string Name, HighlightStatus Status, DateTime ExpiresAt);

// --- Live streams ---

public record CreateStreamIn(string Name);

public record StreamOut(
    Guid Id,
    string Name,
    LiveStreamStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? EndedAt);

// Only returned at creation: the stream key is stored hashed and cannot be shown again.
public record CreatedStreamOut(StreamOut Stream, string StreamKey, string IngestUrl);

// --- Admin ---

public record CreateTenantIn(string Name);

// The API key is only shown here; it is stored hashed.
public record CreatedTenantOut(int Id, string Name, string ApiKey);

// --- MediaMTX auth callback ---

public record MediaMtxAuthIn(string? Action, string? Path, string? Query);
