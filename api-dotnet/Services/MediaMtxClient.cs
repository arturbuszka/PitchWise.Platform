using System.Text.Json;

namespace PitchWise.Api.Services;

/// <summary>An encoder sending media to a path. The counters run from when it connected.</summary>
public record Publisher(
    string ConnectionId,
    string Protocol,
    DateTime ConnectedSince,
    long BytesReceived,
    IReadOnlyList<string> Tracks,
    SrtStats? Srt = null);

/// <summary>What SRT reports about the link. RTMP runs over TCP, which hides all of this.</summary>
public record SrtStats(long PacketsReceived, long PacketsLost, long PacketsDropped, double RttMs);

public interface IMediaMtxClient
{
    /// <summary>Everyone publishing right now, by path.</summary>
    Task<IReadOnlyDictionary<string, Publisher>> GetPublishersAsync(CancellationToken ct);

    /// <summary>Disconnects whoever is publishing to the path, if anyone is.</summary>
    Task KickPublisherAsync(string path, CancellationToken ct);
}

/// <summary>Talks to the MediaMTX control API (v3).</summary>
public class MediaMtxClient(HttpClient http) : IMediaMtxClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, Publisher>> GetPublishersAsync(CancellationToken ct)
    {
        var paths = (await ListAsync<PathItem>("/v3/paths/list", ct))
            .Where(p => p is { Ready: true, Source: not null })
            .ToList();

        var srt = paths.Any(p => p.Source!.Type == "srtConn")
            ? (await ListAsync<SrtConnItem>("/v3/srtconns/list", ct)).ToDictionary(c => c.Id)
            : [];

        return paths.ToDictionary(p => p.Name, p => new Publisher(
            p.Source!.Id,
            p.Source.Type.Replace("Conn", "").ToLowerInvariant(),
            p.ReadyTime ?? DateTime.UtcNow,
            p.InboundBytes,
            p.Tracks ?? [],
            srt.TryGetValue(p.Source.Id, out var c)
                ? new SrtStats(c.PacketsReceivedUnique, c.PacketsReceivedLoss, c.PacketsReceivedDrop, c.MsRTT)
                : null));
    }

    public async Task KickPublisherAsync(string path, CancellationToken ct)
    {
        var source = (await ListAsync<PathItem>("/v3/paths/list", ct)).FirstOrDefault(p => p.Name == path)?.Source;
        var endpoint = source?.Type switch
        {
            "rtmpConn" => "rtmpconns",
            "srtConn" => "srtconns",
            _ => null,
        };
        if (endpoint is null) return;

        using var response = await http.PostAsync($"/v3/{endpoint}/kick/{source!.Id}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<T>> ListAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync($"{url}?itemsPerPage=1000", ct);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<ListResponse<T>>(Json, ct);
        return list!.Items;
    }

    private record ListResponse<T>(List<T> Items);

    private record PathSource(string Type, string Id);

    private record PathItem(
        string Name,
        bool Ready,
        // Null for a path nobody is publishing to, which stays listed for as long as it is in use.
        DateTime? ReadyTime,
        PathSource? Source,
        long InboundBytes,
        List<string>? Tracks);

    private record SrtConnItem(
        string Id,
        long PacketsReceivedUnique,
        long PacketsReceivedLoss,
        long PacketsReceivedDrop,
        double MsRTT);
}
