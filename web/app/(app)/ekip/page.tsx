"use client";

import { useState, type FormEvent } from "react";
import { when } from "@/lib/format";
import { useSession } from "@/lib/session";
import type { Role } from "@/lib/types";
import { useData } from "@/lib/useData";

const roleText: Record<Role, string> = { Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" };

interface User { id: string; fullName: string; email: string; role: Role; createdAt: string }
interface Invitation { id: string; email: string; role: Role; expiresAt: string; acceptedAt: string | null }

export default function Team() {
  const { api, me } = useSession();
  const admin = me?.role === "Admin";
  const users = useData<User[]>(admin ? "/api/users" : null);
  const invitations = useData<Invitation[]>(admin ? "/api/invitations" : null);
  const [link, setLink] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function invite(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    setError(null);
    try {
      const created = await api<{ token: string }>("/api/invitations", { method: "POST", json: Object.fromEntries(new FormData(form)) });
      // The token is shown once; only its hash is stored.
      setLink(`${window.location.origin}/davet?token=${encodeURIComponent(created.token)}`);
      form.reset();
      invitations.reload();
    } catch (err) {
      setError((err as Error).message);
    }
  }

  if (!admin) return <div className="stack"><h1>Ekip</h1><p className="muted">Ekibi yalnızca yöneticiler görür.</p></div>;

  return (
    <div className="stack">
      <div className="head"><h1>Ekip</h1></div>
      <section className="card">
        <h2>Davet et</h2>
        <form className="inline" onSubmit={invite}>
          <label>E-posta<input name="email" type="email" required /></label>
          <label>Rol
            <select name="role" defaultValue="Accountant">
              <option value="Accountant">Muhasebeci: eşler, senkron başlatır</option>
              <option value="Viewer">İzleyici: sadece görür</option>
              <option value="Admin">Yönetici: her şey</option>
            </select>
          </label>
          <button>Davet bağlantısı oluştur</button>
        </form>
        {error && <p className="error">{error}</p>}
        {link && (
          <div className="notice" style={{ marginTop: 12 }}>
            Bu bağlantıyı davet ettiğiniz kişiye iletin. 7 gün geçerli, bir kez kullanılır ve bir daha gösterilmez:
            <div className="num" style={{ wordBreak: "break-all", marginTop: 6 }}>{link}</div>
          </div>
        )}
      </section>
      <section className="card table-wrap">
        <h2>Kullanıcılar</h2>
        <table>
          <thead><tr><th>Ad</th><th>E-posta</th><th>Rol</th></tr></thead>
          <tbody>{users.data?.map((u) => <tr key={u.id}><td>{u.fullName}</td><td>{u.email}</td><td>{roleText[u.role]}</td></tr>)}</tbody>
        </table>
      </section>
      <section className="card table-wrap">
        <h2>Davetler</h2>
        <table>
          <thead><tr><th>E-posta</th><th>Rol</th><th>Durum</th></tr></thead>
          <tbody>
            {invitations.data?.map((i) => (
              <tr key={i.id}><td>{i.email}</td><td>{roleText[i.role]}</td><td>{i.acceptedAt ? `Katıldı, ${when(i.acceptedAt)}` : `Bekliyor, ${when(i.expiresAt)} tarihine kadar`}</td></tr>
            ))}
            {invitations.data?.length === 0 && <tr><td colSpan={3} className="muted">Davet yok.</td></tr>}
          </tbody>
        </table>
      </section>
    </div>
  );
}
