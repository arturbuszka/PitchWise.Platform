"use client";

import { useEffect, useRef, useState, useCallback } from "react";
import { LiveHlsPlayer } from "./LiveHlsPlayer";
import { PitchMinimap, PlayerDot } from "./PitchMinimap";
import { LiveTipsPanel } from "./LiveTipsPanel";
import { api, LiveSession } from "@/lib/api";

interface Tip {
  text: string;
  timestamp: number;
}

interface Props {
  session: LiveSession;
  onStop: () => void;
}

type ConnectionStatus = "connecting" | "ready" | "stopped" | "error";

export function LiveSessionView({ session, onStop }: Props) {
  const wsRef = useRef<WebSocket | null>(null);
  const [status, setStatus] = useState<ConnectionStatus>("connecting");
  const [hlsUrl, setHlsUrl] = useState<string | null>(null);
  const [players, setPlayers] = useState<PlayerDot[]>([]);
  const [tips, setTips] = useState<Tip[]>([]);
  const [calibrated, setCalibrated] = useState(false);
  const [errorMsg, setErrorMsg] = useState<string | null>(null);
  const [stats, setStats] = useState<{ fps: number; infer_ms: number; counts: Record<string, number> } | null>(null);
  const elapsedRef = useRef(0);

  const send = useCallback((msg: object) => {
    if (wsRef.current?.readyState === WebSocket.OPEN) {
      wsRef.current.send(JSON.stringify(msg));
    }
  }, []);

  useEffect(() => {
    // `cancelled` tracks whether THIS effect run has been torn down (e.g. React 18
    // Strict Mode mounts the effect, runs cleanup, then mounts again in dev). Once
    // cancelled, handlers must not touch component state — otherwise the aborted
    // first socket's onclose latches the UI to "stopped" and the surviving socket's
    // `start` is never the one we listen to.
    let cancelled = false;
    const ws = new WebSocket(session.ws_url);
    wsRef.current = ws;

    ws.onopen = () => {
      if (cancelled) return;
      ws.send(JSON.stringify({ type: "start", source_url: session.source_url }));
    };

    ws.onmessage = (evt) => {
      if (cancelled) return;
      let msg: Record<string, unknown>;
      try {
        msg = JSON.parse(evt.data);
      } catch {
        return;
      }

      switch (msg.type) {
        case "ready": {
          setStatus("ready");
          // hls_url from the WS is relative to the live server (e.g. /live_hls/<sid>/index.m3u8).
          // Resolve it against the live server's HTTP origin. Use the actual socket URL
          // (ws.url is always absolute & normalized) and guard the whole thing: a throw
          // here previously left hlsUrl=null after "ready" → green "Live" but no player.
          const rawUrl = msg.hls_url as string;
          let resolved = rawUrl;
          if (rawUrl.startsWith("/")) {
            try {
              const httpUrl = (ws.url || session.ws_url)
                .replace(/^ws:/, "http:")
                .replace(/^wss:/, "https:");
              resolved = new URL(httpUrl).origin + rawUrl;
            } catch (err) {
              console.warn("[LiveSessionView] could not derive live origin from", ws.url, session.ws_url, err);
              // Last-resort fallback: resolve against the page origin.
              resolved = window.location.origin + rawUrl;
            }
          }
          console.log("[LiveSessionView] ready → hlsUrl =", resolved, "(raw:", rawUrl, "ws.url:", ws.url, ")");
          setHlsUrl(resolved);
          break;
        }
        case "positions":
          elapsedRef.current = (msg.timestamp as number) ?? 0;
          setPlayers((msg.players as PlayerDot[]) ?? []);
          break;
        case "tip":
          setTips((prev) => [{ text: msg.text as string, timestamp: elapsedRef.current }, ...prev]);
          break;
        case "stats":
          setStats({
            fps: msg.fps as number,
            infer_ms: msg.infer_ms as number,
            counts: (msg.counts as Record<string, number>) ?? {},
          });
          break;
        case "error":
          setStatus("error");
          setErrorMsg(msg.message as string);
          break;
      }
    };

    ws.onclose = () => {
      if (cancelled) return;
      // Functional update avoids the stale `status` closure: a late close from a
      // superseded socket must not clobber an already-good session.
      setStatus((prev) => (prev === "stopped" ? prev : "stopped"));
    };

    ws.onerror = () => {
      if (cancelled) return;
      setStatus("error");
      setErrorMsg("WebSocket connection failed");
    };

    return () => {
      cancelled = true;
      ws.onmessage = ws.onclose = ws.onerror = null;
      if (ws.readyState === WebSocket.OPEN) {
        ws.close();
      } else if (ws.readyState === WebSocket.CONNECTING) {
        // Closing a still-CONNECTING socket aborts the handshake — the browser
        // logs "WebSocket is closed before the connection is established" and the
        // server sees an open→close with no frames. Instead, wait until it opens
        // and close it cleanly then. (Strict Mode's throwaway first socket.)
        ws.onopen = () => ws.close();
      } else {
        ws.onopen = null;
      }
    };
  }, [session.ws_url, session.source_url]);

  async function handleStop() {
    send({ type: "stop" });
    wsRef.current?.close();
    await api.live.stop(session.id);
    setStatus("stopped");
    onStop();
  }

  function handleCalibrate(points: { pixel: [number, number]; pitch: [number, number] }[]) {
    send({ type: "calibrate", points });
    setCalibrated(true);
  }

  return (
    <div className="flex flex-col gap-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-[20px] font-black tracking-tight text-[#14181f]">Live Analysis</h2>
          <p className="text-[13px] text-[#9aa0a8] font-medium truncate max-w-[400px]">{session.source_url}</p>
        </div>
        <div className="flex items-center gap-3">
          {stats && (
            <span className="text-[12px] text-[#6b7280] font-medium bg-[#f4f6f9] rounded-lg px-3 py-1.5">
              {stats.fps.toFixed(1)} fps · {stats.infer_ms.toFixed(0)}ms
            </span>
          )}
          <span className={`px-3 py-1.5 rounded-full text-[12px] font-semibold ${
            status === "ready" ? "bg-green-100 text-green-700" :
            status === "connecting" ? "bg-orange-100 text-orange-700" :
            status === "error" ? "bg-red-100 text-red-700" :
            "bg-[#eef0f3] text-[#6b7280]"
          }`}>
            {status === "connecting" ? "Connecting…" :
             status === "ready" ? "● Live" :
             status === "error" ? "Error" : "Stopped"}
          </span>
          <button
            onClick={handleStop}
            className="bg-red-600 hover:bg-red-700 text-white text-[13px] font-semibold rounded-lg px-4 py-2 transition-colors"
          >
            Stop
          </button>
        </div>
      </div>

      {status === "error" && (
        <div className="bg-red-50 border border-red-200 rounded-xl p-4 text-[13px] text-red-700">
          {errorMsg || "An error occurred"}
        </div>
      )}

      {/* 3-column layout */}
      <div className="grid grid-cols-[1fr_420px_300px] gap-4 items-start">
        {/* Annotated HLS stream */}
        <div className="bg-black rounded-xl overflow-hidden aspect-video flex items-center justify-center">
          {hlsUrl ? (
            <LiveHlsPlayer
              hlsUrl={hlsUrl}
              className="w-full h-full object-contain"
            />
          ) : (
            <div className="flex flex-col items-center text-white/50 gap-2">
              <span className="text-4xl">📡</span>
              <span className="text-[13px]">
                {status === "connecting" ? "Connecting to stream…" : "Waiting for stream…"}
              </span>
            </div>
          )}
        </div>

        {/* Pitch minimap */}
        <PitchMinimap
          players={players}
          calibrated={calibrated}
          onCalibrate={handleCalibrate}
          frameSnapshotUrl={null}
        />

        {/* Tactical tips */}
        <LiveTipsPanel tips={tips} />
      </div>
    </div>
  );
}
