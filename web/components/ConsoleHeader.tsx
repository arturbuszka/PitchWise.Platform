import Link from "next/link";

export function ConsoleHeader() {
  return (
    <header className="bg-[#14181f] px-6 py-4 flex items-center justify-between">
      <Link href="/" className="text-[19px] font-black text-white tracking-tight">
        Pitch<span className="text-[#6f9bff]">Wise</span>
      </Link>
      <nav className="flex gap-5 text-[13px] font-medium text-[#8b919b]">
        <Link href="/" className="hover:text-white">Matches</Link>
        <Link href="/analyses" className="hover:text-white">Analyses</Link>
      </nav>
    </header>
  );
}
