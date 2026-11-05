using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Auth;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Models;
using StackExchange.Redis;

namespace PitchWise.Api.Controllers;

// A match: encoders publish to its inputs over RTMP or SRT, and viewers watch over signed HLS.
[ApiController]
[Route("api/matches")]
public class MatchesController(
    AppDbContext db, MatchMapper mapper, IMediaMtxClient mediaMtx, RecordingQueue recordings, ILogger<MatchesController> log)
    : ControllerBase
{
    private static readonly object MatchNotFound = new { detail = "Match not found" };
    private static readonly object InputNotFound = new { detail = "Input not found" };
    private static readonly object MatchEnded = new { detail = "Match has ended" };

    [HttpPost]
    public async Task<ActionResult<CreatedMatchOut>> Create([FromBody] CreateMatchIn body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "name is required" });

        var tenantId = User.TenantId();
        var name = body.Name.Trim();
        var streamKey = Secrets.Generate("sk_");
        var match = new Match { Id = Guid.NewGuid(), TenantId = tenantId, Name = name, PublicId = Secrets.GeneratePublicId() };
        var input = new MatchInput
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            Name = string.IsNullOrWhiteSpace(body.InputName) ? "Main camera" : body.InputName.Trim(),
            StreamKeyHash = Secrets.Hash(streamKey),
        };

        // A match and its input point at each other, so it takes more than one write.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var analysis = new AnalysisSession { TenantId = tenantId, Name = name };
        db.AnalysisSessions.Add(analysis);
        await db.SaveChangesAsync(ct);
        match.AnalysisId = analysis.Id;
        db.Matches.Add(match);
        await db.SaveChangesAsync(ct);
        db.MatchInputs.Add(input);
        await db.SaveChangesAsync(ct);
        match.ProgramInputId = input.Id;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = match.Id },
            new CreatedMatchOut(mapper.ToOut(match, [input]), mapper.Access(input.Id, streamKey)));
    }

    [HttpGet]
    public async Task<List<MatchOut>> List(CancellationToken ct)
    {
        var tenantId = User.TenantId();
        var matches = await db.Matches
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync(ct);
        var ids = matches.Select(m => m.Id).ToList();
        var inputs = (await db.MatchInputs.AsNoTracking().Where(i => ids.Contains(i.MatchId)).ToListAsync(ct))
            .ToLookup(i => i.MatchId);
        return matches.Select(m => mapper.ToOut(m, inputs[m.Id])).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MatchOut>> Get(Guid id, CancellationToken ct)
    {
        var match = await Find(id, ct);
        return match is null ? NotFound(MatchNotFound) : await Out(match, ct);
    }

    // Mints a signed URL for the edge; the API never carries the video itself.
    [HttpGet("{id:guid}/playback")]
    public async Task<ActionResult<PlaybackOut>> Playback(Guid id, CancellationToken ct)
    {
        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);
        return mapper.Playback(match) is { } playback ? playback : Conflict(new { detail = "Match is not live" });
    }

    [HttpPost("{id:guid}/inputs")]
    public async Task<ActionResult<CreatedInputOut>> AddInput(Guid id, [FromBody] AddInputIn body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
            return BadRequest(new { detail = "name is required" });

        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);

        var streamKey = Secrets.Generate("sk_");
        var input = new MatchInput
        {
            Id = Guid.NewGuid(),
            MatchId = match.Id,
            Name = body.Name.Trim(),
            StreamKeyHash = Secrets.Hash(streamKey),
        };
        db.MatchInputs.Add(input);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = match.Id },
            new CreatedInputOut(mapper.ToOut(match, input), mapper.Access(input.Id, streamKey)));
    }

    // For a lost or leaked key: it can't be shown again, so it is replaced. Whoever is publishing
    // with the old one is disconnected.
    [HttpPost("{id:guid}/inputs/{inputId:guid}/rotate-key")]
    public async Task<ActionResult<InputAccessOut>> RotateKey(Guid id, Guid inputId, CancellationToken ct)
    {
        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);

        var input = await db.MatchInputs.FirstOrDefaultAsync(i => i.Id == inputId && i.MatchId == id, ct);
        if (input is null) return NotFound(InputNotFound);

        var streamKey = Secrets.Generate("sk_");
        input.StreamKeyHash = Secrets.Hash(streamKey);
        await db.SaveChangesAsync(ct);
        await Kick(input.Id, ct);

        return mapper.Access(input.Id, streamKey);
    }

    // Chooses the input viewers get. Players reload to change over, so this is a choice of source
    // rather than a cut: a seamless one needs the switcher.
    [HttpPut("{id:guid}/program")]
    public async Task<ActionResult<MatchOut>> SetProgram(Guid id, [FromBody] SetProgramIn body, CancellationToken ct)
    {
        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);
        if (!await db.MatchInputs.AnyAsync(i => i.Id == body.InputId && i.MatchId == id, ct))
            return NotFound(InputNotFound);

        match.ProgramInputId = body.InputId;
        await db.SaveChangesAsync(ct);
        return await Out(match, ct);
    }

    // Closes the match for good: encoders are disconnected and cannot publish again.
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<MatchOut>> End(Guid id, CancellationToken ct)
    {
        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);

        if (match.Status != MatchStatus.Ended)
        {
            match.Status = MatchStatus.Ended;
            match.EndedAt = DateTime.UtcNow;
            match.RecordingStatus = RecordingStatus.Processing;
            await db.SaveChangesAsync(ct);

            var inputIds = await db.MatchInputs.Where(i => i.MatchId == id).Select(i => i.Id).ToListAsync(ct);
            foreach (var inputId in inputIds) await Kick(inputId, ct);
            await QueueRecording(id);
        }
        return await Out(match, ct);
    }

    // For a recording that failed, say on a full disk, or a job that never reached the worker. The
    // raw segments of anything unfinished are still there, and what is finished isn't done again.
    [HttpPost("{id:guid}/recording/retry")]
    public async Task<ActionResult<MatchOut>> RetryRecording(Guid id, CancellationToken ct)
    {
        var match = await Find(id, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.RecordingStatus is not (RecordingStatus.Failed or RecordingStatus.Processing))
            return Conflict(new { detail = "There is no unfinished recording" });

        match.RecordingStatus = RecordingStatus.Processing;
        match.RecordingError = null;
        await db.SaveChangesAsync(ct);
        await QueueRecording(id);
        return await Out(match, ct);
    }

    // The match is already over, so a failure here must not read as if it wasn't. The status stays
    // "processing", which the worker picks up again when it starts and which can be retried.
    private async Task QueueRecording(Guid id)
    {
        try
        {
            await recordings.EnqueueAsync(id);
        }
        catch (RedisException e)
        {
            log.LogWarning("Could not queue the recording of match {MatchId}: {Message}", id, e.Message);
        }
    }

    // The change is already saved, so a failure here must not read as if it wasn't. An encoder can
    // only stay on while MediaMTX is unreachable, and it can't reconnect without a valid key.
    private async Task Kick(Guid inputId, CancellationToken ct)
    {
        try
        {
            await mediaMtx.KickPublisherAsync(MatchInput.PathFor(inputId), ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning("Could not disconnect the encoder on input {InputId}: {Message}", inputId, e.Message);
        }
    }

    private Task<Match?> Find(Guid id, CancellationToken ct)
    {
        var tenantId = User.TenantId();
        return db.Matches.FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct);
    }

    private async Task<MatchOut> Out(Match match, CancellationToken ct) =>
        mapper.ToOut(match, await db.MatchInputs.AsNoTracking().Where(i => i.MatchId == match.Id).ToListAsync(ct));
}
