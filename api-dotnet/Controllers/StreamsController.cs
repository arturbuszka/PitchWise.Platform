using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Auth;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

// Live channels: create one, publish to it over RTMP, watch it over signed HLS.
[ApiController]
[Route("api/streams")]
public class StreamsController(AppDbContext db, AppSettings settings, HlsSigner hls, IMediaMtxClient mediaMtx)
    : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<CreatedStreamOut>> Create([FromBody] CreateStreamIn body)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "name is required" });

        var streamKey = Secrets.Generate("sk_");
        var stream = new LiveStream
        {
            Id = Guid.NewGuid(),
            TenantId = User.TenantId(),
            Name = body.Name.Trim(),
            StreamKeyHash = Secrets.Hash(streamKey),
        };
        db.LiveStreams.Add(stream);
        await db.SaveChangesAsync();

        var ingestUrl = $"{settings.RtmpIngestUrl.TrimEnd('/')}/live/{stream.Id}?key={streamKey}";
        return CreatedAtAction(nameof(Get), new { id = stream.Id },
            new CreatedStreamOut(ToOut(stream), streamKey, ingestUrl));
    }

    [HttpGet]
    public async Task<List<StreamOut>> List()
    {
        var tenantId = User.TenantId();
        var streams = await db.LiveStreams
            .Where(s => s.TenantId == tenantId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return streams.Select(ToOut).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StreamOut>> Get(Guid id)
    {
        var stream = await Find(id);
        return stream is null ? NotFound(new { detail = "Stream not found" }) : ToOut(stream);
    }

    // Mints a signed URL for the edge; the API never carries the video itself.
    [HttpGet("{id:guid}/playback")]
    public async Task<ActionResult<HlsUrlOut>> Playback(Guid id)
    {
        var stream = await Find(id);
        if (stream is null) return NotFound(new { detail = "Stream not found" });
        if (stream.Status != LiveStreamStatus.Live)
            return Conflict(new { detail = "Stream is not live" });

        var (url, expiresAt) = hls.SignLive(stream.Id);
        return new HlsUrlOut(url, expiresAt.UtcDateTime);
    }

    // Closes the channel for good: the encoder is disconnected and cannot publish again.
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<StreamOut>> End(Guid id, CancellationToken ct)
    {
        var stream = await Find(id);
        if (stream is null) return NotFound(new { detail = "Stream not found" });

        if (stream.Status != LiveStreamStatus.Ended)
        {
            stream.Status = LiveStreamStatus.Ended;
            stream.EndedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await mediaMtx.KickPublisherAsync($"live/{stream.Id}", ct);
        }
        return ToOut(stream);
    }

    private Task<LiveStream?> Find(Guid id)
    {
        var tenantId = User.TenantId();
        return db.LiveStreams.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId);
    }

    private static StreamOut ToOut(LiveStream s) =>
        new(s.Id, s.Name, s.Status, s.CreatedAt, s.StartedAt, s.EndedAt);
}
