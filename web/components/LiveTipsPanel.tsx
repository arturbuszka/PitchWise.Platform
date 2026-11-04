"use client";

interface Tip {
  text: string;
  timestamp: number;
}

export function LiveTipsPanel({ tips }: { tips: Tip[] }) {
  return (
    <div className="flex flex-col h-full bg-white rounded-xl border border-[#eaecf0] shadow-sm overflow-hidden">
      <div className="px-4 py-3 border-b border-[#eaecf0] flex items-center justify-between">
        <div>
          <h3 className="text-[14px] font-bold text-[#14181f]">Live Tactical Tips</h3>
          <p className="text-[11px] text-[#9aa0a8] mt-0.5">AI analysis updated every ~30s</p>
        </div>
        <span className="w-2 h-2 rounded-full bg-green-400 animate-pulse" />
      </div>

      <div className="flex-1 overflow-y-auto p-4 space-y-3">
        {tips.length === 0 ? (
          <div className="flex flex-col items-center justify-center h-full text-center py-8">
            <span className="text-3xl mb-3">📡</span>
            <p className="text-[13px] text-[#9aa0a8] font-medium">
              Tactical tips will appear here once the stream starts
            </p>
          </div>
        ) : (
          tips.map((tip, i) => (
            <div
              key={i}
              className="bg-[#f8f9fb] rounded-lg p-3 border border-[#eaecf0]"
            >
              <p className="text-[13px] text-[#14181f] leading-relaxed">{tip.text}</p>
              <p className="text-[11px] text-[#9aa0a8] mt-1.5 font-medium">
                t={Math.floor(tip.timestamp / 60)}:{String(Math.floor(tip.timestamp % 60)).padStart(2, "0")}
              </p>
            </div>
          ))
        )}
      </div>
    </div>
  );
}
