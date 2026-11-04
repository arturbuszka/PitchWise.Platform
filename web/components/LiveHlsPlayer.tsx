"use client";

import { useEffect, useRef } from "react";
import Hls from "hls.js";

export function LiveHlsPlayer({
  hlsUrl,
  className,
}: {
  hlsUrl: string;
  className?: string;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);
  const hlsRef = useRef<Hls | null>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video || !hlsUrl) return;

    // Prefer hls.js wherever it's supported (Chrome/Edge/Firefox). Chrome reports a
    // truthy canPlayType("application/vnd.apple.mpegurl") ("maybe") but does NOT actually
    // play HLS natively — going down the native branch there left a black 0:00 frame.
    // Native HLS is the fallback only when hls.js can't run (real Safari/iOS).
    if (!Hls.isSupported()) {
      if (video.canPlayType("application/vnd.apple.mpegurl")) {
        console.log("[LiveHlsPlayer] native HLS playback", hlsUrl);
        video.src = hlsUrl;
        video.play().catch((err) => console.warn("[LiveHlsPlayer] native play() rejected:", err));
      } else {
        console.error("[LiveHlsPlayer] neither hls.js nor native HLS is supported");
      }
      return;
    }

    const hls = new Hls({
      liveSyncDurationCount: 3,
      liveMaxLatencyDurationCount: 6,
      maxBufferLength: 10,
      maxMaxBufferLength: 20,
      // Retry aggressively on 404 (segment not yet available)
      manifestLoadingRetryDelay: 500,
      manifestLoadingMaxRetry: 20,
      levelLoadingRetryDelay: 500,
      levelLoadingMaxRetry: 20,
    });

    hlsRef.current = hls;
    console.log("[LiveHlsPlayer] loading", hlsUrl);
    hls.loadSource(hlsUrl);
    hls.attachMedia(video);

    hls.on(Hls.Events.MANIFEST_PARSED, (_e, data) => {
      console.log("[LiveHlsPlayer] manifest parsed, levels:", data.levels?.length);
      video.play().then(() => console.log("[LiveHlsPlayer] playing")).catch((err) => console.warn("[LiveHlsPlayer] play() rejected:", err));
    });
    hls.on(Hls.Events.LEVEL_LOADED, (_e, data) => {
      console.log("[LiveHlsPlayer] level loaded, fragments:", data.details?.fragments?.length, "live:", data.details?.live);
    });
    hls.on(Hls.Events.FRAG_BUFFERED, (_e, data) => {
      console.log("[LiveHlsPlayer] frag buffered:", data.frag?.sn);
    });

    hls.on(Hls.Events.ERROR, (_evt, data) => {
      console.error("[LiveHlsPlayer] HLS error:", data.type, data.details, "fatal:", data.fatal, data.response?.code ?? "");
      if (data.fatal) {
        // For live streams, try to recover instead of giving up
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
          hls.startLoad();
        } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          hls.recoverMediaError();
        }
      }
    });

    return () => {
      hls.destroy();
      hlsRef.current = null;
    };
  }, [hlsUrl]);

  return (
    <video
      ref={videoRef}
      autoPlay
      muted
      playsInline
      controls
      className={className}
    />
  );
}
