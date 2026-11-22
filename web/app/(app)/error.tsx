"use client";

import { useReloadOnStaleBuild } from "@/lib/staleBuild";

// A page that failed to load, inside the app; after a rebuild it reloads itself once (lib/staleBuild).
export default function PageError({ error, reset }: { error: Error; reset: () => void }) {
  useReloadOnStaleBuild(error);

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
