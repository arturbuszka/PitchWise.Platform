using System.Text.Json;
using PitchWise.Vision;

namespace PitchWise.Live;

/// <summary>
/// Lazily-built, process-wide <see cref="Detector"/> shared by all live sessions
/// (model load is slow, ~10-15s).
/// frame_stride is 1 for live (every frame).
/// </summary>
public sealed class SharedDetector : IDisposable
{
    private readonly LiveSettings _settings;
    private readonly Lazy<Detector> _detector;

    public SharedDetector(LiveSettings settings)
    {
        _settings = settings;
        _detector = new Lazy<Detector>(Build, isThreadSafe: true);
    }

    public Detector Get() => _detector.Value;

    /// <summary>Force model init (call at startup so the first session doesn't stall).</summary>
    public void Preload() => _ = _detector.Value;

    private Detector Build()
    {
        IReadOnlyDictionary<int, string> names = LoadNames(_settings.YoloModelPath, _settings.YoloNamesPath);
        return new Detector(
            _settings.YoloModelPath, names,
            frameRate: 25, frameStride: 1, imgsz: _settings.LiveImgsz,
            executionProvider: _settings.OnnxExecutionProvider, deviceId: _settings.OnnxDeviceId);
    }

    private static IReadOnlyDictionary<int, string> LoadNames(string modelPath, string? namesPath)
    {
        string path = namesPath ?? Path.ChangeExtension(modelPath, ".names.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Class-names sidecar not found: {path}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement obj = doc.RootElement.TryGetProperty("names", out JsonElement n) ? n : doc.RootElement;
        var map = new Dictionary<int, string>();
        foreach (JsonProperty p in obj.EnumerateObject())
            map[int.Parse(p.Name)] = p.Value.GetString()!;
        return map;
    }

    public void Dispose()
    {
        if (_detector.IsValueCreated) _detector.Value.Dispose();
    }
}
