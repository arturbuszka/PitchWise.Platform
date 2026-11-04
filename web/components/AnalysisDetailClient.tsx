"use client";

import { useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import {
  AnalysisDetail,
  AnalysisEvent,
  EventType,
  EventTypeConfig,
  Highlight,
  MatchStats,
  VideoItem,
  VisionJob,
  api,
  formatTime,
} from "@/lib/api";
import { Chat } from "./Chat";
import { QuickActions } from "./QuickActions";
import { EventResultsPanel } from "./EventResultsPanel";
import { MatchStatsPanel } from "./MatchStatsPanel";
import { HlsPlayer } from "./HlsPlayer";
import { AnnotatedHlsPlayer } from "./AnnotatedHlsPlayer";

const SPORT_LABELS: Record<string, string> = {
  football: "⚽ Football",
  basketball: "🏀 Basketball",
  handball: "🤾 Handball",
};

export function AnalysisDetailClient({
  analysis,
  events: initialEvents,
  eventTypes,
}: {
  analysis: AnalysisDetail;
  events: AnalysisEvent[];
  eventTypes: EventTypeConfig[];
}) {
  const router = useRouter();
  const [videos, setVideos] = useState<VideoItem[]>(analysis.videos);
  const [events, setEvents] = useState<AnalysisEvent[]>(initialEvents);
  const [activeVideoId, setActiveVideoId] = useState<number | null>(
    analysis.videos[0]?.id ?? null
  );
  const [activeFilters, setActiveFilters] = useState<Set<EventType>>(new Set());
  const [job, setJob] = useState<VisionJob | null>(null);
  const [stats, setStats] = useState<MatchStats | null>(null);
  const [busy, setBusy] = useState(false);
  const [uploadPct, setUploadPct] = useState<number | null>(null);
  const [uploadError, setUploadError] = useState<string | null>(null);

  // Highlight creation
  const [selectedEvents, setSelectedEvents] = useState<Set<number>>(new Set());
  const [highlight, setHighlight] = useState<Highlight | null>(null);
  const [highlightHlsUrl, setHighlightHlsUrl] = useState<string | null>(null);
  const [shareUrl, setShareUrl] = useState<string | null>(null);
  const [shareCopied, setShareCopied] = useState(false);

  const videoRef = useRef<HTMLVideoElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const activeVideo = videos.find((v) => v.id === activeVideoId) ?? null;
  // Annotated playback is available only for the video the current job belongs to.
  const annotatedReady =
    !!job && job.video_id === activeVideoId && job.annotated_ready;

  function seekVideo(seconds: number) {
    if (videoRef.current) {
      videoRef.current.currentTime = seconds;
      videoRef.current.play();
    }
  }

  function toggleFilter(type: EventType) {
    setActiveFilters((prev) => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type);
      else next.add(type);
      return next;
    });
  }

  function toggleEventSelected(id: number) {
    setSelectedEvents((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function handleGenerateHighlight() {
    if (selectedEvents.size === 0) return;
    setBusy(true);
    setShareUrl(null);
    setShareCopied(false);
    try {
      const h = await api.analyses.highlights.create(
        analysis.id,
        `Highlight · ${selectedEvents.size} events`,
        [...selectedEvents]
      );
      setHighlight(h);
    } finally {
      setBusy(false);
    }
  }

  async function handleShare() {
    if (!highlight || highlight.status !== "done") return;
    const link = await api.analyses.highlights.share(analysis.id, highlight.id).catch(() => null);
    if (!link) return;
    setShareUrl(link.url);
    try {
      await navigator.clipboard.writeText(link.url);
      setShareCopied(true);
    } catch {
      // clipboard may be blocked — the URL is still shown for manual copy
    }
  }

  async function refreshEvents() {
    const fresh = await api.analyses.events.list(analysis.id).catch(() => null);
    if (fresh) setEvents(fresh);
  }

  // Hydrate job status when the active video changes (e.g. on reload) so a previously
  // analysed video shows its annotated playback and segment progress without a re-run.
  useEffect(() => {
    if (activeVideoId == null) {
      setJob(null);
      return;
    }
    let cancelled = false;
    api.analyses.videos.status(analysis.id, activeVideoId).then((s) => {
      if (!cancelled) setJob(s);
    });
    // Match stats: present once analysis has produced a row (404 → null before then).
    setStats(null);
    api.analyses.videos.stats(analysis.id, activeVideoId).then((s) => {
      if (!cancelled) setStats(s);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeVideoId, analysis.id]);

  // Poll the status of the active job. With segmented analysis we refresh the events
  // timeline on every tick so it fills in per segment instead of only at the end.
  useEffect(() => {
    if (!job || job.status === "done" || job.status === "failed") return;
    const interval = setInterval(async () => {
      if (activeVideoId == null) return;
      const updated = await api.analyses.videos
        .status(analysis.id, activeVideoId)
        .catch(() => null);
      if (updated) setJob(updated);
      // A finished segment means new events are queryable — pull them in live.
      await refreshEvents();
      if (updated?.status === "done") {
        clearInterval(interval);
        // Analysis finished — the stats row now exists; pull it in.
        api.analyses.videos.stats(analysis.id, activeVideoId).then(setStats).catch(() => {});
        router.refresh();
      } else if (updated?.status === "failed") {
        clearInterval(interval);
      }
    }, 3000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job, activeVideoId, analysis.id]);

  // Poll the highlight render job until it finishes
  useEffect(() => {
    if (!highlight || highlight.status === "done" || highlight.status === "failed") return;
    const interval = setInterval(async () => {
      const updated = await api.analyses.highlights
        .status(analysis.id, highlight.id)
        .catch(() => null);
      if (updated) setHighlight(updated);
      if (updated?.status === "done" || updated?.status === "failed") {
        clearInterval(interval);
      }
    }, 3000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [highlight, analysis.id]);

  // Once the highlight is done, fetch its signed HLS URL (edge-served). null → MP4 fallback.
  useEffect(() => {
    if (highlight?.status !== "done") {
      setHighlightHlsUrl(null);
      return;
    }
    let cancelled = false;
    api.analyses.highlights.hlsUrl(analysis.id, highlight.id).then((h) => {
      if (!cancelled) setHighlightHlsUrl(h?.url ?? null);
    });
    return () => {
      cancelled = true;
    };
  }, [highlight?.status, highlight?.id, analysis.id]);

  async function handleUpload(file: File) {
    setBusy(true);
    setUploadError(null);
    setUploadPct(0);
    try {
      const v = await api.analyses.videos.uploadWithProgress(
        analysis.id,
        file,
        file.name,
        (frac) => setUploadPct(Math.round(frac * 100))
      );
      setVideos((prev) => [...prev, v]);
      setActiveVideoId(v.id);
    } catch (e) {
      setUploadError((e as Error).message || "Upload failed");
    } finally {
      setBusy(false);
      setUploadPct(null);
    }
  }

  async function handleAnalyze() {
    if (activeVideoId == null) return;
    setBusy(true);
    try {
      const j = await api.analyses.videos.analyze(analysis.id, activeVideoId);
      setJob(j);
    } finally {
      setBusy(false);
    }
  }

  async function handleCancelAnalysis() {
    if (activeVideoId == null) return;
    setBusy(true);
    try {
      const j = await api.analyses.videos.cancel(analysis.id, activeVideoId);
      setJob(j); // failed/cancelled → "Run analysis" re-enabled, dedup unblocked
    } finally {
      setBusy(false);
    }
  }

  const analyzing = job?.status === "running" || job?.status === "pending";

  const jobLabel: Record<string, string> = {
    pending: "Queued…",
    running: "Analyzing…",
    done: "Done",
    failed: "Analysis error",
  };

  return (
    <div className="h-screen flex overflow-hidden bg-[#eceef1]">
      {/* ===== LEFT SIDEBAR ===== */}
      <aside className="w-[236px] bg-[#14181f] flex flex-col overflow-hidden shrink-0">
        {/* Logo */}
        <div className="px-4 py-[18px] flex items-center gap-2">
          <span className="w-[26px] h-[26px] rounded-[7px] bg-[#2f5fe0] flex items-center justify-center text-[13px]">
            ⚽
          </span>
          <span className="text-[19px] font-black text-white leading-none tracking-tight">
            Pitch<span className="text-[#6f9bff]">Wise</span>
          </span>
        </div>

        {/* ANALYSIS */}
        <div className="px-[10px] mb-2">
          <p className="text-[11px] font-bold uppercase tracking-[.06em] text-[#6b7280] px-1.5">
            ANALYSIS
          </p>
        </div>
        <div className="mx-3 bg-[#22272f] rounded-[9px] px-3 py-2.5 mb-5">
          <p className="text-[14px] font-semibold text-white truncate">{analysis.name}</p>
          <p className="text-[12px] text-[#8b919b] mt-0.5">
            {SPORT_LABELS[analysis.sport] ?? analysis.sport}
          </p>
        </div>

        {/* VIDEOS */}
        <div className="px-[10px] flex-1 overflow-y-auto">
          <div className="flex items-center justify-between mb-2 px-1.5">
            <p className="text-[11px] font-bold uppercase tracking-[.06em] text-[#6b7280]">
              VIDEOS
            </p>
            <button
              onClick={() => fileInputRef.current?.click()}
              disabled={busy}
              className="bg-[#22272f] text-[#9aa0a8] rounded-[7px] text-[12px] px-2 py-0.5 font-semibold hover:text-white transition-colors disabled:opacity-50"
            >
              + attach
            </button>
            <input
              ref={fileInputRef}
              type="file"
              accept=".mp4,.mov,.mkv,.avi"
              className="hidden"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) handleUpload(f);
                e.target.value = "";
              }}
            />
          </div>

          {uploadPct !== null && (
            <div className="px-1.5 mb-2">
              <div className="flex items-center justify-between text-[11px] text-[#8b919b] mb-1">
                <span>Uploading…</span>
                <span className="tabular-nums">{uploadPct}%</span>
              </div>
              <div className="h-1.5 bg-[#22272f] rounded-full overflow-hidden">
                <div
                  className="h-full bg-[#2f5fe0] rounded-full transition-all"
                  style={{ width: `${uploadPct}%` }}
                />
              </div>
            </div>
          )}
          {uploadError && (
            <p className="text-[11px] text-red-400 px-1.5 mb-2">{uploadError}</p>
          )}

          {videos.length === 0 ? (
            <p className="text-[12px] text-[#6b7280] px-1.5">No videos</p>
          ) : (
            videos.map((v) => {
              const isActive = v.id === activeVideoId;
              return (
                <button
                  key={v.id}
                  onClick={() => setActiveVideoId(v.id)}
                  className={`w-full flex items-center gap-2 rounded-[8px] px-[9px] py-2 text-[13px] font-medium mb-1 transition-colors ${
                    isActive
                      ? "bg-[#2f5fe0] text-white"
                      : "bg-[#22272f] text-[#cbd0d6] hover:bg-[#2b3038]"
                  }`}
                >
                  <span className="w-8 h-[22px] shrink-0 bg-[#2b3038] rounded-[4px] flex items-center justify-center text-[10px] text-[#8b919b]">
                    ▶
                  </span>
                  <span className="flex-1 min-w-0 truncate text-left">{v.name}</span>
                  {v.duration_seconds != null && (
                    <span className="text-[11px] tabular-nums opacity-80 shrink-0">
                      {formatTime(v.duration_seconds)}
                    </span>
                  )}
                </button>
              );
            })
          )}
        </div>

        {/* Bottom */}
        <div className="px-3 pb-4 pt-3 border-t border-[#22272f] flex flex-col gap-2">
          <Link
            href="/"
            className="text-[13px] text-[#8b919b] hover:text-white transition-colors font-medium px-1"
          >
            ↩ All analyses
          </Link>
        </div>
      </aside>

      {/* ===== CENTER ===== */}
      <main className="flex-1 flex flex-col overflow-hidden min-w-0">
        <div className="bg-white border-b border-[#eaecf0] px-6 py-3.5 flex items-center justify-between shrink-0">
          <span className="text-[15px] font-semibold text-[#14181f] truncate">
            {activeVideo?.name ?? analysis.name}
            {activeVideo?.duration_seconds != null && (
              <span className="text-[#9aa0a8] font-normal">
                {" "}· {formatTime(activeVideo.duration_seconds)}
              </span>
            )}
          </span>
          <div className="flex items-center gap-3">
            {job && (
              <span
                className={`text-[12px] font-semibold ${
                  job.status === "done"
                    ? "text-green-600"
                    : job.status === "failed"
                      ? "text-red-500"
                      : "text-[#2f5fe0]"
                }`}
              >
                {jobLabel[job.status]}
                {job.status === "running" && job.progress > 0
                  ? ` ${Math.round(job.progress * 100)}%`
                  : ""}
                {annotatedReady ? " · ▦ boxes" : ""}
              </span>
            )}
            <button
              onClick={handleGenerateHighlight}
              disabled={busy || selectedEvents.size === 0 || highlight?.status === "running" || highlight?.status === "pending"}
              className="bg-[#14181f] hover:bg-[#2b3038] disabled:opacity-40 text-white rounded-[8px] px-3 py-1.5 text-[12px] font-semibold transition-colors"
              title={selectedEvents.size === 0 ? "Select events first" : "Stitch a highlight reel from the selected events"}
            >
              ✦ Generate highlight{selectedEvents.size > 0 ? ` (${selectedEvents.size})` : ""}
            </button>
            {analyzing ? (
              <button
                onClick={handleCancelAnalysis}
                disabled={busy || activeVideoId == null}
                className="bg-red-600 hover:bg-red-700 disabled:opacity-50 text-white rounded-[8px] px-3 py-1.5 text-[12px] font-semibold transition-colors"
              >
                ■ Stop
              </button>
            ) : (
              <button
                onClick={handleAnalyze}
                disabled={busy || activeVideoId == null}
                className="bg-[#2f5fe0] hover:bg-[#2451c7] disabled:opacity-50 text-white rounded-[8px] px-3 py-1.5 text-[12px] font-semibold transition-colors"
              >
                Run analysis
              </button>
            )}
          </div>
        </div>

        <div className="flex-1 overflow-y-auto">
          {/* Player */}
          <div className="p-5">
            <div className="relative rounded-xl overflow-hidden bg-[#1a3d2e] shadow-sm">
              {activeVideoId == null ? (
                <div className="w-full h-64 flex items-center justify-center text-[#9aa0a8] text-sm">
                  Attach a video to get started
                </div>
              ) : annotatedReady ? (
                // Only ever show the annotated (boxes burned-in) video — a progressive HLS
                // playlist that grows as analysis runs. Until it exists, a placeholder.
                <AnnotatedHlsPlayer
                  key={`${activeVideoId}-annotated`}
                  videoRef={videoRef}
                  hlsUrl={api.analyses.videos.annotatedHlsUrl(analysis.id, activeVideoId)}
                  controls
                  className="w-full max-h-[420px] object-contain bg-black"
                />
              ) : (
                <div className="w-full aspect-video flex flex-col items-center justify-center gap-2 text-[#9aa0a8] text-sm">
                  <span className="text-3xl">🎬</span>
                  <span>
                    {job?.status === "running" || job?.status === "pending"
                      ? "Analyzing… detections will appear here"
                      : job?.status === "failed"
                        ? "Analysis failed"
                        : "Run analysis to see detections"}
                  </span>
                </div>
              )}
            </div>
          </div>

          {/* Highlight reel */}
          {highlight && (
            <div className="px-5 pb-4">
              <div className="border border-[#eaecf0] rounded-xl bg-white p-4">
                <div className="flex items-center justify-between mb-3">
                  <p className="text-[14px] font-bold text-[#14181f]">
                    ✦ {highlight.name}
                  </p>
                  <span
                    className={`text-[12px] font-semibold ${
                      highlight.status === "done"
                        ? "text-green-600"
                        : highlight.status === "failed"
                          ? "text-red-500"
                          : "text-[#2f5fe0]"
                    }`}
                  >
                    {highlight.status === "done"
                      ? "Ready"
                      : highlight.status === "failed"
                        ? "Render error"
                        : `Rendering… ${Math.round(highlight.progress * 100)}%`}
                  </span>
                </div>

                {highlight.status !== "done" && highlight.status !== "failed" && (
                  <div className="h-1.5 bg-[#eceef1] rounded-full overflow-hidden mb-2">
                    <div
                      className="h-full bg-[#2f5fe0] rounded-full transition-all"
                      style={{ width: `${Math.round(highlight.progress * 100)}%` }}
                    />
                  </div>
                )}

                {highlight.status === "failed" && (
                  <p className="text-[12px] text-red-500">{highlight.error ?? "Failed to render highlight."}</p>
                )}

                {highlight.status === "done" && (
                  <>
                    <HlsPlayer
                      key={highlight.id}
                      hlsUrl={highlightHlsUrl}
                      fallbackSrc={api.analyses.highlights.streamUrl(analysis.id, highlight.id)}
                      controls
                      autoPlay
                      className="w-full max-h-[340px] object-contain bg-black rounded-lg"
                    />
                    <div className="flex items-center gap-3 mt-3">
                      <button
                        onClick={handleShare}
                        className="bg-[#2f5fe0] hover:bg-[#2451c7] text-white rounded-[8px] px-3 py-1.5 text-[12px] font-semibold transition-colors"
                      >
                        🔗 Copy share link
                      </button>
                      {shareUrl && (
                        <span className="text-[12px] text-[#6b7280] truncate flex-1">
                          {shareCopied ? "Copied! " : ""}
                          <a href={shareUrl} target="_blank" rel="noreferrer" className="text-[#2f5fe0] hover:underline">
                            {shareUrl}
                          </a>
                        </span>
                      )}
                    </div>
                  </>
                )}
              </div>
            </div>
          )}

          {/* Quick actions */}
          <div className="px-5 pb-4">
            <p className="text-[12px] font-bold uppercase tracking-[.05em] text-[#9aa0a8] mb-2">
              QUICK ACTIONS
            </p>
            <QuickActions active={activeFilters} onToggle={toggleFilter} />
          </div>

          {/* Match statistics */}
          <div className="mb-4">
            <MatchStatsPanel stats={stats} />
          </div>

          {/* Results */}
          <EventResultsPanel
            events={events}
            activeFilters={activeFilters}
            eventTypes={eventTypes}
            onSeek={seekVideo}
            selectedEvents={selectedEvents}
            onToggleSelected={toggleEventSelected}
          />
        </div>
      </main>

      {/* ===== RIGHT CHAT ===== */}
      <aside className="w-[316px] shrink-0 flex flex-col overflow-hidden border-l border-[#eaecf0]">
        <Chat analysisId={analysis.id} onSeek={seekVideo} />
      </aside>
    </div>
  );
}
