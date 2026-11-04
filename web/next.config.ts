import type { NextConfig } from "next";

// Where the Next server proxies /api/* to. This runs server-side (in the container
// under compose, on the host under dev.ps1), so it must be routable from there:
//   - compose:  http://api:8000      (docker service name)
//   - dev host: http://localhost:8000 (default below)
// Set API_INTERNAL_URL in the environment to override (compose does this).
const API_INTERNAL_URL = process.env.API_INTERNAL_URL || "http://localhost:8000";

const nextConfig: NextConfig = {
  output: "standalone",
  // Proxy: the browser uses relative /api/* paths (same-origin, no CORS) which Next
  // rewrites to the API. NOTE: the proxy buffers the body, so very large uploads are
  // better off going direct — fine for dev/demo.
  async rewrites() {
    return [
      {
        source: "/api/:path*",
        destination: `${API_INTERNAL_URL}/api/:path*`,
      },
    ];
  },
};

export default nextConfig;
