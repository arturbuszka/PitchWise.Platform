import { api } from "@/lib/api";
import { notFound } from "next/navigation";
import { AnalysisDetailClient } from "@/components/AnalysisDetailClient";

export default async function AnalysisPage({
  params,
}: {
  params: Promise<{ id: string }>;
}) {
  const { id } = await params;
  const analysisId = Number(id);

  const analysis = await api.analyses.get(analysisId).catch(() => null);
  if (!analysis || "detail" in (analysis as object)) notFound();

  const [events, eventTypes] = await Promise.all([
    api.analyses.events.list(analysisId).catch(() => []),
    api.eventTypes.list().catch(() => []),
  ]);

  return (
    <AnalysisDetailClient
      analysis={analysis}
      events={events}
      eventTypes={eventTypes}
    />
  );
}
