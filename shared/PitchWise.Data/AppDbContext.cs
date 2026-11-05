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
    public DbSet<LiveStream> LiveStreams => Set<LiveStream>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // enum→string converters (snake_case).
        // The logic lives in static EnumMap methods — EF expression trees don't accept switch.
        var sessionStatus = new ValueConverter<SessionStatus, string>(
            v => EnumMap.SessionToDb(v), v => EnumMap.SessionFromDb(v));

        var liveStatus = new ValueConverter<LiveStreamStatus, string>(
            v => EnumMap.LiveToDb(v), v => EnumMap.LiveFromDb(v));

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
            e.HasIndex(x => x.AnalysisId);
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

        b.Entity<LiveStream>(e =>
        {
            e.ToTable("livestream");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.StreamKeyHash).HasColumnName("stream_key_hash");
            e.Property(x => x.Status).HasColumnName("status").HasConversion(liveStatus);
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.Property(x => x.StartedAt).HasColumnName("started_at");
            e.Property(x => x.EndedAt).HasColumnName("ended_at");
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Status);
            e.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId);
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

    public static string LiveToDb(LiveStreamStatus v) => v switch
    {
        LiveStreamStatus.Live => "live",
        LiveStreamStatus.Ended => "ended",
        _ => "idle",
    };

    public static LiveStreamStatus LiveFromDb(string v) => v switch
    {
        "live" => LiveStreamStatus.Live,
        "ended" => LiveStreamStatus.Ended,
        _ => LiveStreamStatus.Idle,
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
