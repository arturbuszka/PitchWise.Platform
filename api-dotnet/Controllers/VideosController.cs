using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Api.Dtos;
using PitchWise.Data.Models;
using PitchWise.Api.Services;

namespace PitchWise.Api.Controllers;

// Videos: upload and streaming (Range).
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

    public VideosController(AppDbContext db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
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
