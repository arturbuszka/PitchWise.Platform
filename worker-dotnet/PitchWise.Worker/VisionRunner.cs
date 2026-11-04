using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Data.Models;
using PitchWise.Vision;

namespace PitchWise.Worker;

/// <summary>
/// Runs one vision job: analyses a video and writes Event/Clip/Video + progress/status.
/// Uses short-lived DbContext scopes
/// so progress writes don't hold a long transaction.
/// </summary>
public sealed class VisionRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppSettings _appSettings;
    private readonly WorkerSettings _worker;
    private readonly IReadOnlyDictionary<int, string> _classNames;

    public VisionRunner(
        IServiceScopeFactory scopeFactory,
        AppSettings appSettings,
        WorkerSettings worker,
        IReadOnlyDictionary<int, string> classNames)
    {
        _scopeFactory = scopeFactory;
        _appSettings = appSettings;
        _worker = worker;
        _classNames = classNames;
    }

    private AppDbContext NewDb(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>();

    public async Task RunAsync(int jobId, CancellationToken ct)
    {
        // 1. Load job + video, mark running.
        int videoId;
        string videoPath;
        using (IServiceScope scope = _scopeFactory.CreateScope())
        {
            AppDbContext db = NewDb(scope);
            VisionJob? job = await db.VisionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is null) return;

            Video? video = await db.Videos.FirstOrDefaultAsync(v => v.Id == job.VideoId, ct);
            if (video is null)
            {
                job.Status = VisionJobStatus.Failed;
                job.Error = "Video not found";
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            job.Status = VisionJobStatus.Running;
            await db.SaveChangesAsync(ct);

            videoId = video.Id;
            videoPath = Path.Combine(_appSettings.UploadsDir, video.Filename);
        }

        try
        {
            // 2. Single streaming pass: encode a growing HLS VOD with boxes AND detect
            // events in rolling windows. Boxes appear within seconds; the timeline fills
            // in as windows complete; nothing waits for the whole file.
            double lastSaved = 0.0;
            void SaveProgress(double p)
            {
                if (p - lastSaved < 0.02 && p < 1.0) return;
                lastSaved = p;
                _ = SaveProgressAsync(jobId, p);   // fire-and-forget short write
            }

            // HLS lives in a per-video dir under uploads; annotated_filename stores the dir.
            string hlsDirName = $"annotated_v{videoId}";
            string hlsDir = Path.Combine(_appSettings.UploadsDir, hlsDirName);
            if (Directory.Exists(hlsDir))
                try { Directory.Delete(hlsDir, recursive: true); } catch { /* re-analysis: wipe old */ }

            int readyFlagged = 0;
            void OnFirstSegment()
            {
                // Flag playback-ready the moment the first segment lands (once).
                if (Interlocked.Exchange(ref readyFlagged, 1) == 0)
                    _ = SetAnnotatedDirAsync(videoId, hlsDirName);
            }
            void OnEvents(IReadOnlyList<DetectedEvent> evs) =>
                _ = SaveEventsAsync(videoId, evs, videoPath);   // fire-and-forget per window

            // Cooperative cancel: the API sets the job to Failed/cancelled; the worker (a
            // separate process) notices via this DB poll and stops. Cached to keep the poll
            // off the hot path (checked at most ~every 2s inside StreamingAnnotator).
            bool IsCancelled() => IsJobCancelledSync(jobId);

            // ENGINE_DUMP=1 records one engine observation per processed frame next to the HLS
            // output. Replaying it through WorldStateReplay is how the possession and ball-filter
            // thresholds get tuned — without it, every iteration costs a full video pass.
            string? engineDumpPath =
                (Environment.GetEnvironmentVariable("ENGINE_DUMP") ?? "") is "1" or "true"
                    ? Path.Combine(hlsDir, "world_state.jsonl")
                    : null;

            StreamingAnnotator.Result result = await Task.Run(() => StreamingAnnotator.Run(
                videoPath, hlsDir,
                _worker.YoloModelPath, _classNames,
                frameStride: _worker.FrameStride, imgsz: _worker.Imgsz,
                onProgress: SaveProgress,
                onFirstSegment: _worker.RenderAnnotated ? OnFirstSegment : null,
                onEvents: OnEvents,
                executionProvider: _worker.OnnxExecutionProvider,
                deviceId: _worker.OnnxDeviceId,
                isCancelled: IsCancelled,
                reidModelPath: _worker.ReidModelPath,
                // A pitch-keypoint model, when configured, registers each frame to metres per
                // frame (broadcast camera) and switches possession/passes on. Without it the
                // engine stays in normalized coordinates and emits no distance-derived events.
                homography: null,
                engineDumpPath: engineDumpPath,
                pitchModelPath: _worker.PitchModelPath), ct);

            // Persist the per-player time-on-pitch report next to the HLS output (JSON sidecar).
            // Best-effort: a report we can't write shouldn't fail the whole analysis job.
            if (!result.Cancelled && result.TimeOnPitch.Count > 0)
                WriteTimeOnPitchReport(hlsDir, result.TimeOnPitch);

            // 3. Finalise in one scope: duration/fps, ensure annotated dir set, mark done.
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);

            Video? video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId, ct);
            VisionJob? job = await db.VisionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (video is null || job is null) return;

            // Cancelled: the API already marked the job (Failed/cancelled). Leave its status,
            // just clean up the partial annotated dir and stop.
            if (result.Cancelled)
            {
                try { if (Directory.Exists(hlsDir)) Directory.Delete(hlsDir, recursive: true); } catch { }
                video.AnnotatedFilename = null;
                await db.SaveChangesAsync(ct);
                return;
            }

            video.DurationSeconds = result.DurationSeconds;
            video.Fps = result.Fps;
            if (_worker.RenderAnnotated && result.EncodeOk)
                video.AnnotatedFilename = hlsDirName;

            // Whole-match aggregate stats: upsert one row per video (re-analysis overwrites).
            // Written even when zero (engine ran without pitch coords), so the UI shows an honest
            // empty state rather than "no data at all".
            if (result.MatchStats is MatchStatsReport ms)
                await UpsertMatchStatsAsync(db, videoId, video.AnalysisId, ms, result.TimeOnPitch, ct);

            job.Status = VisionJobStatus.Done;
            job.Progress = 1.0;
            job.FinishedAt = DateTime.UtcNow;

            // 4. Flip the session to "done" when every job across the analysis is finished.
            var siblingStatuses = await db.VisionJobs
                .Join(db.Videos, j => j.VideoId, v => v.Id, (j, v) => new { j.Status, v.AnalysisId })
                .Where(x => x.AnalysisId == video.AnalysisId && x.Status != VisionJobStatus.Done)
                .Select(x => x.Status)
                .ToListAsync(ct);
            // The current job isn't Done in the DB yet (saved below), so exclude it: if no
            // OTHER job is unfinished, this is the last one.
            if (siblingStatuses.Count == 0)
            {
                AnalysisSession? session = await db.AnalysisSessions
                    .FirstOrDefaultAsync(s => s.Id == video.AnalysisId, ct);
                if (session is not null)
                {
                    session.Status = SessionStatus.Done;
                    session.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception exc)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);
            VisionJob? job = await db.VisionJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
            if (job is not null)
            {
                job.Status = VisionJobStatus.Failed;
                job.Error = $"{exc.GetType().Name}: {exc.Message}";
                job.FinishedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    // Inserts or updates the one MatchStats row for this video. The per-player time-on-pitch list
    // rides along as JSON (variable length); team aggregates are plain columns.
    private static async Task UpsertMatchStatsAsync(
        AppDbContext db, int videoId, int analysisId,
        MatchStatsReport ms, IReadOnlyList<PlayerTimeOnPitch> timeOnPitch, CancellationToken ct)
    {
        string topJson = System.Text.Json.JsonSerializer.Serialize(
            timeOnPitch.Select(p => new
            {
                player_id = p.PlayerId,
                seconds_on_pitch = Math.Round(p.SecondsOnPitch, 1),
                frames_seen = p.FramesSeen,
            }));

        MatchStats? row = await db.MatchStats.FirstOrDefaultAsync(s => s.VideoId == videoId, ct);
        if (row is null)
        {
            row = new MatchStats { VideoId = videoId, AnalysisId = analysisId };
            db.MatchStats.Add(row);
        }

        row.PossessionPctA = Math.Round(ms.TeamA.PossessionPct, 1);
        row.PossessionPctB = Math.Round(ms.TeamB.PossessionPct, 1);
        row.ControlledSeconds = Math.Round(ms.ControlledSeconds, 1);
        row.LooseSeconds = Math.Round(ms.LooseSeconds, 1);
        row.PassesA = ms.TeamA.Passes;
        row.PassesB = ms.TeamB.Passes;
        row.TurnoversA = ms.TeamA.Turnovers;
        row.TurnoversB = ms.TeamB.Turnovers;
        row.PassAccuracyPctA = Math.Round(ms.TeamA.PassAccuracyPct, 1);
        row.PassAccuracyPctB = Math.Round(ms.TeamB.PassAccuracyPct, 1);
        row.TimeOnPitchJson = topJson;

        // SaveChanges is called by the caller's scope alongside the video/job updates.
    }

    // Writes the per-player on-pitch time as a JSON sidecar in the video's HLS dir. Also logs
    // a short summary to the console so it's visible during a manual/verification run.
    private static void WriteTimeOnPitchReport(string hlsDir, IReadOnlyList<PlayerTimeOnPitch> report)
    {
        try
        {
            Directory.CreateDirectory(hlsDir);
            string path = Path.Combine(hlsDir, "time_on_pitch.json");
            string json = System.Text.Json.JsonSerializer.Serialize(
                report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            Console.WriteLine($"[Worker] time-on-pitch: {report.Count} players -> {path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Worker] time-on-pitch report write skipped: {ex.Message}");
        }
    }

    // Set the annotated HLS dir as soon as the first segment lands, so the frontend can
    // start playing while analysis continues. Best-effort short write.
    private async Task SetAnnotatedDirAsync(int videoId, string hlsDirName)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);
            Video? video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId);
            if (video is not null && video.AnnotatedFilename != hlsDirName)
            {
                video.AnnotatedFilename = hlsDirName;
                await db.SaveChangesAsync();
            }
        }
        catch { /* best-effort; finalise step also sets it */ }
    }

    // Persist a window's worth of freshly detected events so the timeline fills in live.
    private async Task SaveEventsAsync(int videoId, IReadOnlyList<DetectedEvent> events, string videoPath)
    {
        if (events.Count == 0) return;
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);
            Video? video = await db.Videos.FirstOrDefaultAsync(v => v.Id == videoId);
            if (video is null) return;

            foreach (DetectedEvent det in events)
            {
                var ev = new Event
                {
                    AnalysisId = video.AnalysisId,
                    VideoId = video.Id,
                    Type = EventTypeMap.FromDb(det.Type),
                    Source = EventSource.Auto,
                    TimestampSeconds = det.TimestampSeconds,
                    Confidence = det.Confidence,
                    Label = det.Label,
                };
                db.Events.Add(ev);
                await db.SaveChangesAsync();   // flush to get ev.Id

                if (_worker.GenerateClips)
                {
                    double start = Math.Max(0.0, det.TimestampSeconds - _worker.ClipPreSeconds);
                    double end = det.TimestampSeconds + _worker.ClipPostSeconds;
                    string clipName = $"video{video.Id}_event{ev.Id}.mp4";
                    string clipPath = Path.Combine(_appSettings.ClipsDir, clipName);
                    if (FfmpegTools.ExtractClip(videoPath, clipPath, start, end))
                        db.Clips.Add(new Clip
                        {
                            EventId = ev.Id,
                            VideoId = video.Id,
                            Filename = clipName,
                            StartSeconds = start,
                            EndSeconds = end,
                        });
                }
            }
            await db.SaveChangesAsync();
        }
        catch { /* best-effort; a dropped window just means a few missing events */ }
    }

    // True once the job is no longer Running in the DB — i.e. the API cancelled it (set it
    // to Failed). Synchronous because it's polled from the CPU-bound analysis loop.
    private bool IsJobCancelledSync(int jobId)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);
            VisionJobStatus? status = db.VisionJobs
                .Where(j => j.Id == jobId)
                .Select(j => (VisionJobStatus?)j.Status)
                .FirstOrDefault();
            return status is not null && status != VisionJobStatus.Running;
        }
        catch { return false; }   // transient DB error → keep going, don't cancel spuriously
    }

    private async Task SaveProgressAsync(int jobId, double progress)
    {
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            AppDbContext db = NewDb(scope);
            VisionJob? job = await db.VisionJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is not null && job.Status == VisionJobStatus.Running)
            {
                job.Progress = progress;
                await db.SaveChangesAsync();
            }
        }
        catch { /* progress write is best-effort */ }
    }
}
