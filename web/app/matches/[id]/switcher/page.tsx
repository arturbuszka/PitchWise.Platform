import { SwitcherPanel } from "@/components/SwitcherPanel";

export default async function SwitcherPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  return <SwitcherPanel id={id} />;
}
