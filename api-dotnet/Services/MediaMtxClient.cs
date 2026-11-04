using System.Text.Json;

namespace PitchWise.Api.Services;

public interface IMediaMtxClient
{
    /// <summary>Paths that currently have a publisher sending media.</summary>
    Task<IReadOnlySet<string>> GetReadyPathsAsync(CancellationToken ct);

    /// <summary>Disconnects whoever is publishing to the path, if anyone is.</summary>
    Task KickPublisherAsync(string path, CancellationToken ct);
}

/// <summary>Talks to the MediaMTX control API (v3).</summary>
public class MediaMtxClient(HttpClient http) : IMediaMtxClient
{
    public async Task<IReadOnlySet<string>> GetReadyPathsAsync(CancellationToken ct)
    {
        using var doc = await ListPathsAsync(ct);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(p => p.GetProperty("ready").GetBoolean())
            .Select(p => p.GetProperty("name").GetString()!)
            .ToHashSet();
    }

    public async Task KickPublisherAsync(string path, CancellationToken ct)
    {
        using var doc = await ListPathsAsync(ct);
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() != path) continue;
            if (!item.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object) return;
            if (source.GetProperty("type").GetString() != "rtmpConn") return;

            var id = source.GetProperty("id").GetString();
            using var response = await http.PostAsync($"/v3/rtmpconns/kick/{id}", null, ct);
            response.EnsureSuccessStatusCode();
            return;
        }
    }

    private async Task<JsonDocument> ListPathsAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/v3/paths/list?itemsPerPage=1000", ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
    }
}
