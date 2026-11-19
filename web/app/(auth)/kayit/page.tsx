"use client";

import Link from "next/link";
import { useEffect, useState } from "react";
import { AuthForm } from "@/lib/AuthForm";

export default function Register() {
  // Behind the public demo address a new company needs a sign-up code; on a developer's machine it does not.
  const [codeRequired, setCodeRequired] = useState(false);
  useEffect(() => {
    fetch("/api/auth/register").then((r) => r.json()).then((o) => setCodeRequired(o.codeRequired === true), () => {});
  }, []);

  return (
    <>
      <h2>Şirket hesabı açın</h2>
      <p className="muted">İlk kullanıcı yönetici olur; ekibinizi sonra davet edersiniz.</p>
      <AuthForm
        path="/api/auth/register"
        submit="Hesabı aç"
        fields={[
          { name: "companyName", label: "Şirket adı" },
          { name: "fullName", label: "Adınız soyadınız", autoComplete: "name" },
          { name: "email", label: "E-posta", type: "email", autoComplete: "username", placeholder: "ad@sirket.com" },
          { name: "password", label: "Şifre", type: "password", autoComplete: "new-password", hint: "En az 10 karakter; büyük harf, küçük harf ve rakam." },
          ...(codeRequired ? [{ name: "signUpCode", label: "Kayıt kodu", autoComplete: "off", hint: "Bu demo adresinde yeni şirket kodla açılıyor. Kodu demoyu yapan kişiden isteyin." }] : []),
        ]}
        // A page opened before the code was switched on learns it from the refusal and shows the field.
        onError={(status) => status === 403 && setCodeRequired(true)}
        footer={<>Hesabınız var mı? <Link href="/giris">Giriş yapın</Link></>}
      />
    </>
  );
}
