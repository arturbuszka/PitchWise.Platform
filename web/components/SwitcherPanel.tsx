"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { Match, MatchInput, Switcher, SwitcherCut, api } from "@/lib/api";
import { ConsoleHeader } from "./ConsoleHeader";
import { WhepVideo } from "./WhepVideo";

const POLL_MS = 1000;

const STATUS: Record<Switcher["status"], { text: string; style: string }> = {
  stopped: { text: "Stopped", style: "bg-[#eef0f3] text-[#6b7280]" },
  waiting_for_cameras: { text: "Waiting for the first camera", style: "bg-[#eef0f3] text-[#6b7280]" },
  starting: { text: "Starting", style: "bg-amber-100 text-amber-800" },
  running: { text: "Running", style: "bg-green-100 text-green-800" },
  restarting: { text: "Restarting", style: "bg-amber-100 text-amber-800" },
  offline: { text: "State unknown", style: "bg-red-100 text-red-700" },
};

function clock(seconds: number) {
  const whole = Math.floor(seconds);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(Math.floor(whole / 3600))}:${pad(Math.floor((whole % 3600) / 60))}:${pad(whole % 60)}`;
}

/** Where an operator cuts: every camera and the program with little delay, buttons, and number keys. */
export function SwitcherPanel({ id }: { id: string }) {
  const [match, setMatch] = useState<Match | null>(null);
  const [cuts, setCuts] = useState<SwitcherCut[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    const load = () => {
      api.matches.get(id).then((m) => active && setMatch(m)).catch(() => {});
      api.matches.switcherCuts(id).then((c) => active && setCuts(c)).catch(() => {});
    };
    load();
    const interval = setInterval(load, POLL_MS);
    return () => {
      active = false;
      clearInterval(interval);
    };
  }, [id]);

  const previewAddress = useCallback(
    async (inputId: string) => {
      const preview = await api.matches.switcherPreview(id);
      const source = preview.sources.find((s) => s.input_id === inputId);
      if (!source) throw new Error("No preview for this input");
      return source.whep_url;
    },
    [id]
  );

  const send = useCallback(
    async (action: () => Promise<Switcher>) => {
      setError(null);
      try {
        const switcher = await action();
        setMatch((m) => m && { ...m, switcher });
      } catch {
        setError("That didn't reach the API, so nothing changed. Check that it is running, then try again.");
      }
    },
    []
  );

  const cut = useCallback((input: MatchInput) => send(() => api.matches.cut(id, input.id)), [id, send]);
  const chooseAudio = useCallback((input: MatchInput) => send(() => api.matches.chooseAudio(id, input.id)), [id, send]);

  // Number keys cut, and with Shift they choose the sound. By key position, as Shift changes the character.
  const cameras = useRef<MatchInput[]>([]);
  useEffect(() => {
    cameras.current = match?.inputs ?? [];
  }, [match]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement;
      if (e.repeat || e.ctrlKey || e.metaKey || e.altKey || target.isContentEditable) return;
      if (["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName)) return;
      const digit = /^(?:Digit|Numpad)([1-9])$/.exec(e.code);
      if (!digit) return;
      const input = cameras.current[Number(digit[1]) - 1];
      if (!input) return;
      e.preventDefault();
      if (e.shiftKey) chooseAudio(input);
      else cut(input);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [cut, chooseAudio]);

  if (!match) return <ConsoleHeader />;
  const switcher = match.switcher;
  const names = new Map(match.inputs.map((i) => [i.id, i.name]));
  const wantedName = switcher ? names.get(switcher.video_input_id) : undefined;
  const onSlate = switcher?.status === "running" && switcher.on_air_video_input_id === null;
  const silent = switcher?.status === "running" && switcher.on_air_audio_input_id === null;

  return (
    <div className="min-h-screen">
      <ConsoleHeader />
      <main className="max-w-[1400px] mx-auto p-4 flex flex-col gap-4">
        <div className="flex items-center gap-3">
          <h1 className="text-[20px] font-black min-w-0 truncate">{match.name}</h1>
          {switcher && (
            <span className={`px-2.5 py-0.5 rounded-full text-[12px] font-semibold ${STATUS[switcher.status].style}`}>
              {STATUS[switcher.status].text}
            </span>
          )}
          <span className="flex-1" />
          <Link href={`/matches/${id}`} className="text-[13px] font-semibold text-[#6b7280] hover:underline">
            Back to the match
          </Link>
        </div>

        {!switcher ? (
          <p className="text-[14px] text-[#6b7280]">
            {match.status === "ended" ? "This match has ended." : "This match doesn't use the switcher."}{" "}
            <Link href={`/matches/${id}`} className="text-[#2f5fe0] hover:underline">
              Go to the match
            </Link>
          </p>
        ) : (
          <>
            {error && <p className="text-[13px] text-red-600">{error}</p>}
            {switcher.error && <p className="text-[13px] text-red-600">{switcher.error}</p>}
            {switcher.warning && <p className="text-[13px] text-amber-700">{switcher.warning}</p>}
            {onSlate && (
              <p className="text-[13px] font-semibold text-red-600">
                Viewers see black: nothing is arriving from {wantedName ?? "the chosen camera"}. Cut to another camera.
              </p>
            )}
            {silent && !onSlate && (
              <p className="text-[13px] font-semibold text-amber-700">
                Viewers hear silence: nothing is arriving from {names.get(switcher.audio_input_id) ?? "the chosen source"}. Choose another sound.
              </p>
            )}

            <div className="grid gap-4 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
              <section className="flex flex-col gap-2">
                <h2 className="text-[13px] font-bold">Program</h2>
                <div className="rounded-lg overflow-hidden border-2 border-red-600">
                  <WhepVideo
                    label="Program"
                    live={switcher.program_signal.state !== "no_signal"}
                    address={() => previewAddress(switcher.program_input_id)}
                  />
                </div>
              </section>
              <History cuts={cuts} names={names} />
            </div>

            <section className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              {match.inputs.map((input, index) => (
                <CameraCard
                  key={input.id}
                  input={input}
                  number={index + 1}
                  switcher={switcher}
                  address={() => previewAddress(input.id)}
                  onCut={() => cut(input)}
                  onAudio={() => chooseAudio(input)}
                />
              ))}
            </section>

            <p className="text-[12px] text-[#6b7280]">
              Keys: <kbd className="font-semibold">1</kbd>–<kbd className="font-semibold">9</kbd> cut to that camera,{" "}
              <kbd className="font-semibold">Shift</kbd> + number takes the sound from it. Previews have no sound, as
              WebRTC doesn&apos;t carry AAC.
            </p>
          </>
        )}
      </main>
    </div>
  );
}

function CameraCard({
  input,
  number,
  switcher,
  address,
  onCut,
  onAudio,
}: {
  input: MatchInput;
  number: number;
  switcher: Switcher;
  address: () => Promise<string>;
  onCut: () => void;
  onAudio: () => void;
}) {
  const onAir = switcher.on_air_video_input_id === input.id;
  const wanted = switcher.video_input_id === input.id;
  const givesSound = switcher.audio_input_id === input.id;
  const hasSignal = input.signal.state === "receiving";
  const border = onAir ? "border-red-600" : wanted ? "border-amber-500" : "border-[#eaecf0]";

  return (
    <div className={`bg-white rounded-lg overflow-hidden border-2 ${border} flex flex-col`}>
      <WhepVideo label={input.name} live={input.signal.state !== "no_signal"} address={address} />
      <div className="p-2 flex flex-col gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <kbd className="px-1.5 rounded bg-[#eef0f3] text-[12px] font-bold">{number}</kbd>
          <span className="text-[13px] font-semibold truncate">{input.name}</span>
          <span className="flex-1" />
          {onAir && <span className="px-1.5 rounded bg-red-600 text-white text-[11px] font-bold uppercase">On air</span>}
          {!onAir && wanted && <span className="text-[11px] font-semibold text-amber-700">Chosen, no picture</span>}
        </div>
        <div className="flex gap-2">
          <button
            onClick={onCut}
            disabled={!hasSignal || wanted}
            className="flex-1 rounded-md bg-red-600 disabled:bg-[#eef0f3] disabled:text-[#9aa0a8] text-white text-[13px] font-bold py-1.5"
          >
            Cut
          </button>
          <button
            onClick={onAudio}
            disabled={!hasSignal || givesSound}
            className="flex-1 rounded-md border border-[#2f5fe0] text-[#2f5fe0] disabled:border-[#eaecf0] disabled:text-[#9aa0a8] text-[13px] font-semibold py-1.5"
          >
            {givesSound ? "Sound from here" : "Take sound"}
          </button>
        </div>
      </div>
    </div>
  );
}

function History({ cuts, names }: { cuts: SwitcherCut[]; names: Map<string, string> }) {
  return (
    <section className="flex flex-col gap-2 min-h-0">
      <h2 className="text-[13px] font-bold">History</h2>
      <ol className="bg-white border border-[#eaecf0] rounded-lg p-2 flex flex-col gap-1 overflow-y-auto max-h-[360px] text-[12px]">
        {cuts.length === 0 && <li className="text-[#6b7280]">Nothing yet.</li>}
        {cuts.map((c) => (
          <li key={c.id} className="flex gap-2">
            <span className="tabular-nums text-[#6b7280]">
              {c.match_seconds === null ? new Date(c.at).toLocaleTimeString() : clock(c.match_seconds)}
            </span>
            <span className="min-w-0 truncate">
              {c.kind === "audio" ? "Sound: " : ""}
              {c.input_id ? (names.get(c.input_id) ?? "Unknown input") : c.kind === "audio" ? "silence" : "black"}
            </span>
            {c.origin === "automatic" && <span className="text-amber-700">automatic</span>}
          </li>
        ))}
      </ol>
    </section>
  );
}
