"use client";

import { useState } from "react";
import Link from "next/link";
import { api, LiveSession } from "@/lib/api";
import { LiveSessionView } from "@/components/LiveSessionView";

export default function LivePage() {
  const [sourceUrl, setSourceUrl] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [session, setSession] = useState<LiveSession | null>(null);

  async function handleStart(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!sourceUrl.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const s = await api.live.create(sourceUrl.trim());
      setSession(s);
    } catch {
      setError("Failed to create live session. Check the API is running.");
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="min-h-screen flex flex-col">
      {/* Nav */}
      <nav className="bg-white border-b border-[#eaecf0] px-6 h-[58px] flex items-center justify-between">
        <div className="flex items-center gap-2.5">
          <Link href="/" className="flex items-center gap-2.5">
            <span className="w-[30px] h-[30px] rounded-[8px] bg-[#14181f] text-white flex items-center justify-center text-[15px]">
              ⚽
            </span>
            <span className="text-[21px] font-black tracking-tight leading-none">
              Pitch<span className="text-[#2f5fe0]">Wise</span>
            </span>
          </Link>
          <span className="text-[#d1d5db] mx-1">/</span>
          <span className="text-[15px] font-semibold text-[#14181f]">Live Analysis</span>
        </div>
        <Link
          href="/"
          className="text-[13px] font-medium text-[#6b7280] hover:text-[#14181f] transition-colors"
        >
          ← Back to analyses
        </Link>
      </nav>

      <main className="flex-1 px-6 py-8" style={{ maxWidth: 1380, margin: "0 auto", width: "100%" }}>
        {!session ? (
          <div className="max-w-xl mx-auto mt-16">
            <div className="text-center mb-8">
              <div className="w-16 h-16 rounded-2xl bg-[#14181f] text-white flex items-center justify-center text-3xl mx-auto mb-4">
                📡
              </div>
              <h1 className="text-[28px] font-black tracking-tight text-[#14181f]">
                Live Analysis
              </h1>
              <p className="text-[14px] text-[#9aa0a8] mt-2 font-medium">
                Stream a live match and get real-time player tracking, field projection, and tactical tips
              </p>
            </div>

            <form onSubmit={handleStart} className="bg-white rounded-2xl border border-[#eaecf0] shadow-sm p-6 space-y-4">
              <div>
                <label className="block text-[13px] font-semibold text-[#14181f] mb-2">
                  Stream URL
                </label>
                <input
                  type="url"
                  value={sourceUrl}
                  onChange={(e) => setSourceUrl(e.target.value)}
                  placeholder="https://www.youtube.com/watch?v=... or HLS stream URL"
                  className="w-full border border-[#e4e7ec] rounded-xl px-4 py-3 text-[14px] focus:outline-none focus:border-[#2f5fe0] focus:ring-2 focus:ring-[#2f5fe0]/20"
                  required
                />
                <p className="text-[12px] text-[#9aa0a8] mt-1.5">
                  Accepts HLS (.m3u8), RTMP, or any ffmpeg-compatible stream URL
                </p>
              </div>

              {error && (
                <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-[13px] text-red-700">
                  {error}
                </div>
              )}

              <button
                type="submit"
                disabled={loading || !sourceUrl.trim()}
                className="w-full bg-[#2f5fe0] hover:bg-[#2451c7] disabled:opacity-50 text-white text-[14px] font-semibold rounded-xl py-3 transition-colors flex items-center justify-center gap-2"
              >
                {loading ? (
                  <>
                    <span className="w-4 h-4 border-2 border-white/30 border-t-white rounded-full animate-spin" />
                    Starting…
                  </>
                ) : (
                  <>▶ Start Live Analysis</>
                )}
              </button>
            </form>
          </div>
        ) : (
          <LiveSessionView
            session={session}
            onStop={() => setSession(null)}
          />
        )}
      </main>
    </div>
  );
}
