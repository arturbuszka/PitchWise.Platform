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
    int Order,
    List<GapOut> Gaps)
{
    public static VideoOut From(Video v) => new(
        v.Id, v.AnalysisId, v.Name, v.DurationSeconds, v.Fps, v.Order,
        v.Gaps.Select(g => new GapOut(g.Start, g.End)).ToList());
}

// A part of a recording with no signal, in seconds. Players skip it.
public record GapOut(double Start, double End);

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

// --- Matches ---

public record CreateMatchIn(string Name, string? InputName = null);

public record AddInputIn(string Name);

public record SetProgramIn(Guid InputId);

// What an input is receiving now. Loss, drops and round trip are null over RTMP: TCP hides them.
public record SignalOut(
    SignalState State,
    string? Protocol,
    DateTime? ConnectedSince,
    double? BitrateKbps,
    double? LossPercent,
    long? PacketsLost,
    long? PacketsDropped,
    double? RttMs,
    IReadOnlyList<string> Tracks);

// The destination's key is never returned, so the only way to see it is where it was issued.
public record OutputOut(
    Guid Id,
    string Name,
    string Url,
    bool Enabled,
    OutputStatus Status,
    string? Error,
    string? Warning,
    int Attempts,
    DateTime? NextRetryAt,
    DateTime? LiveSince);

public record CreateOutputIn(string? Name, string? Url, string? StreamKey);

public record UpdateOutputIn(string? Name, string? Url, string? StreamKey);

public record MatchInputOut(Guid Id, string Name, bool IsProgram, SignalOut Signal);

public record MatchOut(
    Guid Id,
    string Name,
    MatchStatus Status,
    string PublicId,
    string PublicUrl,
    string EmbedUrl,
    string EmbedHtml,
    Guid? ProgramInputId,
    int AnalysisId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? EndedAt,
    RecordingStatus RecordingStatus,
    string? RecordingError,
    List<MatchInputOut> Inputs,
    List<OutputOut> Outputs,
    SwitcherOut? Switcher);

// Null fields for what is on air mean the slate: a black picture, or silence.
public record SwitcherOut(
    SwitcherStatus Status,
    string? Error,
    string? Warning,
    int Attempts,
    DateTime? NextRetryAt,
    Guid ProgramInputId,
    Guid VideoInputId,
    Guid AudioInputId,
    Guid? OnAirVideoInputId,
    Guid? OnAirAudioInputId,
    SignalOut ProgramSignal);

public record SwitchToIn(Guid InputId);

// MatchSeconds is on the match timeline, so it is also the position in the match's recordings.
public record SwitcherCutOut(long Id, SwitcherCutKind Kind, Guid? InputId, SwitcherCutOrigin Origin, DateTime At, double? MatchSeconds);

public record PreviewOut(DateTime ExpiresAt, List<PreviewSourceOut> Sources);

// A WHEP address, already signed. Empty input id: the program.
public record PreviewSourceOut(Guid InputId, string WhepUrl);

// Where an encoder publishes. Only returned when a key is issued: it is stored hashed, so it cannot
// be shown again. OBS asks for the RTMP server and stream name separately.
public record InputAccessOut(
    Guid InputId,
    string StreamKey,
    string RtmpUrl,
    string RtmpServer,
    string RtmpStreamName,
    string SrtUrl);

public record CreatedMatchOut(MatchOut Match, InputAccessOut Access);

public record CreatedInputOut(MatchInputOut Input, InputAccessOut Access);

public record PlaybackOut(string Url, DateTime ExpiresAt, Guid InputId);

// A finished recording, on the edge. Positions in it are seconds since the match started.
public record VodOut(string Url, DateTime ExpiresAt, double? DurationSeconds, List<GapOut> Gaps);

// What anyone with the public link may see, so no tenant data or keys.
public record PublicMatchOut(
    string Name,
    MatchStatus Status,
    DateTime? StartedAt,
    DateTime? EndedAt,
    PlaybackOut? Playback,
    RecordingStatus RecordingStatus,
    VodOut? Vod);

// --- Admin ---

public record CreateTenantIn(string Name);

// The API key is only shown here; it is stored hashed.
public record CreatedTenantOut(int Id, string Name, string ApiKey);

// --- MediaMTX auth callback ---

public record MediaMtxAuthIn(string? Action, string? Path, string? Query, string? Protocol, string? User = null, string? Password = null);
