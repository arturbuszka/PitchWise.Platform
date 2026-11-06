"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { InputAccess, Match, MatchInput, Playback, RecordingStatus, Signal, api } from "@/lib/api";
import { ConsoleHeader } from "./ConsoleHeader";
import { HlsPlayer } from "./HlsPlayer";
import { MatchStatusBadge } from "./MatchStatusBadge";
import { takeIssuedAccess } from "./issuedAccess";

const POLL_MS = 2000;

export function MatchPanel({ id }: { id: string }) {
  const [match, setMatch] = useState<Match | null>(null);
  const [missing, setMissing] = useState(false);
  // Keys just issued, by input. The API can't return them again.
  const [access, setAccess] = useState<Record<string, InputAccess>>({});
  const [newInput, setNewInput] = useState("");
  const [playback, setPlayback] = useState<Playback | null>(null);
  const [error, setError] = useState<string | null>(null);

  const programId = match?.program_input_id;
  const isLive = match?.status === "live";

  useEffect(() => {
    let active = true;
    const load = () =>
      api.matches
        .get(id)
        .then((m) => {
          if (!active) return;
          setMatch(m);
          const issued = takeIssuedAccess(m.inputs.map((i) => i.id));
          if (Object.keys(issued).length > 0) setAccess((prev) => ({ ...prev, ...issued }));
        })
        .catch((e: Error) => {
          if (active && e.message === "HTTP 404") setMissing(true);
        });
    load();
    const interval = setInterval(load, POLL_MS);
    return () => {
      active = false;
      clearInterval(interval);
    };
  }, [id]);

  // The URL is signed and expires, so it is fetched when needed rather than kept with the match.
  useEffect(() => {
    if (!isLive) return;
    let active = true;
    api.matches.playback(id).then((p) => {
      if (active) setPlayback(p);
    });
    return () => {
      active = false;
      setPlayback(null);
    };
  }, [id, isLive, programId]);

  async function act(action: () => Promise<void>) {
    setError(null);
    try {
      await action();
    } catch {
      setError("That didn't work. Check that the API is running, then try again.");
    }
  }

  const handleAddInput = (e: React.SyntheticEvent<HTMLFormElement>) => {
    e.preventDefault();
    if (!newInput.trim()) return;
    return act(async () => {
      const created = await api.matches.addInput(id, newInput.trim());
      setAccess((prev) => ({ ...prev, [created.input.id]: created.access }));
      setMatch((m) => m && { ...m, inputs: [...m.inputs, created.input] });
      setNewInput("");
    });
  };

  const handleRotate = (input: MatchInput) => {
    if (!confirm(`Issue a new key for "${input.name}"? Its encoder is disconnected until it uses the new one.`)) return;
    return act(async () => {
      const issued = await api.matches.rotateKey(id, input.id);
      setAccess((prev) => ({ ...prev, [input.id]: issued }));
    });
  };

  const handleEnd = () => {
    if (!confirm("End this match? Encoders are disconnected and can't publish to it again.")) return;
    return act(async () => setMatch(await api.matches.end(id)));
  };

  const handleRetryRecording = () => act(async () => setMatch(await api.matches.retryRecording(id)));

  if (missing) {
    return (
      <div className="min-h-screen">
        <ConsoleHeader />
        <p className="p-6 text-[14px] text-[#6b7280]">This match does not exist.</p>
      </div>
    );
  }
  if (!match) return <ConsoleHeader />;

  const ended = match.status === "ended";

  return (
    <div className="min-h-screen">
      <ConsoleHeader />
      <main className="max-w-[1000px] mx-auto p-6 flex flex-col gap-5">
        <div className="flex items-center gap-3">
          <h1 className="text-[22px] font-black flex-1 min-w-0 truncate">{match.name}</h1>
          <MatchStatusBadge status={match.status} />
          {!ended && (
            <button onClick={handleEnd} className="text-[13px] font-semibold text-red-600 hover:underline">
              End match
            </button>
          )}
        </div>
        {error && <p className="text-[13px] text-red-600">{error}</p>}

        <section className="flex flex-col gap-3">
          <h2 className="text-[15px] font-bold">Inputs</h2>
          {match.inputs.map((input) => (
            <InputCard
              key={input.id}
              input={input}
              access={access[input.id]}
              ended={ended}
              onPutOnAir={() =>
                act(async () => {
                  setMatch(await api.matches.setProgram(id, input.id));
                })
              }
              onRotate={() => handleRotate(input)}
              onDismissAccess={() =>
                setAccess((prev) => {
                  const rest = { ...prev };
                  delete rest[input.id];
                  return rest;
                })
              }
            />
          ))}
          {!ended && (
            <form onSubmit={handleAddInput} className="flex gap-2">
              <input
                value={newInput}
                onChange={(e) => setNewInput(e.target.value)}
                placeholder="Another input, e.g. Wide camera"
                className="flex-1 min-w-0 bg-white border border-[#eaecf0] rounded-[8px] px-3 py-2 text-[14px]"
              />
              <button className="border border-[#2f5fe0] text-[#2f5fe0] hover:bg-[#eef1ff] rounded-[8px] px-4 py-2 text-[14px] font-semibold">
                Add input
              </button>
            </form>
          )}
        </section>

        <section className="flex flex-col gap-3">
          <h2 className="text-[15px] font-bold">On air</h2>
          <div className="bg-black rounded-xl overflow-hidden aspect-video">
            {isLive && playback ? (
              <HlsPlayer
                key={playback.url}
                hlsUrl={playback.url}
                autoPlay
                muted
                className="w-full h-full object-contain"
              />
            ) : (
              <div className="w-full h-full flex items-center justify-center text-[14px] text-[#9aa0a8]">
                {ended ? "This match has ended." : "Waiting for the signal on the input on air…"}
              </div>
            )}
          </div>
        </section>

        {ended && <RecordingCard match={match} onRetry={handleRetryRecording} />}

        <section className="bg-white border border-[#eaecf0] rounded-xl p-4 flex flex-col gap-3">
          <h2 className="text-[15px] font-bold">Share</h2>
          <Field label="Public page" value={match.public_url} />
          <Field label="Embed code" value={match.embed_html} />
          <p className="text-[13px] text-[#6b7280]">
            Mark moments and build highlights in{" "}
            <Link href={`/analyses/${match.analysis_id}`} className="text-[#2f5fe0] hover:underline">
              this match&apos;s analysis
            </Link>
            . Each input is recorded, and its video arrives there once the match has ended.
          </p>
        </section>
      </main>
    </div>
  );
}

const RECORDING_MESSAGE: Record<RecordingStatus, string> = {
  none: "Nothing was recorded, because no signal arrived.",
  processing: "Joining the recording. This takes a few minutes at most, and needs no action.",
  ready: "The recording is ready, on the public page and in this match's analysis.",
  failed: "The recording could not be finished. Nothing is lost: its raw files are kept, so you can try again.",
};

function RecordingCard({ match, onRetry }: { match: Match; onRetry: () => void }) {
  const status = match.recording_status;
  return (
    <section className="bg-white border border-[#eaecf0] rounded-xl p-4 flex flex-col gap-2">
      <h2 className="text-[15px] font-bold">Recording</h2>
      <p className="text-[13px] text-[#6b7280]">{RECORDING_MESSAGE[status]}</p>
      {match.recording_error && <p className="text-[13px] text-red-600">{match.recording_error}</p>}
      {status === "failed" && (
        <button onClick={onRetry} className="self-start text-[13px] font-semibold text-[#2f5fe0] hover:underline">
          Try again
        </button>
      )}
    </section>
  );
}

function InputCard({
  input,
  access,
  ended,
  onPutOnAir,
  onRotate,
  onDismissAccess,
}: {
  input: MatchInput;
  access?: InputAccess;
  ended: boolean;
  onPutOnAir: () => void;
  onRotate: () => void;
  onDismissAccess: () => void;
}) {
  return (
    <div className="bg-white border border-[#eaecf0] rounded-xl p-4 flex flex-col gap-3">
      <div className="flex flex-wrap items-center gap-3">
        <SignalLight state={input.signal.state} />
        <span className="text-[14px] font-semibold">{input.name}</span>
        {input.is_program && (
          <span className="px-2 py-0.5 rounded bg-red-600 text-white text-[11px] font-bold uppercase tracking-wide">
            On air
          </span>
        )}
        <span className="flex-1" />
        {!ended && !input.is_program && (
          <button onClick={onPutOnAir} className="text-[13px] font-semibold text-[#2f5fe0] hover:underline">
            Put on air
          </button>
        )}
        {!ended && (
          <button onClick={onRotate} className="text-[13px] font-semibold text-[#6b7280] hover:underline">
            New key
          </button>
        )}
      </div>

      <SignalStats signal={input.signal} />

      {access && (
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3 flex flex-col gap-2">
          <p className="text-[13px] font-semibold">
            Copy this now: it contains the stream key, which is not shown again.
          </p>
          <Field label="SRT (better on poor links)" value={access.srt_url} />
          <Field label="RTMP" value={access.rtmp_url} />
          <p className="text-[12px] text-[#6b7280]">If your encoder asks for the RTMP server and key separately, as OBS does:</p>
          <Field label="Server" value={access.rtmp_server} />
          <Field label="Stream key" value={access.rtmp_stream_name} />
          <button onClick={onDismissAccess} className="self-start text-[12px] font-semibold text-[#6b7280] hover:underline">
            I&apos;ve saved it, hide this
          </button>
        </div>
      )}
    </div>
  );
}

const SIGNAL_LABEL: Record<Signal["state"], string> = {
  no_signal: "No signal",
  receiving: "Receiving",
  stalled: "Connected, no data",
};

function SignalLight({ state }: { state: Signal["state"] }) {
  const color = { no_signal: "bg-[#c5cad1]", receiving: "bg-green-500", stalled: "bg-amber-500" }[state];
  return (
    <span className="flex items-center gap-1.5 text-[12px] font-semibold text-[#6b7280] w-[130px]">
      <span className={`w-2.5 h-2.5 rounded-full ${color}`} />
      {SIGNAL_LABEL[state]}
    </span>
  );
}

function SignalStats({ signal }: { signal: Signal }) {
  if (signal.state === "no_signal") return null;
  const isSrt = signal.protocol === "srt";
  return (
    <dl className="grid grid-cols-2 sm:grid-cols-5 gap-3 text-[13px]">
      <Stat label="Protocol" value={signal.protocol?.toUpperCase() ?? "—"} />
      <Stat label="Bitrate" value={signal.bitrate_kbps === null ? "—" : `${(signal.bitrate_kbps / 1000).toFixed(2)} Mbps`} />
      <Stat label="Loss" value={isSrt ? `${signal.loss_percent ?? 0}%` : "n/a on RTMP"} />
      <Stat label="Lost / unrecovered" value={isSrt ? `${signal.packets_lost ?? 0} / ${signal.packets_dropped ?? 0}` : "n/a on RTMP"} />
      <Stat label="Round trip" value={isSrt ? `${signal.rtt_ms ?? 0} ms` : "n/a on RTMP"} />
    </dl>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[11px] uppercase tracking-wide text-[#9aa0a8] font-semibold">{label}</dt>
      <dd className="font-semibold tabular-nums">{value}</dd>
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="flex items-center justify-between">
        <span className="text-[12px] font-semibold text-[#6b7280]">{label}</span>
        <button
          onClick={() => navigator.clipboard?.writeText(value)}
          className="text-[12px] font-semibold text-[#2f5fe0] hover:underline"
        >
          Copy
        </button>
      </div>
      <code className="block bg-[#f7f8fa] border border-[#eaecf0] rounded-[6px] p-2 text-[12px] break-all">{value}</code>
    </div>
  );
}
