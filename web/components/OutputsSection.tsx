"use client";

import { useState } from "react";
import { ApiError, Match, MatchOutput, OutputStatus, api } from "@/lib/api";

const STATUS: Record<OutputStatus, { label: string; color: string }> = {
  stopped: { label: "Stopped", color: "text-[#6b7280] bg-[#f3f4f6]" },
  waiting_for_signal: { label: "Waiting for signal", color: "text-[#92610a] bg-[#fdf3dc]" },
  connecting: { label: "Connecting", color: "text-[#2f5fe0] bg-[#eef1ff]" },
  live: { label: "Sending", color: "text-[#14713d] bg-[#e3f6ea]" },
  retrying: { label: "Retrying", color: "text-[#92610a] bg-[#fdf3dc]" },
  incompatible: { label: "Can't send this signal", color: "text-red-700 bg-red-50" },
  failed: { label: "Failed", color: "text-red-700 bg-red-50" },
  offline: { label: "Unknown", color: "text-red-700 bg-red-50" },
};

// Polled with the match by the panel above, so what shows here is at most a couple of seconds old.
export function OutputsSection({ match, ended }: { match: Match; ended: boolean }) {
  const [name, setName] = useState("");
  const [url, setUrl] = useState("");
  const [streamKey, setStreamKey] = useState("");
  const [error, setError] = useState<string | null>(null);

  async function act(action: () => Promise<unknown>) {
    setError(null);
    try {
      await action();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "That didn't work. Check that the API is running, then try again.");
    }
  }

  const handleAdd = (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    return act(async () => {
      await api.matches.addOutput(match.id, { name: name.trim(), url: url.trim(), stream_key: streamKey.trim() });
      setName("");
      setUrl("");
      setStreamKey("");
    });
  };

  const field = "min-w-0 bg-white border border-[#eaecf0] rounded-[8px] px-3 py-2 text-[14px]";

  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-[15px] font-bold">Destinations</h2>
      <p className="text-[13px] text-[#6b7280]">
        Sends what is on air to YouTube, Twitch or another RTMP server, exactly as the encoder sends it. Each destination
        runs by itself, so a failure in one doesn&apos;t affect the others, the public page or the recording.
      </p>
      {match.outputs.map((output) => (
        <OutputCard key={output.id} matchId={match.id} output={output} ended={ended} act={act} />
      ))}
      {error && <p className="text-[13px] text-red-600">{error}</p>}
      {!ended && (
        <form onSubmit={handleAdd} className="flex flex-wrap gap-2">
          <input
            required
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Name, e.g. YouTube"
            className={`${field} w-[160px]`}
          />
          <input
            required
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="Server, e.g. rtmps://a.rtmps.youtube.com/live2"
            className={`${field} flex-1 min-w-[240px]`}
          />
          <input
            required
            type="password"
            autoComplete="off"
            value={streamKey}
            onChange={(e) => setStreamKey(e.target.value)}
            placeholder="Stream key"
            className={`${field} w-[180px]`}
          />
          <button className="border border-[#2f5fe0] text-[#2f5fe0] hover:bg-[#eef1ff] rounded-[8px] px-4 py-2 text-[14px] font-semibold">
            Add destination
          </button>
        </form>
      )}
    </section>
  );
}

function OutputCard({
  matchId,
  output,
  ended,
  act,
}: {
  matchId: string;
  output: MatchOutput;
  ended: boolean;
  act: (action: () => Promise<unknown>) => Promise<void>;
}) {
  const status = STATUS[output.status];
  const link = "text-[13px] font-semibold text-[#2f5fe0] hover:underline";

  const handleReplaceKey = () => {
    const key = prompt(`New stream key for "${output.name}". It restarts the destination if it's running.`);
    if (key?.trim()) return act(() => api.matches.replaceOutputKey(matchId, output.id, key.trim()));
  };

  const handleDelete = () => {
    if (confirm(`Remove "${output.name}"? Sending to it stops, and its key is deleted.`))
      return act(() => api.matches.deleteOutput(matchId, output.id));
  };

  return (
    <div className="bg-white border border-[#eaecf0] rounded-xl p-4 flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-3">
        <h3 className="text-[14px] font-bold">{output.name}</h3>
        <span className={`text-[12px] font-semibold rounded-full px-2 py-0.5 ${status.color}`}>{status.label}</span>
        <span className="text-[13px] text-[#6b7280] truncate min-w-0 flex-1">{output.url}</span>
      </div>
      {output.status === "retrying" && (
        <p className="text-[13px] text-[#92610a]">
          Attempt {output.attempts}. It tries again by itself, with longer waits, and gives up if this keeps failing.
        </p>
      )}
      {output.error && <p className="text-[13px] text-red-600 break-words">{output.error}</p>}
      {output.warning && <p className="text-[13px] text-[#92610a]">{output.warning}</p>}
      {!ended && (
        <div className="flex flex-wrap gap-4">
          {output.enabled ? (
            <>
              <button onClick={() => act(() => api.matches.stopOutput(matchId, output.id))} className={link}>
                Stop
              </button>
              {output.status === "failed" || output.status === "retrying" ? (
                <button onClick={() => act(() => api.matches.startOutput(matchId, output.id))} className={link}>
                  Try again now
                </button>
              ) : null}
            </>
          ) : (
            <button onClick={() => act(() => api.matches.startOutput(matchId, output.id))} className={link}>
              Start
            </button>
          )}
          <button onClick={handleReplaceKey} className={link}>
            Replace key
          </button>
          <button onClick={handleDelete} className="text-[13px] font-semibold text-red-600 hover:underline">
            Remove
          </button>
        </div>
      )}
    </div>
  );
}
