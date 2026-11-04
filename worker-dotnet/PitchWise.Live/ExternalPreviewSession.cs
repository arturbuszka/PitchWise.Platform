using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCvSharp;
using PitchWise.Vision;

namespace PitchWise.Live;

/// <summary>
/// Live HLS analysis from an external URL (YouTube/Twitch/HLS/RTMP).
///
/// Pipeline: yt-dlp -g → OpenCvSharp VideoCapture (decode thread) → optional YOLO
/// detect + overlay → ffmpeg (raw BGR pipe → H.264 → HLS segments) served by this app.
///
/// WS in:  {type:"start", source_url} | {type:"toggle", layer, on} |
///         {type:"calibrate", points:[{pixel:[x,y], pitch:[px,py]}]} | {type:"stop"}
/// WS out: {type:"ready", hls_url} | {type:"positions", ...} | {type:"tip", text} |
///         {type:"stats", fps, infer_ms, counts} | {type:"error", message}
/// </summary>
public sealed class ExternalPreviewSession
{
    private const int HlsSegmentSeconds = 2;
    private const int HlsListSize = 10;
    private const double EncodeFpsCap = 25.0;
    private const int FrameQueueSize = 8;

    private readonly string _sessionId;
    private readonly WebSocket _ws;
    private readonly LiveSettings _settings;
    private readonly SharedDetector _sharedDetector;
    private readonly TacticalTip _tip;
    private readonly string _outDir;

    private readonly OverlayFlags _flags = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _sourceUrl;
    private Homography? _homography;
    private (int w, int h) _frameSize;
    private readonly StatsTracker _stats = new();

    public ExternalPreviewSession(
        string sessionId, WebSocket ws, LiveSettings settings,
        SharedDetector sharedDetector, TacticalTip tip)
    {
        _sessionId = sessionId;
        _ws = ws;
        _settings = settings;
        _sharedDetector = sharedDetector;
        _tip = tip;
        _outDir = Path.Combine(settings.HlsBaseDir, sessionId);
        Directory.CreateDirectory(_outDir);
    }

    // ------------------------------------------------------------------
    // Public entry point
    // ------------------------------------------------------------------

    public async Task RunAsync()
    {
        // Control channel runs concurrently with the stream loop.
        Task control = ControlLoop();
        try
        {
            // Wait (max 60s) for the 'start' message that sets _sourceUrl.
            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(startTimeout.Token, _stop.Token);
            try
            {
                while (_sourceUrl is null && !_stop.IsCancellationRequested)
                    await Task.Delay(100, linked.Token);
            }
            catch (OperationCanceledException) when (startTimeout.IsCancellationRequested)
            {
                await SendError("No start message received within 60s");
                return;
            }

            if (_sourceUrl is not null)
                await StreamLoop();
        }
        catch (Exception ex)
        {
            await SendError(ex.Message);
        }
        finally
        {
            _stop.Cancel();
            try { await control; } catch { /* ignore */ }
        }
    }

    public void Cleanup()
    {
        _stop.Cancel();
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // Control loop (WS text in)
    // ------------------------------------------------------------------

    private async Task ControlLoop()
    {
        var buf = new byte[8192];
        try
        {
            while (!_stop.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult res = await _ws.ReceiveAsync(buf, _stop.Token);
                if (res.MessageType == WebSocketMessageType.Close) break;

                string text = Encoding.UTF8.GetString(buf, 0, res.Count);
                JsonElement msg;
                try { msg = JsonDocument.Parse(text).RootElement; }
                catch (JsonException) { continue; }

                string type = msg.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "start":
                        _sourceUrl = msg.TryGetProperty("source_url", out JsonElement su) ? su.GetString() : "";
                        break;
                    case "toggle":
                        HandleToggle(msg);
                        break;
                    case "calibrate":
                        HandleCalibrate(msg);
                        break;
                    case "stop":
                        _stop.Cancel();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { _stop.Cancel(); }
    }

    private void HandleToggle(JsonElement msg)
    {
        string layer = msg.TryGetProperty("layer", out JsonElement l) ? l.GetString() ?? "" : "";
        bool on = !msg.TryGetProperty("on", out JsonElement o) || o.GetBoolean();
        switch (layer)
        {
            case "boxes": _flags.Boxes = on; break;
            case "labels": _flags.Labels = on; break;
            case "traces": _flags.Traces = on; break;
        }
    }

    private void HandleCalibrate(JsonElement msg)
    {
        if (!msg.TryGetProperty("points", out JsonElement pts) || pts.ValueKind != JsonValueKind.Array) return;
        var pixel = new List<(double, double)>();
        var pitch = new List<(double, double)>();
        foreach (JsonElement p in pts.EnumerateArray())
        {
            if (p.TryGetProperty("pixel", out JsonElement px) && p.TryGetProperty("pitch", out JsonElement pt))
            {
                pixel.Add((px[0].GetDouble(), px[1].GetDouble()));
                pitch.Add((pt[0].GetDouble(), pt[1].GetDouble()));
            }
        }
        if (pixel.Count >= 4)
        {
            try { _homography = Homography.FromPoints(pixel, pitch); }
            catch { /* invalid calibration ignored */ }
        }
    }

    /// <summary>Scales the [0,1] normalized fallback (no calibration yet) up to pitch units
    /// (105m x 68m) so it lines up with calibrated <see cref="Homography.Project"/> output —
    /// the frontend always divides pitch_x/pitch_y by the real pitch dimensions.</summary>
    private static (double x, double y) ScaleNormalizedToPitch((double x, double y) normalized) =>
        (normalized.x * 105.0, normalized.y * 68.0);

    // ------------------------------------------------------------------
    // Main streaming loop
    // ------------------------------------------------------------------

    private async Task StreamLoop()
    {
        if (string.IsNullOrEmpty(_sourceUrl))
        {
            await SendError("No source URL provided");
            return;
        }

        string? streamUrl = ResolveUrl(_sourceUrl);
        if (streamUrl is null)
        {
            await SendError($"Could not resolve stream URL: {_sourceUrl}");
            return;
        }

        // Some CDNs mask HLS segment URLs behind non-standard extensions (.png, .ts.m3u8,
        // signed query strings, ...). ffmpeg's HLS demuxer rejects those by default
        // ("not in allowed_segment_extensions"). OpenCvSharp's ffmpeg backend only takes
        // demuxer options via this env var (no C++ API for it) — set it right before
        // opening so it applies to this capture's ffmpeg instance.
        Environment.SetEnvironmentVariable(
            "OPENCV_FFMPEG_CAPTURE_OPTIONS",
            "allowed_extensions;ALL|protocol_whitelist;file,http,https,tcp,tls,crypto");

        using var cap = new VideoCapture(streamUrl);
        if (!cap.IsOpened())
        {
            await SendError($"Cannot open stream: {streamUrl}");
            return;
        }

        double sourceFps = cap.Get(VideoCaptureProperties.Fps);
        if (sourceFps <= 0 || double.IsNaN(sourceFps)) sourceFps = 25.0;
        int w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
        int h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
        double fps = Math.Min(sourceFps, EncodeFpsCap);

        if (_settings.MaxWidth > 0 && w > _settings.MaxWidth)
        {
            double scale = _settings.MaxWidth / (double)w;
            w = (int)(w * scale);
            h = (int)(h * scale);
        }
        _frameSize = (w, h);

        foreach (string f in Directory.GetFiles(_outDir)) { try { File.Delete(f); } catch { } }

        Process? ffmpeg = StartEncodeFfmpeg(w, h, fps);
        if (ffmpeg is null)
        {
            await SendError("ffmpeg encode process failed to start");
            return;
        }

        string mode = (_settings.LivePipelineMode ?? "passthrough").Trim().ToLowerInvariant();
        try
        {
            if (mode == "detect") await RunDetect(cap, ffmpeg, w, h);
            else await RunPassthrough(cap, ffmpeg, w, h);
        }
        finally
        {
            _stop.Cancel();
            KillFfmpeg(ffmpeg);
        }
    }

    private async Task<bool> SendReadyIfNeeded(bool alreadySent, string playlistPath)
    {
        if (alreadySent) return true;
        if (File.Exists(playlistPath) && new FileInfo(playlistPath).Length > 0)
        {
            await SendJson(new { type = "ready", hls_url = $"/live_hls/{_sessionId}/index.m3u8" });
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // Pipeline: passthrough (raw frames, no analysis)
    // ------------------------------------------------------------------

    private async Task RunPassthrough(VideoCapture cap, Process ffmpeg, int w, int h)
    {
        string playlist = Path.Combine(_outDir, "index.m3u8");
        bool ready = false;
        using var frame = new Mat();
        while (!_stop.IsCancellationRequested)
        {
            bool ok = await Task.Run(() => cap.Read(frame));
            if (!ok || frame.Empty()) break;

            if (frame.Width != w || frame.Height != h)
                Cv2.Resize(frame, frame, new Size(w, h));

            if (!WriteFrame(ffmpeg, frame)) break;
            ready = await SendReadyIfNeeded(ready, playlist);
        }
    }

    // ------------------------------------------------------------------
    // Pipeline: detect (YOLO + overlay on every frame)
    // ------------------------------------------------------------------

    private async Task RunDetect(VideoCapture cap, Process ffmpeg, int w, int h)
    {
        Detector detector = _sharedDetector.Get();
        string playlist = Path.Combine(_outDir, "index.m3u8");
        bool ready = false;
        int frameIdx = 0;
        using var frame = new Mat();
        while (!_stop.IsCancellationRequested)
        {
            bool ok = await Task.Run(() => cap.Read(frame));
            if (!ok || frame.Empty()) break;

            if (frame.Width != w || frame.Height != h)
                Cv2.Resize(frame, frame, new Size(w, h));

            double ts = frameIdx / EncodeFpsCap;
            long t0 = Stopwatch.GetTimestamp();
            FrameResult fr = await Task.Run(() => detector.DetectFrame(frame, frameIdx, ts));
            if (_flags.AnyOverlay()) Overlay.Draw(frame, fr, _flags);
            double inferMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            var counts = new Dictionary<string, int>();
            foreach (Detection d in fr.Detections)
                counts[d.Cls] = counts.GetValueOrDefault(d.Cls) + 1;
            _stats.Record(inferMs, counts);

            var players = fr.Detections.Select(d =>
            {
                (double px, double py) = _homography is not null
                    ? _homography.Project(d.X1, d.Y1, d.X2, d.Y2)
                    : ScaleNormalizedToPitch(Homography.PixelToNormalized(d.X1, d.Y1, d.X2, d.Y2, w, h));
                return new { track_id = d.TrackId, cls = d.Cls, pitch_x = px, pitch_y = py };
            }).ToList();
            await SendJson(new { type = "positions", players, timestamp = ts });

            if (!WriteFrame(ffmpeg, frame)) break;

            frameIdx++;
            if (frameIdx % 30 == 0)
                await SendJson(new
                {
                    type = "stats",
                    fps = Math.Round(_stats.Fps(), 1),
                    infer_ms = Math.Round(inferMs, 1),
                    counts,
                });

            ready = await SendReadyIfNeeded(ready, playlist);
        }
    }

    private bool WriteFrame(Process ffmpeg, Mat frame)
    {
        try
        {
            // ffmpeg expects tightly-packed BGR24. AsSpan needs a continuous Mat; a Mat
            // from VideoCapture.Read / Resize normally is, but clone if it isn't.
            if (frame.IsContinuous())
            {
                ffmpeg.StandardInput.BaseStream.Write(frame.AsSpan<byte>());
            }
            else
            {
                using Mat cont = frame.Clone();
                ffmpeg.StandardInput.BaseStream.Write(cont.AsSpan<byte>());
            }
            return true;
        }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
    }

    // ------------------------------------------------------------------
    // URL resolver: yt-dlp -g for social media, passthrough for direct HLS/RTMP
    // ------------------------------------------------------------------

    private string? ResolveUrl(string url)
    {
        string lowered = url.ToLowerInvariant();
        // Look at the PATH only (drop the query string) so a tokenised HLS URL like
        // ".../master.m3u8?tcstring=...&dvr=..." is still recognised as direct HLS —
        // otherwise EndsWith(".m3u8") is false and it wrongly goes to yt-dlp.
        string path = lowered.Split('?', 2)[0];
        // Direct stream URLs and existing local files bypass yt-dlp.
        if (lowered.StartsWith("rtmp") || path.EndsWith(".m3u8") ||
            path.EndsWith(".ts") || path.EndsWith(".mpd") ||
            lowered.Contains("manifest") || lowered.Contains(".m3u8") ||
            File.Exists(url))
            return url;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _settings.YtDlpPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
            {
                "-g", "--no-playlist",
                "-f", "best[protocol=m3u8_native]/best[protocol=https]/best",
                url,
            }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return url;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            if (proc.ExitCode == 0)
            {
                string? first = stdout.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0);
                if (first is not null) return first;
            }
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // yt-dlp not found — try the URL directly.
            return url;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    // ffmpeg encode: raw BGR frames → HLS segments
    // ------------------------------------------------------------------

    private Process? StartEncodeFfmpeg(int w, int h, double fps)
    {
        string playlist = Path.Combine(_outDir, "index.m3u8");
        string segPattern = Path.Combine(_outDir, "seg_%05d.ts");
        var psi = new ProcessStartInfo
        {
            FileName = _settings.FfmpegPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[]
        {
            "-y",
            "-f", "rawvideo", "-vcodec", "rawvideo",
            "-s", $"{w}x{h}",
            "-pix_fmt", "bgr24",
            "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-tune", "zerolatency",
            "-g", Math.Max(1, (int)(fps * HlsSegmentSeconds)).ToString(),
            "-force_key_frames", $"expr:gte(t,n_forced*{HlsSegmentSeconds})",
            "-pix_fmt", "yuv420p",
            "-f", "hls",
            "-hls_time", HlsSegmentSeconds.ToString(),
            "-hls_list_size", HlsListSize.ToString(),
            "-hls_flags", "delete_segments+append_list+independent_segments",
            "-hls_segment_filename", segPattern,
            playlist,
        }) psi.ArgumentList.Add(a);

        try { return Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    private static void KillFfmpeg(Process proc)
    {
        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.WaitForExit(3000)) proc.Kill(); } catch { }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task SendJson(object payload)
    {
        try
        {
            if (_ws.State != WebSocketState.Open) return;
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _stop.Token);
        }
        catch { /* peer gone */ }
    }

    private Task SendError(string message) => SendJson(new { type = "error", message });
}
