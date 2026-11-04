"use client";

import { MatchStats } from "@/lib/api";

const TEAM_A = "#2f5fe0"; // blue
const TEAM_B = "#e0732f"; // orange

function pct(n: number): string {
  return `${Math.round(n)}%`;
}

export function MatchStatsPanel({ stats }: { stats: MatchStats | null }) {
  if (!stats) {
    return (
      <div className="border border-[#eaecf0] rounded-xl bg-white p-4">
        <h3 className="text-[13px] font-semibold text-[#1a1d21] mb-1">Statystyki meczu</h3>
        <p className="text-[12px] text-[#8b919b]">
          Brak statystyk — analiza jeszcze nie została ukończona.
        </p>
      </div>
    );
  }

  const controlled = stats.controlled_seconds;
  const hasPossession = controlled > 0;
  // With no pitch homography the engine attributes no possession, so everything is zero. Say so
  // explicitly rather than drawing an empty 50/50 bar that looks like real data.
  const noEngineData =
    !hasPossession &&
    stats.team_a.passes === 0 &&
    stats.team_b.passes === 0 &&
    stats.team_a.turnovers === 0 &&
    stats.team_b.turnovers === 0;

  const pa = hasPossession ? stats.team_a.possession_pct : 50;
  const pb = hasPossession ? stats.team_b.possession_pct : 50;

  const topPlayers = [...stats.time_on_pitch]
    .sort((x, y) => y.seconds_on_pitch - x.seconds_on_pitch)
    .slice(0, 8);

  return (
    <div className="border border-[#eaecf0] rounded-xl bg-white p-4">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-[13px] font-semibold text-[#1a1d21]">Statystyki meczu</h3>
        <div className="flex items-center gap-3 text-[11px]">
          <span className="flex items-center gap-1">
            <span className="w-2 h-2 rounded-full" style={{ background: TEAM_A }} /> Drużyna A
          </span>
          <span className="flex items-center gap-1">
            <span className="w-2 h-2 rounded-full" style={{ background: TEAM_B }} /> Drużyna B
          </span>
        </div>
      </div>

      {noEngineData && (
        <div className="mb-3 rounded-lg bg-[#fff8ec] border border-[#f0e2c4] px-3 py-2 text-[11px] text-[#8a6d2f]">
          Silnik nie przypisał jeszcze pozycji na boisku (brak kalibracji boiska). Statystyki
          pojawią się, gdy analiza będzie miała dane o pozycjach.
        </div>
      )}

      {/* Possession bar */}
      <div className="mb-4">
        <div className="flex items-center justify-between text-[11px] text-[#5b616b] mb-1">
          <span>Posiadanie</span>
          <span className={noEngineData ? "text-[#b0b4bb]" : ""}>
            {pct(pa)} · {pct(pb)}
          </span>
        </div>
        <div className="h-2.5 rounded-full overflow-hidden flex bg-[#eceef1]">
          <div style={{ width: `${pa}%`, background: noEngineData ? "#d4d7dc" : TEAM_A }} />
          <div style={{ width: `${pb}%`, background: noEngineData ? "#c4c7cc" : TEAM_B }} />
        </div>
      </div>

      {/* Passing */}
      <div className="grid grid-cols-2 gap-3 mb-4">
        {(
          [
            ["Drużyna A", stats.team_a, TEAM_A],
            ["Drużyna B", stats.team_b, TEAM_B],
          ] as const
        ).map(([name, t, color]) => (
          <div key={name} className="rounded-lg border border-[#eaecf0] p-2.5">
            <div className="flex items-center gap-1.5 mb-1.5">
              <span className="w-2 h-2 rounded-full" style={{ background: color }} />
              <span className="text-[11px] font-medium text-[#1a1d21]">{name}</span>
            </div>
            <dl className="text-[11px] text-[#5b616b] space-y-0.5">
              <div className="flex justify-between">
                <dt>Podania</dt>
                <dd className="tabular-nums text-[#1a1d21]">{t.passes}</dd>
              </div>
              <div className="flex justify-between">
                <dt>Straty</dt>
                <dd className="tabular-nums text-[#1a1d21]">{t.turnovers}</dd>
              </div>
              <div className="flex justify-between">
                <dt>Celność</dt>
                <dd className="tabular-nums text-[#1a1d21]">
                  {t.passes + t.turnovers > 0 ? pct(t.pass_accuracy_pct) : "—"}
                </dd>
              </div>
            </dl>
          </div>
        ))}
      </div>

      {/* Time on pitch */}
      {topPlayers.length > 0 && (
        <div>
          <div className="text-[11px] text-[#5b616b] mb-1.5">Czas na boisku</div>
          <ul className="space-y-1">
            {topPlayers.map((p) => (
              <li key={p.player_id} className="flex items-center justify-between text-[11px]">
                <span className="text-[#1a1d21]">Zawodnik {p.player_id}</span>
                <span className="tabular-nums text-[#5b616b]">
                  {Math.round(p.seconds_on_pitch)} s
                </span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
