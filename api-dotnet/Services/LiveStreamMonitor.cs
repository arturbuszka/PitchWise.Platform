using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Services;

/// <summary>Makes stream statuses match what MediaMTX reports.</summary>
public class LiveStreamSync(AppDbContext db, IMediaMtxClient mediaMtx)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var ready = await mediaMtx.GetReadyPathsAsync(ct);
        var streams = await db.LiveStreams.Where(s => s.Status != LiveStreamStatus.Ended).ToListAsync(ct);

        foreach (var stream in streams)
        {
            var isLive = ready.Contains($"live/{stream.Id}");
            if (isLive && stream.Status != LiveStreamStatus.Live)
            {
                stream.Status = LiveStreamStatus.Live;
                stream.StartedAt ??= DateTime.UtcNow;
            }
            else if (!isLive && stream.Status == LiveStreamStatus.Live)
            {
                // Not ended: encoders drop and reconnect, and only the customer can end a stream.
                stream.Status = LiveStreamStatus.Idle;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Polls rather than relying on MediaMTX calling back: a missed callback or a crash of either side
/// would otherwise leave a stream showing "live" forever. Polling cannot drift for longer than one
/// interval, and several API instances running it just do the same harmless write.
/// </summary>
public class LiveStreamMonitor(IServiceScopeFactory scopes, AppSettings settings, ILogger<LiveStreamMonitor> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.MediaMtxPollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<LiveStreamSync>().RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // MediaMTX or the database being down says nothing about the streams: leave them be.
                log.LogWarning("Live stream sync failed: {Message}", e.Message);
            }
        }
    }
}
