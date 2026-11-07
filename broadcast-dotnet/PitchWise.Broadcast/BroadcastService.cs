using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast;

/// <summary>Runs the supervisor while this instance is the one that may send.</summary>
public sealed class BroadcastService(
    OutputSupervisor supervisor,
    SingleInstanceLock instance,
    OutputKeyCipher cipher,
    BroadcastSettings settings,
    ILogger<BroadcastService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!cipher.IsConfigured) log.LogWarning("{Message} Outputs will show this as their error.", OutputKeyCipher.NotConfigured);

        var holding = false;
        var standingBy = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (await instance.HoldAsync(stoppingToken))
                    {
                        if (!holding)
                        {
                            var orphans = OrphanSweep.Run();
                            if (orphans > 0) log.LogWarning("Ended {Count} ffmpeg left by an earlier run", orphans);
                            holding = true;
                            standingBy = false;
                        }
                        await supervisor.TickAsync(stoppingToken);
                    }
                    else
                    {
                        if (!standingBy) log.LogWarning("Another broadcast service is running. Standing by.");
                        standingBy = true;
                        holding = false;
                        await supervisor.StopAllAsync();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Most likely the database. What is running keeps running, as it doesn't need it.
                    log.LogWarning("Could not look after outputs: {Message}", e.Message);
                }
                await Task.Delay(settings.TickInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        finally
        {
            await supervisor.StopAllAsync();
            await instance.DisposeAsync();
        }
    }
}
