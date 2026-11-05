using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;
using PitchWise.Worker;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

var settings = new AppSettings();
builder.Configuration.GetSection("App").Bind(settings);
settings.StorageDir = Environment.GetEnvironmentVariable("STORAGE_DIR") ?? settings.StorageDir;
settings.DatabaseConnection = Environment.GetEnvironmentVariable("DATABASE_CONNECTION") ?? settings.DatabaseConnection;
settings.RedisUrl = Environment.GetEnvironmentVariable("REDIS_URL") ?? settings.RedisUrl;
settings.HighlightQueue = Environment.GetEnvironmentVariable("HIGHLIGHT_QUEUE") ?? settings.HighlightQueue;
settings.RecordingQueue = Environment.GetEnvironmentVariable("RECORDING_QUEUE") ?? settings.RecordingQueue;
settings.EnsureDirs();
builder.Services.AddSingleton(settings);

var worker = new WorkerSettings();
builder.Configuration.GetSection("Worker").Bind(worker);
if (double.TryParse(Environment.GetEnvironmentVariable("CLIP_PRE_SECONDS"), out double pre)) worker.ClipPreSeconds = pre;
if (double.TryParse(Environment.GetEnvironmentVariable("CLIP_POST_SECONDS"), out double post)) worker.ClipPostSeconds = post;
worker.FfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? worker.FfmpegPath;
worker.FfprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? worker.FfprobePath;
builder.Services.AddSingleton(worker);
FfmpegTools.FfmpegPath = worker.FfmpegPath;
FfmpegTools.FfprobePath = worker.FfprobePath;

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(settings.DatabaseConnection));
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(RedisConfig(settings.RedisUrl)));
builder.Services.AddSingleton<HighlightRunner>();
builder.Services.AddSingleton<RecordingRunner>();
builder.Services.AddHostedService<QueueWorker>();

IHost host = builder.Build();

// A render left "running" belongs to a worker that died: its queue entry is already gone, so
// without this it would look busy forever.
using (IServiceScope scope = host.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orphaned = await db.Highlights.Where(h => h.Status == HighlightStatus.Running).ToListAsync();
        foreach (var h in orphaned)
        {
            h.Status = HighlightStatus.Failed;
            h.Error = "worker restarted during the render";
            h.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        // Unlike a render, nobody can ask for a recording again without it, and the job is safe to
        // repeat. So a job lost to a crash, or never queued, is simply queued again.
        var unfinished = await db.Matches
            .Where(m => m.RecordingStatus == RecordingStatus.Processing)
            .Select(m => m.Id)
            .ToListAsync();
        var queue = host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        foreach (var id in unfinished)
            await queue.ListLeftPushAsync(settings.RecordingQueue, System.Text.Json.JsonSerializer.Serialize(new { match_id = id }));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Worker] Orphan cleanup skipped: {ex.Message}");
    }
}

host.Run();

static ConfigurationOptions RedisConfig(string url)
{
    string trimmed = url.Replace("redis://", "").Replace("rediss://", "");
    ConfigurationOptions cfg = ConfigurationOptions.Parse(trimmed);
    // Keep retrying instead of crashing when Redis isn't up yet at worker start.
    cfg.AbortOnConnectFail = false;
    return cfg;
}
