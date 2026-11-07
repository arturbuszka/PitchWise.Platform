using PitchWise.Api.Dtos;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Api.Services;

/// <summary>Builds what the API says about a match, including the addresses that depend on the deployment.</summary>
public class MatchMapper(AppSettings settings, SignalTracker signals, HlsSigner hls)
{
    // The service reports every second or two, so this is several missed reports.
    private static readonly TimeSpan OutputServiceQuiet = TimeSpan.FromSeconds(15);

    public MatchOut ToOut(Match match, IEnumerable<MatchInput> inputs, IEnumerable<MatchOutput> outputs)
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
            match.RecordingStatus,
            match.RecordingError,
            inputs.OrderBy(i => i.CreatedAt).Select(i => ToOut(match, i)).ToList(),
            outputs.OrderBy(o => o.CreatedAt).Select(ToOut).ToList());
    }

    public OutputOut ToOut(MatchOutput output)
    {
        var quiet = output.Enabled && output.LastSeenAt < DateTime.UtcNow - OutputServiceQuiet;
        return new OutputOut(
            output.Id,
            output.Name,
            output.Url,
            output.Enabled,
            quiet ? OutputStatus.Offline : output.Status,
            quiet ? "The broadcast service isn't responding, so this output's state is unknown." : output.Error,
            output.Warning,
            output.Attempts,
            output.NextRetryAt,
            output.LiveSince);
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

    public VodOut Vod(Video video)
    {
        var (url, expiresAt) = hls.SignVod(video.Filename);
        return new VodOut(url, expiresAt.UtcDateTime, video.DurationSeconds,
            video.Gaps.Select(g => new GapOut(g.Start, g.End)).ToList());
    }

    /// <summary>The signed URL of what is on air. Null unless the match is live.</summary>
    public PlaybackOut? Playback(Match match)
    {
        if (match.Status != MatchStatus.Live || match.ProgramInputId is not { } inputId) return null;
        var (url, expiresAt) = hls.SignLive(inputId);
        return new PlaybackOut(url, expiresAt.UtcDateTime, inputId);
    }
}
