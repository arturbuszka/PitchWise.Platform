using System.Diagnostics;

namespace PitchWise.Broadcast.Tests;

/// <summary>The process wrapper, with scripts standing in where a real destination would be needed.</summary>
public sealed class OutputProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pitchwise-output-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Script(string body)
    {
        var path = Path.Combine(_dir, $"ffmpeg-{Guid.NewGuid():N}");
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static FfmpegOutputProcess Start(string ffmpeg, params string[] secrets) =>
        new(ffmpeg, new OutputCommand("rtsp://source", "rtmp://destination", secrets), TimeProvider.System);

    private static async Task Until(Func<bool> condition)
    {
        var limit = Stopwatch.StartNew();
        while (!condition() && limit.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Progress_IsReadFromTheOutput()
    {
        var process = Start(Script("echo total_size=N/A\necho total_size=1000\necho total_size=2500\nexec sleep 30"));

        await Until(() => process.BytesSent == 2500);
        Assert.False(process.HasExited);

        await process.StopAsync();
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task AnExit_KeepsTheCodeAndTheErrorWithoutTheSecrets()
    {
        var process = Start(Script("""
            echo 'first line' >&2
            echo '[rtmp @ 0x55aa] Cannot open rtmp://host/live2/my-key' >&2
            echo 'rtsp://broadcast:p%40ss@mediamtx failed' >&2
            exit 224
            """), "my-key", "p@ss");

        await Until(() => process.HasExited);

        Assert.Equal(224, process.ExitCode);
        Assert.Equal("Cannot open rtmp://host/live2/*** rtsp://broadcast:***@mediamtx failed", process.LastError);
    }

    [Fact]
    public async Task Stop_AsksFirst()
    {
        var marker = Path.Combine(_dir, "finished");
        // Ends as ffmpeg does, on reading one "q".
        var process = Start(Script($"dd bs=1 count=1 of='{marker}' 2>/dev/null\nexit 0"));

        await process.StopAsync();

        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("q", File.ReadAllText(marker));
    }

    [Fact]
    public async Task Stop_KillsWhatIgnoresTheRequest()
    {
        var process = Start(Script("trap '' TERM\nexec sleep 300"));
        var started = Stopwatch.StartNew();

        await process.StopAsync();

        Assert.True(process.HasExited);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RealFfmpeg_AcceptsTheArgumentsAndReportsAFailureWithoutTheSecrets()
    {
        // Nothing listens on port 1. The point is that ffmpeg takes these options, and what it says about it.
        var process = new FfmpegOutputProcess("ffmpeg",
            new OutputCommand("rtsp://broadcast:read-secret@127.0.0.1:1/live/x", "rtmp://127.0.0.1:1/live2/stream-key", ["read-secret", "stream-key"]),
            TimeProvider.System);

        await Until(() => process.HasExited);

        Assert.NotEqual(0, process.ExitCode);
        Assert.NotNull(process.LastError);
        Assert.DoesNotContain("read-secret", process.LastError);
        Assert.DoesNotContain("Unrecognized option", process.LastError);
        Assert.DoesNotContain("Option not found", process.LastError);
    }

    [Fact]
    public void Arguments_CopyWithoutReencodingAndKeepTheAddressesOutOfTheOptions()
    {
        var arguments = FfmpegOutputProcess.Arguments(new OutputCommand("rtsp://s", "rtmp://d/k", [])).ToList();

        Assert.Equal("copy", arguments[arguments.IndexOf("-c") + 1]);
        Assert.Equal("rtsp://s", arguments[arguments.IndexOf("-i") + 1]);
        Assert.Equal("rtmp://d/k", arguments[^1]);
    }
}
