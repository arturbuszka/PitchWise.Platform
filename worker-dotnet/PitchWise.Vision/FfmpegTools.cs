using System.Diagnostics;
using System.Globalization;

namespace PitchWise.Vision;

/// <summary>
/// ffmpeg/ffprobe CLI wrappers. — probe, clip cut, concat,
/// HLS segmentation. ffmpeg/ffprobe must be on PATH (or pass an absolute path).
/// </summary>
public static class FfmpegTools
{
    public static string FfmpegPath { get; set; } = "ffmpeg";
    public static string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Returns (durationSeconds, fps) via ffprobe, or (null, null) on failure.</summary>
    public static (double? duration, double? fps) ProbeVideo(string videoPath)
    {
        string? outText = RunCapture(FfprobePath, new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=r_frame_rate,duration",
            "-show_entries", "format=duration",
            "-of", "default=noprint_wrappers=1:nokey=0",
            videoPath,
        });
        if (outText is null) return (null, null);

        double? duration = null, fps = null;
        foreach (string line in outText.Split('\n'))
        {
            string l = line.Trim();
            if (l.StartsWith("duration=") && duration is null)
            {
                if (double.TryParse(l.AsSpan(9), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    duration = d;
            }
            else if (l.StartsWith("r_frame_rate="))
            {
                string val = l[13..];
                if (val.Contains('/'))
                {
                    string[] parts = val.Split('/');
                    if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double num)
                        && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den)
                        && den != 0)
                        fps = num / den;
                }
            }
        }
        return (duration, fps);
    }

    /// <summary>Cuts [start, end] into outPath via stream copy. Port of extract_clip.</summary>
    public static bool ExtractClip(string videoPath, string outPath, double startSeconds, double endSeconds)
    {
        double start = Math.Max(0.0, startSeconds);
        double duration = Math.Max(0.5, endSeconds - start);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        return Run(FfmpegPath, new[]
        {
            "-y",
            "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-t", duration.ToString("F3", CultureInfo.InvariantCulture),
            "-c", "copy",
            outPath,
        }) && File.Exists(outPath);
    }

    /// <summary>Stitches clips (in order) into outPath via the concat demuxer + re-encode.
    /// Port of concat_clips — robust to mismatched codecs/resolutions across sources.</summary>
    public static bool ConcatClips(IReadOnlyList<string> clipPaths, string outPath)
    {
        var clips = clipPaths.Where(File.Exists).ToList();
        if (clips.Count == 0) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        string listPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        try
        {
            using (var w = new StreamWriter(listPath))
                foreach (string p in clips)
                {
                    string safe = Path.GetFullPath(p).Replace('\\', '/').Replace("'", "'\\''");
                    w.Write($"file '{safe}'\n");
                }

            return Run(FfmpegPath, new[]
            {
                "-y",
                "-f", "concat", "-safe", "0",
                "-i", listPath,
                "-c:v", "libx264", "-preset", "veryfast",
                "-c:a", "aac",
                "-strict", "experimental",
                "-movflags", "+faststart",
                outPath,
            }) && File.Exists(outPath);
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Segments an MP4 into an HLS VOD playlist (index.m3u8 + *.ts). Port of to_hls.</summary>
    public static bool ToHls(string mp4Path, string outDir)
    {
        if (!File.Exists(mp4Path)) return false;
        Directory.CreateDirectory(outDir);
        string playlist = Path.Combine(outDir, "index.m3u8");
        return Run(FfmpegPath, new[]
        {
            "-y",
            "-i", mp4Path,
            "-c", "copy",
            "-f", "hls",
            "-hls_time", "4",
            "-hls_playlist_type", "vod",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", Path.Combine(outDir, "segment_%03d.ts"),
            playlist,
        }) && File.Exists(playlist);
    }

    /// <summary>
    /// Starts an ffmpeg process that consumes raw BGR24 frames on stdin and writes a
    /// growing HLS **event** playlist (index.m3u8 + seg_%05d.ts) into <paramref name="outDir"/>.
    /// playlist_type=event: append-only, segments never removed, no #EXT-X-ENDLIST until the
    /// input closes — so hls.js polls it live and picks up new segments as they land, then
    /// switches to a fully-seekable VOD once ffmpeg finishes. Caller writes frames to
    /// StandardInput.BaseStream and closes it when done.
    /// </summary>
    public static Process? StartEncodeHls(string outDir, int w, int h, double fps, int segSeconds = 4)
    {
        Directory.CreateDirectory(outDir);
        string playlist = Path.Combine(outDir, "index.m3u8");
        string segPattern = Path.Combine(outDir, "seg_%05d.ts");
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
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
            "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-pix_fmt", "yuv420p",
            "-g", Math.Max(1, (int)(fps * segSeconds)).ToString(),
            "-force_key_frames", $"expr:gte(t,n_forced*{segSeconds})",
            "-f", "hls",
            "-hls_time", segSeconds.ToString(),
            "-hls_playlist_type", "event",
            "-hls_list_size", "0",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", segPattern,
            playlist,
        }) psi.ArgumentList.Add(a);

        try { return Process.Start(psi); }
        catch (Exception) { return null; }
    }

    private static bool Run(string exe, string[] args)
    {
        try
        {
            using var proc = Process.Start(BuildInfo(exe, args, captureStdout: false));
            if (proc is null) return false;
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }

    private static string? RunCapture(string exe, string[] args)
    {
        try
        {
            using var proc = Process.Start(BuildInfo(exe, args, captureStdout: true));
            if (proc is null) return null;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? stdout : null;
        }
        catch (Exception) { return null; }
    }

    private static ProcessStartInfo BuildInfo(string exe, string[] args, bool captureStdout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
