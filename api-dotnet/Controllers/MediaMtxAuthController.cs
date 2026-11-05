using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Dtos;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Controllers;

// MediaMTX asks this endpoint whether to allow each connection. The secret in the URL proves the
// caller is MediaMTX, since this API is reachable from outside the deployment.
[AllowAnonymous]
[ApiController]
[Route("internal/mediamtx/auth")]
public class MediaMtxAuthController(AppDbContext db, AppSettings settings) : ControllerBase
{
    [HttpPost("{secret}")]
    public async Task<IActionResult> Authorize(string secret, [FromBody] MediaMtxAuthIn request)
    {
        if (!Secrets.FixedTimeEquals(secret, settings.MediaMtxAuthSecret))
            return Unauthorized();

        return request.Action switch
        {
            // HLS comes through the edge, which has already checked the viewer's signature. RTMP and
            // SRT are open to the internet, and reading over them would skip that check.
            "read" => request.Protocol == "hls" ? Ok() : Unauthorized(),
            "publish" => await CanPublish(request) ? Ok() : Unauthorized(),
            _ => Unauthorized(),
        };
    }

    private async Task<bool> CanPublish(MediaMtxAuthIn request)
    {
        const string prefix = "live/";
        if (request.Path is null || !request.Path.StartsWith(prefix)
            || !Guid.TryParse(request.Path[prefix.Length..], out var inputId))
            return false;

        var query = QueryHelpers.ParseQuery(request.Query);
        if (!query.TryGetValue("key", out var key) || string.IsNullOrEmpty(key))
            return false;

        var hash = Secrets.Hash(key.ToString());
        return await (
            from input in db.MatchInputs
            join match in db.Matches on input.MatchId equals match.Id
            where input.Id == inputId && input.StreamKeyHash == hash && match.Status != MatchStatus.Ended
            select input.Id).AnyAsync();
    }
}
