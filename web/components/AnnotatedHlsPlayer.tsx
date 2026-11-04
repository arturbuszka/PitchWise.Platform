"use client";

import { useEffect, useRef, RefObject } from "react";
import Hls from "hls.js";

// Plays the annotated (boxes) analysis video, a progressive HLS "event" playlist that
// grows while the worker analyses the file. Unlike the shared HlsPlayer this has NO mp4
// fallback: the source is only ever an HLS playlist, so a failure must not fall back to
// re-loading the same .m3u8 (that caused a request storm — hundreds of index.m3u8 hits
// per second). hls.js is configured to poll a live/growing playlist calmly and to give up
// after a bounded number of retries instead of hammering.
export function AnnotatedHlsPlayer({
  hlsUrl,
  className,
  controls = true,
  videoRef: externalRef,
}: {
  hlsUrl: string;
  className?: string;
  controls?: boolean;
  videoRef?: RefObject<HTMLVideoElement | null>;
}) {
  const internalRef = useRef<HTMLVideoElement>(null);
  const videoRef = externalRef ?? internalRef;

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    // Prefer hls.js everywhere it works (Chrome/Firefox/Edge). Only use the browser's
    // native HLS when hls.js is unsupported AND the browser really can play HLS (Safari).
    if (Hls.isSupported()) {
      const hls = new Hls({
        // Growing "event" playlist: poll politely, don't retry into a storm.
        manifestLoadPolicy: {
          default: {
            maxTimeToFirstByteMs: 10_000,
            maxLoadTimeMs: 20_000,
            timeoutRetry: { maxNumRetry: 2, retryDelayMs: 1000, maxRetryDelayMs: 4000 },
            errorRetry: { maxNumRetry: 3, retryDelayMs: 1000, maxRetryDelayMs: 8000 },
          },
        },
      });
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.ERROR, (_evt, data) => {
        // On a fatal error, try one media/network recovery; otherwise stop cleanly —
        // never re-point the <video> at the playlist (that loops).
        if (!data.fatal) return;
        if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
          hls.startLoad();
        } else if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          hls.recoverMediaError();
        } else {
          hls.destroy();
        }
      });
      return () => hls.destroy();
    }

    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      return;
    }
    // No HLS support at all — leave the player empty (no mp4 to fall back to).
  }, [hlsUrl, videoRef]);

  return (
    <video ref={videoRef} controls={controls} className={className} />
  );
}
