using PitchWise.Api.Dtos;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Services;

/// <summary>Builds what the API says about a match, including the addresses that depend on the deployment.</summary>
public class MatchMapper(AppSettings settings, SignalTracker signals, HlsSigner hls)
{
    public MatchOut ToOut(Match match, IEnumerable<MatchInput> inputs)
    {
        var web = settings.WebOrigin.TrimEnd('/');
        var embedUrl = $"{web}/embed/{match.PublicId}";
        return new MatchOut(
            match.Id,
            match.Name,
            match.Status,
            match.PublicId,
            $"{web}/m/{match.PublicId}",
            embedUrl,
            $"<iframe src=\"{embedUrl}\" width=\"960\" height=\"540\" allow=\"autoplay; fullscreen\" allowfullscreen frameborder=\"0\" title=\"Live match\"></iframe>",
            match.ProgramInputId,
            match.AnalysisId,
            match.CreatedAt,
            match.StartedAt,
            match.EndedAt,
            inputs.OrderBy(i => i.CreatedAt).Select(i => ToOut(match, i)).ToList());
    }

    public MatchInputOut ToOut(Match match, MatchInput input) =>
        new(input.Id, input.Name, input.Id == match.ProgramInputId, signals.Get(MatchInput.PathFor(input.Id)));

    public InputAccessOut Access(Guid inputId, string streamKey)
    {
        var rtmp = settings.RtmpIngestUrl.TrimEnd('/');
        var streamName = $"{inputId}?key={streamKey}";
        // Empty user and password, so the key arrives the same way it does over RTMP.
        var srtId = $"publish:{MatchInput.PathFor(inputId)}:::key={streamKey}";
        return new InputAccessOut(
            inputId,
            streamKey,
            $"{rtmp}/live/{streamName}",
            $"{rtmp}/live",
            streamName,
            $"{settings.SrtIngestUrl.TrimEnd('/')}?streamid={srtId}&pkt_size=1316");
    }

    /// <summary>The signed URL of what is on air. Null unless the match is live.</summary>
    public PlaybackOut? Playback(Match match)
    {
        if (match.Status != MatchStatus.Live || match.ProgramInputId is not { } inputId) return null;
        var (url, expiresAt) = hls.SignLive(inputId);
        return new PlaybackOut(url, expiresAt.UtcDateTime, inputId);
    }
}
