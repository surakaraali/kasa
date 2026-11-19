"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { AssistantDock } from "@/lib/AssistantDock";
import { LiveProvider, useLive } from "@/lib/live";
import { useSession } from "@/lib/session";

const nav = [
  { href: "/panel", label: "Panel" },
  { href: "/hareketler", label: "Hareketler" },
  { href: "/eslesmeyenler", label: "Eşleşmeyenler" },
  { href: "/baglantilar", label: "Banka bağlantıları" },
  { href: "/erp", label: "ERP ve cariler" },
  { href: "/ekip", label: "Ekip" },
];

function LiveDot() {
  const { connected } = useLive();
  return <span className={`dot ${connected ? "ok" : "idle"}`}>{connected ? "Canlı" : "Bağlanıyor"}</span>;
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const { ready, me, signOut } = useSession();
  const router = useRouter();
  const path = usePathname();

  useEffect(() => {
    if (ready && !me) router.replace("/giris");
  }, [ready, me, router]);

  if (!me) return null;

  return (
    <LiveProvider>
      <div className="shell">
        <nav className="side" aria-label="Ana menü">
          <div className="brand"><b>Kasa</b><small>{me.companyName}</small></div>
          {nav.map((n) => (
            <Link key={n.href} href={n.href} aria-current={path.startsWith(n.href) ? "page" : undefined}>{n.label}</Link>
          ))}
          <div className="foot">
            <b>{me.fullName}</b>
            <span>{{ Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" }[me.role]}</span>
            <div className="row">
              <LiveDot />
              <button className="secondary" style={{ height: 28, padding: "0 12px", fontSize: 13 }} onClick={() => signOut()}>Çıkış</button>
            </div>
          </div>
        </nav>
        <main className="main">{children}</main>
      </div>
      <AssistantDock />
    </LiveProvider>
  );
}
