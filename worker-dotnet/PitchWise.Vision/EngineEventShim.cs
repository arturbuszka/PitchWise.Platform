using PitchWise.Engine;

namespace PitchWise.Vision;

/// <summary>
/// Converts engine <see cref="GameEvent"/>s into the <see cref="DetectedEvent"/>s the worker
/// already knows how to persist, so the whole EF/API/frontend path stays untouched.
///
/// Events the database has no type for are dropped. <c>EventTypeMap.FromDb</c> silently coerces
/// an unknown string to <c>EventType.Manual</c>, which would file engine output under "a coach
/// typed this in" — worse than losing it. Internal signals (<c>pass</c>, <c>possession_change</c>)
/// therefore stay inside the engine, where statistics and future rules can still read them.
/// </summary>
public static class EngineEventShim
{
    public static IReadOnlyList<DetectedEvent> ToDetectedEvents(IReadOnlyList<GameEvent> events)
    {
        List<DetectedEvent>? outp = null;
        foreach (GameEvent e in events)
        {
            if (!GameEventType.PersistableTypes.Contains(e.Type)) continue;
            (outp ??= new List<DetectedEvent>()).Add(new DetectedEvent(
                e.Type,
                e.TimestampSeconds,
                Math.Round(e.Confidence, 3),
                Label(e)));
        }
        return (IReadOnlyList<DetectedEvent>?)outp ?? Array.Empty<DetectedEvent>();
    }

    /// <summary>DetectedEvent has no player/team fields, so they ride along in the label —
    /// which is exactly where the existing goal/shot heuristics put their provenance too.</summary>
    private static string Label(GameEvent e)
    {
        string who = e.PlayerId is int pid ? $" [player {pid}, team {e.Team}]" : "";
        return (e.Label ?? e.Type) + who;
    }
}
