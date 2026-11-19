"use client";

import { useEffect, useRef, useState, type FormEvent } from "react";
import { useSession } from "./session";
import { useData } from "./useData";

interface Turn { role: "user" | "assistant"; text: string; steps?: string[]; error?: string }

const examples = [
  "Bankalarda toplam ne kadar param var?",
  "Bu ay ABC Tekstil'den ne kadar tahsilat geldi?",
  "Eşleşmeyen hareketler en çok kimden?",
  "Son 4 haftanın nakit akışını haftalık özetle",
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

/**
 * The assistant on every screen: a button at the bottom right, a panel from the right. It lives in the app layout,
 * so the conversation stays while the person moves between pages.
 */
export function AssistantDock() {
  const { authFetch } = useSession();
  const { data: status } = useData<{ enabled: boolean; model: string }>("/api/assistant/status");
  const [open, setOpen] = useState(false);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState("");
  const end = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => end.current?.scrollIntoView({ block: "end" }), [turns]);
  useEffect(() => {
    if (open) input.current?.focus();
  }, [open]);
  useEffect(() => {
    // Esc closes; Ctrl/Cmd+K opens from anywhere.
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((o) => !o);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

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

  const enabled = status?.enabled ?? false;

  return (
    <>
      <button className="dock-trigger" aria-expanded={open} aria-controls="assistant-panel" onClick={() => setOpen((o) => !o)}>
        Asistana sor <kbd>⌘K</kbd>
      </button>

      <aside id="assistant-panel" className="dock" data-open={open} aria-label="Asistan" aria-hidden={!open} inert={!open}>
        <header className="dock-head">
          <div>
            <b>Asistan</b>
            <span>Hesaplar, hareketler ve cariler hakkında sorun. Sadece okur.</span>
          </div>
          <button className="secondary dock-close" onClick={() => setOpen(false)} aria-label="Asistanı kapat">Kapat</button>
        </header>

        <div className="dock-body" aria-live="polite">
          {status && !enabled && (
            <div className="notice warn">Bu kurulumda kapalı: sunucuda Anthropic API anahtarı tanımlı değil.</div>
          )}
          {turns.length === 0 && (
            <div className="dock-examples">
              {examples.map((q) => <button key={q} className="secondary" disabled={busy || !enabled} onClick={() => ask(q)}>{q}</button>)}
            </div>
          )}
          {turns.map((t, i) => (
            <div key={i} className={`dock-turn ${t.role}`}>
              {t.role === "user" ? <div className="bubble">{t.text}</div> : (
                <div>
                  {t.steps?.map((s, j) => <div key={j} className="dot idle step">{s}</div>)}
                  {t.text && <div className="answer">{t.text}</div>}
                  {!t.text && !t.error && busy && i === turns.length - 1 && <div className="muted">Düşünüyor…</div>}
                  {t.error && <div className="error">{t.error}</div>}
                </div>
              )}
            </div>
          ))}
          <div ref={end} />
        </div>

        <form className="dock-form" onSubmit={submit}>
          <label className="visually-hidden" htmlFor="assistant-input">Sorunuz</label>
          <input id="assistant-input" ref={input} value={draft} onChange={(e) => setDraft(e.target.value)} maxLength={2000}
            placeholder={enabled ? "Geçen hafta en büyük ödeme neydi?" : "Asistan kapalı"} disabled={!enabled} />
          <button disabled={busy || !draft.trim() || !enabled}>{busy ? "Cevaplıyor…" : "Sor"}</button>
        </form>
      </aside>
    </>
  );
}
