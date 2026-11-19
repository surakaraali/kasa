import type { NextConfig } from "next";

// The browser only ever talks to this server. /api and /hubs are passed on to Kasa.Api, so the refresh cookie
// (SameSite=Strict, Path=/api/auth) is a same-site cookie and the API needs no CORS.
const api = process.env.KASA_API_URL ?? "http://localhost:5100";
// Two pages of the test services are also served from here, so a demo behind one public address works:
// the test bank's approval page and the test ERP's voucher page. Their /mock and API endpoints are not.
const banks = process.env.KASA_BANKS_URL ?? "http://localhost:5102";
const erp = process.env.KASA_ERP_URL ?? "http://localhost:5103";

// Production only: the dev server needs eval for hot reload. Next's own inline bootstrap scripts need 'unsafe-inline'
// without nonces. shortcut: nonce-based CSP (middleware) would drop 'unsafe-inline' for scripts.
const csp = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "connect-src 'self'",
  "frame-ancestors 'none'",
  "form-action 'self'",
  "base-uri 'self'",
].join("; ");

const config: NextConfig = {
  // The assistant moved from its own page into a panel on every screen; old links land on the dashboard.
  async redirects() {
    return [{ source: "/asistan", destination: "/panel", permanent: false }];
  },
  async rewrites() {
    return [
      // The API's readiness report (anonymous, states only), for the dashboard's system card.
      { source: "/api/system/health", destination: `${api}/health` },
      { source: "/api/:path*", destination: `${api}/api/:path*` },
      { source: "/hubs/:path*", destination: `${api}/hubs/:path*` },
      { source: "/acikbank/authorize", destination: `${banks}/acikbank/authorize` },
      { source: "/test-erp", destination: `${erp}/` },
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
          ...(process.env.NODE_ENV === "production" ? [{ key: "Content-Security-Policy", value: csp }] : []),
        ],
      },
    ];
  },
};

export default config;
