import Link from "next/link";
import { AuthForm } from "@/lib/AuthForm";

export default function Register() {
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
        ]}
        footer={<>Hesabınız var mı? <Link href="/giris">Giriş yapın</Link></>}
      />
    </>
  );
}
