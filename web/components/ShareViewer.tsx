"use client";

import { useEffect, useState } from "react";
import { SharePublic, api } from "@/lib/api";
import { HlsPlayer } from "./HlsPlayer";

type State =
  | { kind: "loading" }
  | { kind: "ok"; meta: SharePublic }
  | { kind: "expired" }
  | { kind: "missing" };

export function ShareViewer({ token }: { token: string }) {
  const [state, setState] = useState<State>({ kind: "loading" });
  const [hlsUrl, setHlsUrl] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    api.share.meta(token).then((res) => {
      if (cancelled) return;
      if (res.ok && res.data) setState({ kind: "ok", meta: res.data });
      else if (res.status === 410) setState({ kind: "expired" });
      else setState({ kind: "missing" });
    });
    // Fetch the signed HLS URL in parallel; null leaves the player on MP4 fallback.
    api.share.hlsUrl(token).then((h) => {
      if (!cancelled) setHlsUrl(h?.url ?? null);
    });
    return () => {
      cancelled = true;
    };
  }, [token]);

  return (
    <div className="min-h-screen bg-[#0b0e13] text-white flex flex-col items-center justify-center px-4">
      {/* Brand */}
      <div className="flex items-center gap-2 mb-6">
        <span className="w-[26px] h-[26px] rounded-[7px] bg-[#2f5fe0] flex items-center justify-center text-[13px]">
          ⚽
        </span>
        <span className="text-[19px] font-black leading-none tracking-tight">
          Pitch<span className="text-[#6f9bff]">Wise</span>
        </span>
      </div>

      {state.kind === "loading" && (
        <p className="text-[#8b919b] text-sm">Loading highlight…</p>
      )}

      {state.kind === "expired" && (
        <div className="text-center">
          <p className="text-[22px] font-bold mb-2">This link has expired</p>
          <p className="text-[#8b919b] text-sm">Ask for a fresh share link to watch this highlight.</p>
        </div>
      )}

      {state.kind === "missing" && (
        <div className="text-center">
          <p className="text-[22px] font-bold mb-2">Highlight not found</p>
          <p className="text-[#8b919b] text-sm">This share link is invalid.</p>
        </div>
      )}

      {state.kind === "ok" && (
        <div className="w-full max-w-[900px]">
          <p className="text-[18px] font-bold mb-3 text-center">{state.meta.name}</p>
          <div className="rounded-xl overflow-hidden bg-black shadow-2xl">
            <HlsPlayer
              hlsUrl={hlsUrl}
              fallbackSrc={api.share.streamUrl(token)}
              controls
              autoPlay
              className="w-full max-h-[70vh] object-contain bg-black"
            />
          </div>
        </div>
      )}
    </div>
  );
}
