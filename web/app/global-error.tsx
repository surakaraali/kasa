"use client";

import { useEffect } from "react";

// The last safety net, above every layout: instead of the framework's bare English page, Kasa's own message, and
// after a rebuild (an open tab asking for code that no longer exists) one automatic reload, at most once a minute.
const isStaleBuild = (error: Error) => /ChunkLoadError|Loading chunk|dynamically imported module|Failed to load chunk|Failed to fetch/i.test(`${error.name} ${error.message}`);

export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  useEffect(() => {
    if (!isStaleBuild(error)) return;
    try {
      const last = Number(sessionStorage.getItem("kasa-reloaded-at") ?? 0);
      if (Date.now() - last < 60_000) return;
      sessionStorage.setItem("kasa-reloaded-at", String(Date.now()));
    } catch {
      // Storage blocked: reload anyway.
    }
    window.location.reload();
  }, [error]);

  return (
    <html lang="tr">
      <body style={{ margin: 0, minHeight: "100vh", display: "grid", placeItems: "center", background: "#fafafa", color: "#171717", font: "400 14px/20px -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif" }}>
        <div style={{ maxWidth: 360, padding: 24 }}>
          <div style={{ fontSize: 17, fontWeight: 600, marginBottom: 16 }}>Kasa</div>
          <h1 style={{ fontSize: 22, lineHeight: "28px", fontWeight: 500, margin: "0 0 8px" }}>Sayfa yükleniyor</h1>
          <p style={{ color: "#737373", margin: "0 0 20px" }}>Uygulama az önce güncellendi ya da bağlantı kısa bir süre kesildi.</p>
          <div style={{ display: "flex", gap: 8 }}>
            <button onClick={() => window.location.reload()} style={{ height: 36, padding: "0 16px", borderRadius: 8, border: "1px solid #0f5c4c", background: "#0f5c4c", color: "#fff", font: "inherit", fontWeight: 500, cursor: "pointer" }}>Sayfayı yenile</button>
            <button onClick={() => reset()} style={{ height: 36, padding: "0 16px", borderRadius: 8, border: "1px solid rgba(23,23,23,0.14)", background: "#fff", color: "#171717", font: "inherit", cursor: "pointer" }}>Tekrar dene</button>
          </div>
        </div>
      </body>
    </html>
  );
}
