"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { LiveProvider, useLive } from "@/lib/live";
import { useSession } from "@/lib/session";

const nav = [
  { href: "/panel", label: "Panel" },
  { href: "/hareketler", label: "Hareketler" },
  { href: "/eslesmeyenler", label: "Eşleşmeyenler" },
  { href: "/baglantilar", label: "Banka bağlantıları" },
  { href: "/erp", label: "ERP ve cariler" },
  { href: "/asistan", label: "Asistan" },
  { href: "/ekip", label: "Ekip" },
];

function LiveDot() {
  const { connected } = useLive();
  return <span className="live" data-on={connected}>{connected ? "Canlı" : "Bağlanıyor"}</span>;
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
          <div className="brand">Kasa<small>{me.companyName}</small></div>
          {nav.map((n) => (
            <Link key={n.href} href={n.href} aria-current={path.startsWith(n.href) ? "page" : undefined}>{n.label}</Link>
          ))}
          <div className="foot">
            <div>{me.fullName}</div>
            <div>{{ Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" }[me.role]}</div>
            <div style={{ display: "flex", justifyContent: "space-between", marginTop: 8 }}>
              <LiveDot />
              <button className="secondary" style={{ padding: "2px 10px" }} onClick={() => signOut()}>Çıkış</button>
            </div>
          </div>
        </nav>
        <main className="main">{children}</main>
      </div>
    </LiveProvider>
  );
}
