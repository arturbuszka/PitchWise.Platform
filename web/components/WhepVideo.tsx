"use client";

import { useEffect, useRef, useState } from "react";

const RETRY_MS = 2000;
// Candidates from the first moments are enough here: MediaMTX gives its own address in its answer.
const GATHERING_MS = 500;
const PATIENCE = 2;
// A session MediaMTX closed on us can sit in "connecting" and never say so.
const FIRST_FRAME_MS = 5000;

/** Plays a signal over WebRTC, which has far less delay than HLS. `address` is asked for at each connection, as it expires. */
export function WhepVideo({ address, live, label }: { address: () => Promise<string>; live: boolean; label: string }) {
  const video = useRef<HTMLVideoElement>(null);
  const [picture, setPicture] = useState(false);
  // Tries that ended without a single frame. MediaMTX accepts a camera with B-frames, then closes the session.
  const [fruitless, setFruitless] = useState(0);
  // Read through a ref so that a new function from a parent doesn't drop the connection.
  const addressRef = useRef(address);
  useEffect(() => {
    addressRef.current = address;
  }, [address]);

  useEffect(() => {
    if (!live) return;
    let stopped = false;
    let peer: RTCPeerConnection | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    let hadPicture = false;
    let waitingForFrame: ReturnType<typeof setTimeout> | undefined;
    const element = video.current;
    const onPlaying = () => {
      // Chromium "plays" a 2x2 placeholder when the stream never delivers a frame it can decode.
      if (!element || element.videoWidth <= 2) return;
      hadPicture = true;
      setPicture(true);
      setFruitless(0);
    };
    element?.addEventListener("playing", onPlaying);
    element?.addEventListener("resize", onPlaying);

    const again = () => {
      clearTimeout(waitingForFrame);
      peer?.close();
      peer = null;
      setPicture(false);
      if (!hadPicture) setFruitless((n) => n + 1);
      hadPicture = false;
      if (!stopped) retry = setTimeout(connect, RETRY_MS);
    };

    const connect = async () => {
      try {
        const current = new RTCPeerConnection();
        peer = current;
        current.addTransceiver("video", { direction: "recvonly" });
        current.ontrack = (e) => {
          if (video.current) video.current.srcObject = e.streams[0];
        };
        current.onconnectionstatechange = () => {
          if (current !== peer) return;
          if (current.connectionState === "failed" || current.connectionState === "disconnected") again();
        };
        await current.setLocalDescription(await current.createOffer());
        await new Promise((resolve) => setTimeout(resolve, GATHERING_MS));
        const url = await addressRef.current();
        const response = await fetch(url, {
          method: "POST",
          headers: { "Content-Type": "application/sdp" },
          body: current.localDescription?.sdp,
        });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        await current.setRemoteDescription({ type: "answer", sdp: await response.text() });
        waitingForFrame = setTimeout(() => {
          if (!hadPicture && current === peer) again();
        }, FIRST_FRAME_MS);
      } catch {
        again();
      }
    };

    connect();
    return () => {
      stopped = true;
      clearTimeout(retry);
      clearTimeout(waitingForFrame);
      element?.removeEventListener("playing", onPlaying);
      element?.removeEventListener("resize", onPlaying);
      peer?.close();
      peer = null;
      setPicture(false);
      setFruitless(0);
    };
  }, [live]);

  return (
    <div className="relative bg-black aspect-video">
      <video ref={video} autoPlay muted playsInline aria-label={label} className="w-full h-full object-contain" />
      {!(live && picture) && (
        <div className="absolute inset-0 flex items-center justify-center p-3 text-center text-[13px] text-[#9aa0a8]">
          {!live
            ? "No signal"
            : fruitless >= PATIENCE
              ? "No preview. If this encoder uses B-frames, turn them off: WebRTC can't show them. Cutting to it still works."
              : "Connecting…"}
        </div>
      )}
    </div>
  );
}
