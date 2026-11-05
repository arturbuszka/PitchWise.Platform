using System.Globalization;
using System.Text.Json;

namespace PitchWise.Worker;

/// <summary>What ffprobe says about a media file.</summary>
public sealed record MediaProbe(double? Duration, double? MediaEnd, double? Fps, bool HasVideo, string Signature)
{
    public static MediaProbe? Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out JsonElement streams)) return null;

        double? end = null;
        double? fps = null;
        bool hasVideo = false;
        var signature = new List<string>();
        foreach (JsonElement s in streams.EnumerateArray())
        {
            string type = Text(s, "codec_type") ?? "";
            if (type != "video" && type != "audio") continue;

            // The shortest stream, since a video stream runs on past the audio by its reorder delay.
            if (Number(s, "start_time") is double from && Number(s, "duration") is double length)
                end = end is null ? from + length : Math.Min(end.Value, from + length);

            if (type == "video")
            {
                hasVideo = true;
                fps = Rate(Text(s, "r_frame_rate"));
            }
            signature.Add(string.Join('/', type, Text(s, "codec_name"), Text(s, "width"), Text(s, "height"),
                Text(s, "sample_rate"), Text(s, "channels"), Text(s, "extradata_hash")));
        }
        if (signature.Count == 0) return null;
        signature.Sort(StringComparer.Ordinal);

        double? duration = doc.RootElement.TryGetProperty("format", out JsonElement format) ? Number(format, "duration") : null;
        return new MediaProbe(duration, end, fps, hasVideo, string.Join(' ', signature));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;

    private static double? Number(JsonElement e, string name) =>
        double.TryParse(Text(e, name), CultureInfo.InvariantCulture, out double n) ? n : null;

    private static double? Rate(string? fraction)
    {
        string[] parts = (fraction ?? "").Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], CultureInfo.InvariantCulture, out double n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out double d) && d > 0 && n > 0
            ? n / d
            : null;
    }
}
