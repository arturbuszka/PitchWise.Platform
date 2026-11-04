using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Api.Dtos;
using PitchWise.Data.Models;
using PitchWise.Api.Services;

namespace PitchWise.Api.Controllers;

// Highlight reels: create (enqueue render job), poll status, stream the stitched
// file (HTTP Range), and mint a time-limited public share link.
[ApiController]
[Route("api/analyses")]
public class HighlightsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;
    private readonly HighlightQueue _queue;
    private readonly HlsSigner _hls;

    public HighlightsController(AppDbContext db, AppSettings settings, HighlightQueue queue, HlsSigner hls)
    {
        _db = db;
        _settings = settings;
        _queue = queue;
        _hls = hls;
    }

    [HttpPost("{analysisId:int}/highlights")]
    public async Task<ActionResult<HighlightOut>> Create(int analysisId, [FromBody] CreateHighlightIn body)
    {
        if (!await _db.AnalysisSessions.AnyAsync(a => a.Id == analysisId))
            return NotFound(new { detail = "Analysis not found" });

        var ids = (body.EventIds ?? new List<int>()).Distinct().ToList();
        if (ids.Count == 0)
            return BadRequest(new { detail = "Select at least one event" });

        // Keep only events that actually belong to this analysis.
        var validIds = await _db.Events
            .Where(e => e.AnalysisId == analysisId && ids.Contains(e.Id))
            .Select(e => e.Id)
            .ToListAsync();
        if (validIds.Count == 0)
            return BadRequest(new { detail = "No valid events for this analysis" });

        var name = string.IsNullOrWhiteSpace(body.Name) ? "Highlight" : body.Name.Trim();
        var highlight = new Highlight
        {
            AnalysisId = analysisId,
            Name = name,
            EventIds = string.Join(",", validIds),
        };
        _db.Highlights.Add(highlight);
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(highlight.Id);

        return ToOut(highlight);
    }

    [HttpGet("{analysisId:int}/highlights/{id:int}/status")]
    public async Task<ActionResult<HighlightOut>> Status(int analysisId, int id)
    {
        var highlight = await GetOr404(analysisId, id);
        if (highlight is null) return NotFound(new { detail = "Highlight not found" });
        return ToOut(highlight);
    }

    [HttpGet("{analysisId:int}/highlights/{id:int}/stream")]
    public async Task<IActionResult> Stream(int analysisId, int id)
    {
        var highlight = await GetOr404(analysisId, id);
        if (highlight is null || string.IsNullOrEmpty(highlight.Filename))
            return NotFound(new { detail = "Highlight not ready" });

        var path = Path.Combine(_settings.ClipsDir, highlight.Filename);
        if (!System.IO.File.Exists(path)) return NotFound(new { detail = "Highlight file does not exist" });

        return PhysicalFile(Path.GetFullPath(path), ContentTypeFor(path), enableRangeProcessing: true);
    }

    // Mint a signed HLS manifest URL (served by the nginx edge, not the API). This is
    // the scale path: bytes flow nginx→viewer, cached at the edge; the API only signs.
    [HttpGet("{analysisId:int}/highlights/{id:int}/hls")]
    public async Task<ActionResult<HlsUrlOut>> Hls(int analysisId, int id)
    {
        var highlight = await GetOr404(analysisId, id);
        if (highlight is null) return NotFound(new { detail = "Highlight not found" });
        if (!highlight.HlsReady)
            return Conflict(new { detail = "HLS not ready" });

        var (url, expiresAt) = _hls.SignHighlight(highlight.Id);
        return new HlsUrlOut(url, expiresAt.UtcDateTime);
    }

    // Mint (or refresh) a time-limited public share link for a finished highlight.
    [HttpPost("{analysisId:int}/highlights/{id:int}/share")]
    public async Task<ActionResult<ShareOut>> Share(int analysisId, int id)
    {
        var highlight = await GetOr404(analysisId, id);
        if (highlight is null) return NotFound(new { detail = "Highlight not found" });
        if (highlight.Status != HighlightStatus.Done)
            return BadRequest(new { detail = "Highlight is not ready yet" });

        highlight.ShareToken = Guid.NewGuid().ToString("N");
        highlight.ShareExpiresAt = DateTime.UtcNow.AddHours(_settings.ShareLinkTtlHours);
        await _db.SaveChangesAsync();

        var url = $"{_settings.WebOrigin.TrimEnd('/')}/share/{highlight.ShareToken}";
        return new ShareOut(highlight.ShareToken, url, highlight.ShareExpiresAt.Value);
    }

    private async Task<Highlight?> GetOr404(int analysisId, int id)
    {
        var highlight = await _db.Highlights.FindAsync(id);
        if (highlight is null || highlight.AnalysisId != analysisId) return null;
        return highlight;
    }

    private static HighlightOut ToOut(Highlight h) => new(
        h.Id, h.AnalysisId, h.Name, h.Status, h.Progress, h.Error,
        h.ShareToken, h.ShareExpiresAt, h.CreatedAt, h.FinishedAt);

    private static string ContentTypeFor(string path)
    {
        var provider = new FileExtensionContentTypeProvider();
        return provider.TryGetContentType(path, out var ct) ? ct : "application/octet-stream";
    }
}
