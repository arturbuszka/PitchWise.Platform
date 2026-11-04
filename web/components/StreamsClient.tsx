"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { CreatedStream, LiveStream, api } from "@/lib/api";
import { HlsPlayer } from "./HlsPlayer";

const STATUS_STYLE: Record<LiveStream["status"], string> = {
  live: "bg-red-100 text-red-700",
  idle: "bg-[#eef0f3] text-[#6b7280]",
  ended: "bg-[#eef0f3] text-[#9aa0a8]",
};

export function StreamsClient() {
  const [streams, setStreams] = useState<LiveStream[]>([]);
  const [name, setName] = useState("");
  const [created, setCreated] = useState<CreatedStream | null>(null);
  const [watching, setWatching] = useState<string | null>(null);
  const [playbackUrl, setPlaybackUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const watched = streams.find((s) => s.id === watching);
  const watchedIsLive = watched?.status === "live";

  // Status comes from the encoder, so it can change at any moment.
  useEffect(() => {
    const load = () => api.streams.list().then(setStreams).catch(() => {});
    load();
    const interval = setInterval(load, 3000);
    return () => clearInterval(interval);
  }, []);

  // The URL is signed and expires, so it is fetched when needed rather than kept with the stream.
  useEffect(() => {
    if (!watching || !watchedIsLive) return;
    let cancelled = false;
    api.streams.playback(watching).then((p) => {
      if (!cancelled) setPlaybackUrl(p?.url ?? null);
    });
    return () => {
      cancelled = true;
      setPlaybackUrl(null);
    };
  }, [watching, watchedIsLive]);

  async function handleCreate(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!name.trim()) return;
    setError(null);
    try {
      const result = await api.streams.create(name.trim());
      setCreated(result);
      setStreams((prev) => [result.stream, ...prev]);
      setName("");
    } catch {
      setError("Could not create the stream. Check that the API is running.");
    }
  }

  async function handleEnd(id: string) {
    const ended = await api.streams.end(id).catch(() => null);
    if (ended) setStreams((prev) => prev.map((s) => (s.id === id ? ended : s)));
  }

  return (
    <div className="min-h-screen">
      <header className="bg-[#14181f] px-6 py-4 flex items-center justify-between">
        <span className="text-[19px] font-black text-white tracking-tight">
          Pitch<span className="text-[#6f9bff]">Wise</span>
        </span>
        <Link href="/" className="text-[13px] text-[#8b919b] hover:text-white font-medium">
          ↩ All analyses
        </Link>
      </header>

      <main className="max-w-[900px] mx-auto p-6 flex flex-col gap-5">
        <h1 className="text-[22px] font-black">Live streams</h1>

        <form onSubmit={handleCreate} className="flex gap-2">
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Stream name, e.g. Saturday final"
            className="flex-1 bg-white border border-[#eaecf0] rounded-[8px] px-3 py-2 text-[14px]"
          />
          <button className="bg-[#2f5fe0] hover:bg-[#2451c7] text-white rounded-[8px] px-4 py-2 text-[14px] font-semibold">
            Create stream
          </button>
        </form>
        {error && <p className="text-[13px] text-red-600">{error}</p>}

        {created && (
          <div className="bg-amber-50 border border-amber-200 rounded-xl p-4">
            <p className="text-[14px] font-bold mb-1">Publish to “{created.stream.name}”</p>
            <p className="text-[13px] text-[#6b7280] mb-2">
              Copy this now: it contains the stream key, which is not shown again.
            </p>
            <code className="block bg-white border border-amber-200 rounded-[6px] p-2 text-[12px] break-all">
              {created.ingest_url}
            </code>
          </div>
        )}

        {watching && (
          <div className="bg-black rounded-xl overflow-hidden">
            {playbackUrl ? (
              <HlsPlayer
                key={playbackUrl}
                hlsUrl={playbackUrl}
                autoPlay
                className="w-full max-h-[480px] object-contain"
              />
            ) : (
              <div className="aspect-video flex items-center justify-center text-[14px] text-[#9aa0a8]">
                Waiting for the encoder…
              </div>
            )}
          </div>
        )}

        <div className="flex flex-col gap-2">
          {streams.length === 0 && (
            <p className="text-[14px] text-[#6b7280]">No streams yet. Create one to get an address to publish to.</p>
          )}
          {streams.map((s) => (
            <div
              key={s.id}
              className="bg-white border border-[#eaecf0] rounded-xl px-4 py-3 flex items-center gap-3"
            >
              <span className={`px-2.5 py-0.5 rounded-full text-[12px] font-semibold ${STATUS_STYLE[s.status]}`}>
                {s.status}
              </span>
              <span className="flex-1 min-w-0 truncate text-[14px] font-semibold">{s.name}</span>
              {s.status !== "ended" && (
                <>
                  <button
                    onClick={() => setWatching(s.id)}
                    className="text-[13px] font-semibold text-[#2f5fe0] hover:underline"
                  >
                    Watch
                  </button>
                  <button
                    onClick={() => handleEnd(s.id)}
                    className="text-[13px] font-semibold text-red-600 hover:underline"
                  >
                    End
                  </button>
                </>
              )}
            </div>
          ))}
        </div>
      </main>
    </div>
  );
}
