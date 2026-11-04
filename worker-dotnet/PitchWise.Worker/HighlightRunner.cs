using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Data.Models;

namespace PitchWise.Worker;

/// <summary>
/// Renders a highlight reel: cut a clip around each selected event, concat into one MP4,
/// then HLS-segment.
/// </summary>
public sealed class HighlightRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppSettings _appSettings;
    private readonly WorkerSettings _worker;

    public HighlightRunner(
        IServiceScopeFactory scopeFactory, AppSettings appSettings, WorkerSettings worker)
    {
        _scopeFactory = scopeFactory;
        _appSettings = appSettings;
        _worker = worker;
    }

    private static List<int> ParseEventIds(string csv) =>
        (csv ?? "").Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && int.TryParse(s, out _))
            .Select(int.Parse)
            .ToList();

    public async Task RunAsync(int highlightId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Highlight? highlight = await db.Highlights.FirstOrDefaultAsync(h => h.Id == highlightId, ct);
        if (highlight is null) return;

        highlight.Status = HighlightStatus.Running;
        highlight.Progress = 0.0;
        await db.SaveChangesAsync(ct);

        try
        {
            List<int> eventIds = ParseEventIds(highlight.EventIds);
            if (eventIds.Count == 0) throw new InvalidOperationException("No events selected");

            // Selected events for this analysis, in match order.
            var rows = await db.Events
                .Where(e => e.AnalysisId == highlight.AnalysisId && eventIds.Contains(e.Id))
                .OrderBy(e => e.TimestampSeconds)
                .ToListAsync(ct);
            if (rows.Count == 0) throw new InvalidOperationException("Selected events not found");

            var videoPaths = new Dictionary<int, string>();
            var clipPaths = new List<string>();
            int total = rows.Count;
            for (int i = 0; i < rows.Count; i++)
            {
                Event ev = rows[i];
                if (ev.VideoId is not int vid) continue;
                if (!videoPaths.TryGetValue(vid, out string? vpath))
                {
                    Video? video = await db.Videos.FirstOrDefaultAsync(v => v.Id == vid, ct);
                    if (video is null) continue;
                    vpath = Path.Combine(_appSettings.UploadsDir, video.Filename);
                    videoPaths[vid] = vpath;
                }

                double start = Math.Max(0.0, ev.TimestampSeconds - _worker.ClipPreSeconds);
                double end = ev.TimestampSeconds + _worker.ClipPostSeconds;
                string clipName = $"hl{highlight.Id}_ev{ev.Id}.mp4";
                string clipPath = Path.Combine(_appSettings.ClipsDir, clipName);
                if (FfmpegTools.ExtractClip(vpath, clipPath, start, end))
                    clipPaths.Add(clipPath);

                highlight.Progress = Math.Round(0.8 * (i + 1) / total, 3);
                await db.SaveChangesAsync(ct);
            }

            if (clipPaths.Count == 0) throw new InvalidOperationException("Could not extract any clips");

            string outName = $"highlight{highlight.Id}.mp4";
            string outPath = Path.Combine(_appSettings.ClipsDir, outName);
            if (!FfmpegTools.ConcatClips(clipPaths, outPath))
                throw new InvalidOperationException("ffmpeg concat failed");

            highlight.Filename = outName;
            highlight.Status = HighlightStatus.Done;
            highlight.Progress = 1.0;
            highlight.FinishedAt = DateTime.UtcNow;

            // Best-effort HLS; MP4 fallback still works if it fails.
            string hlsDir = Path.Combine(_appSettings.HlsDir, highlight.Id.ToString());
            if (FfmpegTools.ToHls(outPath, hlsDir))
                highlight.HlsReady = true;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception exc)
        {
            Highlight? h = await db.Highlights.FirstOrDefaultAsync(x => x.Id == highlightId, ct);
            if (h is not null)
            {
                h.Status = HighlightStatus.Failed;
                h.Error = $"{exc.GetType().Name}: {exc.Message}";
                h.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
