"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent, type ReactNode } from "react";
import { useSession } from "./session";

interface Field { name: string; label: string; type?: string; autoComplete?: string }

/** One form for sign in, sign up and joining by invitation: they differ only in fields and endpoint. */
export function AuthForm({ path, fields, submit, extra, footer }: {
  path: "/api/auth/login" | "/api/auth/register" | "/api/auth/accept-invitation";
  fields: Field[];
  submit: string;
  extra?: Record<string, string>;
  footer?: ReactNode;
}) {
  const { signIn } = useSession();
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await signIn(path, { ...Object.fromEntries(new FormData(e.currentTarget)), ...extra });
      router.replace("/panel");
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <>
      <form onSubmit={onSubmit}>
        {fields.map((f) => (
          <label key={f.name}>
            {f.label}
            <input name={f.name} type={f.type ?? "text"} autoComplete={f.autoComplete} required />
          </label>
        ))}
        {error && <p className="error" role="alert">{error}</p>}
        <button type="submit" disabled={busy}>{busy ? "Bekleyin…" : submit}</button>
      </form>
      {footer && <p className="muted" style={{ marginTop: 16, marginBottom: 0 }}>{footer}</p>}
    </>
  );
}
