using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;
using PitchWise.Worker;
using Testcontainers.PostgreSql;

namespace PitchWise.Worker.Tests;

/// <summary>Real ffmpeg and a real Postgres. Segments are made in the shape MediaMTX writes them.</summary>
public sealed class RecordingRunnerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("docker.io/library/postgres:16").Build();
    private readonly string _storage = Path.Combine(Path.GetTempPath(), $"pitchwise-worker-tests-{Guid.NewGuid():N}");
    private ServiceProvider _services = null!;
    private AppSettings _settings = null!;
    private RecordingRunner _runner = null!;
    private static readonly DateTime Start = new(2026, 1, 1, 15, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _settings = new AppSettings { StorageDir = _storage };
        _settings.EnsureDirs();
        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(_postgres.GetConnectionString()))
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

        var worker = new WorkerSettings { RecordingSettleSeconds = 0.05, RecordingSettleLimitSeconds = 5 };
        _runner = new RecordingRunner(
            _services.GetRequiredService<IServiceScopeFactory>(), _settings, worker, NullLogger<RecordingRunner>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
        if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<(Match Match, MatchInput Program, MatchInput Wide)> EndedMatch() => Db(async db =>
    {
        var tenant = new Tenant { Name = "t" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var analysis = new AnalysisSession { TenantId = tenant.Id, Name = "m" };
        db.AnalysisSessions.Add(analysis);
        await db.SaveChangesAsync();
        var match = new Match
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "m", PublicId = Guid.NewGuid().ToString("N"),
            AnalysisId = analysis.Id, Status = MatchStatus.Ended, StartedAt = Start,
            RecordingStatus = RecordingStatus.Processing,
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();
        var program = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "Program", StreamKeyHash = "a" };
        var wide = new MatchInput { Id = Guid.NewGuid(), MatchId = match.Id, Name = "Wide", StreamKeyHash = "b" };
        db.MatchInputs.AddRange(program, wide);
        await db.SaveChangesAsync();
        match.ProgramInputId = program.Id;
        await db.SaveChangesAsync();
        return (match, program, wide);
    });

    private Task<Match> Reload(Guid id) => Db(db => db.Matches.AsNoTracking().SingleAsync(m => m.Id == id));

    private Task<Video?> VideoOf(Guid inputId) => Db(db => db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.MatchInputId == inputId));

    // A 2-second keyframe interval, audio and fragments, as an encoder sends and MediaMTX stores it.
    private void WriteSegment(Guid inputId, double startOffset, double length, string size = "320x180")
    {
        var dir = _settings.InputRecordingDir(inputId);
        Directory.CreateDirectory(dir);
        var at = Start.AddSeconds(startOffset);
        var seconds = (long)Math.Floor((at - DateTime.UnixEpoch).TotalSeconds);
        var micros = (at - DateTime.UnixEpoch.AddSeconds(seconds)).Ticks / 10;
        var path = Path.Combine(dir, $"{seconds}-{micros:D6}.mp4");
        Ffmpeg("-y", "-f", "lavfi", "-i", $"testsrc2=size={size}:rate=25", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", length.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "ultrafast", "-g", "50", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-movflags", "frag_keyframe+empty_moov+default_base_moof", path);
    }

    private static string Ffmpeg(params string[] args) => Tool("ffmpeg", args);

    private static string Tool(string name, params string[] args)
    {
        var psi = new ProcessStartInfo(name) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var errors = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, errors.Result);
        return output;
    }

    /// <summary>Video packet times of a file, which is what every consumer ends up going by.</summary>
    private static List<double> PacketTimes(string path, string stream = "v") =>
        Tool("ffprobe", "-v", "error", "-select_streams", stream, "-show_entries", "packet=dts_time", "-of", "csv=p=0", path)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.Parse(l.Trim().TrimEnd(','), CultureInfo.InvariantCulture))
            .ToList();

    [Fact]
    public async Task Gap_StaysInTheFile_AtItsPlaceOnTheTimeline()
    {
        var (match, program, _) = await EndedMatch();
        WriteSegment(program.Id, 0, 4);
        WriteSegment(program.Id, 4, 4);
        WriteSegment(program.Id, 20, 4);

        await _runner.RunAsync(match.Id, default);

        var video = (await VideoOf(program.Id))!;
        var gap = Assert.Single(video.Gaps);
        Assert.InRange(gap.Start, 7.8, 8.2);
        Assert.Equal(20, gap.End, 2);
        Assert.InRange(video.DurationSeconds!.Value, 23.8, 24.3);
        Assert.Equal(25, video.Fps!.Value, 1);

        var path = _settings.VideoPath(video);
        foreach (var stream in new[] { "v", "a" })
        {
            var times = PacketTimes(path, stream);
            var jumps = times.Zip(times.Skip(1)).Where(p => p.Second - p.First > 1).ToList();
            var jump = Assert.Single(jumps);
            Assert.InRange(jump.Second, 19.9, 20.2);
            Assert.All(times.Zip(times.Skip(1)), p => Assert.True(p.Second > p.First));
        }
        Assert.Equal(RecordingStatus.Ready, (await Reload(match.Id)).RecordingStatus);
        Assert.False(Directory.Exists(_settings.InputRecordingDir(program.Id)));
        Assert.StartsWith(_settings.VodDir, path);
    }

    [Fact]
    public async Task EveryInput_GetsItsOwnVideo_OnTheSameTimeline()
    {
        var (match, program, wide) = await EndedMatch();
        WriteSegment(program.Id, 0, 4);
        WriteSegment(wide.Id, 10, 4); // switched on late
        var earlyWide = await EndedMatch();
        WriteSegment(earlyWide.Wide.Id, -5, 12); // switched on early
        WriteSegment(earlyWide.Program.Id, 0, 4);

        await _runner.RunAsync(match.Id, default);
        await _runner.RunAsync(earlyWide.Match.Id, default);

        var late = (await VideoOf(wide.Id))!;
        var gap = Assert.Single(late.Gaps);
        Assert.Equal((0, 10.0), (gap.Start, Math.Round(gap.End, 2)));
        var lateTimes = PacketTimes(_settings.VideoPath(late));
        Assert.Equal(0, lateTimes[0], 1);
        Assert.Contains(lateTimes, t => Math.Abs(t - 10) < 0.1);
        Assert.InRange(late.DurationSeconds!.Value, 13.8, 14.3);

        // 12 s of recording, 5 of them before the start, cut at the keyframe at 6 s: at T=1.
        var early = (await VideoOf(earlyWide.Wide.Id))!;
        var earlyTimes = PacketTimes(_settings.VideoPath(early));
        Assert.InRange(early.DurationSeconds!.Value, 6.8, 7.3);
        Assert.Contains(earlyTimes, t => Math.Abs(t - 1) < 0.1);
    }

    [Fact]
    public async Task EventsMarkedLive_MoveToTheProgramVideo()
    {
        var (match, program, wide) = await EndedMatch();
        WriteSegment(program.Id, 0, 2);
        WriteSegment(wide.Id, 0, 2);
        var eventId = await Db(async db =>
        {
            var e = new Event { AnalysisId = match.AnalysisId, Type = EventType.Goal, Source = EventSource.Manual, TimestampSeconds = 1 };
            db.Events.Add(e);
            await db.SaveChangesAsync();
            return e.Id;
        });

        await _runner.RunAsync(match.Id, default);

        var ev = await Db(db => db.Events.AsNoTracking().SingleAsync(e => e.Id == eventId));
        Assert.Equal((await VideoOf(program.Id))!.Id, ev.VideoId);
    }

    [Fact]
    public async Task ChangedEncoderSettings_Fail_KeepingTheRawSegments_AndTheOtherInput()
    {
        var (match, program, wide) = await EndedMatch();
        WriteSegment(program.Id, 0, 2, "320x180");
        WriteSegment(program.Id, 10, 2, "640x360");
        WriteSegment(wide.Id, 0, 2);

        await _runner.RunAsync(match.Id, default);

        var after = await Reload(match.Id);
        Assert.Equal(RecordingStatus.Failed, after.RecordingStatus);
        Assert.Contains("Program", after.RecordingError);
        Assert.Contains("settings changed", after.RecordingError);
        Assert.Null(await VideoOf(program.Id));
        Assert.Equal(2, Directory.GetFiles(_settings.InputRecordingDir(program.Id)).Length);
        Assert.NotNull(await VideoOf(wide.Id));
    }

    [Fact]
    public async Task Retry_FinishesWhatFailed_AndLeavesWhatWorkedAlone()
    {
        var (match, program, wide) = await EndedMatch();
        WriteSegment(wide.Id, 0, 2);
        WriteSegment(program.Id, 0, 2, "320x180");
        WriteSegment(program.Id, 10, 2, "640x360");
        await _runner.RunAsync(match.Id, default);
        var wideVideo = (await VideoOf(wide.Id))!;

        // whoever looks into it removes the odd segment
        File.Delete(Directory.GetFiles(_settings.InputRecordingDir(program.Id)).Max()!);
        await _runner.RunAsync(match.Id, default);

        var after = await Reload(match.Id);
        Assert.Equal(RecordingStatus.Ready, after.RecordingStatus);
        Assert.Null(after.RecordingError);
        Assert.NotNull(await VideoOf(program.Id));
        Assert.Equal(wideVideo.Id, (await VideoOf(wide.Id))!.Id);
    }

    [Fact]
    public async Task UnreadableSegment_IsSkipped()
    {
        var (match, program, _) = await EndedMatch();
        WriteSegment(program.Id, 0, 2);
        var seconds = (long)(Start.AddSeconds(5) - DateTime.UnixEpoch).TotalSeconds;
        await File.WriteAllBytesAsync(Path.Combine(_settings.InputRecordingDir(program.Id), $"{seconds}-000000.mp4"), [0, 0, 0]);

        await _runner.RunAsync(match.Id, default);

        Assert.Equal(RecordingStatus.Ready, (await Reload(match.Id)).RecordingStatus);
        Assert.Empty((await VideoOf(program.Id))!.Gaps);
    }

    [Fact]
    public async Task MatchThatNeverReceivedASignal_HasNoRecording()
    {
        var (match, _, _) = await EndedMatch();

        await _runner.RunAsync(match.Id, default);

        Assert.Equal(RecordingStatus.None, (await Reload(match.Id)).RecordingStatus);
    }

    [Fact]
    public async Task MatchStillRunning_IsLeftAlone()
    {
        var (match, program, _) = await EndedMatch();
        WriteSegment(program.Id, 0, 2);
        await Db(async db =>
        {
            await db.Matches.Where(m => m.Id == match.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MatchStatus.Live));
            return 0;
        });

        await _runner.RunAsync(match.Id, default);

        Assert.Null(await VideoOf(program.Id));
        Assert.NotEmpty(Directory.GetFiles(_settings.InputRecordingDir(program.Id)));
    }
}
