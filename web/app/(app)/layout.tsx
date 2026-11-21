"use client";

import { ArrowLeftRight, Building2, CircleDashed, FileText, LayoutDashboard, Users, type LucideIcon } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { AssistantDock } from "@/lib/AssistantDock";
import { Avatar } from "@/lib/Avatar";
import { LiveProvider, useLive } from "@/lib/live";
import { KasaMark } from "@/lib/marks";
import { useSession } from "@/lib/session";
import type { Dashboard } from "@/lib/types";
import { useData } from "@/lib/useData";

const nav: { href: string; label: string; icon: LucideIcon; queue?: true }[] = [
  { href: "/panel", label: "Panel", icon: LayoutDashboard },
  { href: "/hareketler", label: "Hareketler", icon: ArrowLeftRight },
  { href: "/eslesmeyenler", label: "Eşleşmeyenler", icon: CircleDashed, queue: true },
  { href: "/baglantilar", label: "Banka bağlantıları", icon: Building2 },
  { href: "/erp", label: "ERP ve cariler", icon: FileText },
  { href: "/ekip", label: "Ekip", icon: Users },
];

function LiveDot() {
  const { connected } = useLive();
  return <span className={`dot ${connected ? "ok" : "idle"}`}>{connected ? "Canlı" : "Bağlanıyor"}</span>;
}

function Shell({ children }: { children: React.ReactNode }) {
  const { me, signOut } = useSession();
  const path = usePathname();
  // The one queue that needs a person: its count beside its name, updated live.
  const { data } = useData<Dashboard>("/api/dashboard?days=30");
  const waiting = data ? data.unmatched + data.exportFailed : 0;

  return (
    <>
      <div className="shell">
        <nav className="side" aria-label="Ana menü">
          <div className="brand">
            <KasaMark size={28} />
            <div><b>Kasa</b><small>{me!.companyName}</small></div>
          </div>
          <div className="links">
            {nav.map(({ href, label, icon: Icon, queue }) => (
              <Link key={href} href={href} aria-current={path.startsWith(href) ? "page" : undefined}>
                <Icon size={16} strokeWidth={1.5} aria-hidden />
                <span>{label}</span>
                {queue && waiting > 0 && <span className="count" aria-label={`${waiting} hareket bekliyor`}>{waiting}</span>}
              </Link>
            ))}
          </div>
          <div className="foot">
            <Link className="who" href="/profil" aria-current={path.startsWith("/profil") ? "page" : undefined} title="Profil">
              <Avatar id={me!.userId} version={me!.avatarVersion} name={me!.fullName} />
              <div><b>{me!.fullName}</b><span>{{ Admin: "Yönetici", Accountant: "Muhasebeci", Viewer: "İzleyici" }[me!.role]}</span></div>
            </Link>
            <div className="row">
              <LiveDot />
              <button className="secondary" style={{ height: 28, padding: "0 12px", fontSize: 13 }} onClick={() => signOut()}>Çıkış</button>
            </div>
          </div>
        </nav>
        <main className="main">{children}</main>
      </div>
      <AssistantDock dashboard={data} firstName={me!.fullName.split(" ")[0]} />
    </>
  );
}

export default function AppLayout({ children }: { children: React.ReactNode }) {
  const { ready, me } = useSession();
  const router = useRouter();

  useEffect(() => {
    if (ready && !me) router.replace("/giris");
  }, [ready, me, router]);

  if (!me) return null;

  return (
    <LiveProvider>
      <Shell>{children}</Shell>
    </LiveProvider>
  );
}
