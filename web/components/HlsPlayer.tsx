"use client";

import { useEffect, useRef } from "react";
import Hls from "hls.js";

// Plays an HLS stream, falling back to a plain MP4 when one is given. A live stream has none.
//
// - Where Media Source Extensions exist: hls.js. HLS segments come from the nginx edge, so the
//   app server is off the byte path.
// - Otherwise, e.g. Safari on iPhone: native HLS via canPlayType.
// - No HLS / hls.js unsupported / fatal error: fall back to the MP4 src.
export function HlsPlayer({
  hlsUrl,
  fallbackSrc,
  className,
  autoPlay,
  controls = true,
}: {
  hlsUrl?: string | null;
  fallbackSrc?: string;
  className?: string;
  autoPlay?: boolean;
  controls?: boolean;
}) {
  const videoRef = useRef<HTMLVideoElement>(null);

  useEffect(() => {
    const video = videoRef.current;
    if (!video) return;

    const playMp4 = () => {
      if (fallbackSrc) video.src = fallbackSrc;
    };

    if (!hlsUrl) {
      playMp4();
      return;
    }

    // hls.js wherever Media Source Extensions exist. It comes first because recent Chrome also
    // claims native HLS support, which failed on our live streams.
    if (Hls.isSupported()) {
      const hls = new Hls();
      hls.loadSource(hlsUrl);
      hls.attachMedia(video);
      hls.on(Hls.Events.ERROR, (_evt, data) => {
        // On an unrecoverable error, fall back to the MP4 so playback still works.
        if (data.fatal) {
          hls.destroy();
          playMp4();
        }
      });
      return () => hls.destroy();
    }

    // Native HLS, for browsers without MSE such as Safari on iPhone.
    if (video.canPlayType("application/vnd.apple.mpegurl")) {
      video.src = hlsUrl;
      // If the HLS manifest/segments fail to load (e.g. edge unreachable, expired
      // signature), fall back to the MP4 so playback still works.
      video.onerror = () => {
        video.onerror = null;
        playMp4();
      };
      return () => {
        video.onerror = null;
      };
    }

    // No HLS support at all.
    playMp4();
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
