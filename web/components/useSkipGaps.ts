"use client";

import { RefObject, useEffect } from "react";
import { Gap } from "@/lib/api";

// A recording keeps its signal gaps, so its positions stay on the match timeline. Left alone, a
// player would hold the last frame for as long as a gap lasts, so this moves past them instead.
// Positions are unaffected: a moment after a gap is at the same time as it would be without this.
export function useSkipGaps(videoRef: RefObject<HTMLVideoElement | null>, gaps: Gap[] | undefined, reloadKey?: unknown) {
  useEffect(() => {
    const video = videoRef.current;
    if (!video || !gaps?.length) return;

    const skip = () => {
      const gap = gaps.find((g) => video.currentTime >= g.start && video.currentTime < g.end);
      if (gap) video.currentTime = gap.end;
    };

    video.addEventListener("timeupdate", skip);
    video.addEventListener("seeked", skip);
    video.addEventListener("loadedmetadata", skip);
    return () => {
      video.removeEventListener("timeupdate", skip);
      video.removeEventListener("seeked", skip);
      video.removeEventListener("loadedmetadata", skip);
    };
  }, [videoRef, gaps, reloadKey]);
}
