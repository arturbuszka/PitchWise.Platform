"use client";

import { useEffect, useRef, useState } from "react";
import { api } from "@/lib/api";

interface Message {
  role: "user" | "assistant";
  content: string;
}

// Renders the assistant content, turning MM:SS markers into clickable "seek in video" links.
function renderContent(
  content: string,
  onSeek?: (seconds: number) => void
): React.ReactNode {
  if (!onSeek) return content;
  const parts = content.split(/(\d{1,2}:\d{2})/g);
  return parts.map((part, i) => {
    const m = part.match(/^(\d{1,2}):(\d{2})$/);
    if (m) {
      const seconds = parseInt(m[1], 10) * 60 + parseInt(m[2], 10);
      return (
        <button
          key={i}
          onClick={() => onSeek(seconds)}
          className="text-[#2f5fe0] underline font-semibold hover:text-[#2451c7]"
        >
          {part}
        </button>
      );
    }
    return <span key={i}>{part}</span>;
  });
}

export function Chat({
  analysisId,
  onSeek,
}: {
  analysisId: number;
  onSeek?: (seconds: number) => void;
}) {
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [running, setRunning] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  async function sendText(text: string) {
    const next: Message[] = [...messages, { role: "user", content: text }];
    setMessages(next);
    setRunning(true);

    const ctrl = new AbortController();
    abortRef.current = ctrl;

    setMessages((m) => [...m, { role: "assistant", content: "" }]);

    try {
      const res = await fetch(api.analyses.chatUrl(analysisId), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          messages: next.map((m) => ({ role: m.role, content: m.content })),
        }),
        signal: ctrl.signal,
      });

      const reader = res.body!.getReader();
      const dec = new TextDecoder();
      let buf = "";

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        const lines = buf.split("\n");
        buf = lines.pop()!;
        for (const line of lines) {
          if (!line.startsWith("data:")) continue;
          const token = line.slice(5).trim();
          if (token === "[DONE]") break;
          setMessages((m) => {
            const copy = [...m];
            copy[copy.length - 1] = {
              role: "assistant",
              content: copy[copy.length - 1].content + token,
            };
            return copy;
          });
        }
      }
    } catch (e) {
      if ((e as Error).name !== "AbortError") {
        setMessages((m) => {
          const copy = [...m];
          copy[copy.length - 1] = {
            role: "assistant",
            content: "LLM connection error.",
          };
          return copy;
        });
      }
    } finally {
      setRunning(false);
      abortRef.current = null;
    }
  }

  async function send() {
    const text = input.trim();
    if (!text || running) return;
    setInput("");
    await sendText(text);
  }

  return (
    <div className="flex flex-col h-full bg-[#fafbfc]">
      {/* Header */}
      <div className="px-4 py-3 border-b border-[#eceef1] flex items-center gap-2">
        <span className="w-6 h-6 rounded-[6px] bg-[#2f5fe0] text-white flex items-center justify-center text-xs">
          ✦
        </span>
        <span className="text-sm font-bold text-[#14181f]">Assistant</span>
        <span className="ml-auto flex items-center gap-1.5 text-[11px] text-green-600 font-semibold">
          <span className="w-1.5 h-1.5 rounded-full bg-green-500 inline-block" />
          online
        </span>
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto p-4 flex flex-col gap-3">
        {messages.length === 0 && (
          <p className="text-[#9aa0a8] text-sm text-center mt-8">
            Ask about the match — events, stats, key moments…
          </p>
        )}
        {messages.map((m, i) => (
          <div
            key={i}
            className={`flex ${m.role === "user" ? "justify-end" : "justify-start"}`}
          >
            <div
              className={`max-w-[88%] rounded-[13px] px-3 py-2.5 text-[13px] leading-[1.45] whitespace-pre-wrap ${
                m.role === "user"
                  ? "bg-[#2f5fe0] text-white rounded-br-[4px]"
                  : "bg-white text-[#14181f] border border-[#eceef1] rounded-bl-[4px]"
              }`}
            >
              {m.content ? (
                m.role === "assistant" ? renderContent(m.content, onSeek) : m.content
              ) : (
                <span className="text-[#9ca3af] animate-pulse">…</span>
              )}
            </div>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>

      {/* Input */}
      <div className="border-t border-[#eceef1] p-3 flex gap-2 items-center bg-white">
        <input
          type="text"
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && !e.shiftKey && send()}
          placeholder="Ask about a player, a play…"
          disabled={running}
          className="flex-1 bg-white border border-[#e4e7ec] rounded-xl px-3 py-2 text-[13px] text-[#14181f] placeholder-[#9aa0a8] focus:outline-none focus:border-[#2f5fe0] focus:ring-1 focus:ring-[#2f5fe0]/30 disabled:opacity-50"
        />
        <button
          onClick={running ? () => abortRef.current?.abort() : send}
          className={`w-9 h-9 rounded-[9px] text-[15px] font-semibold flex items-center justify-center transition-colors ${
            running
              ? "bg-red-500 hover:bg-red-600 text-white"
              : "bg-[#2f5fe0] hover:bg-[#2451c7] text-white disabled:opacity-40"
          }`}
          disabled={!running && !input.trim()}
        >
          {running ? "■" : "↑"}
        </button>
      </div>
    </div>
  );
}
