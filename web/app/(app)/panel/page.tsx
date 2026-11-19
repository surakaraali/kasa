"use client";

import Link from "next/link";
import { Bar, BarChart, CartesianGrid, Cell, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { calendarDay, iban, tl, when } from "@/lib/format";
import type { Dashboard } from "@/lib/types";
import { useData } from "@/lib/useData";

const dependencyNames: Record<string, string> = { sqlserver: "SQL Server", redis: "Redis", mongodb: "MongoDB", rabbitmq: "RabbitMQ" };
const stateText: Record<string, string> = { Healthy: "çalışıyor", Degraded: "kısıtlı", Unhealthy: "kapalı" };

// Axis figures in thousands, written out: "100 bin" cannot be misread as a hundred billion the way "100 B" can.
const thousands = (v: number) => (v === 0 ? "0" : `${new Intl.NumberFormat("tr-TR", { maximumFractionDigits: 0 }).format(Math.abs(v) / 1000)} bin`);
const ACCENT = "#0f5c4c";
const OUT = "#a3a3a3";

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

export default function Panel() {
  const { data, error } = useData<Dashboard>("/api/dashboard?days=30");
  if (error) return <p className="error">{error}</p>;
  if (!data) return <p className="muted">Yükleniyor…</p>;

  const decided = data.matched + data.ignored;
  const total = decided + data.unmatched;
  const last = data.daily.length - 1;
  // Money out below the line, so the two directions read as one picture of the month.
  const chart = data.daily.map((d, i) => ({ day: calendarDay(d.date), Giren: d.in, Çıkan: -d.out, today: i === last }));
  const sumIn = data.daily.reduce((s, d) => s + d.in, 0);
  const sumOut = data.daily.reduce((s, d) => s + d.out, 0);

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Panel</h1>
          <p>Son senkron <span className="num">{when(data.lastSyncAt)}</span></p>
        </div>
        <SystemStatus />
      </div>

      {data.accounts.length === 0 && (
        <div className="notice warn">Henüz banka bağlı değil. <Link href="/baglantilar">Banka bağlayın</Link>; hareketler burada görünür.</div>
      )}

      <div className="kpis">
        <div className="card kpi lead"><div className="label">Toplam bakiye</div><div className="value">{tl(data.totalBalance)}</div><div className="sub">{data.accounts.length} hesap</div></div>
        <div className="card kpi"><div className="label">Bugün giren</div><div className="value in">{tl(data.todayIn)}</div></div>
        <div className="card kpi"><div className="label">Bugün çıkan</div><div className="value out">{tl(data.todayOut)}</div></div>
        <div className="card kpi">
          <div className="label">Eşleşen hareket</div>
          <div className="value">{total ? `%${Math.round((decided / total) * 100)}` : "—"}</div>
          <div className="sub">{data.unmatched > 0 ? <Link href="/eslesmeyenler">{data.unmatched} hareket bekliyor</Link> : "Bekleyen yok"}</div>
        </div>
        <div className="card kpi">
          <div className="label">ERP&apos;ye aktarılan</div>
          <div className="value">{data.exported} fiş</div>
          <div className="sub">
            {data.exportFailed > 0 ? <Link href="/eslesmeyenler" className="out">{data.exportFailed} aktarılamadı</Link> : data.exportPending > 0 ? `${data.exportPending} yolda` : "Hata yok"}
          </div>
        </div>
      </div>

      <section className="card" aria-label="Son 30 günün nakit akışı">
        <div className="chart-head">
          <h2>Nakit akışı, son 30 gün</h2>
          <div className="legend">
            <span><i style={{ background: ACCENT }} />Giren <span className="num">{tl(sumIn)}</span></span>
            <span><i style={{ background: OUT }} />Çıkan <span className="num">{tl(sumOut)}</span></span>
          </div>
        </div>
        <div style={{ height: 320 }}>
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
                {chart.map((d) => <Cell key={d.day} fill={ACCENT} fillOpacity={d.today ? 1 : 0.78} />)}
              </Bar>
              <Bar dataKey="Çıkan" stackId="flow" isAnimationActive={false} radius={[3, 3, 0, 0]}>
                {chart.map((d) => <Cell key={d.day} fill={d.today ? "#525252" : OUT} />)}
              </Bar>
            </BarChart>
          </ResponsiveContainer>
        </div>
      </section>

      <section className="card">
        <h2>Hesaplar</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th>Hesap</th><th>Banka</th><th>IBAN</th><th className="right">Bakiye</th></tr></thead>
            <tbody>
              {data.accounts.map((a) => (
                <tr key={a.id}>
                  <td>{a.name}</td>
                  <td className="muted">{a.bank}</td>
                  <td className="num muted">{iban(a.iban)}</td>
                  <td className="right"><span className="num" style={{ fontSize: 14 }}>{tl(a.balance)}</span><div className="cell-sub num">{when(a.balanceAt)}</div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
