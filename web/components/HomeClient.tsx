"use client";

import { useState, useEffect } from "react";
import Link from "next/link";
import { api, AnalysisSummary, SessionStatus, Sport } from "@/lib/api";
import { NewAnalysisModal } from "./NewAnalysisModal";

const SPORT_OPTIONS: { value: Sport | ""; label: string; icon: string }[] = [
  { value: "", label: "All sports", icon: "" },
  { value: "football", label: "Football", icon: "⚽" },
  { value: "basketball", label: "Basketball", icon: "🏀" },
  { value: "handball", label: "Handball", icon: "🤾" },
];

const SPORT_LABELS: Record<Sport, string> = {
  football: "⚽ Football",
  basketball: "🏀 Basketball",
  handball: "🤾 Handball",
};

function StatusBadge({ status }: { status: SessionStatus }) {
  if (status === "processing") {
    return (
      <span className="inline-block px-3 py-1 rounded-full text-xs font-semibold bg-orange-100 text-orange-700">
        Analyzing…
      </span>
    );
  }
  if (status === "done") {
    return (
      <span className="inline-block px-3 py-1 rounded-full text-xs font-semibold bg-green-100 text-green-700">
        Ready
      </span>
    );
  }
  return (
    <span className="inline-block px-3 py-1 rounded-full text-xs font-semibold bg-[#eef0f3] text-[#6b7280]">
      Draft
    </span>
  );
}

export function HomeClient() {
  const [analyses, setAnalyses] = useState<AnalysisSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [modalOpen, setModalOpen] = useState(false);
  const [search, setSearch] = useState("");
  const [sport, setSport] = useState<Sport | "">("");

  useEffect(() => {
    api.analyses.list()
      .then(setAnalyses)
      .catch(() => setAnalyses([]))
      .finally(() => setLoading(false));
  }, []);

  const filtered = analyses.filter((a) => {
    const matchesSport = sport === "" || a.sport === sport;
    const matchesSearch = a.name.toLowerCase().includes(search.toLowerCase());
    return matchesSport && matchesSearch;
  });

  return (
    <div className="min-h-screen flex flex-col">
      {/* Nav */}
      <nav className="bg-white border-b border-[#eaecf0] px-6 h-[58px] flex items-center justify-between">
        <div className="flex items-center gap-2.5">
          <span className="w-[30px] h-[30px] rounded-[8px] bg-[#14181f] text-white flex items-center justify-center text-[15px]">
            ⚽
          </span>
          <span className="text-[21px] font-black tracking-tight leading-none">
            Pitch<span className="text-[#2f5fe0]">Wise</span>
          </span>
        </div>
        <div className="flex items-center gap-3">
          <Link
            href="/live"
            className="border border-[#2f5fe0] text-[#2f5fe0] hover:bg-[#eef1ff] text-[14px] font-semibold rounded-[10px] px-4 py-2.5 flex items-center gap-1.5 transition-colors"
          >
            <span className="text-[15px] leading-none">▶</span>
            Live analysis
          </Link>
          <button
            onClick={() => setModalOpen(true)}
            className="bg-[#2f5fe0] hover:bg-[#2451c7] text-white text-[14px] font-semibold rounded-[10px] px-4 py-2.5 flex items-center gap-1.5 shadow-sm transition-colors"
          >
            <span className="text-[17px] leading-none">+</span>
            New analysis
          </button>
          <div className="w-9 h-9 rounded-full bg-[#eceef1] flex items-center justify-center text-[13px] font-bold text-[#6b7280]">
            TK
          </div>
        </div>
      </nav>

      {/* Main */}
      <main className="flex-1 px-6 py-8" style={{ maxWidth: 1380, margin: "0 auto", width: "100%" }}>
        {/* Heading row */}
        <div className="flex items-end justify-between mb-6">
          <div>
            <h1 className="text-[26px] font-black tracking-tight">Your analyses</h1>
            <p className="text-[14px] text-[#9aa0a8] mt-1 font-medium">
              Click a row to open the analysis panel
            </p>
          </div>
          <div className="flex gap-2">
            <select
              value={sport}
              onChange={(e) => setSport(e.target.value as Sport | "")}
              className="border border-[#e4e7ec] rounded-[9px] px-3 py-2 text-[13px] text-[#6b7280] font-medium bg-white focus:outline-none focus:border-[#2f5fe0]"
            >
              {SPORT_OPTIONS.map((s) => (
                <option key={s.value} value={s.value}>{s.label}</option>
              ))}
            </select>
            <input
              type="search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="🔍 Search"
              className="border border-[#e4e7ec] rounded-[9px] px-3 py-2 text-[13px] text-[#6b7280] font-medium bg-white focus:outline-none focus:border-[#2f5fe0] w-48"
            />
          </div>
        </div>

        {/* Table */}
        <div className="bg-white rounded-xl border border-[#eaecf0] overflow-hidden shadow-sm">
          {/* Column headers */}
          <div className="flex items-center gap-4 px-[18px] py-3 text-[12px] font-semibold uppercase tracking-wider text-[#9aa0a8] border-b border-[#eaecf0]">
            <div className="flex-[1.7]">Name</div>
            <div className="flex-1">Sport</div>
            <div className="w-[120px]">Updated</div>
            <div className="w-[80px] text-center">Videos</div>
            <div className="w-[130px]">Status</div>
            <div className="w-6" />
          </div>

          {loading ? (
            <p className="text-[#9aa0a8] text-sm text-center py-12">Loading…</p>
          ) : filtered.length === 0 ? (
            <p className="text-[#9aa0a8] text-sm text-center py-12">
              {analyses.length === 0
                ? 'No analyses yet. Click "New analysis" to get started.'
                : "No results for this query."}
            </p>
          ) : (
            filtered.map((a) => (
              <Link
                key={a.id}
                href={`/analyses/${a.id}`}
                className="flex items-center gap-4 px-[18px] py-4 border-b border-[#f1f3f5] cursor-pointer hover:bg-[#f7f8fa] transition-colors last:border-b-0"
              >
                <div className="flex-[1.7] flex items-center gap-3 min-w-0">
                  <span className="w-[38px] h-[38px] rounded-[9px] bg-[#f1f3f5] flex items-center justify-center text-base shrink-0">
                    {SPORT_LABELS[a.sport]?.split(" ")[0] ?? "🎯"}
                  </span>
                  <div className="min-w-0">
                    <p className="text-[15px] font-semibold truncate">{a.name}</p>
                    <p className="text-[12px] text-[#9aa0a8] font-medium">
                      {a.subtitle ?? "—"}
                    </p>
                  </div>
                </div>
                <div className="flex-1 text-[14px] text-[#6b7280] font-medium">
                  {SPORT_LABELS[a.sport] ?? a.sport}
                </div>
                <div className="w-[120px] text-[14px] text-[#6b7280] font-medium tabular-nums">
                  {new Date(a.updated_at).toLocaleDateString("en-US")}
                </div>
                <div className="w-[80px] text-center">
                  <span className="inline-flex items-center justify-center min-w-[26px] h-6 px-2 rounded-[7px] bg-[#f1f3f5] text-[13px] font-semibold text-[#6b7280] tabular-nums">
                    {a.video_count}
                  </span>
                </div>
                <div className="w-[130px]">
                  <StatusBadge status={a.status} />
                </div>
                <div className="w-6 text-center text-[20px] text-[#cbd0d6]">›</div>
              </Link>
            ))
          )}
        </div>
      </main>

      {modalOpen && (
        <NewAnalysisModal
          onClose={() => {
            setModalOpen(false);
            api.analyses.list().then(setAnalyses).catch(() => {});
          }}
        />
      )}
    </div>
  );
}
