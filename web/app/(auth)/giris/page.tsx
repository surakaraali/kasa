import Link from "next/link";
import { AuthForm } from "@/lib/AuthForm";

export default function SignIn() {
  return (
    <>
      <h2>Giriş yapın</h2>
      <AuthForm
        path="/api/auth/login"
        submit="Giriş yap"
        fields={[
          { name: "email", label: "E-posta", type: "email", autoComplete: "username", placeholder: "ad@sirket.com" },
          { name: "password", label: "Şifre", type: "password", autoComplete: "current-password" },
        ]}
        footer={<>Şirketiniz henüz yok mu? <Link href="/kayit">Hesap açın</Link></>}
      />
    </>
  );
}
