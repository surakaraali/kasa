"use client";

import { ChevronRight } from "lucide-react";
import Link from "next/link";
import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { calendarDay, day, tl, when } from "@/lib/format";
import type { Dashboard, TransactionPage } from "@/lib/types";
import { Amount } from "@/lib/ui";
import { useData } from "@/lib/useData";

const dependencyNames: Record<string, string> = { sqlserver: "SQL Server", redis: "Redis", mongodb: "MongoDB", rabbitmq: "RabbitMQ" };
const stateText: Record<string, string> = { Healthy: "çalışıyor", Degraded: "kısıtlı", Unhealthy: "kapalı" };

// Axis figures in thousands, written out: "100 bin" cannot be misread as a hundred billion the way "100 B" can.
const thousands = (v: number) => (v === 0 ? "0" : `${new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 0 }).format(Math.abs(v) / 1000)} bin`);
const GREEN = "#0f5c4c";
const OUT = "#b8b8b8";
const count = new Intl.NumberFormat("tr-TR");

/** Each dependency as a status dot. Redis, MongoDB and RabbitMQ being down slows or delays things but loses nothing. */
function SystemStatus() {
  const { data } = useData<{ status: string; checks: Record<string, { status: string; description: string | null }> }>("/api/system/health");
  if (!data) return null;
  return (
    <div className="statusline" aria-label="Sistem durumu">
      {Object.entries(data.checks).filter(([k]) => k in dependencyNames).map(([k, c]) => (
        <span key={k} className={`dot ${c.status === "Healthy" ? "ok" : c.status === "Degraded" ? "warn" : "bad"}`}
          title={c.description ?? `${dependencyNames[k]} ${stateText[c.status] ?? c.status}`}>
          {dependencyNames[k]}{c.status !== "Healthy" && <span className="muted">, {stateText[c.status] ?? c.status}</span>}
        </span>
      ))}
    </div>
  );
}

/**
 * Kasa's own picture: every movement's way from the bank to the current account to an ERP voucher, the same three
 * steps the sign-in screen shows, with what is still waiting for a person at the end.
 */
function Journey({ d }: { d: Dashboard }) {
  const total = d.matched + d.unmatched + d.ignored;
  const pct = (n: number) => (total ? `${Math.max(2, (n / total) * 100)}%` : "0%");
  const banks = new Set(d.accounts.map((a) => a.bank)).size;
  const rules = [
    d.matchedByIban && `${d.matchedByIban} IBAN`,
    d.matchedByTaxNumber && `${d.matchedByTaxNumber} vergi no`,
    d.matchedManually && `${d.matchedManually} elle`,
  ].filter(Boolean).join(", ");
  const waiting = d.unmatched + d.exportFailed;

  return (
    <section className="card journey-card" aria-label="Hareketlerin yolu">
      <div className="chart-head">
        <h2>Hareketlerin yolu</h2>
        <span className="muted" style={{ fontSize: 13 }}>Bütün hareketler, bugüne kadar</span>
      </div>
      <div className="journey-row">
        <div className="stage">
          <span className="kind">Bankadan geldi</span>
          <span className="figure">{count.format(total)}</span>
          <span className="sub">{banks} banka, {d.accounts.length} hesap</span>
          <span className="meter"><i style={{ width: total ? "100%" : "0%" }} /></span>
        </div>
        <ChevronRight className="arrow" size={18} strokeWidth={1.5} aria-hidden />
        <div className="stage">
          <span className="kind">Cariye eşleşti</span>
          <span className="figure">{count.format(d.matched)}</span>
          <span className="sub">{rules || "Henüz eşleşme yok"}</span>
          <span className="meter"><i style={{ width: pct(d.matched) }} /></span>
        </div>
        <ChevronRight className="arrow" size={18} strokeWidth={1.5} aria-hidden />
        <div className="stage">
          <span className="kind">ERP&apos;de fiş</span>
          <span className="figure">{count.format(d.exported)}</span>
          <span className="sub">{d.exportPending > 0 ? `${d.exportPending} yolda` : "Yolda fiş yok"}{d.exportFailed > 0 && `, ${d.exportFailed} aktarılamadı`}</span>
          <span className="meter"><i style={{ width: pct(d.exported) }} /></span>
        </div>
        <div className={`decide ${waiting > 0 ? "" : "done"}`}>
          {waiting > 0 ? (
            <>
              <span className="figure">{count.format(waiting)}</span>
              <span className="sub">hareket kararınızı bekliyor{d.ignored > 0 && `, ${d.ignored} ERP'ye gitmeyecek`}</span>
              <Link className="button" href="/eslesmeyenler">Karar ver</Link>
            </>
          ) : (
            <><span className="figure">0</span><span className="sub">Bekleyen hareket yok; hepsi karara bağlandı</span></>
          )}
        </div>
      </div>
    </section>
  );
}

function LatestVouchers() {
  const { data } = useData<TransactionPage>("/api/transactions?export=Exported&limit=5");
  return (
    <section className="card vouchers-card">
      <div className="chart-head">
        <h2>ERP&apos;deki son fişler</h2>
        <Link href="/hareketler" style={{ fontSize: 13 }}>Hepsi</Link>
      </div>
      {data?.items.length === 0 && <p className="muted">Henüz ERP&apos;ye aktarılmış fiş yok.</p>}
      <ul className="vouchers">
        {data?.items.map((t) => (
          <li key={t.id}>
            <div className="what">
              <b>{t.currentAccountTitle ?? t.counterpartyName}</b>
              <span>{t.amount > 0 ? "Tahsilat" : "Tediye"} fişi <span className="num">{t.erpVoucherNo}</span>, <span className="num">{day(t.bookedAt)}</span></span>
            </div>
            <Amount value={t.amount} />
          </li>
        ))}
      </ul>
    </section>
  );
}

export default function Panel() {
  const { data, error } = useData<Dashboard>("/api/dashboard?days=30");
  if (error) return <p className="error">{error}</p>;
  if (!data) return <p className="muted">Yükleniyor…</p>;

  const waiting = data.unmatched + data.exportFailed;
  const last = data.daily.length - 1;
  // Money out below the line, so the two directions read as one picture of the month.
  const chart = data.daily.map((d, i) => ({ day: calendarDay(d.date), Giren: d.in, Çıkan: -d.out, today: i === last }));
  const sumIn = data.daily.reduce((s, d) => s + d.in, 0);
  const sumOut = data.daily.reduce((s, d) => s + d.out, 0);
  const today = data.todayIn === 0 && data.todayOut === 0
    ? "Bugün hareket yok."
    : `Bugün ${data.todayIn > 0 ? `${tl(data.todayIn)} girdi` : "giren yok"}, ${data.todayOut > 0 ? `${tl(data.todayOut)} çıktı` : "çıkan yok"}.`;
  const finding = data.accounts.length === 0
    ? "Henüz banka bağlı değil; bağlayınca hareketler burada akmaya başlar."
    : waiting > 0
      ? `${waiting} hareket kararınızı bekliyor. ${today}`
      : `Bütün hareketler karara bağlandı. ${today}`;

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Panel</h1>
          <p>{finding}</p>
        </div>
        <div className="head-side">
          <SystemStatus />
          <span className="muted" style={{ fontSize: 13 }}>Son senkron <span className="num">{when(data.lastSyncAt)}</span></span>
        </div>
      </div>

      {data.accounts.length === 0 ? (
        <div className="notice warn">Henüz banka bağlı değil. <Link href="/baglantilar">Banka bağlayın</Link>; hareketler burada görünür.</div>
      ) : (
        <section className="card balances" aria-label="Bakiyeler" style={{ gridTemplateColumns: `1.4fr repeat(${data.accounts.length}, minmax(0, 1fr))` }}>
          <div className="balance lead">
            <span className="kind">Toplam bakiye</span>
            <span className="figure">{tl(data.totalBalance)}</span>
            <span className="sub">{data.accounts.length} hesap</span>
          </div>
          {data.accounts.map((a) => (
            <div key={a.id} className="balance">
              <span className="kind">{a.name}</span>
              <span className="figure">{tl(a.balance)}</span>
              <span className="sub">{a.bank}, sonu <span className="num">{a.iban.slice(-4)}</span></span>
            </div>
          ))}
        </section>
      )}

      <Journey d={data} />

      <div className="split">
        <section className="card" aria-label="Son 30 günün nakit akışı">
          <div className="chart-head">
            <h2>Nakit akışı, son 30 gün</h2>
            <div className="legend">
              <span><i style={{ background: GREEN }} />Giren <span className="num">{tl(sumIn)}</span></span>
              <span><i style={{ background: OUT }} />Çıkan <span className="num">{tl(sumOut)}</span></span>
            </div>
          </div>
          <div style={{ height: 300 }}>
            <ResponsiveContainer>
              <BarChart data={chart} stackOffset="sign" margin={{ top: 4, right: 4, bottom: 0, left: 4 }} barCategoryGap="28%">
                <CartesianGrid vertical={false} stroke="rgba(23,23,23,0.06)" />
                <XAxis dataKey="day" tickLine={false} axisLine={false} interval="preserveStartEnd" minTickGap={24}
                  tick={{ fontSize: 11, fill: "#737373", fontFamily: "var(--mono)" }} />
                <YAxis tickLine={false} axisLine={false} width={64} tickFormatter={thousands}
                  tick={{ fontSize: 11, fill: "#737373", fontFamily: "var(--mono)" }} />
                <ReferenceLine y={0} stroke="rgba(23,23,23,0.24)" />
                <Tooltip cursor={{ fill: "rgba(23,23,23,0.04)" }} formatter={(v) => tl(Math.abs(Number(v)))}
                  contentStyle={{ borderRadius: 8, border: "1px solid rgba(23,23,23,0.08)", fontSize: 13 }} />
                <Bar dataKey="Giren" stackId="flow" isAnimationActive={false} radius={[3, 3, 0, 0]}>
                  {chart.map((d) => <Cell key={d.day} fill={GREEN} fillOpacity={d.today ? 1 : 0.72} />)}
                </Bar>
                <Bar dataKey="Çıkan" stackId="flow" isAnimationActive={false} radius={[3, 3, 0, 0]}>
                  {chart.map((d) => <Cell key={d.day} fill={d.today ? "#525252" : OUT} />)}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          </div>
        </section>
        <LatestVouchers />
      </div>
    </div>
  );
}
