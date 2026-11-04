"use client";

import { AnalysisEvent, EventType, EventTypeConfig, formatTime } from "@/lib/api";

function confColor(conf: number): string {
  if (conf >= 0.8) return "#16a34a"; // green — high
  if (conf >= 0.5) return "#e0a72f"; // amber — medium
  return "#ef4444"; // red — low
}

function playerLabel(ev: AnalysisEvent): string {
  const parts: string[] = [];
  if (ev.player_name || ev.player_number) {
    parts.push(
      ev.player_number ? `#${ev.player_number} ${ev.player_name ?? ""}`.trim() : (ev.player_name ?? "")
    );
  }
  if (ev.assist_name || ev.assist_number) {
    const assist = ev.assist_number
      ? `#${ev.assist_number} ${ev.assist_name ?? ""}`.trim()
      : (ev.assist_name ?? "");
    parts.push(`(assist ${assist})`);
  }
  return parts.join(" ");
}

export function EventResultsPanel({
  events,
  activeFilters,
  eventTypes,
  onSeek,
  selectedEvents,
  onToggleSelected,
}: {
  events: AnalysisEvent[];
  activeFilters: Set<EventType>;
  eventTypes: EventTypeConfig[];
  onSeek: (seconds: number) => void;
  selectedEvents: Set<number>;
  onToggleSelected: (id: number) => void;
}) {
  const cfg = new Map(eventTypes.map((t) => [t.key, t]));

  const filtered =
    activeFilters.size === 0
      ? events
      : events.filter((e) => activeFilters.has(e.type));

  // Dynamic heading
  let heading = "Results";
  if (activeFilters.size === 1) {
    const only = [...activeFilters][0];
    heading = cfg.get(only)?.label ?? "Results";
  } else if (activeFilters.size > 1) {
    heading = "Selected";
  }

  return (
    <div className="px-5 pb-6">
      <div className="flex items-center justify-between mb-2">
        <p className="text-[16px] font-bold text-[#14181f]">
          {heading}{" "}
          <span className="text-[#9aa0a8] font-medium">
            · {filtered.length} {filtered.length === 1 ? "result" : "results"}
          </span>
        </p>
        {filtered.length > 0 && (
          <p className="text-[12px] text-[#9aa0a8] font-medium">
            click a result → seek in video
          </p>
        )}
      </div>

      <div className="border border-[#eaecf0] rounded-xl overflow-hidden bg-white">
        {filtered.length === 0 ? (
          <p className="text-[#9aa0a8] text-sm text-center py-6 px-4">
            {activeFilters.size > 0
              ? "No events of this type — auto-detection coming soon."
              : "No results — run a video analysis."}
          </p>
        ) : (
          <div className="flex flex-col gap-2 p-2">
            {filtered.map((ev) => {
              const t = cfg.get(ev.type);
              const conf = ev.confidence !== null ? Math.round(ev.confidence * 100) : null;
              const player = playerLabel(ev);
              const desc = ev.note ?? ev.label;

              const isSelected = selectedEvents.has(ev.id);

              return (
                <div
                  key={ev.id}
                  onClick={() => onSeek(ev.timestamp_seconds)}
                  className={`flex items-center gap-3 bg-white rounded-xl px-3 py-2.5 cursor-pointer hover:shadow-sm border transition-all ${
                    isSelected ? "border-[#2f5fe0] bg-[#f5f8ff]" : "border-[#eceef1] hover:border-[#2f5fe0]/30"
                  }`}
                >
                  {/* Select for highlight */}
                  <input
                    type="checkbox"
                    checked={isSelected}
                    onClick={(e) => e.stopPropagation()}
                    onChange={() => onToggleSelected(ev.id)}
                    className="w-4 h-4 shrink-0 accent-[#2f5fe0] cursor-pointer"
                    title="Add to highlight"
                  />

                  {/* Timestamp */}
                  <span className="bg-[#14181f] text-white text-xs font-mono rounded-[7px] px-2 py-1 shrink-0 tabular-nums">
                    {formatTime(ev.timestamp_seconds)}
                  </span>

                  {/* Type tag */}
                  <span
                    className="text-[11px] font-bold rounded-full px-2 py-0.5 shrink-0 uppercase tracking-wider"
                    style={{ background: t?.bg ?? "#eef0f3", color: t?.color ?? "#6b7280" }}
                  >
                    {t?.label ?? ev.type}
                  </span>

                  {/* Player / assist */}
                  <span className="text-sm text-[#14181f] shrink-0 max-w-[40%] truncate">
                    {player || "—"}
                  </span>

                  {/* Description */}
                  <span className="text-[13px] text-[#9aa0a8] flex-1 truncate">
                    {desc ?? ""}
                  </span>

                  {/* Confidence */}
                  {conf !== null && (
                    <div className="flex items-center gap-1.5 shrink-0">
                      <div className="w-10 h-1.5 bg-[#eceef1] rounded-full overflow-hidden">
                        <div
                          className="h-full rounded-full"
                          style={{ width: `${conf}%`, background: confColor(ev.confidence!) }}
                        />
                      </div>
                      <span className="text-[11px] text-[#6b7280] font-semibold tabular-nums">
                        {conf}%
                      </span>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
    </div>
  );
}
