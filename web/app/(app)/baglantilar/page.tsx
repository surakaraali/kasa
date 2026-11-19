"use client";

import { useSearchParams } from "next/navigation";
import { Suspense, useState, type FormEvent } from "react";
import { iban, tl, when } from "@/lib/format";
import { canEdit, useSession } from "@/lib/session";
import type { Connection } from "@/lib/types";
import { useData } from "@/lib/useData";

const statusText = { Pending: "Banka onayı bekleniyor", Active: "Bağlı", ReauthRequired: "Yeniden bağlanmalı", Rejected: "Reddedildi", Removed: "Kaldırıldı" } as const;
const returned = { baglandi: ["AcikBank bağlandı, hareketler alındı.", ""], reddedildi: ["Bankada onay verilmedi.", "warn"], hata: ["Bağlantı tamamlanamadı. Tekrar deneyin.", "bad"] } as const;

function Connections() {
  const { api, me } = useSession();
  const { data, error, reload } = useData<Connection[]>("/api/bank-connections");
  const result = useSearchParams().get("sonuc") as keyof typeof returned | null;
  const [notice, setNotice] = useState<{ text: string; kind: string } | null>(result && returned[result] ? { text: returned[result][0], kind: returned[result][1] } : null);
  const [busy, setBusy] = useState<string | null>(null);

  async function run(key: string, work: () => Promise<string | void>) {
    setBusy(key);
    try {
      const text = await work();
      if (text) setNotice({ text, kind: "" });
      await reload();
    } catch (e) {
      setNotice({ text: (e as Error).message, kind: "bad" });
    } finally {
      setBusy(null);
    }
  }

  const connectAcik = () => run("acik", async () => {
    const { authorizationUrl } = await api<{ authorizationUrl: string }>("/api/bank-connections/acikbank", { method: "POST" });
    window.location.assign(authorizationUrl);
  });

  const connectKlasik = (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault();
    const form = Object.fromEntries(new FormData(e.currentTarget));
    run("klasik", async () => {
      await api("/api/bank-connections/klasikbank", { method: "POST", json: form });
      return "KlasikBank bağlandı, hareketler alındı.";
    });
  };

  const sync = (c: Connection) => run(c.id, async () => {
    const r = await api<{ outcome: string; imported: number; error: string | null }>(`/api/bank-connections/${c.id}/sync`, { method: "POST" });
    return r.outcome === "Succeeded" ? `${r.imported} yeni hareket alındı.` : `Senkron başarısız: ${r.error}`;
  });

  const remove = (c: Connection) => {
    if (!confirm(`${c.bank} bağlantısı kaldırılsın mı? Alınmış hareketler kalır; bankaya erişim izni silinir.`)) return;
    run(c.id, async () => {
      await api(`/api/bank-connections/${c.id}`, { method: "DELETE" });
      return "Bağlantı kaldırıldı.";
    });
  };

  const has = (bank: string) => data?.some((c) => c.bank === bank && c.status === "Active");

  return (
    <div className="stack">
      <div className="head"><h1>Banka bağlantıları</h1></div>
      {notice && <div className={`notice ${notice.kind}`} role="status">{notice.text}</div>}
      {error && <p className="error">{error}</p>}

      {data?.map((c) => (
        <section key={c.id} className="card">
          <div className="head" style={{ marginBottom: 12 }}>
            <div>
              <h2 style={{ margin: 0 }}>{c.bank} <span className={`badge ${c.status === "Active" ? "good" : c.status === "Pending" ? "" : "warn"}`}>{statusText[c.status]}</span></h2>
              <div className="cell-sub">
                Son senkron: {when(c.lastSyncAt)}
                {c.lastSyncOutcome === "Failed" && <span className="out"> · {c.lastSyncError}</span>}
                {c.consentExpiresAt && <> · Rıza bitişi: {when(c.consentExpiresAt)}</>}
              </div>
            </div>
            {canEdit(me) && (
              <div className="toolbar">
                {c.status === "Active" && <button className="secondary" disabled={busy !== null} onClick={() => sync(c)}>{busy === c.id ? "Çekiliyor…" : "Şimdi çek"}</button>}
                {c.bank === "AcikBank" && c.status !== "Active" && <button disabled={busy !== null} onClick={connectAcik}>Yeniden bağlan</button>}
                {me?.role === "Admin" && <button className="danger" disabled={busy !== null} onClick={() => remove(c)}>Kaldır</button>}
              </div>
            )}
          </div>
          <table>
            <tbody>
              {c.accounts.map((a) => (
                <tr key={a.id}><td>{a.name}</td><td className="num">{iban(a.iban)}</td><td className="right num">{tl(a.balance)}</td></tr>
              ))}
            </tbody>
          </table>
        </section>
      ))}

      {canEdit(me) && (
        <div className="kpis">
          {!has("AcikBank") && !data?.some((c) => c.bank === "AcikBank") && (
            <section className="card">
              <h2>AcikBank</h2>
              <p className="muted">Açık bankacılık: bankanın sayfasında onay verirsiniz, Kasa şifrenizi hiç görmez. Rıza 90 gün geçerli.</p>
              <button disabled={busy !== null} onClick={connectAcik}>{busy === "acik" ? "Bankaya gidiliyor…" : "Bankaya bağlan"}</button>
            </section>
          )}
          {!has("KlasikBank") && (
            <section className="card">
              <h2>KlasikBank</h2>
              <p className="muted">Bu banka açık bankacılık sunmuyor; internet bankacılığı kullanıcınızla bağlanılır. Şifre şifrelenerek saklanır.</p>
              <form className="inline" onSubmit={connectKlasik}>
                <label>Kullanıcı adı<input name="username" required autoComplete="off" /></label>
                <label>Şifre<input name="password" type="password" required autoComplete="off" /></label>
                <button disabled={busy !== null}>{busy === "klasik" ? "Bağlanıyor…" : "Bağlan"}</button>
              </form>
            </section>
          )}
        </div>
      )}
    </div>
  );
}

export default function ConnectionsPage() {
  return (
    <Suspense>
      <Connections />
    </Suspense>
  );
}
