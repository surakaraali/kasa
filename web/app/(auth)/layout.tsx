/** One movement's whole trip through Kasa, in the product's own words and figures. */
function Journey() {
  return (
    <div className="journey" aria-label="Bir hareketin yolu: bankadan cariye, cariden ERP fişine">
      <div className="journey-step">
        <span className="kind">Banka</span>
        <span className="what"><b>ABC TEKSTİL SANAYİ A.Ş.</b><span>KlasikBank · <span className="num">14.11.2026 10:45</span></span></span>
        <span className="num in">+84.999,99 ₺</span>
      </div>
      <div className="journey-step">
        <span className="kind">Cari</span>
        <span className="what"><b>120.01.001 ABC TEKSTİL SANAYİ A.Ş.</b><span>IBAN ile kendiliğinden eşleşti</span></span>
        <span className="dot ok" style={{ fontSize: 12 }}>Eşleşti</span>
      </div>
      <div className="journey-step">
        <span className="kind">ERP</span>
        <span className="what"><b>Tahsilat fişi</b><span>Firma 001 · birkaç saniye sonra</span></span>
        <span className="voucher">TH000039</span>
      </div>
    </div>
  );
}

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <main className="auth">
      <section className="auth-brand" aria-label="Kasa">
        <div className="auth-logo">Kasa</div>
        <div>
          <h1>Bankadaki her kuruş, doğru cariye ve ERP&apos;ye</h1>
          <p className="lede">Bütün banka hesaplarınız tek yerde. Hareketler kendiliğinden cariye eşleşir, emin olunamayanı size bırakır, eşleşeni ERP&apos;ye fiş olarak yazar.</p>
          <Journey />
        </div>
        <p className="auth-note">Test ortamı: bankalar ve ERP örnek veridir</p>
      </section>
      <section className="auth-form">
        <div className="auth-box">{children}</div>
      </section>
    </main>
  );
}
