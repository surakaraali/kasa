const points = [
  ["Bütün bankalar tek ekranda", "Açık bankacılık ve eski usul bankalar; hareketler 15 dakikada bir kendiliğinden gelir."],
  ["Doğru cariye, kendiliğinden", "IBAN ve vergi numarasıyla eşleşir; emin olunamayanı size bırakır."],
  ["ERP'de fiş, saniyeler içinde", "Eşleşen hareket ERP'ye fiş olarak gider; ERP kapalıyken iş bekler, kaybolmaz."],
];

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <main className="auth">
      <section className="auth-brand" aria-label="Kasa">
        <div className="auth-logo">Kasa</div>
        <div>
          <h1>Şirketinizin parası nerede, kimden geldi, ERP&apos;ye işlendi mi?</h1>
          <ul>
            {points.map(([title, text]) => (
              <li key={title}><strong>{title}</strong><span>{text}</span></li>
            ))}
          </ul>
        </div>
        <p className="auth-note">Test ortamı: bankalar ve ERP örnek veridir.</p>
      </section>
      <section className="auth-form">
        <div className="auth-box">{children}</div>
      </section>
    </main>
  );
}
