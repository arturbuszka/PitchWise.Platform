using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Services;

/// <summary>Makes match statuses and signal readings match what MediaMTX reports.</summary>
public class MatchSignalSync(AppDbContext db, IMediaMtxClient mediaMtx, SignalTracker tracker)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var publishers = await mediaMtx.GetPublishersAsync(ct);
        tracker.Update(publishers, DateTimeOffset.UtcNow);

        var matches = await db.Matches
            .AsNoTracking()
            .Where(m => m.Status != MatchStatus.Ended)
            .Select(m => new { m.Id, m.Status, m.ProgramInputId })
            .ToListAsync(ct);

        foreach (var match in matches)
        {
            publishers.TryGetValue(MatchInput.PathFor(match.ProgramInputId.GetValueOrDefault()), out var program);

            // Not ended on losing the signal: encoders drop and reconnect, and only the customer can
            // end a match. The status check is in the write, so a match the customer ended since it
            // was read above can't come back to life.
            if (program is not null && match.Status != MatchStatus.Live)
            {
                await db.Matches
                    .Where(m => m.Id == match.Id && m.Status != MatchStatus.Ended)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(m => m.Status, MatchStatus.Live)
                        .SetProperty(m => m.StartedAt, m => m.StartedAt ?? program.ConnectedSince), ct);
            }
            else if (program is null && match.Status == MatchStatus.Live)
            {
                await db.Matches
                    .Where(m => m.Id == match.Id && m.Status != MatchStatus.Ended)
                    .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MatchStatus.Waiting), ct);
            }
        }
    }
}

/// <summary>
/// Polls rather than relying on MediaMTX calling back: a missed callback or a crash of either side
/// would otherwise leave a match showing "live" forever. Polling cannot drift for longer than one
/// interval, and several API instances running it just do the same harmless write.
/// </summary>
public class MatchSignalMonitor(IServiceScopeFactory scopes, AppSettings settings, ILogger<MatchSignalMonitor> log)
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
                await scope.ServiceProvider.GetRequiredService<MatchSignalSync>().RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // MediaMTX or the database being down says nothing about the matches: leave them be.
                log.LogWarning("Match signal sync failed: {Message}", e.Message);
            }
        }
    }
}
