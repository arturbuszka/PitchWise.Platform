namespace PitchWise.Engine;

/// <summary>
/// Runs every rule against every world state and de-duplicates what they emit.
///
/// Centralising the "don't emit the same thing twice" concern here is what keeps individual
/// rules simple: a rule may fire enthusiastically on consecutive frames, and only one event
/// survives. Same key shape as the vision layer's <c>emittedKeys</c> set — type plus timestamp
/// rounded to a tenth of a second — plus a per-type cooldown, mirroring <c>EventConfig</c>.
///
/// Adding a new analysis means adding a rule. It never means touching this class.
/// </summary>
public sealed class RuleEngine
{
    private readonly IReadOnlyList<IGameRule> _rules;
    private readonly double _cooldownSeconds;
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _lastEmittedAt = new(StringComparer.Ordinal);

    public RuleEngine(IReadOnlyList<IGameRule> rules, EngineConfig? cfg = null)
    {
        _rules = rules;
        _cooldownSeconds = (cfg ?? new EngineConfig()).EventCooldownSeconds;
    }

    public IReadOnlyList<GameEvent> OnFrame(WorldState state) => Collect(r => r.OnFrame(state));

    public IReadOnlyList<GameEvent> Flush() => Collect(r => r.Flush());

    private IReadOnlyList<GameEvent> Collect(Func<IGameRule, IEnumerable<GameEvent>> pump)
    {
        List<GameEvent>? fresh = null;
        foreach (IGameRule rule in _rules)
            foreach (GameEvent e in pump(rule))
                if (Accept(e))
                    (fresh ??= new List<GameEvent>()).Add(e);
        return (IReadOnlyList<GameEvent>?)fresh ?? Array.Empty<GameEvent>();
    }

    private bool Accept(GameEvent e)
    {
        if (_lastEmittedAt.TryGetValue(e.Type, out double last)
            && e.TimestampSeconds - last < _cooldownSeconds)
            return false;

        string key = $"{e.Type}|{Math.Round(e.TimestampSeconds, 1)}";
        if (!_emitted.Add(key)) return false;

        _lastEmittedAt[e.Type] = e.TimestampSeconds;
        return true;
    }
}
