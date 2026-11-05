using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data;
using PitchWise.Api.Auth;
using PitchWise.Api.Dtos;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

// Analysis sessions: CRUD and events.
[ApiController]
[Route("api/analyses")]
public class AnalysesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AnalysesController(AppDbContext db) => _db = db;

    // ---- Analyses CRUD ----

    [HttpGet]
    public async Task<List<AnalysisListItem>> ListAnalyses([FromQuery] string? sport, [FromQuery] string? search)
    {
        var q = _db.AnalysisSessions.Where(a => a.TenantId == User.TenantId());
        if (!string.IsNullOrEmpty(sport))
            q = q.Where(a => a.Sport == sport);
        if (!string.IsNullOrEmpty(search))
            q = q.Where(a => EF.Functions.ILike(a.Name, $"%{search}%"));

        return await q
            .OrderByDescending(a => a.UpdatedAt)
            .Select(a => new AnalysisListItem(
                a.Id, a.Name, a.Subtitle, a.Sport, a.Status, a.CreatedAt, a.UpdatedAt,
                _db.Videos.Count(v => v.AnalysisId == a.Id)))
            .ToListAsync();
    }

    [HttpPost]
    public async Task<AnalysisDetail> CreateAnalysis([FromBody] AnalysisCreate payload)
    {
        var obj = new AnalysisSession
        {
            Name = payload.Name,
            Subtitle = payload.Subtitle,
            Sport = payload.Sport,
            TenantId = User.TenantId(),
        };
        _db.AnalysisSessions.Add(obj);
        await _db.SaveChangesAsync();
        return new AnalysisDetail(
            obj.Id, obj.Name, obj.Subtitle, obj.Sport, obj.Status,
            obj.CreatedAt, obj.UpdatedAt, new List<VideoOut>());
    }

    [HttpGet("{analysisId:int}")]
    public async Task<ActionResult<AnalysisDetail>> GetAnalysis(int analysisId)
    {
        var obj = await _db.AnalysisSessions.FindAsync(analysisId);
        if (obj is null) return NotFound(new { detail = "Analysis not found" });

        var videos = await _db.Videos
            .Where(v => v.AnalysisId == analysisId)
            .OrderBy(v => v.Order).ThenBy(v => v.CreatedAt)
            .ToListAsync();

        return new AnalysisDetail(
            obj.Id, obj.Name, obj.Subtitle, obj.Sport, obj.Status,
            obj.CreatedAt, obj.UpdatedAt,
            videos.Select(VideoOut.From).ToList());
    }

    // ---- Events ----

    [HttpGet("{analysisId:int}/events")]
    public async Task<ActionResult<List<EventOut>>> ListEvents(int analysisId, [FromQuery] EventType? type)
    {
        if (!await _db.AnalysisSessions.AnyAsync(a => a.Id == analysisId))
            return NotFound(new { detail = "Analysis not found" });

        var q = _db.Events.Where(e => e.AnalysisId == analysisId);
        if (type is not null)
            q = q.Where(e => e.Type == type);
        var events = await q.OrderBy(e => e.TimestampSeconds).ToListAsync();

        return events.Select(EventToOut).ToList();
    }

    [HttpPost("{analysisId:int}/events")]
    public async Task<ActionResult<EventOut>> CreateEvent(int analysisId, [FromBody] EventCreate payload)
    {
        var session = await _db.AnalysisSessions.FindAsync(analysisId);
        if (session is null) return NotFound(new { detail = "Analysis not found" });

        if (payload.VideoId is not null)
        {
            var video = await _db.Videos.FindAsync(payload.VideoId.Value);
            if (video is null || video.AnalysisId != analysisId)
                return BadRequest(new { detail = "video_id does not belong to this analysis" });
        }

        var ev = new Event
        {
            AnalysisId = analysisId,
            VideoId = payload.VideoId,
            Type = payload.Type,
            Source = EventSource.Manual,
            TimestampSeconds = payload.TimestampSeconds,
            Label = payload.Label,
            Note = payload.Note,
            PlayerNumber = payload.PlayerNumber,
            PlayerName = payload.PlayerName,
            AssistNumber = payload.AssistNumber,
            AssistName = payload.AssistName,
        };
        _db.Events.Add(ev);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return EventToOut(ev);
    }

    [HttpDelete("{analysisId:int}/events/{eventId:int}")]
    public async Task<ActionResult> DeleteEvent(int analysisId, int eventId)
    {
        var ev = await _db.Events.FindAsync(eventId);
        if (ev is null || ev.AnalysisId != analysisId)
            return NotFound(new { detail = "Event not found" });

        _db.Events.Remove(ev);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ---- Helpers ----

    private static EventOut EventToOut(Event ev) => new(
        ev.Id, ev.AnalysisId, ev.VideoId, ev.Type, ev.Source,
        ev.TimestampSeconds, ev.Confidence, ev.Label, ev.Note,
        ev.PlayerNumber, ev.PlayerName, ev.AssistNumber, ev.AssistName);
}
