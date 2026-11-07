using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PitchWise.Data;
using PitchWise.Data.Models;

namespace PitchWise.Broadcast.Tests;

/// <summary>A real database, and fakes for the process and for MediaMTX, so that time and failures can be chosen.</summary>
public sealed class SwitcherSupervisorTests(OutputDatabase database) : IClassFixture<OutputDatabase>, IAsyncLifetime
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProcess(Clock clock) : ISwitcherProcess
    {
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public long Frames { get; set; }
        public DateTime LastProgressAt { get; set; } = clock.Now.UtcDateTime;
        public string? LastError { get; set; }
        public Guid? OnAirVideo { get; set; }
        public Guid? OnAirAudio { get; set; }
        public IReadOnlyDictionary<string, string> Unsupported { get; set; } = new Dictionary<string, string>();
        public List<SwitcherState> Sent { get; } = [];
        public bool Stopped { get; private set; }

        public void Produce() { Frames += 25; LastProgressAt = clock.Now.UtcDateTime; }
        public void Exit(int code, string? error = null) { HasExited = true; ExitCode = code; LastError = error; }

        public Task SendAsync(SwitcherState state)
        {
            Sent.Add(state);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Stopped = true;
            HasExited = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Factory(Clock clock) : ISwitcherProcessFactory
    {
        public List<(SwitcherCommand Command, FakeProcess Process)> Started { get; } = [];
        public FakeProcess Last => Started[^1].Process;
        public bool Refuse { get; set; }

        public ISwitcherProcess Start(SwitcherCommand command)
        {
            if (Refuse) throw new InvalidOperationException("python is missing");
            var process = new FakeProcess(clock);
            Started.Add((command, process));
            return process;
        }
    }

    private sealed class Sources : IOutputSources
    {
        public Dictionary<string, IReadOnlyList<string>> Signals { get; } = [];
        public bool Down { get; set; }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken ct)
        {
            if (Down) throw new HttpRequestException("MediaMTX is down");
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>(Signals));
        }
    }

    private readonly Clock _clock = new();
    private readonly Sources _sources = new();
    private readonly BroadcastSettings _settings = new()
    {
        MediaMtxReadSecret = "read-secret",
        MediaMtxRtspUrl = "rtsp://mediamtx:8554",
        MediaMtxRtmpUrl = "rtmp://mediamtx:1935",
        SwitcherRestartDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)],
    };
    private Factory _factory = null!;
    private ServiceProvider _services = null!;
    private SwitcherSupervisor _supervisor = null!;

    public async Task InitializeAsync()
    {
        _factory = new Factory(_clock);
        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .BuildServiceProvider();
        // The supervisor looks after every wanted switcher, so another test's would get in the way.
        await Db(async db =>
        {
            await db.Database.MigrateAsync();
            return await db.MatchSwitchers.ExecuteUpdateAsync(u => u.SetProperty(s => s.Enabled, false));
        });
        _supervisor = new SwitcherSupervisor(
            _services.GetRequiredService<IServiceScopeFactory>(), _sources, _factory, _settings, _clock, NullLogger<SwitcherSupervisor>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _supervisor.StopAllAsync();
        await _services.DisposeAsync();
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<MatchSwitcher> Row(Guid matchId) => Db(db => db.MatchSwitchers.AsNoTracking().SingleAsync(s => s.MatchId == matchId));

    private Task<List<SwitcherCut>> Cuts(Guid matchId) =>
        Db(db => db.SwitcherCuts.AsNoTracking().Where(c => c.MatchId == matchId).OrderBy(c => c.Id).ToListAsync());

    private sealed record Setup(Guid MatchId, MatchInput A, MatchInput B, MatchInput Program);

    private Task<Setup> WantedSwitcher() => Db(async db =>
    {
        var tenant = new Tenant { Name = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var analysis = new AnalysisSession { TenantId = tenant.Id, Name = "m" };
        db.AnalysisSessions.Add(analysis);
        await db.SaveChangesAsync();
        var match = new Match { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "m", PublicId = Guid.NewGuid().ToString("N"), AnalysisId = analysis.Id };
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        var a = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "A", StreamKeyHash = "a", CreatedAt = _clock.Now.UtcDateTime };
        var b = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "B", StreamKeyHash = "b", CreatedAt = _clock.Now.UtcDateTime.AddSeconds(1) };
        var program = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "Program", StreamKeyHash = "unused", IsSwitcherProgram = true };
        db.MatchInputs.AddRange(a, b, program);
        await db.SaveChangesAsync();
        match.ProgramInputId = program.Id;
        db.MatchSwitchers.Add(new MatchSwitcher
        {
            MatchId = match.Id, Enabled = true, ProgramInputId = program.Id, VideoInputId = a.Id, AudioInputId = a.Id,
            Status = SwitcherStatus.WaitingForCameras,
        });
        db.SwitcherCuts.Add(new SwitcherCut { MatchId = match.Id, Kind = SwitcherCutKind.Video, InputId = a.Id, Origin = SwitcherCutOrigin.Operator, At = _clock.Now.UtcDateTime });
        await db.SaveChangesAsync();
        return new Setup(match.Id, a, b, program);
    });

    private void Signal(MatchInput input, bool on = true)
    {
        if (on) _sources.Signals[MatchInput.PathFor(input.Id)] = ["H264", "MPEG-4 Audio"];
        else _sources.Signals.Remove(MatchInput.PathFor(input.Id));
    }

    private Task Tick() => _supervisor.TickAsync(CancellationToken.None);

    private void After(int seconds) => _clock.Now += TimeSpan.FromSeconds(seconds);

    [Fact]
    public async Task NothingStarts_UntilTheFirstCameraArrives_SoTimeZeroIsNotABlackScreen()
    {
        var setup = await WantedSwitcher();

        await Tick();
        Assert.Empty(_factory.Started);
        Assert.Equal(SwitcherStatus.WaitingForCameras, (await Row(setup.MatchId)).Status);

        Signal(setup.A);
        await Tick();
        Assert.Single(_factory.Started);
        Assert.Equal(SwitcherStatus.Starting, (await Row(setup.MatchId)).Status);
    }

    [Fact]
    public async Task Start_PublishesWithAFreshKeyThatOnlyThisProcessKnows()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);

        await Tick();

        var command = _factory.Started[0].Command;
        var key = command.RtmpStream.Split("?key=")[1];
        Assert.Equal($"{setup.Program.Id}?key={key}", command.RtmpStream);
        Assert.Equal("mediamtx", command.RtmpHost);
        Assert.Equal("live", command.RtmpApplication);
        Assert.Contains(key, command.Secrets);
        Assert.Contains("read-secret", command.Secrets);
        var hash = await Db(db => db.MatchInputs.Where(i => i.Id == setup.Program.Id).Select(i => i.StreamKeyHash).SingleAsync());
        Assert.Equal(Secrets.Hash(key), hash);
    }

    [Fact]
    public async Task State_NamesEveryCameraButNeverTheProgramOrASecret()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);

        await Tick();

        var state = _factory.Last.Sent.Single();
        Assert.Equal([setup.A.Id, setup.B.Id], state.Inputs.Select(i => i.Id));
        Assert.Equal(setup.A.Id, state.Video);
        Assert.Equal($"rtsp://mediamtx:8554/live/{setup.A.Id}", state.Inputs[0].Url);
        Assert.DoesNotContain("read-secret", state.Inputs[0].Url);
    }

    [Fact]
    public async Task Cut_ReachesTheProcessWithoutATick_AndOnlyOnce()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();

        await Db(db => db.MatchSwitchers.Where(s => s.MatchId == setup.MatchId).ExecuteUpdateAsync(u => u.SetProperty(s => s.VideoInputId, setup.B.Id)));
        await _supervisor.ApplyAsync(setup.MatchId, CancellationToken.None);

        Assert.Equal(setup.B.Id, _factory.Last.Sent[^1].Video);
        var sent = _factory.Last.Sent.Count;
        await Tick();
        Assert.Equal(sent, _factory.Last.Sent.Count);
    }

    [Fact]
    public async Task MissedNotice_IsMadeUpForByTheNextTick()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();

        await Db(db => db.MatchSwitchers.Where(s => s.MatchId == setup.MatchId).ExecuteUpdateAsync(u => u.SetProperty(s => s.AudioInputId, setup.B.Id)));
        await Tick();

        Assert.Equal(setup.B.Id, _factory.Last.Sent[^1].Audio);
    }

    [Fact]
    public async Task NewCamera_IsPassedOnWithoutRestartingTheProgram()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();

        var added = await Db(async db =>
        {
            var input = new MatchInput { Id = Guid.NewGuid(), MatchId = setup.MatchId, Name = "C", StreamKeyHash = "c", CreatedAt = _clock.Now.UtcDateTime.AddSeconds(2) };
            db.MatchInputs.Add(input);
            await db.SaveChangesAsync();
            return input;
        });
        await Tick();

        Assert.Single(_factory.Started);
        Assert.Contains(added.Id, _factory.Last.Sent[^1].Inputs.Select(i => i.Id));
    }

    [Fact]
    public async Task Crash_RestartsAfterADelay_AndKeepsTryingWithoutEverGivingUp()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            _factory.Last.Exit(1, "fatal: could not connect");
            await Tick();
            var row = await Row(setup.MatchId);
            Assert.Equal(SwitcherStatus.Restarting, row.Status);
            Assert.Equal("fatal: could not connect", row.Error);
            Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt, _factory.Started.Count);

            After(4);
            await Tick();
            Assert.Equal(attempt + 1, _factory.Started.Count);
        }
    }

    [Fact]
    public async Task Restart_WaitsLongerAfterRepeatedFailures()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.Exit(1);
        await Tick();
        After(1);
        await Tick();
        Assert.Equal(2, _factory.Started.Count);

        _factory.Last.Exit(1);
        await Tick();
        After(1);
        await Tick();
        Assert.Equal(2, _factory.Started.Count);
        After(3);
        await Tick();
        Assert.Equal(3, _factory.Started.Count);
    }

    [Fact]
    public async Task Restart_TakesAnotherFreshKey_AndTheWantedCameraAgain()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        var firstKey = _factory.Started[0].Command.RtmpStream;
        await Db(db => db.MatchSwitchers.Where(s => s.MatchId == setup.MatchId).ExecuteUpdateAsync(u => u.SetProperty(s => s.VideoInputId, setup.B.Id)));

        _factory.Last.Exit(1);
        await Tick();
        After(1);
        await Tick();

        Assert.NotEqual(firstKey, _factory.Started[1].Command.RtmpStream);
        Assert.Equal(setup.B.Id, _factory.Last.Sent.Single().Video);
    }

    [Fact]
    public async Task Hang_IsKilledAndRestarted()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        var hung = _factory.Last;
        hung.Produce();

        After(9);
        await Tick();
        Assert.False(hung.Stopped);

        After(2);
        await Tick();
        Assert.True(hung.Stopped);
        var row = await Row(setup.MatchId);
        Assert.Equal(SwitcherStatus.Restarting, row.Status);
        Assert.Contains("stopped producing", row.Error);
    }

    [Fact]
    public async Task LosingACamera_DoesNotTouchTheProcess()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.Produce();

        Signal(setup.A, on: false);
        After(1);
        await Tick();

        Assert.False(_factory.Last.Stopped);
        Assert.Single(_factory.Started);
    }

    [Fact]
    public async Task Steady_RunForgetsEarlierFailures()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.Exit(1);
        await Tick();
        After(1);
        await Tick();
        Assert.Equal(1, (await Row(setup.MatchId)).Attempts);

        _factory.Last.Produce();
        await Tick();
        var row = await Row(setup.MatchId);
        Assert.Equal(SwitcherStatus.Running, row.Status);
        Assert.Null(row.Error);
        Assert.Equal(1, row.Attempts);

        for (var i = 0; i < 7; i++)
        {
            After(9);
            _factory.Last.Produce();
            await Tick();
        }
        Assert.Equal(0, (await Row(setup.MatchId)).Attempts);
    }

    [Fact]
    public async Task EndedMatch_StopsItsSwitcher()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        var process = _factory.Last;

        await Db(db => db.Matches.Where(m => m.Id == setup.MatchId).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MatchStatus.Ended)));
        await Tick();

        Assert.True(process.Stopped);
        Assert.Equal(0, _supervisor.RunningCount);
    }

    [Fact]
    public async Task Disabling_StopsTheProcess_AndALateReportCantUndoIt()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        var process = _factory.Last;
        process.Produce();
        await Tick();

        await Db(async db =>
        {
            (await db.MatchSwitchers.SingleAsync(s => s.MatchId == setup.MatchId)).Stop();
            return await db.SaveChangesAsync();
        });
        await Tick();

        Assert.True(process.Stopped);
        var row = await Row(setup.MatchId);
        Assert.Equal(SwitcherStatus.Stopped, row.Status);
        Assert.False(row.Enabled);
    }

    [Fact]
    public async Task MediaMtxDown_LeavesARunningProgramAloneAndStartsNothing()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.Produce();
        _sources.Down = true;

        await Tick();

        Assert.False(_factory.Last.Stopped);
        Assert.Equal(SwitcherStatus.Running, (await Row(setup.MatchId)).Status);

        var other = await WantedSwitcher();
        Signal(other.A);
        await Tick();
        Assert.Single(_factory.Started);
    }

    [Fact]
    public async Task OneBrokenMatch_DoesNotHoldUpAnother()
    {
        var first = await WantedSwitcher();
        var second = await WantedSwitcher();
        Signal(first.A);
        Signal(second.A);
        _factory.Refuse = true;
        await Tick();
        Assert.Equal(SwitcherStatus.Restarting, (await Row(first.MatchId)).Status);
        Assert.Equal("The switcher could not be started on the broadcast service.", (await Row(first.MatchId)).Error);

        _factory.Refuse = false;
        After(2);
        await Tick();
        Assert.Equal(2, _factory.Started.Count);
    }

    [Fact]
    public async Task Slate_GoesInTheHistoryAsAutomatic_AndSoDoesTheCameraComingBack()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        var process = _factory.Last;
        process.OnAirVideo = setup.A.Id;
        process.Produce();
        await Tick();
        Assert.Single(await Cuts(setup.MatchId));
        Assert.Equal(setup.A.Id, (await Row(setup.MatchId)).OnAirVideoInputId);

        After(3);
        process.OnAirVideo = null;
        process.Produce();
        await Tick();
        After(3);
        process.OnAirVideo = setup.A.Id;
        process.Produce();
        await Tick();

        var cuts = await Cuts(setup.MatchId);
        Assert.Equal(3, cuts.Count);
        Assert.Equal((null, SwitcherCutOrigin.Automatic), (cuts[1].InputId, cuts[1].Origin));
        Assert.Equal((setup.A.Id, SwitcherCutOrigin.Automatic), (cuts[2].InputId, cuts[2].Origin));
    }

    [Fact]
    public async Task OperatorCut_ThatHasntTakenEffectYet_IsNotAnAutomaticOne()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.OnAirVideo = setup.A.Id;
        _factory.Last.Produce();
        await Tick();

        await Db(async db =>
        {
            db.SwitcherCuts.Add(new SwitcherCut { MatchId = setup.MatchId, Kind = SwitcherCutKind.Video, InputId = setup.B.Id, Origin = SwitcherCutOrigin.Operator, At = _clock.Now.UtcDateTime });
            return await db.SaveChangesAsync();
        });
        await Tick();

        Assert.Equal(2, (await Cuts(setup.MatchId)).Count);
    }

    [Fact]
    public async Task UnsupportedCodec_IsReportedByTheCamerasName()
    {
        var setup = await WantedSwitcher();
        Signal(setup.A);
        await Tick();
        _factory.Last.Produce();
        _factory.Last.Unsupported = new Dictionary<string, string> { [$"{setup.B.Id}:video"] = "H265" };

        await Tick();

        var warning = (await Row(setup.MatchId)).Warning;
        Assert.Contains("B sends video as H265", warning);
    }

    [Fact]
    public async Task MissingReadSecret_SaysSoRatherThanFailingOverAndOver()
    {
        _settings.MediaMtxReadSecret = "";
        var setup = await WantedSwitcher();
        Signal(setup.A);

        await Tick();

        Assert.Empty(_factory.Started);
        Assert.Contains("MEDIAMTX_READ_SECRET", (await Row(setup.MatchId)).Error);
    }
}
