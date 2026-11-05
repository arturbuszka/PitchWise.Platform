"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Match, api } from "@/lib/api";
import { ConsoleHeader } from "./ConsoleHeader";
import { MatchStatusBadge } from "./MatchStatusBadge";
import { rememberIssuedAccess } from "./issuedAccess";

export function MatchesClient() {
  const router = useRouter();
  const [matches, setMatches] = useState<Match[] | null>(null);
  const [name, setName] = useState("");
  const [error, setError] = useState<string | null>(null);

  // Status comes from the encoders, so it can change at any moment.
  useEffect(() => {
    const load = () => api.matches.list().then(setMatches).catch(() => {});
    load();
    const interval = setInterval(load, 3000);
    return () => clearInterval(interval);
  }, []);

  async function handleCreate(e: React.SyntheticEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!name.trim()) return;
    setError(null);
    try {
      const created = await api.matches.create(name.trim());
      rememberIssuedAccess(created.access);
      router.push(`/matches/${created.match.id}`);
    } catch {
      setError("Could not create the match. Check that the API is running.");
    }
  }

  return (
    <div className="min-h-screen">
      <ConsoleHeader />
      <main className="max-w-[900px] mx-auto p-6 flex flex-col gap-5">
        <h1 className="text-[22px] font-black">Matches</h1>

        <form onSubmit={handleCreate} className="flex gap-2">
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Match name, e.g. Saturday final"
            className="flex-1 min-w-0 bg-white border border-[#eaecf0] rounded-[8px] px-3 py-2 text-[14px]"
          />
          <button className="bg-[#2f5fe0] hover:bg-[#2451c7] text-white rounded-[8px] px-4 py-2 text-[14px] font-semibold">
            Create match
          </button>
        </form>
        {error && <p className="text-[13px] text-red-600">{error}</p>}

        <div className="flex flex-col gap-2">
          {matches?.length === 0 && (
            <p className="text-[14px] text-[#6b7280]">No matches yet. Create one to get an address for your encoder.</p>
          )}
          {matches?.map((m) => (
            <Link
              key={m.id}
              href={`/matches/${m.id}`}
              className="bg-white border border-[#eaecf0] rounded-xl px-4 py-3 flex items-center gap-3 hover:bg-[#f7f8fa]"
            >
              <MatchStatusBadge status={m.status} />
              <span className="flex-1 min-w-0 truncate text-[14px] font-semibold">{m.name}</span>
              <span className="text-[13px] text-[#6b7280]">
                {m.inputs.length} {m.inputs.length === 1 ? "input" : "inputs"}
              </span>
            </Link>
          ))}
        </div>
      </main>
    </div>
  );
}
