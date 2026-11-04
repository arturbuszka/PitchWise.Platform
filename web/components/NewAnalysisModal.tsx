"use client";

import { useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { api } from "@/lib/api";

type Sport = "football" | "basketball" | "handball";

const SPORTS: { key: Sport; label: string }[] = [
  { key: "football", label: "⚽ Football" },
  { key: "basketball", label: "🏀 Basketball" },
  { key: "handball", label: "🤾 Handball" },
];

export function NewAnalysisModal({ onClose }: { onClose: () => void }) {
  const [title, setTitle] = useState("");
  const [sport, setSport] = useState<Sport>("football");
  const [file, setFile] = useState<File | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);
  const router = useRouter();

  async function handleSubmit() {
    if (!file) return;
    setLoading(true);
    setError(null);
    try {
      const analysis = await api.analyses.create(title || file.name, sport);
      await api.analyses.videos.upload(analysis.id, file, file.name);
      router.push(`/analyses/${analysis.id}`);
    } catch {
      setError("Failed to create the analysis. Please try again.");
      setLoading(false);
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* Backdrop */}
      <div
        className="absolute inset-0 bg-[#14181f]/60 backdrop-blur-sm"
        onClick={onClose}
      />

      {/* Modal card */}
      <div className="relative bg-white rounded-2xl w-full max-w-[480px] mx-4 p-7 shadow-2xl flex flex-col gap-5">
        <div className="flex items-start justify-between">
          <div>
            <h2 className="text-[23px] font-black leading-tight">New analysis</h2>
            <p className="text-[13px] text-[#9aa0a8] font-medium mt-0.5">
              Name the analysis and pick a sport
            </p>
          </div>
          <button
            onClick={onClose}
            className="bg-[#f1f3f5] rounded-[9px] w-9 h-9 text-[15px] text-[#6b7280] hover:bg-[#eceef1] transition-colors flex items-center justify-center"
          >
            ✕
          </button>
        </div>

        {/* Title */}
        <div className="flex flex-col gap-1.5">
          <label className="text-[13px] font-semibold text-[#374151]">
            Analysis name
          </label>
          <input
            type="text"
            value={title}
            onChange={(e) => setTitle(e.target.value)}
            placeholder="e.g. Match: Eagles vs Falcons"
            className="border border-[#e4e7ec] rounded-[10px] px-3 py-3 text-[15px] text-[#374151] focus:outline-none focus:border-[#2f5fe0] focus:ring-2 focus:ring-[#2f5fe0]/12 transition-shadow"
          />
        </div>

        {/* Sport chips */}
        <div className="flex flex-col gap-1.5">
          <label className="text-[13px] font-semibold text-[#374151]">
            Sport
          </label>
          <div className="flex gap-2 flex-wrap">
            {SPORTS.map((s) => (
              <button
                key={s.key}
                onClick={() => setSport(s.key)}
                className={`px-4 py-2 rounded-[9px] text-[13px] font-semibold transition-colors ${
                  sport === s.key
                    ? "bg-[#2f5fe0] text-white"
                    : "border border-[#e4e7ec] text-[#6b7280] hover:border-[#2f5fe0] hover:text-[#2f5fe0]"
                }`}
              >
                {s.label}
              </button>
            ))}
          </div>
        </div>

        {/* File upload */}
        <div className="flex flex-col gap-1.5">
          <label className="text-[13px] font-semibold text-[#374151]">
            Video file
          </label>
          <div
            onClick={() => fileRef.current?.click()}
            className="border-2 border-dashed border-[#e4e7ec] rounded-xl px-4 py-6 text-center cursor-pointer hover:border-[#2f5fe0] transition-colors"
          >
            {file ? (
              <p className="text-sm font-medium text-[#14181f]">{file.name}</p>
            ) : (
              <p className="text-sm text-[#9aa0a8]">
                Click to choose a file (.mp4, .mov, .mkv, .avi)
              </p>
            )}
            <input
              ref={fileRef}
              type="file"
              accept=".mp4,.mov,.mkv,.avi"
              className="hidden"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </div>
        </div>

        {error && <p className="text-red-500 text-sm">{error}</p>}

        {/* Actions */}
        <div className="flex justify-end gap-3 pt-1">
          <button
            onClick={onClose}
            className="border border-[#e4e7ec] bg-white rounded-[10px] px-4 py-2.5 text-[14px] font-semibold text-[#374151] hover:bg-[#f7f8fa] transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={handleSubmit}
            disabled={!file || loading}
            className="bg-[#2f5fe0] hover:bg-[#2451c7] disabled:opacity-40 disabled:cursor-not-allowed text-white rounded-[10px] px-5 py-2.5 text-[14px] font-semibold transition-colors shadow-sm"
          >
            {loading ? "Creating…" : "Create and open →"}
          </button>
        </div>
      </div>
    </div>
  );
}
