namespace PitchWise.Broadcast;

/// <summary>What a look at a signal's tracks found, before anything is sent.</summary>
public sealed record SignalVerdict(string? Problem, string? Warning);

/// <summary>
/// A copy can only send what the destination takes, and ffmpeg won't always say when it can't: H265 over RTMP
/// passes through with no error at all, and whatever the destination then does is out of sight. So the signal
/// is checked first. Both YouTube and Twitch take H264 with AAC. YouTube lists more, but not on terms that hold
/// for every channel. MP3 is left out because MediaMTX reports it and MP2, which FLV can't carry, the same way.
/// </summary>
public static class SignalCheck
{
    private static readonly string[] AudioCodecs =
        ["MPEG-4 Audio", "MPEG-1/2 Audio", "Opus", "AC3", "G711", "G722", "LPCM", "Vorbis", "Speex"];

    public static SignalVerdict Check(IReadOnlyList<string> tracks)
    {
        var video = tracks.FirstOrDefault(t => !AudioCodecs.Contains(t));
        var audio = tracks.FirstOrDefault(AudioCodecs.Contains);

        if (video is null)
            return new SignalVerdict("The signal has no video, so there is nothing to send.", null);
        if (video != "H264")
            return new SignalVerdict($"The signal's video is {video}, and this output sends it without re-encoding. " +
                "Set the encoder to H264.", null);
        if (audio is null)
            return new SignalVerdict(null, "The signal has no audio. A destination may refuse it or warn about it.");
        if (audio != "MPEG-4 Audio")
            return new SignalVerdict($"The signal's audio is {audio}, and this output sends it without re-encoding. " +
                "Set the encoder to AAC.", null);
        return new SignalVerdict(null, null);
    }
}
