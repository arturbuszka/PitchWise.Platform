using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast;

/// <summary>Runs the supervisor while this instance is the one that may send.</summary>
public sealed class BroadcastService(
    OutputSupervisor supervisor,
    SwitcherSupervisor switchers,
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
                        // Side by side, as a slow destination must not hold up a program, or the other way round.
                        await Task.WhenAll(
                            LookAfter("outputs", () => supervisor.TickAsync(stoppingToken)),
                            LookAfter("switchers", () => switchers.TickAsync(stoppingToken)));
                    }
                    else
                    {
                        if (!standingBy) log.LogWarning("Another broadcast service is running. Standing by.");
                        standingBy = true;
                        holding = false;
                        await Task.WhenAll(supervisor.StopAllAsync(), switchers.StopAllAsync());
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Could not tell whether this instance may send: {Message}", e.Message);
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
            await Task.WhenAll(supervisor.StopAllAsync(), switchers.StopAllAsync());
            await instance.DisposeAsync();
        }
    }

    private async Task LookAfter(string what, Func<Task> tick)
    {
        try
        {
            await tick();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Most likely the database. What is running keeps running, as it doesn't need it.
            log.LogWarning("Could not look after {What}: {Message}", what, e.Message);
        }
    }
}
