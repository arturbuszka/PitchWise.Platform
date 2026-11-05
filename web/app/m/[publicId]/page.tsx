import { PublicMatchPlayer } from "@/components/PublicMatchPlayer";

// Public, with no key: the address a club shares with its fans.
export default async function PublicMatchPage({ params }: { params: Promise<{ publicId: string }> }) {
  const { publicId } = await params;
  return (
    <div className="min-h-screen bg-[#0b0e13] text-white flex flex-col items-center justify-center p-4">
      <div className="w-full max-w-[1100px]">
        <PublicMatchPlayer publicId={publicId} showTitle />
        <p className="text-[12px] text-[#6b7280] mt-3 text-center">
          Pitch<span className="text-[#6f9bff]">Wise</span>
        </p>
      </div>
    </div>
  );
}
