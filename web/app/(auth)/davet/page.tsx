"use client";

import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { AuthForm } from "@/lib/AuthForm";

function Invitation() {
  const token = useSearchParams().get("token") ?? "";
  return (
    <>
      <h1>Ekibe katılın</h1>
      <p className="muted">Davet edildiğiniz şirkete katılmak için adınızı ve şifrenizi belirleyin.</p>
      <AuthForm
        path="/api/auth/accept-invitation"
        submit="Katıl"
        extra={{ token }}
        fields={[
          { name: "fullName", label: "Adınız soyadınız", autoComplete: "name" },
          { name: "password", label: "Şifre (en az 10 karakter; büyük, küçük harf ve rakam)", type: "password", autoComplete: "new-password" },
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
