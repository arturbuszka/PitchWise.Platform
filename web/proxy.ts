import { NextRequest, NextResponse } from "next/server";

// Forwards the browser's /api/* calls to the API, adding the key on the way. This is a console for
// one tenant, so anyone who can open it acts as that tenant: put it behind a login before exposing it.
export function proxy(request: NextRequest) {
  const target = new URL(
    request.nextUrl.pathname + request.nextUrl.search,
    process.env.API_INTERNAL_URL ?? "http://localhost:8000"
  );
  const headers = new Headers(request.headers);
  headers.set("X-Api-Key", process.env.WEB_API_KEY ?? "");
  return NextResponse.rewrite(target, { request: { headers } });
}

export const config = { matcher: "/api/:path*" };
