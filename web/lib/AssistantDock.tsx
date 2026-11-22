"use client";

import { X } from "lucide-react";
import Link from "next/link";
import { useEffect, useRef, useState, type FormEvent } from "react";
import { tl } from "./format";
import { SaymanMark } from "./marks";
import { useSession } from "./session";
import type { Dashboard } from "./types";
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
 * Once per visit Sayman says hello with the day's one finding, read from the dashboard (no model call), and offers
 * the one step it leads to. It goes away by itself, sooner if dismissed, and never covers the page for long.
 */
function Greeting({ dashboard, firstName, onAsk, onClose }: { dashboard: Dashboard; firstName: string; onAsk: () => void; onClose: () => void }) {
  const waiting = dashboard.unmatched + dashboard.exportFailed;
  const [hold, setHold] = useState(false);
  useEffect(() => {
    if (hold) return;
    const t = setTimeout(onClose, 14_000);
    return () => clearTimeout(t);
  }, [hold, onClose]);

  return (
    <div className="greeting" role="status" onMouseEnter={() => setHold(true)} onFocus={() => setHold(true)}>
      <button className="greeting-close" onClick={onClose} aria-label="Kapat"><X size={14} strokeWidth={1.5} /></button>
      <div className="greeting-who"><SaymanMark size={28} /><div><b>Sayman</b><span>Kasa&apos;nın asistanı</span></div></div>
      <p>
        Merhaba {firstName}, ben Sayman.{" "}
        {dashboard.accounts.length === 0
          ? "Bir banka bağladığınızda hareketleri sizin için takip ederim."
          : waiting > 0
            ? <>Şu an <b>{waiting} hareket</b> kararınızı bekliyor; bugün {tl(dashboard.todayIn)} girdi.</>
            : <>Bütün hareketler karara bağlı; bugün {tl(dashboard.todayIn)} girdi.</>}
        {" "}Size nasıl yardımcı olabilirim?
      </p>
      <div className="toolbar">
        {waiting > 0 && <Link className="button" href="/eslesmeyenler" onClick={onClose}>Bekleyenlere bak</Link>}
        <button className="secondary" onClick={onAsk}>Bir şey sor</button>
      </div>
    </div>
  );
}

/**
 * The assistant on every screen: a button at the bottom right, a panel from the right. It lives in the app layout,
 * so the conversation stays while the person moves between pages.
 */
export function AssistantDock({ dashboard, firstName }: { dashboard?: Dashboard; firstName: string }) {
  const { authFetch } = useSession();
  const { data: status } = useData<{ enabled: boolean }>("/api/assistant/status");
  const [open, setOpen] = useState(false);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState("");
  const [greet, setGreet] = useState(false);
  const end = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    // Once per browser session, a moment after the first screen settles.
    let seen = false;
    try {
      seen = sessionStorage.getItem("kasa-greeted") === "1";
      sessionStorage.setItem("kasa-greeted", "1");
    } catch {
      // Storage blocked: greet, it is harmless.
    }
    if (seen) return;
    const t = setTimeout(() => setGreet(true), 1200);
    return () => clearTimeout(t);
  }, []);

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
    // Only complete question and answer pairs go back, each cut to the server's 4000 characters. A failed answer
    // would leave its question alone (two questions in a row) and a long one would be refused: either way every
    // later question would fail too.
    const pairs = turns.flatMap((t, i) => {
      const answer = turns[i + 1];
      return t.role === "user" && answer?.role === "assistant" && answer.text && !answer.error ? [t, answer] : [];
    });
    const history = [...pairs, { role: "user" as const, text: question }].map(({ role, text }) => ({ role, text: text.slice(0, 4000) })).slice(-19);
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
      {greet && !open && dashboard && (
        <Greeting dashboard={dashboard} firstName={firstName} onClose={() => setGreet(false)} onAsk={() => { setGreet(false); setOpen(true); }} />
      )}
      <button className="dock-trigger" aria-expanded={open} aria-controls="assistant-panel" onClick={() => { setGreet(false); setOpen((o) => !o); }}>
        <SaymanMark size={22} /> Sayman&apos;a sor <kbd>⌘K</kbd>
      </button>

      <aside id="assistant-panel" className="dock" data-open={open} aria-label="Asistan" aria-hidden={!open} inert={!open}>
        <header className="dock-head">
          <div className="dock-who">
            <SaymanMark size={32} />
            <div>
              <b>Sayman</b>
              <span>Hesaplar, hareketler ve cariler hakkında sorun. Sadece okur, hiçbir şeyi değiştirmez.</span>
            </div>
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
                <div className="reply">
                  <SaymanMark size={20} />
                  <div>
                  {t.steps?.map((s, j) => <div key={j} className="dot idle step">{s}</div>)}
                  {t.text && <div className="answer">{t.text}</div>}
                  {!t.text && !t.error && busy && i === turns.length - 1 && <div className="muted">Düşünüyor…</div>}
                  {t.error && <div className="error">{t.error}</div>}
                  </div>
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
