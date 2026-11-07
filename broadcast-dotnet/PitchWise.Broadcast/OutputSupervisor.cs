using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast;

/// <summary>
/// Makes the running ffmpeg processes match what the database says is wanted, once a second. The database is
/// the only state that matters, so a restarted service simply picks up where the last one stopped. Each output
/// is handled by itself, so that nothing about one can hold up or fail another.
/// </summary>
public sealed class OutputSupervisor(
    IServiceScopeFactory scopes,
    IOutputSources sources,
    IOutputProcessFactory processes,
    ITargetGuard guard,
    OutputKeyCipher cipher,
    BroadcastSettings settings,
    TimeProvider time,
    ILogger<OutputSupervisor> log)
{
    private sealed record Running(IOutputProcess Process, string SourcePath, int Generation, DateTime StartedAt)
    {
        public DateTime? LiveSince { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Running> _running = new();

    public int RunningCount => _running.Count;

    public async Task TickAsync(CancellationToken ct)
    {
        List<(MatchOutput Output, Match Match)> wanted;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            wanted = (await (
                from output in db.MatchOutputs.AsNoTracking()
                join match in db.Matches.AsNoTracking() on output.MatchId equals match.Id
                where output.Enabled && match.Status != MatchStatus.Ended
                select new { output, match }).ToListAsync(ct))
                .Select(row => (row.output, row.match)).ToList();
        }

        var wantedIds = wanted.Select(w => w.Output.Id).ToHashSet();
        await Task.WhenAll(_running.Keys.Where(id => !wantedIds.Contains(id)).Select(StopAsync));

        // Null when MediaMTX can't say. Whatever is running carries on, and nothing new is judged.
        IReadOnlyDictionary<string, IReadOnlyList<string>>? signals = null;
        try
        {
            signals = await sources.GetAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not read signals from MediaMTX: {Message}", e.Message);
        }

        await Task.WhenAll(wanted.Select(w => LookAfterAsync(w.Output, w.Match, signals, ct)));
    }

    public Task StopAllAsync() => Task.WhenAll(_running.Keys.Select(StopAsync));

    private async Task LookAfterAsync(
        MatchOutput output, Match match, IReadOnlyDictionary<string, IReadOnlyList<string>>? signals, CancellationToken ct)
    {
        try
        {
            await ReconcileAsync(output, match, signals, ct);
            await ReportAsync(output, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Output {OutputId} could not be looked after", output.Id);
        }
    }

    private async Task ReconcileAsync(
        MatchOutput output, Match match, IReadOnlyDictionary<string, IReadOnlyList<string>>? signals, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var path = MatchOutput.SourcePathFor(match);
        IReadOnlyList<string>? tracks = null;
        var hasSignal = signals?.TryGetValue(path, out tracks) == true;

        if (_running.TryGetValue(output.Id, out var running))
        {
            // New settings, or another input on air. Neither is a failure.
            if (running.Generation != output.Generation || running.SourcePath != path)
            {
                await StopAsync(output.Id);
            }
            else if (running.Process.HasExited)
            {
                _running.TryRemove(output.Id, out _);
                if (Steady(running, now)) output.Attempts = 0;

                // ffmpeg ends with 0 when its input ends, and a lost signal isn't this output's fault. Without
                // knowing about the signal, a failure can't be pinned on the destination either.
                if (running.Process.ExitCode != 0 && hasSignal)
                {
                    Fail(output, running.Process.LastError ?? $"ffmpeg stopped with code {running.Process.ExitCode}.", now);
                    return;
                }
                output.LiveSince = null;
            }
            else if (now - running.Process.LastProgressAt > settings.StallLimit)
            {
                await StopAsync(output.Id);
                if (Steady(running, now)) output.Attempts = 0;
                if (signals is null || hasSignal)
                {
                    Fail(output, running.Process.BytesSent == 0
                        ? "The destination didn't accept the connection in time."
                        : "The destination stopped accepting data.", now);
                    return;
                }
                output.LiveSince = null;
            }
            else
            {
                // Bytes moving means the destination took the connection and is taking the data.
                if (running.Process.BytesSent > 0)
                {
                    running.LiveSince ??= now;
                    output.Status = OutputStatus.Live;
                    output.LiveSince = running.LiveSince;
                    output.NextRetryAt = null;
                    output.Error = null;
                    if (Steady(running, now)) output.Attempts = 0;
                }
                else
                {
                    output.Status = OutputStatus.Connecting;
                }
                return;
            }
        }

        // Giving up lasts until someone acts, which raises the generation and clears the status.
        if (output.Status == OutputStatus.Failed) return;
        if (signals is null) return;

        if (!hasSignal)
        {
            output.Status = OutputStatus.WaitingForSignal;
            output.Error = null;
            output.Warning = null;
            output.NextRetryAt = null;
            output.LiveSince = null;
            return;
        }

        var verdict = SignalCheck.Check(tracks!);
        output.Warning = verdict.Warning;
        if (verdict.Problem is not null)
        {
            output.Status = OutputStatus.Incompatible;
            output.Error = verdict.Problem;
            output.NextRetryAt = null;
            return;
        }

        if (output.NextRetryAt > now) return;

        await StartAsync(output, path, now, ct);
    }

    private bool Steady(Running running, DateTime now) =>
        running.LiveSince is { } since && now - since >= settings.SteadyAfter;

    private async Task StartAsync(MatchOutput output, string path, DateTime now, CancellationToken ct)
    {
        if (settings.MediaMtxReadSecret.Length == 0)
        {
            GiveUp(output, "MEDIAMTX_READ_SECRET is not set on the broadcast service, so it can't read the signal.");
            return;
        }

        string key;
        try
        {
            key = cipher.Decrypt(output.Id, output.StreamKeyEncrypted);
        }
        catch (OutputKeyException e)
        {
            GiveUp(output, e.Message);
            return;
        }

        try
        {
            if (await guard.ProblemWithAsync(output.Url, ct) is { } problem)
            {
                GiveUp(output, problem);
                return;
            }
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Fail(output, "The destination's address could not be looked up.", now);
            return;
        }

        var command = new OutputCommand(SourceUrl(path), OutputTarget.Publish(output.Url, key), [key, settings.MediaMtxReadSecret]);
        try
        {
            _running[output.Id] = new Running(processes.Start(command), path, output.Generation, now);
            output.Status = OutputStatus.Connecting;
            output.Error = null;
            output.NextRetryAt = null;
            output.LiveSince = null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            GiveUp(output, "ffmpeg could not be started on the broadcast service.");
            log.LogError("ffmpeg could not be started: {Message}", e.Message);
        }
    }

    private string SourceUrl(string path)
    {
        var builder = new UriBuilder(settings.MediaMtxRtspUrl)
        {
            UserName = OutputReader.User,
            Password = Uri.EscapeDataString(settings.MediaMtxReadSecret),
            Path = path,
        };
        return builder.Uri.AbsoluteUri;
    }

    private void Fail(MatchOutput output, string error, DateTime now)
    {
        output.LiveSince = null;
        output.Attempts++;
        if (output.Attempts > settings.RetryDelays.Length)
        {
            GiveUp(output, error);
            return;
        }
        output.Status = OutputStatus.Retrying;
        output.Error = error;
        output.NextRetryAt = now + settings.RetryDelays[output.Attempts - 1];
        log.LogWarning("Output {OutputId} failed, attempt {Attempt}: {Error}", output.Id, output.Attempts, error);
    }

    private void GiveUp(MatchOutput output, string error)
    {
        output.Status = OutputStatus.Failed;
        output.Error = error;
        output.NextRetryAt = null;
        output.LiveSince = null;
        log.LogError("Output {OutputId} gave up: {Error}", output.Id, error);
    }

    private async Task StopAsync(Guid id)
    {
        if (_running.TryRemove(id, out var running)) await running.Process.StopAsync();
    }

    // Only for the settings it was working from: the API may have changed them since this tick began.
    private async Task ReportAsync(MatchOutput output, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        await db.MatchOutputs
            .Where(o => o.Id == output.Id && o.Generation == output.Generation)
            .ExecuteUpdateAsync(u => u
                .SetProperty(o => o.Status, output.Status)
                .SetProperty(o => o.Error, output.Error)
                .SetProperty(o => o.Warning, output.Warning)
                .SetProperty(o => o.Attempts, output.Attempts)
                .SetProperty(o => o.NextRetryAt, output.NextRetryAt)
                .SetProperty(o => o.LiveSince, output.LiveSince)
                .SetProperty(o => o.LastSeenAt, now), ct);
    }
}
