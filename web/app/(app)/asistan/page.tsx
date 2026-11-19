"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useSession } from "@/lib/session";
import { useData } from "@/lib/useData";

interface Turn { role: "user" | "assistant"; text: string; steps?: string[]; error?: string }

const examples = [
  "Bankalarda toplam ne kadar param var?",
  "Bu ay ABC Tekstil'den ne kadar tahsilat geldi?",
  "Eşleşmeyen hareketler en çok kimden?",
  "Son 4 haftanın haftalık nakit akışını özetle.",
];

/** Reads a server-sent event stream from a POST response: "event: x\ndata: {...}\n\n" blocks. */
async function* events(response: Response) {
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";
  for (;;) {
    const { value, done } = await reader.read();
    if (done) return;
    buffer += value;
    let end;
    while ((end = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, end);
      buffer = buffer.slice(end + 2);
      const type = /^event: (.*)$/m.exec(block)?.[1];
      const data = /^data: (.*)$/m.exec(block)?.[1];
      if (type && data) yield { type, text: (JSON.parse(data) as { text: string | null }).text };
    }
  }
}

export default function Assistant() {
  const { authFetch } = useSession();
  const { data: status } = useData<{ enabled: boolean; model: string }>("/api/assistant/status");
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState("");
  const end = useRef<HTMLDivElement>(null);

  useEffect(() => end.current?.scrollIntoView({ block: "end" }), [turns]);

  const update = (change: (t: Turn) => Turn) => setTurns((all) => [...all.slice(0, -1), change(all[all.length - 1])]);

  async function ask(question: string) {
    if (!question.trim() || busy) return;
    // Only finished, error-free turns go back as history.
    const history = [...turns.filter((t) => !t.error && t.text), { role: "user" as const, text: question }].slice(-19);
    setTurns((all) => [...all, { role: "user", text: question }, { role: "assistant", text: "", steps: [] }]);
    setDraft("");
    setBusy(true);
    try {
      const response = await authFetch("/api/assistant/chat", { method: "POST", json: { messages: history.map(({ role, text }) => ({ role, text })) } });
      for await (const e of events(response)) {
        if (e.type === "text") update((t) => ({ ...t, text: t.text + e.text }));
        else if (e.type === "tool") update((t) => ({ ...t, steps: [...(t.steps ?? []), e.text ?? ""] }));
        else if (e.type === "error") update((t) => ({ ...t, error: e.text ?? "Bir hata oldu." }));
      }
    } catch (err) {
      update((t) => ({ ...t, error: (err as Error).message }));
    } finally {
      setBusy(false);
    }
  }

  const submit = (e: FormEvent) => {
    e.preventDefault();
    ask(draft);
  };

  return (
    <div className="stack">
      <div className="head">
        <div>
          <h1>Asistan</h1>
          <p className="muted" style={{ margin: 0 }}>Hesaplarınız, hareketleriniz ve carileriniz hakkında soru sorun. Asistan sadece okur; hiçbir şeyi değiştiremez.</p>
        </div>
      </div>

      {status && !status.enabled && (
        <div className="notice warn">Asistan bu kurulumda kapalı: sunucuda Anthropic API anahtarı tanımlı değil.</div>
      )}

      <section className="card stack" aria-live="polite">
        {turns.length === 0 && (
          <div className="toolbar">
            {examples.map((q) => <button key={q} className="secondary" disabled={busy || !status?.enabled} onClick={() => ask(q)}>{q}</button>)}
          </div>
        )}
        {turns.map((t, i) => (
          <div key={i} style={{ justifySelf: t.role === "user" ? "end" : "start", maxWidth: "80%" }}>
            {t.role === "user" ? (
              <div style={{ background: "var(--bg)", border: "1px solid var(--line)", borderRadius: 8, padding: "8px 12px" }}>{t.text}</div>
            ) : (
              <div>
                {t.steps?.map((s, j) => <div key={j} className="cell-sub">{s}…</div>)}
                {t.text && <div style={{ whiteSpace: "pre-wrap", marginTop: 4 }}>{t.text}</div>}
                {!t.text && !t.error && busy && i === turns.length - 1 && <div className="muted">Düşünüyor…</div>}
                {t.error && <div className="error">{t.error}</div>}
              </div>
            )}
          </div>
        ))}
        <div ref={end} />
        <form className="inline" onSubmit={submit}>
          <label style={{ flex: 1 }}>
            <span className="visually-hidden">Sorunuz</span>
            <input value={draft} onChange={(e) => setDraft(e.target.value)} maxLength={2000} placeholder="Örneğin: Geçen hafta en büyük ödeme neydi?" disabled={!status?.enabled} style={{ width: "100%" }} />
          </label>
          <button disabled={busy || !draft.trim() || !status?.enabled}>{busy ? "Cevaplıyor…" : "Sor"}</button>
        </form>
      </section>
    </div>
  );
}
