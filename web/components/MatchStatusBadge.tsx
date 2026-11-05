import { MatchStatus } from "@/lib/api";

const STYLE: Record<MatchStatus, string> = {
  live: "bg-red-100 text-red-700",
  waiting: "bg-[#eef0f3] text-[#6b7280]",
  ended: "bg-[#eef0f3] text-[#9aa0a8]",
};

export function MatchStatusBadge({ status }: { status: MatchStatus }) {
  return <span className={`px-2.5 py-0.5 rounded-full text-[12px] font-semibold ${STYLE[status]}`}>{status}</span>;
}
