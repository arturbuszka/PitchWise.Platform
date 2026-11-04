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

// One video → one analysis job. AnnotatedReady flags that the boxes-burned-in playback
// file has been rendered (until then the player shows a placeholder).
public record VisionJobOut(
    int Id,
    int VideoId,
    VisionJobStatus Status,
    double Progress,
    string? Error,
    DateTime CreatedAt,
    DateTime? FinishedAt,
    bool AnnotatedReady = false);

public record ClipOut(
    int Id,
    int EventId,
    int VideoId,
    string Filename,
    double StartSeconds,
    double EndSeconds);

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
    string? AssistName,
    ClipOut? Clip = null);

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

public record ChatMessage(string Role, string Content);

public record ChatRequest(List<ChatMessage> Messages, int? AnalysisId = null);

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

// --- Match statistics ---

public record TeamStatsOut(
    double PossessionPct,
    int Passes,
    int Turnovers,
    double PassAccuracyPct);

// Whole-match aggregates for one video. All zero when the engine ran without pitch coordinates;
// the frontend renders an explicit empty state in that case. TimeOnPitch is passed through as the
// raw JSON array stored by the worker.
public record MatchStatsOut(
    int VideoId,
    int AnalysisId,
    TeamStatsOut TeamA,
    TeamStatsOut TeamB,
    double ControlledSeconds,
    double LooseSeconds,
    System.Text.Json.JsonElement TimeOnPitch);
