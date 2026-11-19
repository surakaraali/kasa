export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <main className="auth">
      <div className="card">{children}</div>
    </main>
  );
}
