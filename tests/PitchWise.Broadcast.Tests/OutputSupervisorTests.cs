using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PitchWise.Data;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;
using Testcontainers.PostgreSql;

namespace PitchWise.Broadcast.Tests;

/// <summary>One database for the class, as starting one per test is slow and heavy on a small machine.</summary>
public sealed class OutputDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("docker.io/library/postgres:16").Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>A real database, and fakes for the outside world, so that time and failures can be chosen.</summary>
public sealed class OutputSupervisorTests(OutputDatabase database) : IClassFixture<OutputDatabase>, IAsyncLifetime
{
    private static readonly string EncryptionKey = Convert.ToBase64String(new byte[32]);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeProcess(Clock clock) : IOutputProcess
    {
        public bool HasExited { get; set; }
        public int ExitCode { get; set; }
        public long BytesSent { get; set; }
        public DateTime LastProgressAt { get; set; } = clock.Now.UtcDateTime;
        public string? LastError { get; set; }
        public bool Stopped { get; private set; }

        public void Send() { BytesSent += 1000; LastProgressAt = clock.Now.UtcDateTime; }
        public void Exit(int code, string? error = null) { HasExited = true; ExitCode = code; LastError = error; }

        public Task StopAsync()
        {
            Stopped = true;
            HasExited = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Factory(Clock clock) : IOutputProcessFactory
    {
        public List<(OutputCommand Command, FakeProcess Process)> Started { get; } = [];
        public FakeProcess Last => Started[^1].Process;

        public IOutputProcess Start(OutputCommand command)
        {
            var process = new FakeProcess(clock);
            Started.Add((command, process));
            return process;
        }
    }

    private sealed class Sources : IOutputSources
    {
        public Dictionary<string, IReadOnlyList<string>> Signals { get; } = [];
        public bool Down { get; set; }
        public Func<Task>? Meanwhile { get; set; }

        public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken ct)
        {
            if (Meanwhile is not null) await Meanwhile();
            if (Down) throw new HttpRequestException("MediaMTX is down");
            return new Dictionary<string, IReadOnlyList<string>>(Signals);
        }
    }

    private sealed class Guard : ITargetGuard
    {
        public string? Problem { get; set; }
        public Task<string?> ProblemWithAsync(string url, CancellationToken ct) => Task.FromResult(Problem);
    }

    private readonly Clock _clock = new();
    private readonly Sources _sources = new();
    private readonly Guard _guard = new();
    private readonly BroadcastSettings _settings = new()
    {
        MediaMtxReadSecret = "read-secret",
        MediaMtxRtspUrl = "rtsp://mediamtx:8554",
        RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)],
    };
    private Factory _factory = null!;
    private ServiceProvider _services = null!;
    private OutputSupervisor _supervisor = null!;

    public async Task InitializeAsync()
    {
        _factory = new Factory(_clock);
        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .BuildServiceProvider();
        // The supervisor looks after every wanted output, so another test's would get in the way.
        await Db(async db =>
        {
            await db.Database.MigrateAsync();
            return await db.MatchOutputs.ExecuteDeleteAsync();
        });
        _supervisor = NewSupervisor(EncryptionKey);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
    }

    private OutputSupervisor NewSupervisor(string? encryptionKey) => new(
        _services.GetRequiredService<IServiceScopeFactory>(), _sources, _factory, _guard,
        new OutputKeyCipher(encryptionKey), _settings, _clock, NullLogger<OutputSupervisor>.Instance);

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<(Match Match, MatchInput Input, MatchOutput Output)> WantedOutput(string key = "stream-key") => Db(async db =>
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
        var input = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "i", StreamKeyHash = "h" };
        db.MatchInputs.Add(input);
        await db.SaveChangesAsync();
        match.ProgramInputId = input.Id;
        var output = new MatchOutput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "o", Url = "rtmp://destination/live" };
        output.StreamKeyEncrypted = new OutputKeyCipher(EncryptionKey).Encrypt(output.Id, key);
        output.Start(_clock.Now.UtcDateTime);
        db.MatchOutputs.Add(output);
        await db.SaveChangesAsync();
        return (match, input, output);
    });

    private Task<MatchOutput> Read(Guid id) => Db(db => db.MatchOutputs.AsNoTracking().SingleAsync(o => o.Id == id));

    private Task Change(Guid id, Action<MatchOutput> change) => Db(async db =>
    {
        change(await db.MatchOutputs.SingleAsync(o => o.Id == id));
        return await db.SaveChangesAsync();
    });

    private void Signal(MatchInput input, params string[] tracks) =>
        _sources.Signals[MatchInput.PathFor(input.Id)] = tracks.Length > 0 ? tracks : ["H264", "MPEG-4 Audio"];

    private Task Tick() => _supervisor.TickAsync(default);

    private void Pass(double seconds) => _clock.Now = _clock.Now.AddSeconds(seconds);

    [Fact]
    public async Task ASignal_StartsACopyOfIt_ThatIsLiveOnceDataMoves()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);

        await Tick();

        var (command, process) = Assert.Single(_factory.Started);
        Assert.Equal($"rtsp://broadcast:read-secret@mediamtx:8554/live/{input.Id}", command.SourceUrl);
        Assert.Equal("rtmp://destination/live/stream-key", command.PublishUrl);
        Assert.Contains("stream-key", command.Secrets);
        Assert.Contains("read-secret", command.Secrets);
        Assert.Equal(OutputStatus.Connecting, (await Read(output.Id)).Status);

        process.Send();
        await Tick();

        var live = await Read(output.Id);
        Assert.Equal(OutputStatus.Live, live.Status);
        Assert.NotNull(live.LiveSince);
        Assert.Single(_factory.Started);
    }

    [Fact]
    public async Task NoSignal_MeansWaitingAndCostsNothing()
    {
        var (_, _, output) = await WantedOutput();

        await Tick();

        var waiting = await Read(output.Id);
        Assert.Equal(OutputStatus.WaitingForSignal, waiting.Status);
        Assert.Equal(0, waiting.Attempts);
        Assert.Empty(_factory.Started);
    }

    [Fact]
    public async Task LosingTheSignal_IsNotAFailure_AndItsReturnStartsAgain()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        _factory.Last.Send();
        await Tick();

        // ffmpeg ends with 0 when its input ends.
        _sources.Signals.Clear();
        _factory.Last.Exit(0);
        await Tick();

        var waiting = await Read(output.Id);
        Assert.Equal(OutputStatus.WaitingForSignal, waiting.Status);
        Assert.Equal(0, waiting.Attempts);
        Assert.Null(waiting.Error);

        Signal(input);
        await Tick();

        Assert.Equal(2, _factory.Started.Count);
    }

    [Fact]
    public async Task AnErrorOnTheWayOut_DoesNotCountWhenTheSignalWentToo()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();

        _sources.Signals.Clear();
        _factory.Last.Exit(1, "Broken pipe");
        await Tick();

        var waiting = await Read(output.Id);
        Assert.Equal(OutputStatus.WaitingForSignal, waiting.Status);
        Assert.Equal(0, waiting.Attempts);
    }

    [Fact]
    public async Task Failures_RetryWithGrowingDelays_ThenGiveUpUntilAsked()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();

        _factory.Last.Exit(224, "Connection refused");
        await Tick();
        var retrying = await Read(output.Id);
        Assert.Equal(OutputStatus.Retrying, retrying.Status);
        Assert.Equal(1, retrying.Attempts);
        Assert.Equal("Connection refused", retrying.Error);
        Assert.Equal(_clock.Now.UtcDateTime.AddSeconds(2), retrying.NextRetryAt);

        // Not before its time.
        Pass(1);
        await Tick();
        Assert.Single(_factory.Started);

        Pass(1);
        await Tick();
        Assert.Equal(2, _factory.Started.Count);

        _factory.Last.Exit(224, "Connection refused");
        await Tick();
        retrying = await Read(output.Id);
        Assert.Equal(2, retrying.Attempts);
        Assert.Equal(_clock.Now.UtcDateTime.AddSeconds(4), retrying.NextRetryAt);

        Pass(4);
        await Tick();
        Assert.Equal(3, _factory.Started.Count);

        _factory.Last.Exit(224, "Connection refused");
        await Tick();
        var failed = await Read(output.Id);
        Assert.Equal(OutputStatus.Failed, failed.Status);
        Assert.Equal("Connection refused", failed.Error);
        Assert.Null(failed.NextRetryAt);

        Pass(3600);
        await Tick();
        Assert.Equal(3, _factory.Started.Count);

        await Change(output.Id, o => o.Start(_clock.Now.UtcDateTime));
        await Tick();
        Assert.Equal(4, _factory.Started.Count);
        Assert.Equal(0, (await Read(output.Id)).Attempts);
    }

    [Fact]
    public async Task RunningSteadily_ForgetsEarlierFailures()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        _factory.Last.Exit(224, "Connection refused");
        await Tick();
        Pass(2);
        await Tick();

        _factory.Last.Send();
        await Tick();
        Assert.Equal(1, (await Read(output.Id)).Attempts);

        Pass(30);
        _factory.Last.Send();
        await Tick();
        Assert.Equal(1, (await Read(output.Id)).Attempts);

        Pass(30);
        _factory.Last.Send();
        await Tick();
        var steady = await Read(output.Id);
        Assert.Equal(OutputStatus.Live, steady.Status);
        Assert.Equal(0, steady.Attempts);
    }

    [Fact]
    public async Task AHungDestination_IsKilledAndRetried()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        var hung = _factory.Last;
        hung.Send();
        await Tick();

        Pass(14);
        await Tick();
        Assert.False(hung.Stopped);

        Pass(2);
        await Tick();

        Assert.True(hung.Stopped);
        var retrying = await Read(output.Id);
        Assert.Equal(OutputStatus.Retrying, retrying.Status);
        Assert.Equal("The destination stopped accepting data.", retrying.Error);
    }

    [Fact]
    public async Task ADestinationThatNeverAnswers_IsAlsoReported()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();

        Pass(16);
        await Tick();

        Assert.Equal("The destination didn't accept the connection in time.", (await Read(output.Id)).Error);
    }

    [Fact]
    public async Task AnotherInputOnAir_RestartsTheCopyWithoutCountingAsAFailure()
    {
        var (match, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        var first = _factory.Last;
        first.Send();
        await Tick();

        var other = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "wide", StreamKeyHash = "h2" };
        await Db(async db =>
        {
            db.MatchInputs.Add(other);
            await db.SaveChangesAsync();
            var row = await db.Matches.SingleAsync(m => m.Id == match.Id);
            row.ProgramInputId = other.Id;
            return await db.SaveChangesAsync();
        });
        Signal(other);
        await Tick();

        Assert.True(first.Stopped);
        Assert.Contains($"/live/{other.Id}", _factory.Started[^1].Command.SourceUrl);
        Assert.Equal(0, (await Read(output.Id)).Attempts);
    }

    [Fact]
    public async Task AnUnsendableSignal_IsReportedAndNeverStarted_UntilItChanges()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input, "H265", "MPEG-4 Audio");

        await Tick();
        Pass(600);
        await Tick();

        var refused = await Read(output.Id);
        Assert.Equal(OutputStatus.Incompatible, refused.Status);
        Assert.Contains("H265", refused.Error);
        Assert.Equal(0, refused.Attempts);
        Assert.Empty(_factory.Started);

        Signal(input);
        await Tick();

        Assert.Single(_factory.Started);
        var started = await Read(output.Id);
        Assert.Equal(OutputStatus.Connecting, started.Status);
        Assert.Null(started.Error);
    }

    [Fact]
    public async Task AChangedEncryptionKey_FailsThatOutputAndNotTheOthers()
    {
        var (_, input, output) = await WantedOutput();
        var (_, otherInput, other) = await WantedOutput();
        Signal(input);
        Signal(otherInput);
        var otherKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());
        await Change(other.Id, o => o.StreamKeyEncrypted = new OutputKeyCipher(otherKey).Encrypt(o.Id, "k"));

        await Tick();

        var failed = await Read(other.Id);
        Assert.Equal(OutputStatus.Failed, failed.Status);
        Assert.Contains("Enter the destination's key again", failed.Error);
        Assert.Equal(OutputStatus.Connecting, (await Read(output.Id)).Status);
        Assert.Single(_factory.Started);
    }

    [Fact]
    public async Task NoEncryptionKey_FailsWithItsName()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);

        await NewSupervisor(null).TickAsync(default);

        var failed = await Read(output.Id);
        Assert.Equal(OutputStatus.Failed, failed.Status);
        Assert.Contains("OUTPUT_ENCRYPTION_KEY", failed.Error);
    }

    [Fact]
    public async Task APrivateDestination_IsRefusedWithoutRetrying()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        _guard.Problem = "That address is on a private network, which outputs can't send to.";

        await Tick();

        Assert.Equal(OutputStatus.Failed, (await Read(output.Id)).Status);
        Assert.Empty(_factory.Started);
    }

    [Fact]
    public async Task OneOutputFailing_LeavesTheOtherAlone()
    {
        var (_, input, broken) = await WantedOutput("broken");
        var (_, otherInput, healthy) = await WantedOutput("healthy");
        Signal(input);
        Signal(otherInput);
        await Tick();
        var brokenProcess = _factory.Started.Single(s => s.Command.PublishUrl.EndsWith("broken")).Process;
        var healthyProcess = _factory.Started.Single(s => s.Command.PublishUrl.EndsWith("healthy")).Process;
        healthyProcess.Send();

        brokenProcess.Exit(224, "Connection refused");
        await Tick();

        Assert.Equal(OutputStatus.Retrying, (await Read(broken.Id)).Status);
        Assert.Equal(OutputStatus.Live, (await Read(healthy.Id)).Status);
        Assert.False(healthyProcess.Stopped);
    }

    [Fact]
    public async Task Stopping_EndsTheProcess()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        var process = _factory.Last;

        await Change(output.Id, o => o.Stop());
        await Tick();

        Assert.True(process.Stopped);
        Assert.Equal(0, _supervisor.RunningCount);
        Assert.Equal(OutputStatus.Stopped, (await Read(output.Id)).Status);
    }

    [Fact]
    public async Task EndingTheMatch_EndsTheProcessEvenIfTheOutputIsStillWanted()
    {
        var (match, input, _) = await WantedOutput();
        Signal(input);
        await Tick();
        var process = _factory.Last;

        await Db(async db =>
        {
            (await db.Matches.SingleAsync(m => m.Id == match.Id)).Status = MatchStatus.Ended;
            return await db.SaveChangesAsync();
        });
        await Tick();

        Assert.True(process.Stopped);
    }

    [Fact]
    public async Task ANewKey_RestartsTheCopyWithIt()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        var first = _factory.Last;

        await Change(output.Id, o =>
        {
            o.StreamKeyEncrypted = new OutputKeyCipher(EncryptionKey).Encrypt(o.Id, "new-key");
            o.Start(_clock.Now.UtcDateTime);
        });
        await Tick();

        Assert.True(first.Stopped);
        Assert.Equal("rtmp://destination/live/new-key", _factory.Started[^1].Command.PublishUrl);
    }

    [Fact]
    public async Task MediaMtxBeingDown_LeavesWhatIsRunningAloneAndCountsNothing()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        var running = _factory.Last;
        running.Send();
        await Tick();

        _sources.Down = true;
        await Tick();
        Assert.False(running.Stopped);
        Assert.Equal(OutputStatus.Live, (await Read(output.Id)).Status);

        running.Exit(1, "Connection reset");
        await Tick();

        Assert.Equal(0, (await Read(output.Id)).Attempts);
        Assert.Single(_factory.Started);
    }

    [Fact]
    public async Task ARestartedService_PicksUpWhatIsWantedAndKeepsItsCount()
    {
        var (_, input, output) = await WantedOutput();
        var (_, givenUpInput, givenUp) = await WantedOutput();
        Signal(input);
        Signal(givenUpInput);
        await Change(givenUp.Id, o => { o.Status = OutputStatus.Failed; o.Error = "gave up"; });
        await Tick();
        _factory.Last.Send();
        await Tick();
        await Change(output.Id, o => { o.Attempts = 1; });
        var startedBefore = _factory.Started.Count;

        _supervisor = NewSupervisor(EncryptionKey);
        await Tick();

        Assert.Equal(startedBefore + 1, _factory.Started.Count);
        Assert.Equal(1, (await Read(output.Id)).Attempts);
        var stillFailed = await Read(givenUp.Id);
        Assert.Equal(OutputStatus.Failed, stillFailed.Status);
        Assert.Equal("gave up", stillFailed.Error);
    }

    [Fact]
    public async Task AReportAboutOldSettings_DoesNotOverwriteNewOnes()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        // Someone stops the output after this tick has read it, but before it reports.
        _sources.Meanwhile = () => Change(output.Id, o => o.Stop());

        await Tick();

        var row = await Read(output.Id);
        Assert.Equal(OutputStatus.Stopped, row.Status);
        Assert.False(row.Enabled);

        // The copy that tick started doesn't outlive the stop either.
        _sources.Meanwhile = null;
        var started = _factory.Last;
        await Tick();
        Assert.True(started.Stopped);
    }

    [Fact]
    public async Task Heartbeat_IsWrittenSoTheApiCanTellTheServiceIsAlive()
    {
        var (_, input, output) = await WantedOutput();
        Signal(input);
        await Tick();
        Pass(5);

        await Tick();

        Assert.Equal(_clock.Now.UtcDateTime, (await Read(output.Id)).LastSeenAt);
    }
}
