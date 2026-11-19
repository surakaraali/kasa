import Link from "next/link";
import { AuthForm } from "@/lib/AuthForm";

export default function Register() {
  return (
    <>
      <h1>Şirket hesabı açın</h1>
      <p className="muted">Şirketiniz ve ilk yönetici birlikte oluşturulur. Ekibinizi sonra davet edersiniz.</p>
      <AuthForm
        path="/api/auth/register"
        submit="Hesabı aç"
        fields={[
          { name: "companyName", label: "Şirket adı" },
          { name: "fullName", label: "Adınız soyadınız", autoComplete: "name" },
          { name: "email", label: "E-posta", type: "email", autoComplete: "username" },
          { name: "password", label: "Şifre (en az 10 karakter; büyük, küçük harf ve rakam)", type: "password", autoComplete: "new-password" },
        ]}
        footer={<>Hesabınız var mı? <Link href="/giris">Giriş yapın</Link></>}
      />
    </>
  );
}
