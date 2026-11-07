using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Auth;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

// Cutting between a match's cameras. This records what is wanted and tells the broadcast service, which runs the
// switcher, so nothing here can be held up by video.
[ApiController]
[Route("api/matches/{matchId:guid}/switcher")]
public class SwitcherController(
    AppDbContext db, MatchMapper mapper, PreviewSigner preview, AppSettings settings, TimeProvider time, ILogger<SwitcherController> log)
    : ControllerBase
{
    private const int HistoryLimit = 500;
    private static readonly object MatchNotFound = new { detail = "Match not found" };
    private static readonly object InputNotFound = new { detail = "Input not found" };
    private static readonly object MatchEnded = new { detail = "Match has ended" };
    private static readonly object NotEnabled = new { detail = "This match has no switcher" };

    // From here on the program comes from the switcher. It starts when the first camera arrives.
    [HttpPost]
    public async Task<ActionResult<MatchOut>> Enable(Guid matchId, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);

        var switcher = await db.MatchSwitchers.FirstOrDefaultAsync(s => s.MatchId == matchId, ct);
        if (switcher is { Enabled: true }) return await Out(match, switcher, ct);

        var now = time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Whatever is on air now stays on air, now through the switcher.
        var camera = match.ProgramInputId.GetValueOrDefault();
        if (switcher is null)
        {
            // The service sets the real key each time it starts a switcher, so this one is never used.
            var program = new MatchInput
            {
                Id = Guid.NewGuid(),
                MatchId = matchId,
                Name = "Program",
                StreamKeyHash = Secrets.Hash(Secrets.Generate("sk_")),
                IsSwitcherProgram = true,
            };
            db.MatchInputs.Add(program);
            await db.SaveChangesAsync(ct);
            switcher = new MatchSwitcher { MatchId = matchId, ProgramInputId = program.Id };
            db.MatchSwitchers.Add(switcher);
        }
        switcher.Enabled = true;
        switcher.Status = SwitcherStatus.WaitingForCameras;
        switcher.LastSeenAt = now;
        switcher.VideoInputId = camera;
        switcher.AudioInputId = camera;
        match.ProgramInputId = switcher.ProgramInputId;
        AddCut(matchId, SwitcherCutKind.Video, camera, now);
        AddCut(matchId, SwitcherCutKind.Audio, camera, now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        await Notify(matchId, ct);
        return await Out(match, switcher, ct);
    }

    // Back to one input on air, which viewers reach by reloading: the one the switcher was cutting to.
    [HttpDelete]
    public async Task<ActionResult<MatchOut>> Disable(Guid matchId, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);

        var switcher = await db.MatchSwitchers.FirstOrDefaultAsync(s => s.MatchId == matchId && s.Enabled, ct);
        if (switcher is not null)
        {
            match.ProgramInputId = switcher.VideoInputId;
            switcher.Stop();
            await db.SaveChangesAsync(ct);
            await Notify(matchId, ct);
        }
        return await Out(match, switcher, ct);
    }

    [HttpPost("cut")]
    public Task<ActionResult<SwitcherOut>> Cut(Guid matchId, [FromBody] SwitchToIn body, CancellationToken ct) =>
        SwitchTo(matchId, SwitcherCutKind.Video, body.InputId, ct);

    [HttpPost("audio")]
    public Task<ActionResult<SwitcherOut>> Audio(Guid matchId, [FromBody] SwitchToIn body, CancellationToken ct) =>
        SwitchTo(matchId, SwitcherCutKind.Audio, body.InputId, ct);

    // Newest first. Kept after the match, for going through it.
    [HttpGet("cuts")]
    public async Task<ActionResult<List<SwitcherCutOut>>> Cuts(Guid matchId, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        var cuts = await db.SwitcherCuts.AsNoTracking()
            .Where(c => c.MatchId == matchId).OrderByDescending(c => c.Id).Take(HistoryLimit).ToListAsync(ct);
        return cuts.Select(c => new SwitcherCutOut(c.Id, c.Kind, c.InputId, c.Origin, c.At,
            match.StartedAt is { } zero ? Math.Max(0, (c.At - zero).TotalSeconds) : null)).ToList();
    }

    // Short-lived addresses for watching each camera and the program with little delay.
    [HttpGet("preview")]
    public async Task<ActionResult<PreviewOut>> Preview(Guid matchId, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (settings.MediaMtxReadSecret.Length == 0)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { detail = "MEDIAMTX_READ_SECRET is not set on the API." });

        var ids = await db.MatchInputs.AsNoTracking().Where(i => i.MatchId == matchId).Select(i => i.Id).ToListAsync(ct);
        var expires = time.GetUtcNow().UtcDateTime + PreviewSigner.Lifetime;
        var sources = ids.Select(id =>
        {
            var path = MatchInput.PathFor(id);
            var (token, expiresAt) = preview.Sign(path);
            expires = expiresAt;
            return new PreviewSourceOut(id, $"{settings.WebRtcBaseUrl.TrimEnd('/')}/{path}/whep?token={token}");
        }).ToList();
        return new PreviewOut(expires, sources);
    }

    private async Task<ActionResult<SwitcherOut>> SwitchTo(Guid matchId, SwitcherCutKind kind, Guid inputId, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);
        var switcher = await db.MatchSwitchers.FirstOrDefaultAsync(s => s.MatchId == matchId && s.Enabled, ct);
        if (switcher is null) return Conflict(NotEnabled);
        if (!await db.MatchInputs.AnyAsync(i => i.Id == inputId && i.MatchId == matchId && !i.IsSwitcherProgram, ct))
            return NotFound(InputNotFound);

        var current = kind == SwitcherCutKind.Audio ? switcher.AudioInputId : switcher.VideoInputId;
        if (current != inputId)
        {
            if (kind == SwitcherCutKind.Audio) switcher.AudioInputId = inputId;
            else switcher.VideoInputId = inputId;
            AddCut(matchId, kind, inputId, time.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            await Notify(matchId, ct);
        }
        return mapper.ToOut(switcher);
    }

    private void AddCut(Guid matchId, SwitcherCutKind kind, Guid inputId, DateTime at) =>
        db.SwitcherCuts.Add(new SwitcherCut { MatchId = matchId, Kind = kind, InputId = inputId, Origin = SwitcherCutOrigin.Operator, At = at });

    // The change is already saved, and the service picks it up within a second anyway. This only makes it immediate.
    private async Task Notify(Guid matchId, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_notify('switcher', {matchId.ToString()})", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning("Could not tell the broadcast service about match {MatchId} right away: {Message}", matchId, e.Message);
        }
    }

    private Task<Match?> FindMatch(Guid id, CancellationToken ct)
    {
        var tenantId = User.TenantId();
        return db.Matches.FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct);
    }

    private async Task<MatchOut> Out(Match match, MatchSwitcher? switcher, CancellationToken ct) =>
        mapper.ToOut(match,
            await db.MatchInputs.AsNoTracking().Where(i => i.MatchId == match.Id).ToListAsync(ct),
            await db.MatchOutputs.AsNoTracking().Where(o => o.MatchId == match.Id).ToListAsync(ct),
            switcher);
}
