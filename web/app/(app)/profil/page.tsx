"use client";

import { useState, type ChangeEvent, type FormEvent } from "react";
import { Avatar } from "@/lib/Avatar";
import { useSession } from "@/lib/session";

const roleText = { Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" } as const;

/** Cuts the middle square out of any photo and shrinks it to 256 px, so a phone picture of a few MB uploads as ~20 KB. */
async function squarePhoto(file: File): Promise<Blob> {
  const image = await createImageBitmap(file);
  const side = Math.min(image.width, image.height);
  const canvas = document.createElement("canvas");
  canvas.width = canvas.height = 256;
  const ctx = canvas.getContext("2d")!;
  ctx.fillStyle = "#fff"; // a transparent PNG would otherwise turn black as JPEG
  ctx.fillRect(0, 0, 256, 256);
  ctx.drawImage(image, (image.width - side) / 2, (image.height - side) / 2, side, side, 0, 0, 256, 256);
  image.close();
  return new Promise((ok, fail) => canvas.toBlob((b) => (b ? ok(b) : fail(new Error())), "image/jpeg", 0.88));
}

export default function Profile() {
  const { api, me, reload } = useSession();
  const [name, setName] = useState(me?.fullName ?? "");
  const [nameNotice, setNameNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [passNotice, setPassNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [photoNotice, setPhotoNotice] = useState<{ text: string; bad?: boolean } | null>(null);
  const [busy, setBusy] = useState<"name" | "password" | "photo" | null>(null);

  async function changePhoto(e: ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    e.target.value = ""; // the same file can be picked again
    if (!file) return;
    setBusy("photo");
    setPhotoNotice(null);
    try {
      const photo = await squarePhoto(file).catch(() => { throw new Error("Bu dosya fotoğraf olarak açılamadı. JPEG, PNG ya da WebP seçin."); });
      const body = new FormData();
      body.append("file", photo, "foto.jpg");
      await api("/api/me/avatar", { method: "PUT", body });
      await reload();
      setPhotoNotice({ text: "Fotoğrafınız kaydedildi." });
    } catch (err) {
      setPhotoNotice({ text: (err as Error).message, bad: true });
    } finally {
      setBusy(null);
    }
  }

  async function removePhoto() {
    setBusy("photo");
    try {
      await api("/api/me/avatar", { method: "DELETE" });
      await reload();
      setPhotoNotice({ text: "Fotoğrafınız kaldırıldı." });
    } catch (err) {
      setPhotoNotice({ text: (err as Error).message, bad: true });
    } finally {
      setBusy(null);
    }
  }

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
        <div className="photo-row">
          <Avatar id={me.userId} version={me.avatarVersion} name={me.fullName} size={64} />
          <div className="actions">
            <input id="photo" type="file" accept="image/jpeg,image/png,image/webp" onChange={changePhoto} disabled={busy !== null} />
            <label htmlFor="photo" className="button">{busy === "photo" ? "Yükleniyor…" : me.avatarVersion == null ? "Fotoğraf yükle" : "Fotoğrafı değiştir"}</label>
            {me.avatarVersion != null && <button type="button" className="secondary" onClick={removePhoto} disabled={busy !== null}>Kaldır</button>}
            <small className="muted">Ortadan kare olarak kırpılır. Sadece şirketinizdeki kişiler görür.</small>
          </div>
        </div>
        {photoNotice && <div className={`notice ${photoNotice.bad ? "bad" : ""}`} role="status" style={{ marginBottom: 16 }}>{photoNotice.text}</div>}
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
