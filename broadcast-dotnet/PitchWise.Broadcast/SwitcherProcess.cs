using System.Diagnostics;
using System.Text.Json;

namespace PitchWise.Broadcast;

/// <summary>The settings a switcher starts with. Sent over stdin, as the key and secret mustn't show in a process list.</summary>
public sealed record SwitcherCommand(
    int Width, int Height, int VideoKbps, string RtmpHost, int RtmpPort, string RtmpApplication, string RtmpStream,
    string RtspUser, string RtspPassword, IReadOnlyList<string> Secrets);

/// <summary>What a switcher is asked to do. It takes the whole state each time, so a lost message costs nothing.</summary>
public sealed record SwitcherState(IReadOnlyList<SwitcherSource> Inputs, Guid Video, Guid Audio);

public sealed record SwitcherSource(Guid Id, string Url);

/// <summary>One running switcher: the process that makes one match's program.</summary>
public interface ISwitcherProcess
{
    bool HasExited { get; }
    int ExitCode { get; }
    /// <summary>Frames sent to the encoder so far. It stops moving if the process hangs or the program can't get out.</summary>
    long Frames { get; }
    DateTime LastProgressAt { get; }
    /// <summary>What it last complained about, with every secret removed.</summary>
    string? LastError { get; }
    /// <summary>Null means the slate.</summary>
    Guid? OnAirVideo { get; }
    Guid? OnAirAudio { get; }
    /// <summary>Tracks it can't take, by input and kind, with their codec.</summary>
    IReadOnlyDictionary<string, string> Unsupported { get; }
    Task SendAsync(SwitcherState state);
    Task StopAsync();
}

public interface ISwitcherProcessFactory
{
    ISwitcherProcess Start(SwitcherCommand command);
}

public sealed class PythonSwitcherFactory(BroadcastSettings settings, TimeProvider time) : ISwitcherProcessFactory
{
    public ISwitcherProcess Start(SwitcherCommand command) =>
        new PythonSwitcherProcess(settings.PythonPath, Path.Combine(AppContext.BaseDirectory, settings.SwitcherScript), command, time);
}

/// <summary>broadcast-dotnet/switcher/switcher.py, which holds the GStreamer pipeline. See that file for the protocol.</summary>
public sealed class PythonSwitcherProcess : ISwitcherProcess
{
    private const int KeptErrorLines = 2;
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly Process _process;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<string> _secrets;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Queue<string> _errors = new();
    private readonly Task _finished;
    private long _frames;
    private long _lastProgressTicks;
    private volatile IReadOnlyDictionary<string, string> _unsupported = new Dictionary<string, string>();
    private readonly object _onAirLock = new();
    private Guid? _onAirVideo;
    private Guid? _onAirAudio;

    public PythonSwitcherProcess(string python, string script, SwitcherCommand command, TimeProvider time)
    {
        _time = time;
        _secrets = command.Secrets;
        _lastProgressTicks = time.GetUtcNow().UtcTicks;

        var info = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(script);
        _process = Process.Start(info) ?? throw new InvalidOperationException("the switcher did not start");
        _finished = Task.WhenAll(ReadEvents(), ReadErrors(), _process.WaitForExitAsync());

        _process.StandardInput.WriteLine(JsonSerializer.Serialize(new
        {
            width = command.Width,
            height = command.Height,
            video_kbps = command.VideoKbps,
            rtmp_host = command.RtmpHost,
            rtmp_port = command.RtmpPort,
            rtmp_application = command.RtmpApplication,
            rtmp_stream = command.RtmpStream,
            rtsp_user = command.RtspUser,
            rtsp_password = command.RtspPassword,
        }));
        _process.StandardInput.Flush();
    }

    public bool HasExited => _finished.IsCompleted;
    public int ExitCode => _process.ExitCode;
    public long Frames => Interlocked.Read(ref _frames);
    public DateTime LastProgressAt => new(Interlocked.Read(ref _lastProgressTicks), DateTimeKind.Utc);
    public IReadOnlyDictionary<string, string> Unsupported => _unsupported;

    public Guid? OnAirVideo
    {
        get { lock (_onAirLock) return _onAirVideo; }
    }

    public Guid? OnAirAudio
    {
        get { lock (_onAirLock) return _onAirAudio; }
    }

    public string? LastError
    {
        get
        {
            lock (_errors) return _errors.Count == 0 ? null : string.Join(" ", _errors);
        }
    }

    public async Task SendAsync(SwitcherState state)
    {
        var line = JsonSerializer.Serialize(new
        {
            inputs = state.Inputs.Select(i => new { id = i.Id.ToString(), url = i.Url }),
            video = state.Video.ToString(),
            audio = state.Audio.ToString(),
        });
        await _writing.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(line);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // It has gone, which the supervisor finds out from HasExited.
        }
        finally
        {
            _writing.Release();
        }
    }

    public async Task StopAsync()
    {
        if (HasExited) return;
        try
        {
            // Closing its input is what ends it, the same as when this service dies.
            _process.StandardInput.Close();
            await _finished.WaitAsync(StopGrace);
        }
        catch (Exception e) when (e is TimeoutException or IOException or InvalidOperationException)
        {
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

    private async Task ReadEvents()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            try
            {
                Handle(line);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // A line it didn't mean as an event, such as something a library printed.
            }
        }
    }

    private void Handle(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        switch (root.GetProperty("type").GetString())
        {
            case "status":
                var frames = root.GetProperty("frames").GetInt64();
                if (frames > Frames)
                {
                    Interlocked.Exchange(ref _frames, frames);
                    Interlocked.Exchange(ref _lastProgressTicks, _time.GetUtcNow().UtcTicks);
                }
                _unsupported = root.GetProperty("unsupported").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
                break;
            case "on_air":
                var input = root.GetProperty("input");
                Guid? source = input.ValueKind == JsonValueKind.Null ? null : Guid.Parse(input.GetString()!);
                lock (_onAirLock)
                {
                    if (root.GetProperty("kind").GetString() == "audio") _onAirAudio = source;
                    else _onAirVideo = source;
                }
                break;
        }
    }

    private async Task ReadErrors()
    {
        while (await _process.StandardError.ReadLineAsync() is { } line)
        {
            // A camera coming and going is normal, and must not read as the reason for a restart.
            if (!line.StartsWith("[switcher] fatal", StringComparison.Ordinal)) continue;
            var clean = FfmpegOutputProcess.Clean(line["[switcher] ".Length..], _secrets);
            lock (_errors)
            {
                _errors.Enqueue(clean);
                while (_errors.Count > KeptErrorLines) _errors.Dequeue();
            }
        }
    }
}
