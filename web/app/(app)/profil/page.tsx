"use client";

import { useState, type FormEvent } from "react";
import { useSession } from "@/lib/session";

const roleText = { Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" } as const;

export default function Profile() {
  const { api, me, reload } = useSession();
  const [name, setName] = useState(me?.fullName ?? "");
  const [nameNotice, setNameNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [passNotice, setPassNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [busy, setBusy] = useState<"name" | "password" | null>(null);

  async function saveName(e: FormEvent) {
    e.preventDefault();
    setBusy("name");
    try {
      await api("/api/me", { method: "PUT", json: { fullName: name } });
      await reload();
      setNameNotice({ text: "Adınız kaydedildi." });
    } catch (err) {
      setNameNotice({ text: (err as Error).message, bad: true });
    } finally {
      setBusy(null);
    }
  }

  async function changePassword(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = e.currentTarget;
    const data = Object.fromEntries(new FormData(form)) as Record<string, string>;
    if (data.newPassword !== data.repeat) {
      setPassNotice({ text: "Yeni şifre iki alanda aynı değil.", bad: true });
      return;
    }
    setBusy("password");
    try {
      const { accessToken } = await api<{ accessToken: string }>("/api/me/password", { method: "POST", json: { currentPassword: data.currentPassword, newPassword: data.newPassword } });
      await reload(accessToken);
      form.reset();
      setPassNotice({ text: "Şifreniz değişti. Diğer cihazlardaki oturumlar kapatıldı." });
    } catch (err) {
      setPassNotice({ text: (err as Error).message, bad: true });
    } finally {
      setBusy(null);
    }
  }

  if (!me) return null;

  return (
    <div className="stack" style={{ maxWidth: 720 }}>
      <div className="head">
        <div>
          <h1>Profil</h1>
          <p>{me.companyName}, {roleText[me.role].toLocaleLowerCase("tr-TR")}</p>
        </div>
      </div>

      <section className="card">
        <h2>Bilgileriniz</h2>
        <form className="profile-form" onSubmit={saveName}>
          <label>Ad soyad<input value={name} onChange={(e) => setName(e.target.value)} required minLength={2} maxLength={200} autoComplete="name" /></label>
          <label>E-posta<input value={me.email} readOnly aria-describedby="email-hint" /></label>
          <small id="email-hint" className="muted">E-posta adresi giriş için kullanılır ve buradan değiştirilemez.</small>
          <label>Rol<input value={roleText[me.role]} readOnly aria-describedby="role-hint" /></label>
          <small id="role-hint" className="muted">Rolünüzü şirketinizin yöneticisi belirler.</small>
          {nameNotice && <div className={`notice ${nameNotice.bad ? "bad" : ""}`} role="status">{nameNotice.text}</div>}
          <div><button disabled={busy !== null || name.trim() === me.fullName}>{busy === "name" ? "Kaydediliyor…" : "Adı kaydet"}</button></div>
        </form>
      </section>

      <section className="card">
        <h2>Şifre</h2>
        <form className="profile-form" onSubmit={changePassword}>
          <label>Mevcut şifre<input name="currentPassword" type="password" required autoComplete="current-password" /></label>
          <label>Yeni şifre<input name="newPassword" type="password" required minLength={10} autoComplete="new-password" aria-describedby="pw-hint" /></label>
          <small id="pw-hint" className="muted">En az 10 karakter; büyük harf, küçük harf ve rakam. Değişince diğer cihazlardaki oturumlar kapanır.</small>
          <label>Yeni şifre, tekrar<input name="repeat" type="password" required minLength={10} autoComplete="new-password" /></label>
          {passNotice && <div className={`notice ${passNotice.bad ? "bad" : ""}`} role="status">{passNotice.text}</div>}
          <div><button disabled={busy !== null}>{busy === "password" ? "Değiştiriliyor…" : "Şifreyi değiştir"}</button></div>
        </form>
      </section>
    </div>
  );
}
