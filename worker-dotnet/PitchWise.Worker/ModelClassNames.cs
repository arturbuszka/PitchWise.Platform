using System.Text.Json;

namespace PitchWise.Worker;

/// <summary>Loads the model's class-id → name map from a sidecar JSON.</summary>
public static class ModelClassNames
{
    /// <summary>
    /// Resolves names for <paramref name="modelPath"/>. Looks at <paramref name="namesPath"/>
    /// if given, else "&lt;model&gt;.names.json". Accepts either a flat {"0":"person",...}
    /// object or a golden-style {"names": {...}} wrapper.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Load(string modelPath, string? namesPath)
    {
        string path = namesPath ?? Path.ChangeExtension(modelPath, ".names.json");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Class-names sidecar not found: {path}. Generate it with export_and_golden.py " +
                "or write a {\"0\":\"ball\",...} JSON next to the model.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement obj = doc.RootElement.TryGetProperty("names", out JsonElement n) ? n : doc.RootElement;

        var map = new Dictionary<int, string>();
        foreach (JsonProperty p in obj.EnumerateObject())
            map[int.Parse(p.Name)] = p.Value.GetString()!;
        return map;
    }
}
