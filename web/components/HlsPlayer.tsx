"use client";

import { useEffect, useRef } from "react";
import Hls from "hls.js";

// Plays an HLS stream when available, falling back to a plain MP4.
//
// - Safari/iOS: native HLS via the canPlayType path (no hls.js needed).
// - Chrome/Firefox: hls.js (Media Source Extensions). HLS segments come from the
//   nginx edge — the app server is off the byte path.
// - No HLS / hls.js unsupported / fatal error: fall back to the MP4 src.
export function HlsPlayer({
  hlsUrl,
  fallbackSrc,
  className,
  autoPlay,
  controls = true,
}: {
  hlsUrl?: string | null;
  fallbackSrc: string;
  className?: string;
  autoPlay?: boolean;
  controls?: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const useMp4 = () => {
      video.src = fallbackSrc;
    };

    if (!hlsUrl) {
      useMp4();
      return;
    }

    // Native HLS (Safari, iOS, Android Chrome).
    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      // If the HLS manifest/segments fail to load (e.g. edge unreachable, expired
      // signature), fall back to the MP4 so playback still works.
      video.onerror = () => {
        video.onerror = null;
        useMp4();
      };
      return () => {
        video.onerror = null;
      };
    }

    // hls.js (Chrome, Firefox).
    if (Hls.isSupported()) {
      const hls = new Hls();
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.ERROR, (_evt, data) => {
        // On an unrecoverable error, fall back to the MP4 so playback still works.
        if (data.fatal) {
          hls.destroy();
          useMp4();
        }
      });
      return () => hls.destroy();
    }

    // No HLS support at all.
    useMp4();
  }, [hlsUrl, fallbackSrc]);

  return (
    <video
      ref={videoRef}
      controls={controls}
      autoPlay={autoPlay}
      className={className}
    />
  );
}
