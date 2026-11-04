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
            // MediaMTX's HLS port is reachable only by the edge, which has already checked the
            // viewer's signature, so reads need no second check.
            "read" => Ok(),
            "publish" => await CanPublish(request) ? Ok() : Unauthorized(),
            _ => Unauthorized(),
        };
    }

    private async Task<bool> CanPublish(MediaMtxAuthIn request)
    {
        const string prefix = "live/";
        if (request.Path is null || !request.Path.StartsWith(prefix)
            || !Guid.TryParse(request.Path[prefix.Length..], out var streamId))
            return false;

        var query = QueryHelpers.ParseQuery(request.Query);
        if (!query.TryGetValue("key", out var key) || string.IsNullOrEmpty(key))
            return false;

        var hash = Secrets.Hash(key.ToString());
        return await db.LiveStreams.AnyAsync(s =>
            s.Id == streamId && s.StreamKeyHash == hash && s.Status != LiveStreamStatus.Ended);
    }
}
