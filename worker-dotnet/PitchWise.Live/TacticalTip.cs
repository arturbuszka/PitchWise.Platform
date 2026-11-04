using System.Text;
using System.Text.Json;

namespace PitchWise.Live;

/// <summary>Tactical LLM tips from player position snapshots.</summary>
public sealed class TacticalTip
{
    private const string SystemPrompt =
        "You are a football tactical analyst. " +
        "Given a series of player positions on the pitch (x=0-105m from left goal, y=0-68m from bottom touchline), " +
        "give ONE concise tactical observation (1-3 sentences). " +
        "Focus on formation shape, defensive/offensive balance, space to exploit, or pressing triggers. " +
        "Be direct and specific. Respond in English.";

    private readonly HttpClient _http;
    private readonly LiveSettings _settings;

    public TacticalTip(HttpClient http, LiveSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    /// <summary>Snapshot = timestamp + players [{track_id, cls, pitch_x, pitch_y}].</summary>
    public async Task<string> GetTipAsync(IReadOnlyList<PositionSnapshot> snapshots, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.LlmApiKey)) return "";

        try
        {
            string prompt = BuildPrompt(snapshots);
            var payload = new
            {
                model = _settings.LlmModel,
                messages = new object[]
                {
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = prompt },
                },
                max_tokens = 150,
                temperature = 0.4,
            };
            string url = $"{_settings.LlmBaseUrl.TrimEnd('/')}/chat/completions";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.LlmApiKey}");

            using HttpResponseMessage resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            string body = await resp.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString()?.Trim() ?? "";
        }
        catch
        {
            return "";  // tips are best-effort
        }
    }

    private static string BuildPrompt(IReadOnlyList<PositionSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return "No position data available.";
        var sb = new StringBuilder("Recent player positions:\n\n");
        foreach (PositionSnapshot snap in snapshots.TakeLast(10))
        {
            var lines = new List<string>();
            foreach (PlayerPosition p in snap.Players)
                lines.Add($"  {p.Cls} #{p.TrackId?.ToString() ?? "?"}: ({p.PitchX:F1}m, {p.PitchY:F1}m)");
            if (lines.Count > 0)
                sb.Append($"t={snap.Timestamp:F1}s:\n").Append(string.Join("\n", lines)).Append("\n\n");
        }
        return sb.ToString().TrimEnd();
    }
}

public readonly record struct PlayerPosition(int? TrackId, string Cls, double PitchX, double PitchY);
public sealed record PositionSnapshot(double Timestamp, IReadOnlyList<PlayerPosition> Players);
