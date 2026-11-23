"use client";

import { useState, type FormEvent } from "react";
import { iban } from "@/lib/format";
import { canEdit, useSession } from "@/lib/session";
import type { CurrentAccount } from "@/lib/types";
import { useData } from "@/lib/useData";

// The test ERP's voucher page, served through this app (next.config.ts).
const erpPage = "/test-erp";

export default function Erp() {
  const { api, me } = useSession();
  const { data, reload } = useData<CurrentAccount[]>("/api/current-accounts");
  const [notice, setNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [busy, setBusy] = useState(false);

  async function work(task: () => Promise<string>) {
    setBusy(true);
    try {
      setNotice({ text: await task() });
      await reload();
    } catch (e) {
      setNotice({ text: (e as Error).message, bad: true });
    } finally {
      setBusy(false);
    }
  }

  const saveFirm = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const firmCode = String(new FormData(e.currentTarget).get("firmCode"));
    work(async () => {
      await api("/api/company/erp", { method: "PUT", json: { firmCode } });
      return `ERP firma kodu ${firmCode} olarak kaydedildi.`;
    });
  };

  const importAccounts = () => work(async () => {
    const r = await api<{ added: number; updated: number; matched: number }>("/api/current-accounts/import", { method: "POST" });
    return `${r.added} yeni, ${r.updated} güncellenen cari kart. ${r.matched} bekleyen hareket eşleşti ve ERP'ye gönderiliyor.`;
  });

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>ERP ve cariler</h1>
          <p>Eşleşen her hareket ERP&apos;de bir fiş olur: giren para tahsilat, çıkan para tediye fişi. <a href={erpPage} target="_blank" rel="noreferrer">Test ERP&apos;sindeki fişler</a></p>
        </div>
      </div>
      {notice && <div className={`notice ${notice.bad ? "bad" : ""}`} role="status">{notice.text}</div>}
      <div className="kpis">
        {me?.role === "Admin" && (
          <section className="card">
            <h2>ERP firması</h2>
            <form className="inline" onSubmit={saveFirm}>
              <label>Firma kodu<input name="firmCode" defaultValue="001" required pattern="[0-9A-Za-z]{1,10}" /></label>
              <button disabled={busy}>Kaydet</button>
            </form>
          </section>
        )}
        {canEdit(me) && (
          <section className="card">
            <h2>Cari kartlar</h2>
            <p className="muted">ERP&apos;deki cari kartları alır, sonra eşleşmemiş hareketlere kuralları yeniden uygular.</p>
            <button disabled={busy} onClick={importAccounts}>{busy ? "Alınıyor…" : "ERP'den al"}</button>
          </section>
        )}
      </div>
      <section className="card table-wrap">
        <h2>Cari kartlar {data && <span className="muted">({data.length})</span>}</h2>
        <table className="stacked">
          <thead><tr><th>Kod</th><th>Unvan</th><th>Vergi no</th><th>IBAN</th></tr></thead>
          <tbody>
            {data?.map((c) => <tr key={c.id}><td className="num">{c.code}</td><td>{c.title}</td><td className="num pin">{c.taxNumber ?? "—"}</td><td className="num">{iban(c.iban)}</td></tr>)}
            {data?.length === 0 && <tr><td colSpan={4} className="muted">Henüz cari kart yok.</td></tr>}
          </tbody>
        </table>
      </section>
    </div>
  );
}
