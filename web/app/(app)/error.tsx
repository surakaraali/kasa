"use client";

import { useEffect } from "react";

// After the web app is rebuilt, an open tab still points at the previous build's code; moving to another page then
// fails to load that page's code. One reload picks up the new build. Once a minute at most, so it can never loop.
const isStaleBuild = (error: Error) => /ChunkLoadError|Loading chunk|dynamically imported module|Failed to load chunk/i.test(`${error.name} ${error.message}`);

export default function PageError({ error, reset }: { error: Error; reset: () => void }) {
  useEffect(() => {
    if (!isStaleBuild(error)) return;
    try {
      const last = Number(sessionStorage.getItem("kasa-reloaded-at") ?? 0);
      if (Date.now() - last < 60_000) return;
      sessionStorage.setItem("kasa-reloaded-at", String(Date.now()));
    } catch {
      // Storage blocked: reload anyway, the browser keeps us from looping fast.
    }
    window.location.reload();
  }, [error]);

  return (
    <div className="stack">
      <h1>Sayfa yüklenemedi</h1>
      <p className="muted">Bağlantı kesilmiş ya da uygulama az önce güncellenmiş olabilir.</p>
      <div className="toolbar">
        <button onClick={() => window.location.reload()}>Sayfayı yenile</button>
        <button className="secondary" onClick={() => reset()}>Tekrar dene</button>
      </div>
    </div>
  );
}
