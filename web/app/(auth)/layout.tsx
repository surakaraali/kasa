import { KasaMark } from "@/lib/marks";

// Lines from the test bank's real week, each with what Kasa did to it. The third one waits for a person: the IBAN
// points to one current account and the tax number to another.
const lines = [
  { date: "14.11", who: "ABC TEKSTİL SANAYİ A.Ş.", amount: "+84.999,99", rule: "IBAN ile", account: "120.01.001", voucher: "TH000039" },
  { date: "13.11", who: "YILDIZ AMBALAJ LTD. ŞTİ.", amount: "−7.320,75", rule: "Vergi no ile", account: "320.01.001", voucher: "TD000040" },
  { date: "12.11", who: "KUZEY LOJİSTİK A.Ş.", amount: "+3.200,00", rule: null, account: null, voucher: null },
  { date: "12.11", who: "DENİZ GIDA PAZARLAMA", amount: "+1.250,50", rule: "Vergi no ile", account: "120.01.002", voucher: "TH000041" },
];

/**
 * The one thing on this screen people remember: a bank statement on continuous form paper. On load its lines print
 * one by one, each gets its current account and then the voucher number, set like an accountant's "posted" stamp.
 * Pure CSS: without motion (or with reduced motion) the finished statement is simply there.
 */
function Statement() {
  return (
    <figure className="statement" aria-label="Bir banka ekstresinin Kasa'da işlenişi">
      <figcaption className="statement-head">
        <span>KlasikBank, Ticari Hesap</span>
        <span className="num">TR69 … 0003</span>
      </figcaption>
      <ol className="statement-lines">
        {lines.map((l, i) => (
          <li key={l.voucher ?? l.who} style={{ "--i": i } as React.CSSProperties}>
            <span className="num date">{l.date}</span>
            <span className="who">{l.who}</span>
            <span className="num amount">{l.amount}</span>
            {l.account ? (
              <>
                <span className="result">{l.rule} eşleşti: <span className="num">{l.account}</span></span>
                <span className="stamp" aria-label={`ERP fişi ${l.voucher}`}><small>ERP</small>{l.voucher}</span>
              </>
            ) : (
              <span className="result waiting">IBAN ve vergi no farklı carileri gösteriyor; kararınızı bekliyor</span>
            )}
          </li>
        ))}
      </ol>
    </figure>
  );
}

export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <main className="auth">
      <section className="auth-brand" aria-label="Kasa">
        <div className="auth-logo"><KasaMark size={28} /> Kasa</div>
        <div>
          <h1>Bankadaki her kuruş, doğru cariye ve ERP&apos;ye</h1>
          <p className="lede">Bütün banka hesaplarınız tek yerde. Kasa hareketleri cariye eşler, emin olamadığını size bırakır, eşleşeni ERP&apos;ye fiş olarak yazar.</p>
          <Statement />
        </div>
        <p className="auth-note">Test ortamı: bankalar ve ERP örnek veridir</p>
      </section>
      <section className="auth-form">
        <div className="auth-box">{children}</div>
      </section>
    </main>
  );
}
