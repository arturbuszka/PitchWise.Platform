using PitchWise.Api.Dtos;

namespace PitchWise.Data.Config;

// Display metadata for each event type, in the order the UI lists them.
public static class EventTypeConfig
{
    public static readonly IReadOnlyList<EventTypeConfigOut> All = new List<EventTypeConfigOut>
    {
        new("goal",         "Goal",          "⚽", "#16a34a", "#e6f5ec"),
        new("shot",         "Shot",          "🎯", "#2f5fe0", "#e8edff"),
        new("wayward_pass", "Turnover",      "↗",  "#e0732f", "#fff0e6"),
        new("foul",         "Foul",          "🟨", "#ef4444", "#fee2e2"),
        new("free_kick",    "Free kick",     "⛳", "#2f5fe0", "#e8edff"),
        new("offside",      "Offside",       "🚩", "#8b5cf6", "#f3e8ff"),
        new("substitution", "Substitution",  "🔄", "#06b6d4", "#e0f7fa"),
        new("set_piece",    "Set piece",     "📐", "#64748b", "#f1f5f9"),
        new("manual",       "Manual",        "✏️",  "#6b7280", "#f9fafb"),
    };
}
