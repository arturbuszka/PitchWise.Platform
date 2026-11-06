using System.Net.Http.Json;
using System.Text.Json;

namespace PitchWise.Broadcast;

public interface IOutputSources
{
    /// <summary>The tracks of every path with a signal right now. Throws if MediaMTX can't say.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken ct);
}

/// <summary>Asks the MediaMTX control API what is arriving.</summary>
public sealed class MediaMtxSources(HttpClient http) : IOutputSources
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAsync(CancellationToken ct)
    {
        using var response = await http.GetAsync("/v3/paths/list?itemsPerPage=1000", ct);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<PathList>(Json, ct);
        return list!.Items.Where(p => p.Ready)
            .ToDictionary(p => p.Name, p => (IReadOnlyList<string>)(p.Tracks ?? []));
    }

    private sealed record PathList(List<PathItem> Items);

    private sealed record PathItem(string Name, bool Ready, List<string>? Tracks);
}
