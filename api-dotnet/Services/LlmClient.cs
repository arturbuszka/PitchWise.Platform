using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PitchWise.Data.Config;

namespace PitchWise.Api.Services;

// Client for any API compatible with OpenAI Chat Completions, with
// SSE streaming; switching provider = base_url + api_key + model in the config.
public class LlmClient
{
    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public LlmClient(HttpClient http, AppSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    // Streams successive text fragments (content deltas) from the model.
    public async IAsyncEnumerable<string> StreamChatAsync(
        IEnumerable<(string Role, string Content)> messages,
        string? system,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var payloadMessages = new List<object>();
        if (!string.IsNullOrEmpty(system))
            payloadMessages.Add(new { role = "system", content = system });
        foreach (var m in messages)
            payloadMessages.Add(new { role = m.Role, content = m.Content });

        var body = new
        {
            model = _settings.LlmModel,
            messages = payloadMessages,
            stream = true,
            max_tokens = 1024,
        };

        var url = $"{_settings.LlmBaseUrl.TrimEnd('/')}/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(_settings.LlmApiKey))
        {
            if (_settings.LlmProvider == "anthropic")
                req.Headers.TryAddWithoutValidation("x-api-key", _settings.LlmApiKey);
            else
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.LlmApiKey}");
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:"))
                continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]")
                break;

            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var deltaEl) &&
                    deltaEl.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.String)
                {
                    delta = contentEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(delta))
                yield return delta;
        }
    }
}
