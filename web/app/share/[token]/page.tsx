import { ShareViewer } from "@/components/ShareViewer";

// Public, unauthenticated highlight viewer reached via a time-limited share link.
// The token is validated (existence + expiry) by the API; the viewer renders the
// player or an "expired" state. Dynamic params are async in this Next version.
export default async function SharePage({
  params,
}: {
  params: Promise<{ token: string }>;
}) {
  const { token } = await params;
  return <ShareViewer token={token} />;
}
