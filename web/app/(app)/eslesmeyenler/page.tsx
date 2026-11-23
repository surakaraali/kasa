"use client";

import Link from "next/link";
import { useState } from "react";
import { day } from "@/lib/format";
import { ApiError, canEdit, useSession } from "@/lib/session";
import type { CurrentAccount, MatchHint, Transaction, TransactionPage } from "@/lib/types";
import { Amount, Counterparty } from "@/lib/ui";
import { useData } from "@/lib/useData";

const by = { Iban: "IBAN", TaxNumber: "vergi no", Name: "adı" } as const;

/** The rules' reason in one sentence, with the current accounts it names. */
function reason(h: MatchHint): string {
  const card = (c: MatchHint["candidates"][number]) => `${c.code} ${c.title}`;
  switch (h.kind) {
    case "NoCounterparty": return "Gönderen bilgisi yok; banka masrafı olabilir.";
    case "One": return `Öneri: ${by[h.candidates[0].by]} ${card(h.candidates[0])} kartına uyuyor.`;
    case "SameName": return `Öneri: IBAN ve vergi no hiçbir karta uymuyor, ama adı ${card(h.candidates[0])} kartıyla aynı.`;
    case "Conflict": return `${h.candidates.map((c) => `${by[c.by]} ${card(c)} kartını`).join(", ")} gösteriyor; hangisi doğru, siz seçin.`;
    case "NoAccount": return "Hiçbir cari kartına uymuyor; yeni bir müşteri olabilir.";
  }
}

function Row({ t, hint, accounts, onDone }: { t: Transaction; hint?: MatchHint; accounts: CurrentAccount[]; onDone: (message: string, bad?: boolean) => void }) {
  const { api, me } = useSession();
  // A suggestion only pre-selects; the person still presses "Eşle".
  const [choice, setChoice] = useState(t.currentAccountId ?? hint?.suggestedId ?? "");
  const [busy, setBusy] = useState(false);

  async function decide(currentAccountId: string | null) {
    setBusy(true);
    try {
      await api(`/api/transactions/${t.id}/match`, { method: "PUT", json: { currentAccountId, rowVersion: t.rowVersion } });
      onDone(currentAccountId ? "Eşlendi; fiş ERP'ye gönderiliyor." : "Bu hareket ERP'ye aktarılmayacak olarak işaretlendi.");
    } catch (e) {
      // 409: someone else changed it, or it was already exported. The list refreshes and shows the new state.
      onDone((e as Error).message, true);
      if (!(e instanceof ApiError && e.status === 409)) setBusy(false);
    }
  }

  return (
    <tr>
      <td className="num">{day(t.bookedAt)}</td>
      <td>
        <Counterparty t={t} />
        {t.exportError && <div className="cell-sub out">{t.exportError}</div>}
        {hint && <div className={`hint ${hint.suggestedId ? "suggested" : ""}`}>{reason(hint)}</div>}
      </td>
      <td className="right"><Amount value={t.amount} /></td>
      <td>
        {canEdit(me) ? (
          <div className="toolbar decision">
            <select aria-label="Cari kart" value={choice} onChange={(e) => setChoice(e.target.value)} disabled={busy}>
              <option value="">Cari seçin…</option>
              {accounts.map((a) => <option key={a.id} value={a.id}>{a.code} {a.title}</option>)}
            </select>
            <button disabled={busy || !choice} onClick={() => decide(choice)}>{t.exportStatus === "Failed" ? "Tekrar gönder" : "Eşle"}</button>
            {t.exportStatus !== "Failed" && <button className="secondary" disabled={busy} onClick={() => decide(null)}>ERP&apos;ye aktarma</button>}
          </div>
        ) : <span className="muted">Eşleme yetkiniz yok</span>}
      </td>
    </tr>
  );
}

export default function Unmatched() {
  const unmatched = useData<TransactionPage>("/api/transactions?match=Unmatched&limit=200");
  const failed = useData<TransactionPage>("/api/transactions?export=Failed&limit=200");
  const { data: accounts } = useData<CurrentAccount[]>("/api/current-accounts");
  const hints = useData<MatchHint[]>("/api/transactions/unmatched/hints");
  const hintOf = new Map(hints.data?.map((h) => [h.transactionId, h]));
  // Rows wait for the hints, so a suggestion is there when a row first renders; without them the page still works.
  const hintsSettled = hints.data !== undefined || hints.error !== null;
  const [notice, setNotice] = useState<{ text: string; bad?: boolean } | null>(null);

  const done = (text: string, bad?: boolean) => {
    setNotice({ text, bad });
    unmatched.reload();
    failed.reload();
    hints.reload();
  };

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Eşleşmeyenler</h1>
          <p>Kurallar karar veremedi. Her satırda nedeni yazıyor; bir öneri varsa o cari seçili gelir, onaylamak size kalır.</p>
        </div>
      </div>
      {notice && <div className={`notice ${notice.bad ? "bad" : ""}`} role="status">{notice.text}</div>}
      {accounts?.length === 0 && <div className="notice warn">Önce <Link href="/erp">ERP&apos;den cari kartları alın</Link>.</div>}

      {(failed.data?.items.length ?? 0) > 0 && (
        <section className="card table-wrap">
          <h2>ERP&apos;ye aktarılamayanlar</h2>
          <table className="stacked decisions"><tbody>{failed.data!.items.map((t) => <Row key={t.id} t={t} accounts={accounts ?? []} onDone={done} />)}</tbody></table>
        </section>
      )}

      <section className="card table-wrap">
        <h2>Karar bekleyen hareketler {unmatched.data && <span className="muted">({unmatched.data.items.length})</span>}</h2>
        {unmatched.error && <p className="error">{unmatched.error}</p>}
        <table className="stacked decisions">
          <thead><tr><th>Tarih</th><th>Karşı taraf / açıklama</th><th className="right">Tutar</th><th>Karar</th></tr></thead>
          <tbody>
            {hintsSettled && unmatched.data?.items.map((t) => <Row key={`${t.id}-${t.rowVersion}`} t={t} hint={hintOf.get(t.id)} accounts={accounts ?? []} onDone={done} />)}
            {unmatched.data?.items.length === 0 && <tr><td colSpan={4} className="muted">Bekleyen hareket yok.</td></tr>}
          </tbody>
        </table>
      </section>
    </div>
  );
}
