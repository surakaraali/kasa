"use client";

import Link from "next/link";
import { Bar, BarChart, CartesianGrid, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { calendarDay, iban, tl, when } from "@/lib/format";
import type { Dashboard } from "@/lib/types";
import { useData } from "@/lib/useData";

const compact = new Intl.NumberFormat("tr-TR", { notation: "compact", maximumFractionDigits: 1 });

const dependencyNames: Record<string, string> = { sqlserver: "SQL Server", redis: "Redis", mongodb: "MongoDB", rabbitmq: "RabbitMQ" };

/** Each dependency's state. Redis, MongoDB and RabbitMQ being down slows or delays things but loses nothing. */
function SystemStatus() {
  const { data } = useData<{ status: string; checks: Record<string, { status: string; description: string | null }> }>("/api/system/health");
  if (!data) return null;
  return (
    <div className="toolbar" aria-label="Sistem durumu">
      {Object.entries(data.checks).filter(([k]) => k in dependencyNames).map(([k, c]) => (
        <span key={k} className={`badge ${c.status === "Healthy" ? "good" : c.status === "Degraded" ? "warn" : "bad"}`} title={c.description ?? undefined}>
          {dependencyNames[k]}: {c.status === "Healthy" ? "çalışıyor" : c.status === "Degraded" ? "kısıtlı çalışıyor" : "kapalı"}
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
  const chart = data.daily.map((d) => ({ day: calendarDay(d.date), Giren: d.in, Çıkan: d.out }));

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Panel</h1>
          <p className="muted" style={{ margin: 0 }}>Son senkron: {when(data.lastSyncAt)}</p>
        </div>
        <SystemStatus />
      </div>

      {data.accounts.length === 0 && (
        <div className="notice warn">Henüz banka bağlı değil. <Link href="/baglantilar">Banka bağlayın</Link>; hareketler burada görünür.</div>
      )}

      <div className="kpis">
        <div className="card kpi"><div className="label">Toplam bakiye</div><div className="value">{tl(data.totalBalance)}</div><div className="sub">{data.accounts.length} hesap</div></div>
        <div className="card kpi"><div className="label">Bugün giren</div><div className="value in">{tl(data.todayIn)}</div></div>
        <div className="card kpi"><div className="label">Bugün çıkan</div><div className="value out">{tl(data.todayOut)}</div></div>
        <div className="card kpi">
          <div className="label">Eşleşen hareket</div>
          <div className="value">{total ? `%${Math.round((decided / total) * 100)}` : "—"}</div>
          <div className="sub">{data.unmatched > 0 ? <Link href="/eslesmeyenler">{data.unmatched} hareket bekliyor</Link> : "Bekleyen yok"}</div>
        </div>
        <div className="card kpi">
          <div className="label">ERP aktarımı</div>
          <div className="value">{data.exported} fiş</div>
          <div className="sub">
            {data.exportPending > 0 && <>{data.exportPending} yolda · </>}
            {data.exportFailed > 0 ? <span className="out">{data.exportFailed} hatalı</span> : "hata yok"}
          </div>
        </div>
      </div>

      <section className="card" aria-label="Son 30 günün nakit akışı">
        <h2>Nakit akışı, son 30 gün</h2>
        <div style={{ height: 260 }}>
          <ResponsiveContainer>
            <BarChart data={chart} margin={{ top: 4, right: 8, bottom: 0, left: 8 }}>
              <CartesianGrid vertical={false} stroke="#e2e0da" />
              <XAxis dataKey="day" tickLine={false} axisLine={false} fontSize={12} interval="preserveStartEnd" minTickGap={16} />
              <YAxis tickLine={false} axisLine={false} fontSize={12} width={56} tickFormatter={(v: number) => compact.format(v)} />
              <Tooltip formatter={(v) => tl(Number(v))} cursor={{ fill: "#f6f5f1" }} />
              <Bar dataKey="Giren" fill="#17663a" radius={[3, 3, 0, 0]} isAnimationActive={false} />
              <Bar dataKey="Çıkan" fill="#a3361f" radius={[3, 3, 0, 0]} isAnimationActive={false} />
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
                  <td>{a.bank}</td>
                  <td className="num">{iban(a.iban)}</td>
                  <td className="right num">{tl(a.balance)}<div className="cell-sub">{when(a.balanceAt)}</div></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}
