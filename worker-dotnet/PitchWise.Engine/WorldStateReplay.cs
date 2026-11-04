using System.Text.Json;

namespace PitchWise.Engine;

/// <summary>
/// Replays a recorded observation dump through a fresh engine. This is the tuning loop: one
/// expensive pass over the video produces <c>world_state.jsonl</c>, then every subsequent
/// iteration over <see cref="EngineConfig"/> thresholds and rule logic runs in milliseconds,
/// deterministically, with no video decode and no ONNX.
///
/// Because the dump holds observations rather than conclusions, the ball filter and the
/// possession state machine are rebuilt from scratch on each replay — so their parameters are
/// genuinely tunable here, not frozen at capture time.
/// </summary>
public static class WorldStateReplay
{
    public sealed record Result(
        WorldStateJsonl.Header Header,
        IReadOnlyList<GameEvent> Events,
        int FramesReplayed);

    /// <summary>Runs <paramref name="rules"/> over the dump at <paramref name="jsonlPath"/>.</summary>
    /// <param name="onState">Optional per-frame hook, for dumping the derived state or asserting
    /// on it in tests.</param>
    public static Result Run(
        string jsonlPath,
        IReadOnlyList<IGameRule> rules,
        EngineConfig? cfg = null,
        Action<WorldState>? onState = null)
    {
        using var reader = new StreamReader(jsonlPath);
        return Run(reader, rules, cfg, onState);
    }

    public static Result Run(
        TextReader reader,
        IReadOnlyList<IGameRule> rules,
        EngineConfig? cfg = null,
        Action<WorldState>? onState = null)
    {
        EngineConfig config = cfg ?? new EngineConfig();
        var builder = new WorldStateBuilder(config);
        var engine = new RuleEngine(rules, config);

        WorldStateJsonl.Header? header = null;
        var events = new List<GameEvent>();
        int frames = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("type", out JsonElement t) && t.GetString() == "header")
            {
                header = ParseHeader(root);
                continue;
            }

            FrameObservation obs = ParseFrame(root);
            WorldState state = builder.Add(obs);
            frames++;
            onState?.Invoke(state);
            events.AddRange(engine.OnFrame(state));
        }
        events.AddRange(engine.Flush());

        if (header is null)
            throw new InvalidDataException("Dump has no header line; it is not a world_state.jsonl.");

        return new Result(header.Value, events, frames);
    }

    private static WorldStateJsonl.Header ParseHeader(JsonElement root) => new(
        Fps: root.GetProperty("fps").GetDouble(),
        FrameStride: root.GetProperty("stride").GetInt32(),
        PitchLength: root.GetProperty("pitchLength").GetDouble(),
        PitchWidth: root.GetProperty("pitchWidth").GetDouble(),
        NormalizedCoords: root.GetProperty("normalizedCoords").GetBoolean(),
        TeamColorA: root.TryGetProperty("teamColorA", out JsonElement a) ? a.GetString() : null,
        TeamColorB: root.TryGetProperty("teamColorB", out JsonElement b) ? b.GetString() : null);

    private static FrameObservation ParseFrame(JsonElement root)
    {
        JsonElement ballEl = root.GetProperty("ball");
        BallObservation ball = ballEl.ValueKind == JsonValueKind.Null
            ? BallObservation.Missing
            : new BallObservation(
                ballEl.GetProperty("x").GetDouble(),
                ballEl.GetProperty("y").GetDouble(),
                ballEl.GetProperty("c").GetDouble(),
                Detected: true);

        JsonElement playersEl = root.GetProperty("p");
        var players = new List<PlayerObservation>(playersEl.GetArrayLength());
        foreach (JsonElement p in playersEl.EnumerateArray())
            players.Add(new PlayerObservation(
                p.GetProperty("id").GetInt32(),
                (PlayerRole)p.GetProperty("r").GetInt32(),
                (TeamId)p.GetProperty("tm").GetInt32(),
                p.GetProperty("x").GetDouble(),
                p.GetProperty("y").GetDouble(),
                p.GetProperty("c").GetDouble()));

        return new FrameObservation(
            root.GetProperty("f").GetInt32(),
            root.GetProperty("t").GetDouble(),
            players,
            ball,
            root.GetProperty("n").GetBoolean());
    }
}
