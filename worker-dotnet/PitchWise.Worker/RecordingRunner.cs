using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PitchWise.Data;
using PitchWise.Data.Config;
using PitchWise.Data.Models;

namespace PitchWise.Worker;

/// <summary>A recording that can't be put together. The raw segments stay, so it can be tried again.</summary>
public sealed class RecordingException(string message) : Exception(message);

/// <summary>
/// Once a match has ended, turns the segments MediaMTX wrote for each input into one MP4 per input
/// and adds them to the match's analysis. Safe to repeat: an input that already has a video is
/// skipped, and an input's raw segments are only deleted once its video is safely recorded.
/// </summary>
public sealed class RecordingRunner(
    IServiceScopeFactory scopes, AppSettings settings, WorkerSettings worker, ILogger<RecordingRunner> log)
{
    public async Task RunAsync(Guid matchId, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Match? match = await db.Matches.FirstOrDefaultAsync(m => m.Id == matchId, ct);
        if (match is null || match.Status != MatchStatus.Ended) return;

        match.RecordingStatus = RecordingStatus.Processing;
        match.RecordingError = null;
        await db.SaveChangesAsync(ct);

        var errors = new List<string>();
        try
        {
            List<MatchInput> inputs = await db.MatchInputs.Where(i => i.MatchId == matchId).ToListAsync(ct);
            await WaitForFilesToSettle(inputs, ct);

            // Without a program signal there is no time zero, so the first thing recorded becomes it.
            DateTime? zero = match.StartedAt ?? inputs
                .SelectMany(i => ListSegmentFiles(i.Id))
                .Select(f => (RecordedSegment.TryParseStart(f, out DateTime t), t))
                .Where(x => x.Item1)
                .Select(x => (DateTime?)x.t)
                .Min();

            if (zero is not null)
            {
                foreach (MatchInput input in inputs.OrderByDescending(i => i.Id == match.ProgramInputId).ThenBy(i => i.CreatedAt))
                {
                    try
                    {
                        await RecordInput(db, match, input, zero.Value, ct);
                    }
                    catch (RecordingException e)
                    {
                        log.LogError("Recording of input {InputId} failed: {Message}", input.Id, e.Message);
                        errors.Add($"{input.Name}: {e.Message}");
                    }
                }
            }
        }
        catch (RecordingException e)
        {
            errors.Add(e.Message);
        }

        bool hasVideo = await db.Videos.AnyAsync(v => v.AnalysisId == match.AnalysisId && v.MatchInputId != null, ct);
        match.RecordingError = errors.Count > 0 ? string.Join("; ", errors) : null;
        match.RecordingStatus = errors.Count > 0 ? RecordingStatus.Failed
            : hasVideo ? RecordingStatus.Ready
            : RecordingStatus.None;
        await db.SaveChangesAsync(ct);
    }

    private async Task RecordInput(AppDbContext db, Match match, MatchInput input, DateTime zero, CancellationToken ct)
    {
        if (await db.Videos.AnyAsync(v => v.MatchInputId == input.Id, ct))
        {
            DeleteRawSegments(input.Id);
            return;
        }

        string[] files = ListSegmentFiles(input.Id).ToArray();
        if (files.Length == 0) return;

        var segments = new List<RecordedSegment>();
        string? signature = null;
        foreach (string file in files)
        {
            MediaProbe? probe = FfmpegTools.Probe(file);
            // A crash just after a file was created can leave one with no media in it.
            if (probe?.MediaEnd is not double length || !RecordedSegment.TryParseStart(file, out DateTime start))
            {
                log.LogWarning("Skipping unreadable recording segment {File}", file);
                continue;
            }
            // Joined without re-encoding, the result has only the first segment's stream settings,
            // so anything after a change would come out garbled. Better to say so.
            if (signature is not null && signature != probe.Signature)
                throw new RecordingException(
                    "the encoder's settings changed during the match, so its segments can't be joined without re-encoding");
            signature = probe.Signature;
            segments.Add(new RecordedSegment(file, start, length));
        }

        RecordingPlan plan = RecordingTimeline.Plan(segments, zero, FfmpegTools.KeyframeAtOrAfter);
        if (plan.Entries.Count == 0) return;

        EnsureDiskSpace(files);

        string filename = $"rec_{input.Id:N}.mp4";
        string finalPath = Path.Combine(settings.VodDir, filename);
        string tempPath = Path.Combine(settings.VodDir, $"{Guid.NewGuid():N}.tmp.mp4");
        string listPath = Path.Combine(settings.VodDir, $"{Guid.NewGuid():N}.ffconcat");
        try
        {
            await File.WriteAllTextAsync(listPath, RecordingTimeline.ToConcatList(plan.Entries), ct);
            string? problem = FfmpegTools.Stitch(listPath, tempPath);
            if (problem is not null) throw new RecordingException($"ffmpeg could not join the segments: {problem}");

            // The raw segments go once this succeeds, so check that the result really is the match.
            MediaProbe? result = FfmpegTools.Probe(tempPath);
            if (result is not { HasVideo: true, Duration: double duration } || duration < plan.End - 2)
                throw new RecordingException(
                    $"the joined file is too short: {result?.Duration:F1} s instead of {plan.End:F1} s");

            File.Move(tempPath, finalPath, overwrite: true);

            int order = await db.Videos.CountAsync(v => v.AnalysisId == match.AnalysisId, ct);
            db.Videos.Add(new Video
            {
                AnalysisId = match.AnalysisId,
                Name = input.Name,
                Filename = filename,
                DurationSeconds = duration,
                Fps = result.Fps,
                Order = order,
                MatchInputId = input.Id,
                Gaps = plan.Gaps.ToList(),
            });
            await db.SaveChangesAsync(ct);

            // Moments marked during the broadcast had no video to point at, but their times are
            // already on the timeline this video uses.
            if (input.Id == match.ProgramInputId)
            {
                int videoId = await db.Videos.Where(v => v.MatchInputId == input.Id).Select(v => v.Id).SingleAsync(ct);
                await db.Events
                    .Where(e => e.AnalysisId == match.AnalysisId && e.VideoId == null)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.VideoId, videoId), ct);
            }
        }
        finally
        {
            File.Delete(listPath);
            File.Delete(tempPath);
        }

        DeleteRawSegments(input.Id);
    }

    private IEnumerable<string> ListSegmentFiles(Guid inputId)
    {
        string dir = settings.InputRecordingDir(inputId);
        return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.mp4") : [];
    }

    // Ending a match disconnects its encoders, but MediaMTX may still be flushing to disk.
    private async Task WaitForFilesToSettle(IEnumerable<MatchInput> inputs, CancellationToken ct)
    {
        var settled = TimeSpan.FromSeconds(worker.RecordingSettleSeconds);
        DateTime giveUp = DateTime.UtcNow.AddSeconds(worker.RecordingSettleLimitSeconds);

        while (true)
        {
            List<string> files = inputs.SelectMany(i => ListSegmentFiles(i.Id)).ToList();
            DateTime newest = files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
            TimeSpan quiet = DateTime.UtcNow - newest;
            if (quiet >= settled) return;
            if (DateTime.UtcNow > giveUp)
                throw new RecordingException("an input is still being written to, so its encoder may not have been disconnected");
            await Task.Delay(settled - quiet, ct);
        }
    }

    private void EnsureDiskSpace(string[] files)
    {
        long needed = files.Sum(f => new FileInfo(f).Length);
        long free = new DriveInfo(Path.GetFullPath(settings.VodDir)).AvailableFreeSpace;
        if (free < needed + needed / 20)
            throw new RecordingException($"not enough disk space: {needed / 1_000_000} MB needed, {free / 1_000_000} MB free");
    }

    private void DeleteRawSegments(Guid inputId)
    {
        string dir = settings.InputRecordingDir(inputId);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The video is safe. Leftovers only cost disk space.
            log.LogWarning("Could not delete the raw segments in {Dir}: {Message}", dir, e.Message);
        }
    }
}
