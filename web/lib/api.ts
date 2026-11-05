// Browser: "" → relative /api/* paths, which proxy.ts forwards with the API key added, so the
// key never reaches the browser.
// Server (SSR): must use an absolute URL and send the key itself, since it skips proxy.ts.
const API =
  process.env.NEXT_PUBLIC_API_URL ||
  (typeof window === "undefined"
    ? process.env.API_INTERNAL_URL || "http://localhost:8000"
    : "");

function apiFetch(input: string, init?: RequestInit): Promise<Response> {
  if (typeof window !== "undefined") return fetch(input, init);
  return fetch(input, {
    ...init,
    headers: { ...init?.headers, "X-Api-Key": process.env.WEB_API_KEY ?? "" },
  });
}

export type Sport = "football" | "basketball" | "handball";
export type SessionStatus = "draft" | "processing" | "done";
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

// A part of a recording with no signal, in seconds.
export interface Gap {
  start: number;
  end: number;
}

export interface VideoItem {
  id: number;
  analysis_id: number;
  name: string;
  duration_seconds: number | null;
  fps: number | null;
  order: number;
  gaps: Gap[];
}

export interface AnalysisDetail extends AnalysisSummary {
  videos: VideoItem[];
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
}

export interface EventTypeConfig {
  key: EventType;
  label: string;
  icon: string;
  color: string;
  bg: string;
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

export const api = {
  analyses: {
    list: (sport?: string, search?: string): Promise<AnalysisSummary[]> => {
      const params = new URLSearchParams();
      if (sport) params.set("sport", sport);
      if (search) params.set("search", search);
      const qs = params.toString();
      return apiFetch(`${API}/api/analyses${qs ? `?${qs}` : ""}`, { cache: "no-store" }).then((r) => r.json());
    },
    get: (id: number): Promise<AnalysisDetail> =>
      apiFetch(`${API}/api/analyses/${id}`, { cache: "no-store" }).then((r) => r.json()),
    create: (name: string, sport: Sport, subtitle?: string): Promise<AnalysisDetail> =>
      apiFetch(`${API}/api/analyses`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name, sport, subtitle }),
      }).then((r) => r.json()),

    events: {
      list: (analysisId: number, type?: EventType): Promise<AnalysisEvent[]> => {
        const qs = type ? `?type=${type}` : "";
        return apiFetch(`${API}/api/analyses/${analysisId}/events${qs}`, { cache: "no-store" }).then((r) => r.json());
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
        apiFetch(`${API}/api/analyses/${analysisId}/events`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        }).then((r) => r.json()),
      delete: (analysisId: number, eventId: number): Promise<void> =>
        apiFetch(`${API}/api/analyses/${analysisId}/events/${eventId}`, { method: "DELETE" }).then(() => undefined),
    },

    videos: {
      list: (analysisId: number): Promise<VideoItem[]> =>
        apiFetch(`${API}/api/analyses/${analysisId}/videos`, { cache: "no-store" }).then((r) => r.json()),
      upload: (analysisId: number, file: File, name: string): Promise<VideoItem> => {
        const fd = new FormData();
        fd.append("file", file);
        fd.append("name", name);
        return apiFetch(`${API}/api/analyses/${analysisId}/videos`, { method: "POST", body: fd }).then((r) => r.json());
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
    },

    highlights: {
      create: (analysisId: number, name: string, eventIds: number[]): Promise<Highlight> =>
        apiFetch(`${API}/api/analyses/${analysisId}/highlights`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name, event_ids: eventIds }),
        }).then((r) => r.json()),
      status: (analysisId: number, id: number): Promise<Highlight | null> =>
        apiFetch(`${API}/api/analyses/${analysisId}/highlights/${id}/status`, { cache: "no-store" }).then((r) =>
          r.ok ? r.json() : null
        ),
      streamUrl: (analysisId: number, id: number) =>
        `${API}/api/analyses/${analysisId}/highlights/${id}/stream`,
      // Signed HLS manifest URL (served by the nginx edge, not the API). null if not ready.
      hlsUrl: (analysisId: number, id: number): Promise<HlsUrl | null> =>
        apiFetch(`${API}/api/analyses/${analysisId}/highlights/${id}/hls`, { cache: "no-store" }).then((r) =>
          r.ok ? r.json() : null
        ),
      share: (analysisId: number, id: number): Promise<ShareLink> =>
        apiFetch(`${API}/api/analyses/${analysisId}/highlights/${id}/share`, { method: "POST" }).then((r) => r.json()),
    },

  },

  // Public share access (no auth, time-limited token).
  share: {
    meta: (token: string): Promise<{ ok: boolean; status: number; data: SharePublic | null }> =>
      apiFetch(`${API}/api/share/${token}`, { cache: "no-store" }).then(async (r) => ({
        ok: r.ok,
        status: r.status,
        data: r.ok ? ((await r.json()) as SharePublic) : null,
      })),
    streamUrl: (token: string) => `${API}/api/share/${token}/stream`,
    // Signed HLS manifest URL for public viewers (edge-served, cached). null if not ready.
    hlsUrl: (token: string): Promise<HlsUrl | null> =>
      apiFetch(`${API}/api/share/${token}/hls`, { cache: "no-store" }).then((r) => (r.ok ? r.json() : null)),
  },

  eventTypes: {
    list: (sport?: string): Promise<EventTypeConfig[]> => {
      const qs = sport ? `?sport=${sport}` : "";
      return apiFetch(`${API}/api/event-types${qs}`, { cache: "no-store" }).then((r) => r.json());
    },
  },

  matches: {
    list: (): Promise<Match[]> => apiFetch(`${API}/api/matches`, { cache: "no-store" }).then(json<Match[]>),
    get: (id: string): Promise<Match> => apiFetch(`${API}/api/matches/${id}`, { cache: "no-store" }).then(json<Match>),
    create: (name: string): Promise<CreatedMatch> =>
      apiFetch(`${API}/api/matches`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      }).then(json<CreatedMatch>),
    addInput: (id: string, name: string): Promise<CreatedInput> =>
      apiFetch(`${API}/api/matches/${id}/inputs`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ name }),
      }).then(json<CreatedInput>),
    rotateKey: (id: string, inputId: string): Promise<InputAccess> =>
      apiFetch(`${API}/api/matches/${id}/inputs/${inputId}/rotate-key`, { method: "POST" }).then(json<InputAccess>),
    setProgram: (id: string, inputId: string): Promise<Match> =>
      apiFetch(`${API}/api/matches/${id}/program`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ input_id: inputId }),
      }).then(json<Match>),
    // Signed URL on the edge. null unless the match is live.
    playback: (id: string): Promise<Playback | null> =>
      apiFetch(`${API}/api/matches/${id}/playback`, { cache: "no-store" }).then((r) => (r.ok ? r.json() : null)),
    end: (id: string): Promise<Match> =>
      apiFetch(`${API}/api/matches/${id}/end`, { method: "POST" }).then(json<Match>),
    retryRecording: (id: string): Promise<Match> =>
      apiFetch(`${API}/api/matches/${id}/recording/retry`, { method: "POST" }).then(json<Match>),
  },

  // What the public page and embed read. Needs no key.
  publicMatches: {
    get: (publicId: string): Promise<PublicMatch | null> =>
      apiFetch(`${API}/api/public/matches/${publicId}`, { cache: "no-store" }).then(async (r) => {
        if (r.status === 404) return null;
        return json<PublicMatch>(r);
      }),
  },
};

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json() as Promise<T>;
}

export type MatchStatus = "waiting" | "live" | "ended";
export type RecordingStatus = "none" | "processing" | "ready" | "failed";

export interface Signal {
  state: "no_signal" | "receiving" | "stalled";
  protocol: string | null;
  connected_since: string | null;
  bitrate_kbps: number | null;
  // Loss, drops and round trip are null over RTMP, because TCP hides them.
  loss_percent: number | null;
  packets_lost: number | null;
  packets_dropped: number | null;
  rtt_ms: number | null;
  tracks: string[];
}

export interface MatchInput {
  id: string;
  name: string;
  is_program: boolean;
  signal: Signal;
}

export interface Match {
  id: string;
  name: string;
  status: MatchStatus;
  public_id: string;
  public_url: string;
  embed_url: string;
  embed_html: string;
  program_input_id: string | null;
  analysis_id: number;
  created_at: string;
  started_at: string | null;
  ended_at: string | null;
  recording_status: RecordingStatus;
  recording_error: string | null;
  inputs: MatchInput[];
}

// The stream key is only ever returned when it is issued.
export interface InputAccess {
  input_id: string;
  stream_key: string;
  rtmp_url: string;
  rtmp_server: string;
  rtmp_stream_name: string;
  srt_url: string;
}

export interface CreatedMatch {
  match: Match;
  access: InputAccess;
}

export interface CreatedInput {
  input: MatchInput;
  access: InputAccess;
}

export interface Playback {
  url: string;
  expires_at: string;
  input_id: string;
}

// A finished recording on the edge.
export interface Vod {
  url: string;
  expires_at: string;
  duration_seconds: number | null;
  gaps: Gap[];
}

export interface PublicMatch {
  name: string;
  status: MatchStatus;
  started_at: string | null;
  ended_at: string | null;
  playback: Playback | null;
  recording_status: RecordingStatus;
  vod: Vod | null;
}

export function formatTime(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = Math.floor(seconds % 60);
  return `${m}:${s.toString().padStart(2, "0")}`;
}
