using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using PitchWise.Api.Dtos;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;
using PitchWise.Data.Outputs;

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
            "read" => CanRead(request) ? Ok() : Unauthorized(),
            "publish" => await CanPublish(request) ? Ok() : Unauthorized(),
            _ => Unauthorized(),
        };
    }

    // HLS comes through the edge, which has already checked the viewer's signature. RTSP is how our own
    // services read: its port isn't published, and it needs their secret on top of that. RTMP and SRT are
    // open to the internet and would skip the edge's check, so no credentials ever open those for reading.
    private bool CanRead(MediaMtxAuthIn request) => request.Protocol switch
    {
        "hls" => true,
        "rtsp" => settings.MediaMtxReadSecret.Length > 0
            && request.User == OutputReader.User
            && Secrets.FixedTimeEquals(request.Password ?? "", settings.MediaMtxReadSecret),
        _ => false,
    };

    private async Task<bool> CanPublish(MediaMtxAuthIn request)
    {
        // The only ways in for encoders. The others are for reading, or aren't open at all.
        if (request.Protocol is not ("rtmp" or "srt")) return false;

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
