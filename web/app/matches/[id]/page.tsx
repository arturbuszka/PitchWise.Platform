import { MatchPanel } from "@/components/MatchPanel";

export default async function MatchPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <MatchPanel id={id} />;
}
