using Npgsql;

namespace PitchWise.Broadcast;

/// <summary>
/// Hears from the API the moment an operator cuts, as waiting for the next tick would add up to a second to every
/// cut. Only a shortcut: the tick still reads the same state from the database, so a missed notice costs that second.
/// </summary>
public sealed class SwitcherListener(SwitcherSupervisor switchers, BroadcastSettings settings, ILogger<SwitcherListener> log) : BackgroundService
{
    private static readonly TimeSpan ReconnectAfter = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(settings.DatabaseConnection);
                await connection.OpenAsync(stoppingToken);
                connection.Notification += (_, notice) =>
                {
                    if (Guid.TryParse(notice.Payload, out var matchId)) _ = ApplyAsync(matchId, stoppingToken);
                };
                await using (var listen = new NpgsqlCommand("LISTEN switcher", connection))
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                while (true) await connection.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning("Lost the connection that hears about cuts, so they take up to a second: {Message}", e.Message);
                await Task.Delay(ReconnectAfter, stoppingToken);
            }
        }
    }

    private async Task ApplyAsync(Guid matchId, CancellationToken ct)
    {
        try
        {
            await switchers.ApplyAsync(matchId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Could not pass on a cut for match {MatchId}: {Message}", matchId, e.Message);
        }
    }
}
