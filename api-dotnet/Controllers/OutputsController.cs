using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Auth;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using PitchWise.Data;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;

namespace PitchWise.Api.Controllers;

// Where a match is sent on to, such as YouTube or Twitch. This only records what is wanted: the broadcast
// service does the sending, so nothing here can hold up or be held up by a destination.
[ApiController]
[Route("api/matches/{matchId:guid}/outputs")]
public class OutputsController(AppDbContext db, MatchMapper mapper, OutputKeyCipher cipher) : ControllerBase
{
    private static readonly object MatchNotFound = new { detail = "Match not found" };
    private static readonly object OutputNotFound = new { detail = "Output not found" };
    private static readonly object MatchEnded = new { detail = "Match has ended" };

    [HttpGet]
    public async Task<ActionResult<List<OutputOut>>> List(Guid matchId, CancellationToken ct)
    {
        if (await FindMatch(matchId, ct) is null) return NotFound(MatchNotFound);
        var outputs = await db.MatchOutputs.AsNoTracking()
            .Where(o => o.MatchId == matchId).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        return outputs.Select(mapper.ToOut).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<OutputOut>> Create(Guid matchId, [FromBody] CreateOutputIn body, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);
        if (string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { detail = "name is required" });

        var url = body.Url?.Trim() ?? "";
        var problem = OutputTarget.UrlProblem(url) ?? OutputTarget.KeyProblem(body.StreamKey ?? "");
        if (problem is not null) return BadRequest(new { detail = problem });
        if (!cipher.IsConfigured) return Unavailable();

        var output = new MatchOutput { Id = Guid.NewGuid(), MatchId = matchId, Name = body.Name.Trim(), Url = url };
        output.StreamKeyEncrypted = cipher.Encrypt(output.Id, body.StreamKey!);
        db.MatchOutputs.Add(output);
        await db.SaveChangesAsync(ct);
        return Created($"/api/matches/{matchId}/outputs/{output.Id}", mapper.ToOut(output));
    }

    // Anything left out stays as it is. A new key is how an output recovers from a changed encryption key.
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<OutputOut>> Update(Guid matchId, Guid id, [FromBody] UpdateOutputIn body, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);
        var output = await FindOutput(matchId, id, ct);
        if (output is null) return NotFound(OutputNotFound);

        var url = body.Url?.Trim();
        var problem = (url is null ? null : OutputTarget.UrlProblem(url))
            ?? (body.StreamKey is null ? null : OutputTarget.KeyProblem(body.StreamKey));
        if (problem is not null) return BadRequest(new { detail = problem });
        if (body.Name is not null && string.IsNullOrWhiteSpace(body.Name)) return BadRequest(new { detail = "name is required" });
        if (body.StreamKey is not null && !cipher.IsConfigured) return Unavailable();

        if (body.Name is not null) output.Name = body.Name.Trim();
        var targetChanged = false;
        if (url is not null && url != output.Url)
        {
            output.Url = url;
            targetChanged = true;
        }
        if (body.StreamKey is not null)
        {
            output.StreamKeyEncrypted = cipher.Encrypt(output.Id, body.StreamKey);
            targetChanged = true;
        }
        if (targetChanged)
        {
            if (output.Enabled) output.Start(DateTime.UtcNow);
            else output.Stop();
        }

        await db.SaveChangesAsync(ct);
        return mapper.ToOut(output);
    }

    // Also how to retry: it clears the failures and starts over at once.
    [HttpPost("{id:guid}/start")]
    public async Task<ActionResult<OutputOut>> Start(Guid matchId, Guid id, CancellationToken ct)
    {
        var match = await FindMatch(matchId, ct);
        if (match is null) return NotFound(MatchNotFound);
        if (match.Status == MatchStatus.Ended) return Conflict(MatchEnded);
        var output = await FindOutput(matchId, id, ct);
        if (output is null) return NotFound(OutputNotFound);

        output.Start(DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return mapper.ToOut(output);
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<ActionResult<OutputOut>> Stop(Guid matchId, Guid id, CancellationToken ct)
    {
        if (await FindMatch(matchId, ct) is null) return NotFound(MatchNotFound);
        var output = await FindOutput(matchId, id, ct);
        if (output is null) return NotFound(OutputNotFound);

        output.Stop();
        await db.SaveChangesAsync(ct);
        return mapper.ToOut(output);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid matchId, Guid id, CancellationToken ct)
    {
        if (await FindMatch(matchId, ct) is null) return NotFound(MatchNotFound);
        var output = await FindOutput(matchId, id, ct);
        if (output is null) return NotFound(OutputNotFound);

        db.MatchOutputs.Remove(output);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private ObjectResult Unavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { detail = OutputKeyCipher.NotConfigured });

    private Task<Match?> FindMatch(Guid id, CancellationToken ct)
    {
        var tenantId = User.TenantId();
        return db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id && m.TenantId == tenantId, ct);
    }

    private Task<MatchOutput?> FindOutput(Guid matchId, Guid id, CancellationToken ct) =>
        db.MatchOutputs.FirstOrDefaultAsync(o => o.Id == id && o.MatchId == matchId, ct);
}
