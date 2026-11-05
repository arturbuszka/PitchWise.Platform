using System.Diagnostics;
using System.Globalization;

namespace PitchWise.Worker;

/// <summary>ffmpeg CLI wrappers: clip cut, concat and HLS segmentation.</summary>
public static class FfmpegTools
{
    public static string FfmpegPath { get; set; } = "ffmpeg";
    public static string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Reads a media file's streams. Null if it can't be read, as with one cut off at the very start.</summary>
    public static MediaProbe? Probe(string path)
    {
        var (code, output, _) = Capture(FfprobePath,
            "-v", "error", "-show_format", "-show_streams", "-show_data_hash", "md5", "-of", "json", path);
        return code == 0 ? MediaProbe.Parse(output) : null;
    }

    /// <summary>The first keyframe at or after a position, in seconds. Null if there is none.</summary>
    public static double? KeyframeAtOrAfter(string path, double position)
    {
        var (code, output, _) = Capture(FfprobePath,
            "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path);
        if (code != 0) return null;
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Split(',');
            if (fields.Length >= 2 && fields[1].StartsWith('K')
                && double.TryParse(fields[0], CultureInfo.InvariantCulture, out double pts) && pts >= position)
                return pts;
        }
        return null;
    }

    /// <summary>Joins a concat list into one MP4 without re-encoding. Returns ffmpeg's complaint if it fails.</summary>
    public static string? Stitch(string concatListPath, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var (code, _, errors) = Capture(FfmpegPath,
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "concat", "-safe", "0", "-i", concatListPath,
            "-c", "copy", "-movflags", "+faststart", outPath);
        return code == 0 && File.Exists(outPath) ? null : errors.Trim();
    }

    /// <summary>Cuts [start, end] into outPath via stream copy.</summary>
    public static bool ExtractClip(string videoPath, string outPath, double startSeconds, double endSeconds)
    {
        double start = Math.Max(0.0, startSeconds);
        double duration = Math.Max(0.5, endSeconds - start);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        return Run("-y",
            "-ss", start.ToString("F3", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-t", duration.ToString("F3", CultureInfo.InvariantCulture),
            "-c", "copy",
            outPath) && File.Exists(outPath);
    }

    /// <summary>Stitches clips in order into outPath, re-encoding so that mismatched codecs and
    /// resolutions across sources still work.</summary>
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
                    w.Write($"file '{Path.GetFullPath(p).Replace('\\', '/').Replace("'", "'\\''")}'\n");

            return Run("-y",
                "-f", "concat", "-safe", "0",
                "-i", listPath,
                "-c:v", "libx264", "-preset", "veryfast",
                "-c:a", "aac",
                "-movflags", "+faststart",
                outPath) && File.Exists(outPath);
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Segments an MP4 into an HLS VOD playlist (index.m3u8 + *.ts).</summary>
    public static bool ToHls(string mp4Path, string outDir)
    {
        if (!File.Exists(mp4Path)) return false;
        Directory.CreateDirectory(outDir);
        string playlist = Path.Combine(outDir, "index.m3u8");
        return Run("-y",
            "-i", mp4Path,
            "-c", "copy",
            "-f", "hls",
            "-hls_time", "4",
            "-hls_playlist_type", "vod",
            "-hls_flags", "independent_segments",
            "-hls_segment_filename", Path.Combine(outDir, "segment_%03d.ts"),
            playlist) && File.Exists(playlist);
    }

    private static (int ExitCode, string Output, string Errors) Capture(string program, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = program,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            // Both pipes must be drained at once, or a full one blocks the process forever.
            Task<string> errors = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode, output, errors.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "", e.Message);
        }
    }

    private static bool Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            // Must be drained: ffmpeg blocks once it fills the stderr pipe, and would never exit.
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch (Exception) { return false; }
    }
}
