import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  // Otherwise `next dev` writes an AGENTS.md into the working tree.
  agentRules: false,
  experimental: {
    // Match recordings go through proxy.ts, whose default limit is 10 MB.
    proxyClientMaxBodySize: "20gb",
  },
};

export default nextConfig;
