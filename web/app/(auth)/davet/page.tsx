"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { AuthForm } from "@/lib/AuthForm";

function Invitation() {
  const token = useSearchParams().get("token") ?? "";
  return (
    <>
      <h2>Ekibe katılın</h2>
      <p className="muted">Adınızı ve şifrenizi belirleyin; davet edildiğiniz şirkete katılırsınız.</p>
      <AuthForm
        path="/api/auth/accept-invitation"
        submit="Katıl"
        extra={{ token }}
        fields={[
          { name: "fullName", label: "Adınız soyadınız", autoComplete: "name" },
          { name: "password", label: "Şifre", type: "password", autoComplete: "new-password", hint: "En az 10 karakter; büyük harf, küçük harf ve rakam." },
        ]}
      />
    </>
  );
}

export default function InvitationPage() {
  return (
    <Suspense>
      <Invitation />
    </Suspense>
  );
}
