import type { NextConfig } from "next";

// The browser only ever talks to this server. /api and /hubs are passed on to Kasa.Api, so the refresh cookie
// (SameSite=Strict, Path=/api/auth) is a same-site cookie and the API needs no CORS.
const api = process.env.KASA_API_URL ?? "http://localhost:5100";

const config: NextConfig = {
  async rewrites() {
    return [
      { source: "/api/:path*", destination: `${api}/api/:path*` },
      { source: "/hubs/:path*", destination: `${api}/hubs/:path*` },
    ];
  },
  async headers() {
    return [
      {
        source: "/:path*",
        headers: [
          { key: "X-Frame-Options", value: "DENY" },
          { key: "X-Content-Type-Options", value: "nosniff" },
          { key: "Referrer-Policy", value: "same-origin" },
        ],
      },
    ];
  },
};

export default config;
