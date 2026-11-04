// Browser: "" → relative /api/* paths handled by the Next proxy (same-origin, no CORS).
// Server (SSR): must use an absolute, routable URL — relative fetch isn't allowed in
// server components. API_INTERNAL_URL points at the API (compose: http://api:8000;
// dev host: defaults to http://localhost:8000).
const API =
  process.env.NEXT_PUBLIC_API_URL ||
  (typeof window === "undefined"
    ? process.env.API_INTERNAL_URL || "http://localhost:8000"
    : "");

// ---------------------------------------------------------------------------
// Nowe interfejsy (nowy model danych)
// ---------------------------------------------------------------------------

export type Sport = "football" | "basketball" | "handball";
export type SessionStatus = "draft" | "processing" | "done";
export type VisionJobStatus = "pending" | "running" | "done" | "failed";
export type EventType =
  | "goal"
  | "shot"
  | "wayward_pass"
  | "foul"
  | "free_kick"
  | "offside"
  | "substitution"
  | "set_piece"
  | "manual";

export interface AnalysisSummary {
  id: number;
  name: string;
  subtitle: string | null;
  sport: Sport;
  status: SessionStatus;
  created_at: string;
  updated_at: string;
  video_count: number;
}

export interface VideoItem {
  id: number;
  analysis_id: number;
  name: string;
  duration_seconds: number | null;
  fps: number | null;
  order: number;
}

export interface AnalysisDetail extends AnalysisSummary {
  videos: VideoItem[];
}

export interface ClipItem {
  id: number;
  event_id: number;
  video_id: number;
  filename: string;
  start_seconds: number;
  end_seconds: number;
}

export interface AnalysisEvent {
  id: number;
  analysis_id: number;
  video_id: number | null;
  type: EventType;
  source: "auto" | "manual";
  timestamp_seconds: number;
  confidence: number | null;
  label: string | null;
  note: string | null;
  player_number: number | null;
  player_name: string | null;
  assist_number: number | null;
  assist_name: string | null;
  clip: ClipItem | null;
}

export interface VisionJob {
  id: number;
  video_id: number;
  status: VisionJobStatus;
  progress: number;
  error: string | null;
  created_at: string;
  finished_at: string | null;
  // Whether the annotated (boxes burned-in) playback file has been rendered.
  annotated_ready: boolean;
}

export interface EventTypeConfig {
  key: EventType;
  label: string;
  icon: string;
  color: string;
  bg: string;
}

export interface TeamStats {
  possession_pct: number;
  passes: number;
  turnovers: number;
  pass_accuracy_pct: number;
}

export interface PlayerTimeOnPitch {
  player_id: number;
  seconds_on_pitch: number;
  frames_seen: number;
}

export interface MatchStats {
  video_id: number;
  analysis_id: number;
  team_a: TeamStats;
  team_b: TeamStats;
  controlled_seconds: number;
  loose_seconds: number;
  time_on_pitch: PlayerTimeOnPitch[];
}

export type HighlightStatus = "pending" | "running" | "done" | "failed";

export interface Highlight {
  id: number;
  analysis_id: number;
  name: string;
  status: HighlightStatus;
  progress: number;
  error: string | null;
  share_token: string | null;
  share_expires_at: string | null;
  created_at: string;
  finished_at: string | null;
}

export interface ShareLink {
  token: string;
  url: string;
  expires_at: string;
}

export interface SharePublic {
  name: string;
  status: HighlightStatus;
  expires_at: string;
}

export interface HlsUrl {
  url: string;
  expires_at: string;
}

// ---------------------------------------------------------------------------
// Nowe metody API
// ---------------------------------------------------------------------------

export const api = {
  analyses: {
    list: (sport?: string, search?: string): Promise<AnalysisSummary[]> => {
      const params = new URLSearchParams();
      if (sport) params.set("sport", sport);
      if (search) params.set("search", search);
      const qs = params.toString();
      return fetch(`${API}/api/analyses${qs ? `?${qs}` : ""}`, { cache: "no-store" }).then((r) => r.json());
    },
    get: (id: number): Promise<AnalysisDetail> =>
      fetch(`${API}/api/analyses/${id}`, { cache: "no-store" }).then((r) => r.json()),
    create: (name: string, sport: Sport, subtitle?: string): Promise<AnalysisDetail> =>
      fetch(`${API}/api/analyses`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, sport, subtitle }),
      }).then((r) => r.json()),

    events: {
      list: (analysisId: number, type?: EventType): Promise<AnalysisEvent[]> => {
        const qs = type ? `?type=${type}` : "";
        return fetch(`${API}/api/analyses/${analysisId}/events${qs}`, { cache: "no-store" }).then((r) => r.json());
      },
      create: (
        analysisId: number,
        payload: {
          timestamp_seconds: number;
          type?: EventType;
          label?: string;
          note?: string;
          video_id?: number;
          player_number?: number;
          player_name?: string;
          assist_number?: number;
          assist_name?: string;
        }
      ): Promise<AnalysisEvent> =>
        fetch(`${API}/api/analyses/${analysisId}/events`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        }).then((r) => r.json()),
      delete: (analysisId: number, eventId: number): Promise<void> =>
        fetch(`${API}/api/analyses/${analysisId}/events/${eventId}`, { method: "DELETE" }).then(() => undefined),
    },

    videos: {
      list: (analysisId: number): Promise<VideoItem[]> =>
        fetch(`${API}/api/analyses/${analysisId}/videos`, { cache: "no-store" }).then((r) => r.json()),
      upload: (analysisId: number, file: File, name: string): Promise<VideoItem> => {
        const fd = new FormData();
        fd.append("file", file);
        fd.append("name", name);
        return fetch(`${API}/api/analyses/${analysisId}/videos`, { method: "POST", body: fd }).then((r) => r.json());
      },
      // Upload with progress (XHR) — fetch does not report upload progress.
      // Recommended for large files (up to ~2GB). onProgress receives 0..1.
      uploadWithProgress: (
        analysisId: number,
        file: File,
        name: string,
        onProgress?: (fraction: number) => void
      ): Promise<VideoItem> =>
        new Promise<VideoItem>((resolve, reject) => {
          const fd = new FormData();
          fd.append("file", file);
          fd.append("name", name);
          const xhr = new XMLHttpRequest();
          xhr.open("POST", `${API}/api/analyses/${analysisId}/videos`);
          xhr.upload.onprogress = (e) => {
            if (e.lengthComputable && onProgress) onProgress(e.loaded / e.total);
          };
          xhr.onload = () => {
            if (xhr.status >= 200 && xhr.status < 300) {
              try {
                resolve(JSON.parse(xhr.responseText) as VideoItem);
              } catch {
                reject(new Error("Invalid server response"));
              }
            } else {
              reject(new Error(`Upload failed (HTTP ${xhr.status})`));
            }
          };
          xhr.onerror = () => reject(new Error("Network error during upload"));
          xhr.send(fd);
        }),
      streamUrl: (analysisId: number, videoId: number) =>
        `${API}/api/analyses/${analysisId}/videos/${videoId}/stream`,
      // Annotated (boxes burned-in) playback — progressive HLS VOD. Watchable within
      // seconds of starting analysis; only valid once status.annotated_ready.
      annotatedHlsUrl: (analysisId: number, videoId: number) =>
        `${API}/api/analyses/${analysisId}/videos/${videoId}/annotated/index.m3u8`,
      analyze: (analysisId: number, videoId: number): Promise<VisionJob> =>
        fetch(`${API}/api/analyses/${analysisId}/videos/${videoId}/analyze`, { method: "POST" }).then((r) => r.json()),
      // Cancel the active analysis (marks the job cancelled; the worker stops). Returns the
      // updated job, or null if there was nothing running.
      cancel: (analysisId: number, videoId: number): Promise<VisionJob | null> =>
        fetch(`${API}/api/analyses/${analysisId}/videos/${videoId}/analyze/cancel`, { method: "POST" }).then(
          async (r) => {
            if (!r.ok) return null;
            const text = await r.text();
            return text ? (JSON.parse(text) as VisionJob) : null;
          }
        ),
      status: (analysisId: number, videoId: number): Promise<VisionJob | null> =>
        // Body may be an empty 200 (never analysed) — guard r.json() against empty input.
        fetch(`${API}/api/analyses/${analysisId}/videos/${videoId}/status`, { cache: "no-store" }).then(
          async (r) => {
            if (!r.ok) return null;
            const text = await r.text();
            return text ? (JSON.parse(text) as VisionJob) : null;
          }
        ),
      // Whole-match aggregate stats. 404 (→ null) until analysis has produced a row.
      stats: (analysisId: number, videoId: number): Promise<MatchStats | null> =>
        fetch(`${API}/api/analyses/${analysisId}/videos/${videoId}/stats`, { cache: "no-store" }).then(
          (r) => (r.ok ? (r.json() as Promise<MatchStats>) : null)
        ),
    },

    highlights: {
      create: (analysisId: number, name: string, eventIds: number[]): Promise<Highlight> =>
        fetch(`${API}/api/analyses/${analysisId}/highlights`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name, event_ids: eventIds }),
        }).then((r) => r.json()),
      status: (analysisId: number, id: number): Promise<Highlight | null> =>
        fetch(`${API}/api/analyses/${analysisId}/highlights/${id}/status`, { cache: "no-store" }).then((r) =>
          r.ok ? r.json() : null
        ),
      streamUrl: (analysisId: number, id: number) =>
        `${API}/api/analyses/${analysisId}/highlights/${id}/stream`,
      // Signed HLS manifest URL (served by the nginx edge, not the API). null if not ready.
      hlsUrl: (analysisId: number, id: number): Promise<HlsUrl | null> =>
        fetch(`${API}/api/analyses/${analysisId}/highlights/${id}/hls`, { cache: "no-store" }).then((r) =>
          r.ok ? r.json() : null
        ),
      share: (analysisId: number, id: number): Promise<ShareLink> =>
        fetch(`${API}/api/analyses/${analysisId}/highlights/${id}/share`, { method: "POST" }).then((r) => r.json()),
    },

    chatUrl: (analysisId: number) => `${API}/api/analyses/${analysisId}/chat`,
  },

  // Public share access (no auth, time-limited token).
  share: {
    meta: (token: string): Promise<{ ok: boolean; status: number; data: SharePublic | null }> =>
      fetch(`${API}/api/share/${token}`, { cache: "no-store" }).then(async (r) => ({
        ok: r.ok,
        status: r.status,
        data: r.ok ? ((await r.json()) as SharePublic) : null,
      })),
    streamUrl: (token: string) => `${API}/api/share/${token}/stream`,
    // Signed HLS manifest URL for public viewers (edge-served, cached). null if not ready.
    hlsUrl: (token: string): Promise<HlsUrl | null> =>
      fetch(`${API}/api/share/${token}/hls`, { cache: "no-store" }).then((r) => (r.ok ? r.json() : null)),
  },

  eventTypes: {
    list: (sport?: string): Promise<EventTypeConfig[]> => {
      const qs = sport ? `?sport=${sport}` : "";
      return fetch(`${API}/api/event-types${qs}`, { cache: "no-store" }).then((r) => r.json());
    },
  },

  live: {
    create: (sourceUrl: string): Promise<LiveSession> =>
      fetch(`${API}/api/live`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ source_url: sourceUrl }),
      }).then((r) => r.json()),
    get: (id: string): Promise<LiveSession> =>
      fetch(`${API}/api/live/${id}`, { cache: "no-store" }).then((r) => r.json()),
    stop: (id: string): Promise<LiveSession> =>
      fetch(`${API}/api/live/${id}`, { method: "DELETE" }).then((r) => r.json()),
  },
};

export interface LiveSession {
  id: string;
  source_url: string;
  status: "idle" | "running" | "stopped";
  ws_url: string;
  hls_url: string;
  created_at: string;
  stopped_at: string | null;
}

export function formatTime(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}
