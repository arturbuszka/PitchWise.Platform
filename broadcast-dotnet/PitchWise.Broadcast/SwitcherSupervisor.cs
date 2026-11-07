using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;

namespace PitchWise.Broadcast;

/// <summary>
/// Keeps one switcher process running per match that wants one, restarting it for as long as the match lasts: a
/// program coming back late beats one that gave up. The database says what is wanted, so a restarted service picks
/// up where the last one stopped. Each match is handled by itself, so nothing about one can hold up another.
/// </summary>
public sealed class SwitcherSupervisor(
    IServiceScopeFactory scopes,
    IOutputSources sources,
    ISwitcherProcessFactory processes,
    BroadcastSettings settings,
    TimeProvider time,
    ILogger<SwitcherSupervisor> log)
{
    private sealed class Running(ISwitcherProcess process, DateTime startedAt)
    {
        public ISwitcherProcess Process { get; } = process;
        public DateTime StartedAt { get; } = startedAt;
        public SemaphoreSlim Sending { get; } = new(1, 1);
        public SwitcherState? Sent { get; set; }
    }

    private static readonly TimeSpan OperatorCutSettles = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<Guid, Running> _running = new();

    public int RunningCount => _running.Count;

    public async Task TickAsync(CancellationToken ct)
    {
        List<MatchSwitcher> wanted;
        List<MatchInput> cameras;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            wanted = await (
                from switcher in db.MatchSwitchers.AsNoTracking()
                join match in db.Matches.AsNoTracking() on switcher.MatchId equals match.Id
                where switcher.Enabled && match.Status != MatchStatus.Ended
                select switcher).ToListAsync(ct);
            var ids = wanted.Select(s => s.MatchId).ToList();
            cameras = await db.MatchInputs.AsNoTracking()
                .Where(i => ids.Contains(i.MatchId) && !i.IsSwitcherProgram).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        }

        var wantedIds = wanted.Select(s => s.MatchId).ToHashSet();
        await Task.WhenAll(_running.Keys.Where(id => !wantedIds.Contains(id)).Select(StopAsync));

        // Null when MediaMTX can't say. Whatever is running carries on, and nothing new starts.
        IReadOnlyDictionary<string, IReadOnlyList<string>>? signals = null;
        try
        {
            signals = await sources.GetAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not read signals from MediaMTX: {Message}", e.Message);
        }

        var byMatch = cameras.ToLookup(c => c.MatchId);
        await Task.WhenAll(wanted.Select(s => LookAfterAsync(s, byMatch[s.MatchId].ToList(), signals, ct)));
    }

    /// <summary>Passes on what the API just changed, without waiting for the next tick.</summary>
    public async Task ApplyAsync(Guid matchId, CancellationToken ct)
    {
        if (!_running.TryGetValue(matchId, out var running)) return;
        MatchSwitcher? switcher;
        List<MatchInput> cameras;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            switcher = await db.MatchSwitchers.AsNoTracking().FirstOrDefaultAsync(s => s.MatchId == matchId && s.Enabled, ct);
            cameras = await db.MatchInputs.AsNoTracking()
                .Where(i => i.MatchId == matchId && !i.IsSwitcherProgram).OrderBy(i => i.CreatedAt).ToListAsync(ct);
        }
        if (switcher is not null) await SendAsync(running, switcher, cameras);
    }

    public Task StopAllAsync() => Task.WhenAll(_running.Keys.Select(StopAsync));

    private async Task LookAfterAsync(
        MatchSwitcher switcher, List<MatchInput> cameras, IReadOnlyDictionary<string, IReadOnlyList<string>>? signals, CancellationToken ct)
    {
        try
        {
            await ReconcileAsync(switcher, cameras, signals, ct);
            await ReportAsync(switcher, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "The switcher of match {MatchId} could not be looked after", switcher.MatchId);
        }
    }

    private async Task ReconcileAsync(
        MatchSwitcher switcher, List<MatchInput> cameras, IReadOnlyDictionary<string, IReadOnlyList<string>>? signals, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        switcher.OnAirVideoInputId = null;
        switcher.OnAirAudioInputId = null;
        switcher.Warning = null;

        if (_running.TryGetValue(switcher.MatchId, out var running))
        {
            if (running.Process.HasExited)
            {
                _running.TryRemove(switcher.MatchId, out _);
                Failed(switcher, running, running.Process.LastError ?? $"The switcher stopped with code {running.Process.ExitCode}.", now);
                return;
            }
            if (now - running.Process.LastProgressAt > settings.SwitcherStallLimit)
            {
                await StopAsync(switcher.MatchId);
                Failed(switcher, running, "The switcher stopped producing a program.", now);
                return;
            }

            await SendAsync(running, switcher, cameras);
            switcher.Status = running.Process.Frames > 0 ? SwitcherStatus.Running : SwitcherStatus.Starting;
            switcher.OnAirVideoInputId = running.Process.OnAirVideo;
            switcher.OnAirAudioInputId = running.Process.OnAirAudio;
            switcher.Warning = Describe(running.Process.Unsupported, cameras);
            if (switcher.Status == SwitcherStatus.Running)
            {
                switcher.Error = null;
                switcher.NextRetryAt = null;
                if (now - running.StartedAt >= settings.SteadyAfter) switcher.Attempts = 0;
            }
            return;
        }

        if (switcher.NextRetryAt > now)
        {
            switcher.Status = SwitcherStatus.Restarting;
            return;
        }
        if (signals is null) return;

        if (!cameras.Any(c => signals.ContainsKey(MatchInput.PathFor(c.Id))))
        {
            switcher.Status = SwitcherStatus.WaitingForCameras;
            switcher.NextRetryAt = null;
            return;
        }

        await StartAsync(switcher, cameras, now, ct);
    }

    private async Task StartAsync(MatchSwitcher switcher, List<MatchInput> cameras, DateTime now, CancellationToken ct)
    {
        if (settings.MediaMtxReadSecret.Length == 0)
        {
            switcher.Status = SwitcherStatus.Restarting;
            switcher.Error = "MEDIAMTX_READ_SECRET is not set on the broadcast service, so it can't read the cameras.";
            switcher.NextRetryAt = now + settings.SwitcherRestartDelays[^1];
            return;
        }

        // A new key each time, which only this process ever knows. The program publishes like any encoder.
        var key = Secrets.Generate("sk_");
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.MatchInputs.Where(i => i.Id == switcher.ProgramInputId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.StreamKeyHash, Secrets.Hash(key)), ct);
        }

        var rtmp = new Uri(settings.MediaMtxRtmpUrl);
        var command = new SwitcherCommand(
            settings.ProgramWidth, settings.ProgramHeight, settings.ProgramVideoKbps,
            rtmp.Host, rtmp.IsDefaultPort || rtmp.Port < 0 ? 1935 : rtmp.Port, "live", $"{switcher.ProgramInputId}?key={key}",
            OutputReader.User, settings.MediaMtxReadSecret, [key, settings.MediaMtxReadSecret]);
        try
        {
            var running = new Running(processes.Start(command), now);
            _running[switcher.MatchId] = running;
            switcher.Status = SwitcherStatus.Starting;
            switcher.NextRetryAt = null;
            await SendAsync(running, switcher, cameras);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.LogError("The switcher could not be started: {Message}", e.Message);
            switcher.Attempts++;
            switcher.Status = SwitcherStatus.Restarting;
            switcher.Error = "The switcher could not be started on the broadcast service.";
            switcher.NextRetryAt = now + DelayFor(switcher.Attempts);
        }
    }

    private async Task SendAsync(Running running, MatchSwitcher switcher, List<MatchInput> cameras)
    {
        var state = new SwitcherState(
            cameras.Select(c => new SwitcherSource(c.Id, SourceUrl(c.Id))).ToList(), switcher.VideoInputId, switcher.AudioInputId);
        await running.Sending.WaitAsync();
        try
        {
            if (running.Sent is { } sent && sent.Video == state.Video && sent.Audio == state.Audio && sent.Inputs.SequenceEqual(state.Inputs))
                return;
            await running.Process.SendAsync(state);
            running.Sent = state;
        }
        finally
        {
            running.Sending.Release();
        }
    }

    private string SourceUrl(Guid inputId)
    {
        var builder = new UriBuilder(settings.MediaMtxRtspUrl) { Path = MatchInput.PathFor(inputId) };
        return builder.Uri.AbsoluteUri;
    }

    private void Failed(MatchSwitcher switcher, Running running, string error, DateTime now)
    {
        if (now - running.StartedAt >= settings.SteadyAfter) switcher.Attempts = 0;
        switcher.Attempts++;
        switcher.Status = SwitcherStatus.Restarting;
        switcher.Error = error;
        switcher.NextRetryAt = now + DelayFor(switcher.Attempts);
        log.LogWarning("The switcher of match {MatchId} stopped, restart {Attempt}: {Error}", switcher.MatchId, switcher.Attempts, error);
    }

    private TimeSpan DelayFor(int attempts) =>
        settings.SwitcherRestartDelays[Math.Min(attempts, settings.SwitcherRestartDelays.Length) - 1];

    private static string? Describe(IReadOnlyDictionary<string, string> unsupported, List<MatchInput> cameras)
    {
        var problems = unsupported.Select(track =>
        {
            var parts = track.Key.Split(':');
            var name = cameras.FirstOrDefault(c => c.Id.ToString() == parts[0])?.Name ?? parts[0];
            return $"{name} sends {parts[^1]} as {track.Value}, which the switcher can't take. Set its encoder to H264 and AAC.";
        }).ToList();
        return problems.Count == 0 ? null : string.Join(" ", problems);
    }

    private async Task StopAsync(Guid matchId)
    {
        if (_running.TryRemove(matchId, out var running)) await running.Process.StopAsync();
    }

    // Only while it is still wanted: the API may have stopped it since this tick began, and it clears these fields.
    private async Task ReportAsync(MatchSwitcher switcher, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        await db.MatchSwitchers
            .Where(s => s.MatchId == switcher.MatchId && s.Enabled)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, switcher.Status)
                .SetProperty(s => s.Error, switcher.Error)
                .SetProperty(s => s.Warning, switcher.Warning)
                .SetProperty(s => s.Attempts, switcher.Attempts)
                .SetProperty(s => s.NextRetryAt, switcher.NextRetryAt)
                .SetProperty(s => s.OnAirVideoInputId, switcher.OnAirVideoInputId)
                .SetProperty(s => s.OnAirAudioInputId, switcher.OnAirAudioInputId)
                .SetProperty(s => s.LastSeenAt, now), ct);

        if (_running.ContainsKey(switcher.MatchId))
        {
            await RecordAutomaticCutAsync(db, switcher.MatchId, SwitcherCutKind.Video, switcher.OnAirVideoInputId, now, ct);
            await RecordAutomaticCutAsync(db, switcher.MatchId, SwitcherCutKind.Audio, switcher.OnAirAudioInputId, now, ct);
        }
    }

    /// <summary>
    /// The history should say what really went out. A cut an operator asked for is already there when it takes
    /// effect, so anything else on air is the slate taking over, or a camera coming back.
    /// </summary>
    private static async Task RecordAutomaticCutAsync(
        AppDbContext db, Guid matchId, SwitcherCutKind kind, Guid? onAir, DateTime now, CancellationToken ct)
    {
        var last = await db.SwitcherCuts.AsNoTracking()
            .Where(c => c.MatchId == matchId && c.Kind == kind).OrderByDescending(c => c.Id).FirstOrDefaultAsync(ct);
        if (last is null || last.InputId == onAir) return;

        // The switcher hasn't got to the operator's cut yet, which isn't a change of its own.
        if (last.Origin == SwitcherCutOrigin.Operator && now - last.At < OperatorCutSettles) return;

        db.SwitcherCuts.Add(new SwitcherCut { MatchId = matchId, Kind = kind, InputId = onAir, Origin = SwitcherCutOrigin.Automatic, At = now });
        await db.SaveChangesAsync(ct);
    }
}
