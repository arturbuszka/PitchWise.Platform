using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Api.Dtos;
using PitchWise.Data.Models;
using PitchWise.Api.Services;

namespace PitchWise.Api.Controllers;

// Videos: upload, streaming (Range), analyze→enqueue, status.
[ApiController]
[Route("api/analyses")]
public class VideosController : ControllerBase
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi",
    };

    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly VisionQueue _queue;

    public VideosController(AppDbContext db, AppSettings settings, VisionQueue queue)
    {
        _db = db;
        _settings = settings;
        _queue = queue;
    }

    [HttpGet("{analysisId:int}/videos")]
    public async Task<ActionResult<List<VideoOut>>> ListVideos(int analysisId)
    {
        if (!await _db.AnalysisSessions.AnyAsync(a => a.Id == analysisId))
            return NotFound(new { detail = "Analysis not found" });

        var rows = await _db.Videos
            .Where(v => v.AnalysisId == analysisId)
            .OrderBy(v => v.Order).ThenBy(v => v.CreatedAt)
            .ToListAsync();
        return rows.Select(v => new VideoOut(v.Id, v.AnalysisId, v.Name, v.DurationSeconds, v.Fps, v.Order)).ToList();
    }

    [HttpPost("{analysisId:int}/videos")]
    public async Task<ActionResult<VideoOut>> UploadVideo(int analysisId, [FromForm] string name, IFormFile file)
    {
        var session = await _db.AnalysisSessions.FindAsync(analysisId);
        if (session is null) return NotFound(new { detail = "Analysis not found" });

        var ext = Path.GetExtension(file.FileName ?? "").ToLowerInvariant();
        if (!AllowedExt.Contains(ext))
            return BadRequest(new { detail = $"Unsupported format: {ext}. Allowed: {string.Join(", ", AllowedExt.OrderBy(x => x))}" });

        var storedName = $"{Guid.NewGuid():N}{ext}";
        var dest = Path.Combine(_settings.UploadsDir, storedName);
        await using (var outStream = System.IO.File.Create(dest))
            await file.CopyToAsync(outStream);

        var order = await _db.Videos.CountAsync(v => v.AnalysisId == analysisId);

        var video = new Video
        {
            AnalysisId = analysisId,
            Name = name,
            Filename = storedName,
            Order = order,
        };
        _db.Videos.Add(video);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return new VideoOut(video.Id, video.AnalysisId, video.Name, video.DurationSeconds, video.Fps, video.Order);
    }

    [HttpGet("{analysisId:int}/videos/{videoId:int}/stream")]
    public async Task<IActionResult> StreamVideo(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null) return NotFound(new { detail = "Video not found" });

        var path = Path.Combine(_settings.UploadsDir, video.Filename);
        if (!System.IO.File.Exists(path)) return NotFound(new { detail = "Video file does not exist" });

        // enableRangeProcessing => video seeking in the browser (HTTP Range).
        return PhysicalFile(Path.GetFullPath(path), ContentTypeFor(path), enableRangeProcessing: true);
    }

    // ---- YOLO pipeline ----

    [HttpPost("{analysisId:int}/videos/{videoId:int}/analyze")]
    public async Task<ActionResult<VisionJobOut>> StartAnalysis(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null) return NotFound(new { detail = "Video not found" });

        // Do not start a second run if one is already active.
        var existing = await _db.VisionJobs
            .Where(j => j.VideoId == videoId &&
                        (j.Status == VisionJobStatus.Pending || j.Status == VisionJobStatus.Running))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
        if (existing is not null)
            return JobToOut(existing, video);

        var job = new VisionJob { VideoId = videoId };

        // Re-analysis: drop the old annotated file reference until the new render lands.
        video.AnnotatedFilename = null;
        _db.VisionJobs.Add(job);

        var session = await _db.AnalysisSessions.FindAsync(analysisId);
        if (session is not null)
        {
            session.Status = SessionStatus.Processing;
            session.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(job.Id);

        return JobToOut(job, video);
    }

    [HttpPost("{analysisId:int}/videos/{videoId:int}/analyze/cancel")]
    public async Task<ActionResult<VisionJobOut?>> CancelAnalysis(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null) return NotFound(new { detail = "Video not found" });

        // Mark the active job Failed/cancelled. The worker polls the job status and stops
        // when it's no longer Running (cross-process signal, no Redis pub/sub needed). This
        // also unblocks StartAnalysis's dedup so the user can re-run.
        var job = await _db.VisionJobs
            .Where(j => j.VideoId == videoId &&
                        (j.Status == VisionJobStatus.Pending || j.Status == VisionJobStatus.Running))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
        if (job is null) return Ok((VisionJobOut?)null);

        job.Status = VisionJobStatus.Failed;
        job.Error = "cancelled";
        job.FinishedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(JobToOut(job, video));
    }

    [HttpGet("{analysisId:int}/videos/{videoId:int}/status")]
    public async Task<ActionResult<VisionJobOut?>> GetStatus(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null) return NotFound(new { detail = "Video not found" });

        var job = await _db.VisionJobs
            .Where(j => j.VideoId == videoId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync();
        if (job is null) return Ok((VisionJobOut?)null);
        return Ok(JobToOut(job, video));
    }

    private static VisionJobOut JobToOut(VisionJob j, Video video) =>
        new(j.Id, j.VideoId, j.Status, j.Progress, j.Error, j.CreatedAt, j.FinishedAt,
            AnnotatedReady: !string.IsNullOrEmpty(video.AnnotatedFilename));

    // Whole-match aggregate stats for one video. 404 until analysis has produced a row.
    [HttpGet("{analysisId:int}/videos/{videoId:int}/stats")]
    public async Task<ActionResult<MatchStatsOut>> GetStats(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null) return NotFound(new { detail = "Video not found" });

        var s = await _db.MatchStats.FirstOrDefaultAsync(x => x.VideoId == videoId);
        if (s is null) return NotFound(new { detail = "No stats yet" });

        return Ok(new MatchStatsOut(
            s.VideoId, s.AnalysisId,
            new TeamStatsOut(s.PossessionPctA, s.PassesA, s.TurnoversA, s.PassAccuracyPctA),
            new TeamStatsOut(s.PossessionPctB, s.PassesB, s.TurnoversB, s.PassAccuracyPctB),
            s.ControlledSeconds, s.LooseSeconds,
            // The time-on-pitch list is already JSON in the DB; hand it through as a parsed node
            // so it serializes as a real array, not a string.
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(s.TimeOnPitchJson)));
    }

    // ---- Annotated (boxes burned-in) playback: progressive HLS VOD ----
    // AnnotatedFilename holds the HLS dir name (e.g. "annotated_v6") under UploadsDir.
    // The worker grows index.m3u8 + seg_*.ts while analysing, so the video is watchable
    // within seconds and fully seekable over what's rendered so far.

    [HttpGet("{analysisId:int}/videos/{videoId:int}/annotated/index.m3u8")]
    public async Task<IActionResult> AnnotatedPlaylist(int analysisId, int videoId)
    {
        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null || string.IsNullOrEmpty(video.AnnotatedFilename))
            return NotFound(new { detail = "Annotated video not ready" });

        var path = Path.Combine(_settings.UploadsDir, video.AnnotatedFilename, "index.m3u8");
        if (!System.IO.File.Exists(path)) return NotFound(new { detail = "Playlist not ready" });

        // no-store: the playlist grows during analysis, don't let the browser cache it.
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, "application/vnd.apple.mpegurl");
    }

    [HttpGet("{analysisId:int}/videos/{videoId:int}/annotated/{segment}")]
    public async Task<IActionResult> AnnotatedSegment(int analysisId, int videoId, string segment)
    {
        // Only allow plain segment filenames (no path traversal).
        if (segment.Contains('/') || segment.Contains('\\') || segment.Contains("..") ||
            !segment.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { detail = "invalid segment" });

        var video = await GetVideoOr404(analysisId, videoId);
        if (video is null || string.IsNullOrEmpty(video.AnnotatedFilename))
            return NotFound(new { detail = "Annotated video not ready" });

        var path = Path.Combine(_settings.UploadsDir, video.AnnotatedFilename, segment);
        if (!System.IO.File.Exists(path)) return NotFound(new { detail = "Segment not found" });

        return PhysicalFile(Path.GetFullPath(path), "video/mp2t", enableRangeProcessing: true);
    }

    // ---- Clips ----

    [HttpGet("{analysisId:int}/clips/{clipId:int}/stream")]
    public async Task<IActionResult> StreamClip(int analysisId, int clipId)
    {
        var clip = await _db.Clips.FindAsync(clipId);
        if (clip is null) return NotFound(new { detail = "Clip not found" });

        var video = await _db.Videos.FindAsync(clip.VideoId);
        if (video is null || video.AnalysisId != analysisId)
            return NotFound(new { detail = "Clip not found" });

        var path = Path.Combine(_settings.ClipsDir, clip.Filename);
        if (!System.IO.File.Exists(path)) return NotFound(new { detail = "Clip file does not exist" });

        return PhysicalFile(Path.GetFullPath(path), ContentTypeFor(path), enableRangeProcessing: true);
    }

    // ---- Helpers ----

    private async Task<Video?> GetVideoOr404(int analysisId, int videoId)
    {
        var video = await _db.Videos.FindAsync(videoId);
        if (video is null || video.AnalysisId != analysisId) return null;
        return video;
    }

    private static string ContentTypeFor(string path)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(path, out var ct) ? ct : "application/octet-stream";
    }
}
