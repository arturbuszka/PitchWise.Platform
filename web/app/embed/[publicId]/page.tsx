import { PublicMatchPlayer } from "@/components/PublicMatchPlayer";

// Just the player, for a club to put on its own site in an iframe.
export default async function EmbedPage({ params }: { params: Promise<{ publicId: string }> }) {
  const { publicId } = await params;
  return (
    <div className="h-screen bg-black text-white flex items-center justify-center">
      <PublicMatchPlayer publicId={publicId} fill />
    </div>
  );
}
