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
    // Set when this is the recording of a match input rather than an upload.
    public Guid? MatchInputId { get; set; }
    // Parts of a recording with no signal. The file keeps them as holes, so its positions stay on
    // the match timeline. Empty for uploads.
    public List<TimelineGap> Gaps { get; set; } = [];
}

// A stretch of the match timeline, in seconds, during which an input sent nothing.
public class TimelineGap
{
    public double Start { get; set; }
    public double End { get; set; }
}

public class Event
{
    public int Id { get; set; }
    public int AnalysisId { get; set; }
    public int? VideoId { get; set; }
    public EventType Type { get; set; }
    public EventSource Source { get; set; }
    // On the match timeline for a match: seconds since Match.StartedAt, with signal gaps counted.
    // That is also the position in any of the match's recordings.
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

// One broadcast. Encoders publish to its inputs; viewers watch whichever input is on air.
public class Match
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    // Unguessable id in the public page and embed URLs, so viewers never see the tenant's match id.
    public string PublicId { get; set; } = string.Empty;
    public MatchStatus Status { get; set; } = MatchStatus.Waiting;
    // The input viewers get. Set in the same transaction that creates the match.
    public Guid? ProgramInputId { get; set; }
    // Where the match's events and highlights live, so a broadcast and a recording share one model.
    public int AnalysisId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // When the program signal first arrived: time zero of the match timeline.
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public RecordingStatus RecordingStatus { get; set; } = RecordingStatus.None;
    public string? RecordingError { get; set; }
}

// A copy of the program sent to a destination such as YouTube or Twitch. The API writes what is wanted, and
// the broadcast service writes what is happening. Each change the API makes raises Generation, which tells
// the service to start over, and stops a report about the old settings from overwriting the new state.
public class MatchOutput
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    // Encrypted with OutputKeyCipher, as it has to be sent on and so can't be hashed. Never returned.
    public string StreamKeyEncrypted { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int Generation { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public OutputStatus Status { get; set; } = OutputStatus.Stopped;
    public string? Error { get; set; }
    public string? Warning { get; set; }
    // Failures since the output last ran steadily. Losing the input signal doesn't count.
    public int Attempts { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime? LiveSince { get; set; }
    // When the broadcast service last looked after it, so a dead service doesn't pass for a live output.
    public DateTime? LastSeenAt { get; set; }

    /// <summary>Asks for it to run, forgetting any failures.</summary>
    public void Start(DateTime now)
    {
        Reset(OutputStatus.Connecting);
        Enabled = true;
        LastSeenAt = now;
    }

    public void Stop()
    {
        Reset(OutputStatus.Stopped);
        Enabled = false;
    }

    private void Reset(OutputStatus status)
    {
        Generation++;
        Status = status;
        Error = null;
        Warning = null;
        Attempts = 0;
        NextRetryAt = null;
        LiveSince = null;
    }

    // The one place that says what an output sends. With a switcher, the input on air is its program.
    public static string SourcePathFor(Match match) => MatchInput.PathFor(match.ProgramInputId.GetValueOrDefault());
}

// Cuts between a match's cameras in our own panel. The program it makes is an input of the match, hidden from the
// list of cameras, so the public page, the recording and the outputs take it like any other. Without one of these,
// the club sends a ready program from its own OBS or vMix. The API writes what is wanted, and the broadcast service
// writes what is happening.
public class MatchSwitcher
{
    public Guid MatchId { get; set; }
    public bool Enabled { get; set; }
    public Guid ProgramInputId { get; set; }
    public Guid VideoInputId { get; set; }
    // One source of sound, chosen by itself, so that sound needn't jump with each cut.
    public Guid AudioInputId { get; set; }

    public SwitcherStatus Status { get; set; } = SwitcherStatus.Stopped;
    public string? Error { get; set; }
    public string? Warning { get; set; }
    // Restarts since it last ran steadily.
    public int Attempts { get; set; }
    public DateTime? NextRetryAt { get; set; }
    // Null while the slate is on air: a black picture for video, silence for audio.
    public Guid? OnAirVideoInputId { get; set; }
    public Guid? OnAirAudioInputId { get; set; }
    // When the broadcast service last looked after it, so a dead service doesn't pass for a running switcher.
    public DateTime? LastSeenAt { get; set; }

    public void Stop()
    {
        Enabled = false;
        Status = SwitcherStatus.Stopped;
        Error = null;
        Warning = null;
        Attempts = 0;
        NextRetryAt = null;
        OnAirVideoInputId = null;
        OnAirAudioInputId = null;
    }
}

// One change of what is on air, kept for the operator and for going through the match afterwards.
public class SwitcherCut
{
    public long Id { get; set; }
    public Guid MatchId { get; set; }
    public SwitcherCutKind Kind { get; set; }
    // Null when the slate took over.
    public Guid? InputId { get; set; }
    public SwitcherCutOrigin Origin { get; set; }
    public DateTime At { get; set; }
}

// One signal coming into a match, usually a camera. An encoder publishes to it over RTMP or SRT.
public class MatchInput
{
    public Guid Id { get; set; }
    public Guid MatchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StreamKeyHash { get; set; } = string.Empty;
    // The program a switcher publishes, rather than a camera. Operators don't see or choose it as an input.
    public bool IsSwitcherProgram { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // The MediaMTX path of an input. Its id, not a secret: publishing also needs the stream key.
    public static string PathFor(Guid inputId) => $"live/{inputId}";
}
