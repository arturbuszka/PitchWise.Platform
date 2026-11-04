using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Data.Config;
using PitchWise.Data;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

[ApiController]
[Route("api/live")]
public class LiveController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AppSettings _settings;

    public LiveController(AppDbContext db, AppSettings settings)
    {
        _db = db;
        _settings = settings;
    }

    // POST /api/live — create a new live analysis session
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateLiveSessionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SourceUrl))
            return BadRequest(new { error = "source_url is required" });

        var sessionId = Guid.NewGuid().ToString();
        var baseUrl = _settings.LiveWorkerUrl.TrimEnd('/');

        // ws:// URL for the frontend WebSocket connection
        var wsUrl = baseUrl
            .Replace("http://", "ws://")
            .Replace("https://", "wss://")
            + $"/ws/live/external/{sessionId}";

        var hlsUrl = $"{baseUrl}/live_hls/{sessionId}/index.m3u8";

        var session = new LiveSession
        {
            Id = sessionId,
            SourceUrl = req.SourceUrl,
            Status = "idle",
            WsUrl = wsUrl,
            HlsUrl = hlsUrl,
            CreatedAt = DateTime.UtcNow,
        };

        _db.LiveSessions.Add(session);
        await _db.SaveChangesAsync();

        return Ok(new LiveSessionDto(session));
    }

    // GET /api/live/{id}
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var session = await _db.LiveSessions.FindAsync(id);
        if (session is null) return NotFound();
        return Ok(new LiveSessionDto(session));
    }

    // DELETE /api/live/{id} — mark session stopped
    [HttpDelete("{id}")]
    public async Task<IActionResult> Stop(string id)
    {
        var session = await _db.LiveSessions.FindAsync(id);
        if (session is null) return NotFound();
        session.Status = "stopped";
        session.StoppedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new LiveSessionDto(session));
    }
}

public record CreateLiveSessionRequest(string SourceUrl);

public record LiveSessionDto(
    string Id,
    string SourceUrl,
    string Status,
    string WsUrl,
    string HlsUrl,
    DateTime CreatedAt,
    DateTime? StoppedAt
)
{
    public LiveSessionDto(LiveSession s) : this(
        s.Id, s.SourceUrl, s.Status, s.WsUrl, s.HlsUrl, s.CreatedAt, s.StoppedAt
    ) { }
}
