"use client";

import { useEffect, useRef, useState } from "react";
import { Playback, PublicMatch, Vod, api } from "@/lib/api";
import { HlsPlayer } from "./HlsPlayer";
import { useSkipGaps } from "./useSkipGaps";

// Every viewer runs this, so it must stay gentle on the API: a viewer who is watching asks twice a
// minute. The random part stops viewers who opened the page together from asking together.
const WATCHING_POLL_MS = 30_000;
const WAITING_POLL_MS = 10_000;
const AFTER_ERROR_POLL_MS = 3_000;
const JITTER_MS = 3_000;
// Ask for a new signed URL this long before the current one stops working.
const RENEW_BEFORE_MS = 5 * 60_000;
// A recording isn't expected to fail like a live stream does, so stop trying after a few goes.
const MAX_VOD_RETRIES = 3;

type Loaded = { kind: "loading" } | { kind: "missing" } | { kind: "ok"; match: PublicMatch };

// A signed URL differs on every call, so only replace it if the source changed or it is expiring.
function keepOrReplace(current: Playback | null, next: Playback | null): Playback | null {
  if (!current || !next) return next;
  const expiring = new Date(current.expires_at).getTime() - Date.now() < RENEW_BEFORE_MS;
  return current.input_id !== next.input_id || expiring ? next : current;
}

function keepOrReplaceVod(current: Vod | null, next: Vod | null): Vod | null {
  if (!current || !next) return next;
  return new Date(current.expires_at).getTime() - Date.now() < RENEW_BEFORE_MS ? next : current;
}

function VodPlayer({
  vod,
  resumeAtRef,
  onError,
}: {
  vod: Vod;
  resumeAtRef: React.RefObject<number>;
  onError: () => void;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  useSkipGaps(videoRef, vod.gaps, vod.url);

  return (
    <video
      ref={videoRef}
      src={vod.url}
      controls
      playsInline
      preload="metadata"
      // A link that expires while someone watches comes back here with a new one, at the same place.
      onLoadedMetadata={(e) => {
        if (resumeAtRef.current > 0) e.currentTarget.currentTime = resumeAtRef.current;
      }}
      onTimeUpdate={(e) => {
        resumeAtRef.current = e.currentTarget.currentTime;
      }}
      onError={onError}
      className="w-full h-full object-contain"
    />
  );
}

export function PublicMatchPlayer({
  publicId,
  showTitle,
  fill,
}: {
  publicId: string;
  showTitle?: boolean;
  fill?: boolean;
}) {
  const [loaded, setLoaded] = useState<Loaded>({ kind: "loading" });
  const [playback, setPlayback] = useState<Playback | null>(null);
  const [vod, setVod] = useState<Vod | null>(null);
  // Bumped after a playback error, to start the player again on the same URL.
  const [attempt, setAttempt] = useState(0);
  const afterError = useRef<() => void>(() => {});
  const vodPosition = useRef(0);

  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const poll = async () => {
      let delay = WAITING_POLL_MS;
      try {
        const match = await api.publicMatches.get(publicId);
        if (stopped) return;
        if (!match) {
          setLoaded({ kind: "missing" });
          return;
        }
        setLoaded({ kind: "ok", match });
        setPlayback((current) => keepOrReplace(current, match.playback));
        setVod((current) => keepOrReplaceVod(current, match.vod));
        // Nothing more can change after the end, except a recording that is still being prepared.
        if (match.status === "ended" && match.recording_status !== "processing") return;
        if (match.status === "live") delay = WATCHING_POLL_MS;
      } catch {
        // The API being away doesn't stop a player that is already running. Just ask again later.
      }
      if (stopped) return;
      clearTimeout(timer);
      timer = setTimeout(poll, delay + Math.random() * JITTER_MS);
    };

    // The stream went away under the player. Find out whether it's a drop or the end of the match,
    // then start the player again if it should still be playing.
    afterError.current = () => {
      clearTimeout(timer);
      timer = setTimeout(async () => {
        await poll();
        if (!stopped) setAttempt((a) => a + 1);
      }, AFTER_ERROR_POLL_MS);
    };

    timer = setTimeout(poll, 0);
    return () => {
      stopped = true;
      clearTimeout(timer);
    };
  }, [publicId]);

  const sizing = fill ? "w-full h-full" : "w-full aspect-video rounded-xl overflow-hidden shadow-2xl";
  const match = loaded.kind === "ok" ? loaded.match : null;
  const showVod = match?.status === "ended" && vod && attempt <= MAX_VOD_RETRIES;

  return (
    <div className={fill ? "w-full h-full" : "w-full"}>
      {showTitle && match && (
        <div className="flex items-center gap-3 mb-3">
          <h1 className="text-[20px] font-bold truncate">{match.name}</h1>
          {match.status === "live" && (
            <span className="px-2 py-0.5 rounded bg-red-600 text-[11px] font-bold uppercase tracking-wide">Live</span>
          )}
        </div>
      )}
      <div className={`${sizing} bg-black`}>
        {match?.status === "live" && playback ? (
          <HlsPlayer
            key={`${playback.url}#${attempt}`}
            hlsUrl={playback.url}
            autoPlay
            muted
            onFatalError={() => afterError.current()}
            className="w-full h-full object-contain"
          />
        ) : showVod ? (
          <VodPlayer
            key={`${vod.url}#${attempt}`}
            vod={vod}
            resumeAtRef={vodPosition}
            onError={() => afterError.current()}
          />
        ) : (
          <div className="w-full h-full flex items-center justify-center text-center p-6 text-[15px] text-[#9aa0a8]">
            {message(loaded)}
          </div>
        )}
      </div>
    </div>
  );
}

function message(loaded: Loaded): string {
  if (loaded.kind === "loading") return "Loading…";
  if (loaded.kind === "missing") return "This match does not exist.";
  const { status, started_at, recording_status } = loaded.match;
  if (status === "ended" && recording_status === "processing") return "This broadcast has ended. The recording will be here soon.";
  if (status === "ended") return "This broadcast has ended.";
  if (status === "live") return "Connecting…";
  return started_at ? "The signal was lost. The broadcast will continue when it returns." : "The broadcast has not started yet.";
}
