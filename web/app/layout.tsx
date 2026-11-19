import type { Metadata } from "next";
import { SessionProvider } from "@/lib/session";
import "./globals.css";

export const metadata: Metadata = { title: "Kasa", description: "Şirket nakit yönetimi: bankalar, eşleştirme ve ERP aktarımı" };

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="tr">
      <body>
        <SessionProvider>{children}</SessionProvider>
      </body>
    </html>
  );
}
