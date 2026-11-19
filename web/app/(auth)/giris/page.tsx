import Link from "next/link";
import { AuthForm } from "@/lib/AuthForm";

export default function SignIn() {
  return (
    <>
      <h1>Kasa</h1>
      <p className="muted">Şirket hesabınızla giriş yapın.</p>
      <AuthForm
        path="/api/auth/login"
        submit="Giriş yap"
        fields={[
          { name: "email", label: "E-posta", type: "email", autoComplete: "username" },
          { name: "password", label: "Şifre", type: "password", autoComplete: "current-password" },
        ]}
        footer={<>Şirketiniz henüz yok mu? <Link href="/kayit">Şirket hesabı açın</Link></>}
      />
    </>
  );
}
