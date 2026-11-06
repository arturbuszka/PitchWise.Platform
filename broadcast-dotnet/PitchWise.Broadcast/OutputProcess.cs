using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PitchWise.Broadcast;

/// <summary>One running copy of a signal to a destination.</summary>
public interface IOutputProcess
{
    bool HasExited { get; }
    int ExitCode { get; }
    /// <summary>Bytes sent so far. It stops moving when the destination stops accepting them.</summary>
    long BytesSent { get; }
    DateTime LastProgressAt { get; }
    /// <summary>What ffmpeg last complained about, with every secret removed.</summary>
    string? LastError { get; }
    Task StopAsync();
}

public sealed record OutputCommand(string SourceUrl, string PublishUrl, IReadOnlyList<string> Secrets);

public interface IOutputProcessFactory
{
    IOutputProcess Start(OutputCommand command);
}

public sealed class FfmpegOutputFactory(BroadcastSettings settings, TimeProvider time) : IOutputProcessFactory
{
    public IOutputProcess Start(OutputCommand command) => new FfmpegOutputProcess(settings.FfmpegPath, command, time);
}

/// <summary>ffmpeg copying a signal over RTSP to an RTMP destination, without re-encoding.</summary>
public sealed partial class FfmpegOutputProcess : IOutputProcess
{
    private const int KeptErrorLines = 2;
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _secrets;
    private readonly Queue<string> _errors = new();
    private readonly Task _finished;
    private long _bytesSent;
    private long _lastProgressTicks;

    public FfmpegOutputProcess(string ffmpeg, OutputCommand command, TimeProvider time)
    {
        _time = time;
        _secrets = command.Secrets;
        _lastProgressTicks = time.GetUtcNow().UtcTicks;

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in Arguments(command)) info.ArgumentList.Add(argument);

        _process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg did not start");
        _finished = Task.WhenAll(ReadProgress(), ReadErrors(), _process.WaitForExitAsync());
    }

    public static IEnumerable<string> Arguments(OutputCommand command) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostats",
        // TCP, as MediaMTX offers no other way. The timeout is in microseconds, and covers a silent server.
        "-rtsp_transport", "tcp", "-timeout", "5000000",
        "-i", command.SourceUrl,
        "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy",
        // Progress is how a hung destination shows: ffmpeg itself just waits.
        "-progress", "pipe:1",
        "-f", "flv", "-flvflags", "no_duration_filesize",
        command.PublishUrl,
    ];

    public bool HasExited => _finished.IsCompleted;
    public int ExitCode => _process.ExitCode;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public DateTime LastProgressAt => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);

    public string? LastError
    {
        get
        {
            lock (_errors) return _errors.Count == 0 ? null : string.Join(" ", _errors);
        }
    }

    public async Task StopAsync()
    {
        if (HasExited) return;
        try
        {
            // ffmpeg's own way to finish, so that it closes the connection properly.
            await _process.StandardInput.WriteAsync("q");
            await _process.StandardInput.FlushAsync();
            await _finished.WaitAsync(StopGrace);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
            // A hung destination keeps ffmpeg from getting to the request above.
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It has just ended by itself.
            }
            await _finished;
        }
    }

    private async Task ReadProgress()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("total_size=")) continue;
            if (long.TryParse(line["total_size=".Length..], CultureInfo.InvariantCulture, out var size) && size > BytesSent)
            {
                Interlocked.Exchange(ref _bytesSent, size);
                Interlocked.Exchange(ref _lastProgressTicks, _time.GetUtcNow().UtcTicks);
            }
        }
    }

    private async Task ReadErrors()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            var clean = Clean(line, _secrets);
            if (clean.Length == 0) continue;
            lock (_errors)
            {
                _errors.Enqueue(clean);
                while (_errors.Count > KeptErrorLines) _errors.Dequeue();
            }
        }
    }

    /// <summary>ffmpeg quotes the addresses it was given, which contain the keys.</summary>
    public static string Clean(string line, IEnumerable<string> secrets)
    {
        foreach (var secret in secrets.Where(s => s.Length > 0))
            line = line.Replace(secret, "***").Replace(Uri.EscapeDataString(secret), "***");
        line = ComponentPrefix().Replace(line, "").Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    [GeneratedRegex(@"^\[[^\]]* @ 0x[0-9a-f]+\]\s*")]
    private static partial Regex ComponentPrefix();
}
