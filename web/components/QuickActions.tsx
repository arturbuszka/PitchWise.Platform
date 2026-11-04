"use client";

import { EventType } from "@/lib/api";

const ACTIONS: { icon: string; label: string; type: EventType }[] = [
  { icon: "⚽", label: "Goals", type: "goal" },
  { icon: "🎯", label: "Shots on goal", type: "shot" },
  { icon: "↗", label: "Wayward passes", type: "wayward_pass" },
  { icon: "🟨", label: "Fouls and cards", type: "foul" },
  { icon: "⛳", label: "Free kicks", type: "free_kick" },
  { icon: "🚩", label: "Offsides", type: "offside" },
  { icon: "🔄", label: "Substitutions", type: "substitution" },
  { icon: "📐", label: "Set pieces", type: "set_piece" },
];

/**
 * Quick-actions bar = multi-select filters for the results panel.
 * Nothing selected by default => the panel shows all events.
 * Toggling a type narrows the list (union of selected types, OR).
 */
export function QuickActions({
  active,
  onToggle,
}: {
  active: Set<EventType>;
  onToggle: (type: EventType) => void;
}) {
  return (
    <div className="flex flex-wrap gap-2">
      {ACTIONS.map((a) => {
        const isActive = active.has(a.type);
        return (
          <button
            key={a.type}
            onClick={() => onToggle(a.type)}
            aria-pressed={isActive}
            className={`flex items-center gap-1.5 px-3 py-2 rounded-[9px] text-[13px] font-semibold border transition-colors ${
              isActive
                ? "border-[#2f5fe0] text-[#2f5fe0] bg-[#eef3ff] hover:bg-[#dde8ff]"
                : "border-[#e4e7ec] text-[#374151] bg-white hover:border-[#2f5fe0] hover:text-[#2f5fe0]"
            }`}
          >
            <span>{a.icon}</span>
            {a.label}
          </button>
        );
      })}
    </div>
  );
}
