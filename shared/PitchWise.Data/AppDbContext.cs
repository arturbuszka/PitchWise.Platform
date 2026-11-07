using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PitchWise.Data.Models;

namespace PitchWise.Data;

// The schema shared by the API and the worker. The API applies migrations at startup;
// the worker only reads and writes.
public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AnalysisSession> AnalysisSessions => Set<AnalysisSession>();
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Highlight> Highlights => Set<Highlight>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchInput> MatchInputs => Set<MatchInput>();
    public DbSet<MatchOutput> MatchOutputs => Set<MatchOutput>();
    public DbSet<MatchSwitcher> MatchSwitchers => Set<MatchSwitcher>();
    public DbSet<SwitcherCut> SwitcherCuts => Set<SwitcherCut>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // enum→string converters (snake_case).
        // The logic lives in static EnumMap methods — EF expression trees don't accept switch.
        var sessionStatus = new ValueConverter<SessionStatus, string>(
            v => EnumMap.SessionToDb(v), v => EnumMap.SessionFromDb(v));

        var matchStatus = new ValueConverter<MatchStatus, string>(
            v => EnumMap.MatchToDb(v), v => EnumMap.MatchFromDb(v));

        var recordingStatus = new ValueConverter<RecordingStatus, string>(
            v => EnumMap.RecordingToDb(v), v => EnumMap.RecordingFromDb(v));

        var outputStatus = new ValueConverter<OutputStatus, string>(
            v => EnumMap.OutputToDb(v), v => EnumMap.OutputFromDb(v));

        var switcherStatus = new ValueConverter<SwitcherStatus, string>(
            v => EnumMap.SwitcherToDb(v), v => EnumMap.SwitcherFromDb(v));

        var eventType = new ValueConverter<EventType, string>(
            v => EventTypeMap.ToDb(v), v => EventTypeMap.FromDb(v));

        var eventSource = new ValueConverter<EventSource, string>(
            v => EnumMap.SourceToDb(v), v => EnumMap.SourceFromDb(v));

        var highlightStatus = new ValueConverter<HighlightStatus, string>(
            v => EnumMap.HighlightToDb(v), v => EnumMap.HighlightFromDb(v));

        b.Entity<AnalysisSession>(e =>
        {
            e.ToTable("analysissession");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.HasIndex(x => x.TenantId);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Subtitle).HasColumnName("subtitle");
            e.Property(x => x.Sport).HasColumnName("sport");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(sessionStatus);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });

        b.Entity<Video>(e =>
        {
            e.ToTable("video");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AnalysisId).HasColumnName("analysis_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.DurationSeconds).HasColumnName("duration_seconds");
            e.Property(x => x.Fps).HasColumnName("fps");
            e.Property(x => x.Order).HasColumnName("order");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.MatchInputId).HasColumnName("match_input_id");
            e.OwnsMany(x => x.Gaps, g => g.ToJson("gaps"));
            e.HasIndex(x => x.AnalysisId);
            // An input has one recording, however many times the job runs.
            e.HasIndex(x => x.MatchInputId).IsUnique();
            e.HasOne<MatchInput>().WithMany().HasForeignKey(x => x.MatchInputId);
        });

        b.Entity<Event>(e =>
        {
            e.ToTable("event");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AnalysisId).HasColumnName("analysis_id");
            e.Property(x => x.VideoId).HasColumnName("video_id");
            e.Property(x => x.Type).HasColumnName("type").HasConversion(eventType);
            e.Property(x => x.Source).HasColumnName("source").HasConversion(eventSource);
            e.Property(x => x.TimestampSeconds).HasColumnName("timestamp_seconds");
            e.Property(x => x.Confidence).HasColumnName("confidence");
            e.Property(x => x.Label).HasColumnName("label");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.PlayerNumber).HasColumnName("player_number");
            e.Property(x => x.PlayerName).HasColumnName("player_name");
            e.Property(x => x.AssistNumber).HasColumnName("assist_number");
            e.Property(x => x.AssistName).HasColumnName("assist_name");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.AnalysisId);
            e.HasIndex(x => x.VideoId);
        });

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenant");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        b.Entity<ApiKey>(e =>
        {
            e.ToTable("apikey");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.KeyHash).HasColumnName("key_hash");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
        });

        b.Entity<Match>(e =>
        {
            e.ToTable("match");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.PublicId).HasColumnName("public_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(matchStatus);
            e.Property(x => x.ProgramInputId).HasColumnName("program_input_id");
            e.Property(x => x.AnalysisId).HasColumnName("analysis_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.Property(x => x.RecordingStatus).HasColumnName("recording_status").HasConversion(recordingStatus);
            e.Property(x => x.RecordingError).HasColumnName("recording_error");
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.AnalysisId).IsUnique();
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
            e.HasOne<AnalysisSession>().WithOne().HasForeignKey<Match>(x => x.AnalysisId);
            e.HasOne<MatchInput>().WithOne().HasForeignKey<Match>(x => x.ProgramInputId);
        });

        b.Entity<MatchInput>(e =>
        {
            e.ToTable("match_input");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.StreamKeyHash).HasColumnName("stream_key_hash");
            e.Property(x => x.IsSwitcherProgram).HasColumnName("is_switcher_program");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasIndex(x => x.MatchId);
            e.HasOne<Match>().WithMany().HasForeignKey(x => x.MatchId);
        });

        b.Entity<MatchOutput>(e =>
        {
            e.ToTable("match_output");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Url).HasColumnName("url");
            e.Property(x => x.StreamKeyEncrypted).HasColumnName("stream_key_encrypted");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.Generation).HasColumnName("generation");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(outputStatus);
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.Warning).HasColumnName("warning");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(x => x.LiveSince).HasColumnName("live_since");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.HasIndex(x => x.MatchId);
            e.HasIndex(x => x.Enabled);
            e.HasOne<Match>().WithMany().HasForeignKey(x => x.MatchId);
        });

        b.Entity<MatchSwitcher>(e =>
        {
            e.ToTable("match_switcher");
            e.HasKey(x => x.MatchId);
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.Enabled).HasColumnName("enabled");
            e.Property(x => x.ProgramInputId).HasColumnName("program_input_id");
            e.Property(x => x.VideoInputId).HasColumnName("video_input_id");
            e.Property(x => x.AudioInputId).HasColumnName("audio_input_id");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(switcherStatus);
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.Warning).HasColumnName("warning");
            e.Property(x => x.Attempts).HasColumnName("attempts");
            e.Property(x => x.NextRetryAt).HasColumnName("next_retry_at");
            e.Property(x => x.OnAirVideoInputId).HasColumnName("on_air_video_input_id");
            e.Property(x => x.OnAirAudioInputId).HasColumnName("on_air_audio_input_id");
            e.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            e.HasIndex(x => x.Enabled);
            e.HasOne<Match>().WithOne().HasForeignKey<MatchSwitcher>(x => x.MatchId);
            e.HasOne<MatchInput>().WithOne().HasForeignKey<MatchSwitcher>(x => x.ProgramInputId);
        });

        b.Entity<SwitcherCut>(e =>
        {
            e.ToTable("switcher_cut");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.MatchId).HasColumnName("match_id");
            e.Property(x => x.Kind).HasColumnName("kind").HasConversion(
                v => v == SwitcherCutKind.Audio ? "audio" : "video",
                v => v == "audio" ? SwitcherCutKind.Audio : SwitcherCutKind.Video);
            e.Property(x => x.InputId).HasColumnName("input_id");
            e.Property(x => x.Origin).HasColumnName("origin").HasConversion(
                v => v == SwitcherCutOrigin.Automatic ? "automatic" : "operator",
                v => v == "automatic" ? SwitcherCutOrigin.Automatic : SwitcherCutOrigin.Operator);
            e.Property(x => x.At).HasColumnName("at");
            e.HasIndex(x => new { x.MatchId, x.Id });
            e.HasOne<Match>().WithMany().HasForeignKey(x => x.MatchId);
            e.HasOne<MatchInput>().WithMany().HasForeignKey(x => x.InputId);
        });

        b.Entity<Highlight>(e =>
        {
            e.ToTable("highlight");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.AnalysisId).HasColumnName("analysis_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.EventIds).HasColumnName("event_ids");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(highlightStatus);
            e.Property(x => x.Progress).HasColumnName("progress");
            e.Property(x => x.Filename).HasColumnName("filename");
            e.Property(x => x.HlsReady).HasColumnName("hls_ready");
            e.Property(x => x.Error).HasColumnName("error");
            e.Property(x => x.ShareToken).HasColumnName("share_token");
            e.Property(x => x.ShareExpiresAt).HasColumnName("share_expires_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.FinishedAt).HasColumnName("finished_at");
            e.HasIndex(x => x.AnalysisId);
            e.HasIndex(x => x.ShareToken);
        });
    }
}

// enum ↔ string mappings (static methods — usable in EF expression trees).
public static class EnumMap
{
    public static string SessionToDb(SessionStatus v) => v switch
    {
        SessionStatus.Processing => "processing",
        SessionStatus.Done => "done",
        _ => "draft",
    };

    public static SessionStatus SessionFromDb(string v) => v switch
    {
        "processing" => SessionStatus.Processing,
        "done" => SessionStatus.Done,
        _ => SessionStatus.Draft,
    };

    public static string MatchToDb(MatchStatus v) => v switch
    {
        MatchStatus.Live => "live",
        MatchStatus.Ended => "ended",
        _ => "waiting",
    };

    public static MatchStatus MatchFromDb(string v) => v switch
    {
        "live" => MatchStatus.Live,
        "ended" => MatchStatus.Ended,
        _ => MatchStatus.Waiting,
    };

    public static string RecordingToDb(RecordingStatus v) => v switch
    {
        RecordingStatus.Processing => "processing",
        RecordingStatus.Ready => "ready",
        RecordingStatus.Failed => "failed",
        _ => "none",
    };

    public static RecordingStatus RecordingFromDb(string v) => v switch
    {
        "processing" => RecordingStatus.Processing,
        "ready" => RecordingStatus.Ready,
        "failed" => RecordingStatus.Failed,
        _ => RecordingStatus.None,
    };

    public static string SwitcherToDb(SwitcherStatus v) => v switch
    {
        SwitcherStatus.WaitingForCameras => "waiting_for_cameras",
        SwitcherStatus.Starting => "starting",
        SwitcherStatus.Running => "running",
        SwitcherStatus.Restarting => "restarting",
        SwitcherStatus.Offline => "offline",
        _ => "stopped",
    };

    public static SwitcherStatus SwitcherFromDb(string v) => v switch
    {
        "waiting_for_cameras" => SwitcherStatus.WaitingForCameras,
        "starting" => SwitcherStatus.Starting,
        "running" => SwitcherStatus.Running,
        "restarting" => SwitcherStatus.Restarting,
        "offline" => SwitcherStatus.Offline,
        _ => SwitcherStatus.Stopped,
    };

    public static string OutputToDb(OutputStatus v) => v switch
    {
        OutputStatus.WaitingForSignal => "waiting_for_signal",
        OutputStatus.Connecting => "connecting",
        OutputStatus.Live => "live",
        OutputStatus.Retrying => "retrying",
        OutputStatus.Incompatible => "incompatible",
        OutputStatus.Failed => "failed",
        OutputStatus.Offline => "offline",
        _ => "stopped",
    };

    public static OutputStatus OutputFromDb(string v) => v switch
    {
        "waiting_for_signal" => OutputStatus.WaitingForSignal,
        "connecting" => OutputStatus.Connecting,
        "live" => OutputStatus.Live,
        "retrying" => OutputStatus.Retrying,
        "incompatible" => OutputStatus.Incompatible,
        "failed" => OutputStatus.Failed,
        "offline" => OutputStatus.Offline,
        _ => OutputStatus.Stopped,
    };

    public static string SourceToDb(EventSource v) => v == EventSource.Auto ? "auto" : "manual";

    public static EventSource SourceFromDb(string v) => v == "auto" ? EventSource.Auto : EventSource.Manual;

    public static string HighlightToDb(HighlightStatus v) => v switch
    {
        HighlightStatus.Running => "running",
        HighlightStatus.Done => "done",
        HighlightStatus.Failed => "failed",
        _ => "pending",
    };

    public static HighlightStatus HighlightFromDb(string v) => v switch
    {
        "running" => HighlightStatus.Running,
        "done" => HighlightStatus.Done,
        "failed" => HighlightStatus.Failed,
        _ => HighlightStatus.Pending,
    };
}

// EventType ↔ string mapping shared by the EF converter and DTO serialization.
public static class EventTypeMap
{
    public static string ToDb(EventType v) => v switch
    {
        EventType.Goal => "goal",
        EventType.Shot => "shot",
        EventType.WaywardPass => "wayward_pass",
        EventType.Foul => "foul",
        EventType.FreeKick => "free_kick",
        EventType.Offside => "offside",
        EventType.Substitution => "substitution",
        EventType.SetPiece => "set_piece",
        EventType.Manual => "manual",
        _ => "manual",
    };

    public static EventType FromDb(string v) => v switch
    {
        "goal" => EventType.Goal,
        "shot" => EventType.Shot,
        "wayward_pass" => EventType.WaywardPass,
        "foul" => EventType.Foul,
        "free_kick" => EventType.FreeKick,
        "offside" => EventType.Offside,
        "substitution" => EventType.Substitution,
        "set_piece" => EventType.SetPiece,
        "manual" => EventType.Manual,
        _ => EventType.Manual,
    };
}
