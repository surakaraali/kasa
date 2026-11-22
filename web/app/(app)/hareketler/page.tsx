"use client";

import { Download } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { day } from "@/lib/format";
import { useLive } from "@/lib/live";
import { useSession } from "@/lib/session";
import type { Account, Transaction, TransactionPage } from "@/lib/types";
import { Amount, Counterparty, ExportBadge, MatchBadge } from "@/lib/ui";
import { useData } from "@/lib/useData";

export default function Transactions() {
  const { api, authFetch } = useSession();
  const { version } = useLive();
  const { data: accounts } = useData<Account[]>("/api/accounts");
  const [filters, setFilters] = useState({ search: "", direction: "", match: "", accountId: "", from: "", to: "" });
  const [downloading, setDownloading] = useState(false);
  const [rows, setRows] = useState<Transaction[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const query = useCallback((after?: string | null) => {
    const q = new URLSearchParams({ limit: "50" });
    Object.entries(filters).forEach(([k, v]) => v && q.set(k, v));
    if (after) q.set("cursor", after);
    return `/api/transactions?${q}`;
  }, [filters]);

  /** The same filters as the list, as a file Excel opens. */
  async function download() {
    setDownloading(true);
    try {
      const q = new URLSearchParams(Object.entries(filters).filter(([, v]) => v));
      // shortcut: the file passes through the browser's memory (fine to ~100 thousand rows); past that, a one-time link that streams to disk.
      const response = await authFetch(`/api/transactions/export?${q}`);
      const name = /filename="([^"]+)"/.exec(response.headers.get("content-disposition") ?? "")?.[1] ?? "kasa-hesap-ozeti.csv";
      const url = URL.createObjectURL(await response.blob());
      const link = Object.assign(document.createElement("a"), { href: url, download: name });
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setDownloading(false);
    }
  }

  useEffect(() => {
    let current = true;
    api<TransactionPage>(query())
      .then((page) => {
        if (!current) return;
        setRows(page.items);
        setCursor(page.nextCursor);
        setError(null);
      })
      .catch((e: Error) => current && setError(e.message));
    return () => {
      current = false;
    };
  }, [api, query, version]);

  const [loadingMore, setLoadingMore] = useState(false);
  async function more() {
    // Disabled while loading, so a double click does not add the same page twice.
    setLoadingMore(true);
    try {
      const page = await api<TransactionPage>(query(cursor));
      setRows((r) => [...r, ...page.items]);
      setCursor(page.nextCursor);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoadingMore(false);
    }
  }

  const set = (key: keyof typeof filters) => (e: { target: { value: string } }) => setFilters((f) => ({ ...f, [key]: e.target.value }));

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Hareketler</h1>
          <p>Filtreleri seçip hesap özetini indirebilirsiniz; dosya Excel&apos;de açılır.</p>
        </div>
        <button className="icon-button" onClick={download} disabled={downloading}>
          <Download size={16} strokeWidth={1.5} aria-hidden />{downloading ? "Hazırlanıyor…" : "Hesap özetini indir"}
        </button>
      </div>
      <div className="toolbar">
        <label>Ara<input type="search" placeholder="Açıklama ya da karşı taraf" value={filters.search} onChange={set("search")} /></label>
        <label>Yön
          <select value={filters.direction} onChange={set("direction")}>
            <option value="">Hepsi</option><option value="In">Giren</option><option value="Out">Çıkan</option>
          </select>
        </label>
        <label>Eşleşme
          <select value={filters.match} onChange={set("match")}>
            <option value="">Hepsi</option><option value="Matched">Eşleşti</option><option value="Unmatched">Eşleşmedi</option><option value="Ignored">ERP&apos;ye gitmeyecek</option>
          </select>
        </label>
        <label>Hesap
          <select value={filters.accountId} onChange={set("accountId")}>
            <option value="">Hepsi</option>
            {accounts?.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.bank})</option>)}
          </select>
        </label>
        <label>Başlangıç<input type="date" value={filters.from} max={filters.to || undefined} onChange={set("from")} /></label>
        <label>Bitiş<input type="date" value={filters.to} min={filters.from || undefined} onChange={set("to")} /></label>
      </div>
      {error && <p className="error">{error}</p>}
      <div className="card table-wrap">
        <table>
          <thead><tr><th>Tarih</th><th>Karşı taraf / açıklama</th><th>Hesap</th><th>Cari</th><th className="right">Tutar</th><th>Durum</th></tr></thead>
          <tbody>
            {rows.map((t) => (
              <tr key={t.id}>
                <td className="num">{day(t.bookedAt)}</td>
                <td><Counterparty t={t} /></td>
                <td>{t.accountName}<div className="cell-sub">{t.bank}</div></td>
                <td>{t.currentAccountTitle ?? "—"}{t.currentAccountCode && <div className="cell-sub">{t.currentAccountCode}</div>}</td>
                <td className="right"><Amount value={t.amount} /></td>
                <td style={{ display: "grid", gap: 4, justifyItems: "start" }}><MatchBadge t={t} /><ExportBadge t={t} /></td>
              </tr>
            ))}
            {rows.length === 0 && <tr><td colSpan={6} className="muted">Bu filtreye uyan hareket yok.</td></tr>}
          </tbody>
        </table>
      </div>
      {cursor && <div><button className="secondary" onClick={more} disabled={loadingMore}>{loadingMore ? "Yükleniyor…" : "Daha eski hareketler"}</button></div>}
    </div>
  );
}
