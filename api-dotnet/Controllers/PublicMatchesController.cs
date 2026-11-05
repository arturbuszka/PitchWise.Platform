using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Dtos;
using PitchWise.Api.Services;
using PitchWise.Data.Models;
using PitchWise.Data;

namespace PitchWise.Api.Controllers;

// What the public match page and embed read, with no key. Access rules for paid viewing belong here.
[AllowAnonymous]
[ApiController]
[Route("api/public/matches")]
public class PublicMatchesController(AppDbContext db, MatchMapper mapper) : ControllerBase
{
    [HttpGet("{publicId}")]
    public async Task<ActionResult<PublicMatchOut>> Get(string publicId, CancellationToken ct)
    {
        var match = await db.Matches.AsNoTracking().FirstOrDefaultAsync(m => m.PublicId == publicId, ct);
        if (match is null) return NotFound(new { detail = "Match not found" });

        // Until the switcher makes one program, what viewers get afterwards is the input on air at the end.
        var recording = match.Status == MatchStatus.Ended && match.ProgramInputId is { } inputId
            ? await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.MatchInputId == inputId, ct)
            : null;

        return new PublicMatchOut(
            match.Name, match.Status, match.StartedAt, match.EndedAt, mapper.Playback(match), match.RecordingStatus,
            recording is null ? null : mapper.Vod(recording));
    }
}
