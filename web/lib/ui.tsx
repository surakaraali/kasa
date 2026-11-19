import type { Transaction } from "./types";
import { tl } from "./format";

export function Amount({ value }: { value: number }) {
  // A real minus sign, so "+" and "−" line up in a column of mono figures.
  return <span className={`num ${value > 0 ? "in" : "out"}`}>{value > 0 ? "+" : "−"}{tl(Math.abs(value))}</span>;
}

const ruleText = { Iban: "IBAN ile", TaxNumber: "vergi no ile", Manual: "elle" } as const;

export function MatchBadge({ t }: { t: Transaction }) {
  if (t.matchStatus === "Unmatched") return <span className="dot warn">Eşleşmedi</span>;
  if (t.matchStatus === "Ignored") return <span className="dot idle">ERP&apos;ye gitmeyecek</span>;
  return <span className="dot ok">Eşleşti{t.matchRule ? `, ${ruleText[t.matchRule]}` : ""}</span>;
}

export function ExportBadge({ t }: { t: Transaction }) {
  switch (t.exportStatus) {
    case "Pending": return <span className="dot idle">ERP&apos;ye gidiyor</span>;
    case "Exported": return <span className="voucher"><span>Fiş</span>{t.erpVoucherNo}</span>;
    case "Failed": return <span className="dot bad" title={t.exportError ?? undefined}>Aktarılamadı</span>;
    default: return null;
  }
}

export function Counterparty({ t }: { t: Transaction }) {
  return (
    <>
      <div>{t.counterpartyName ?? t.description}</div>
      <div className="cell-sub">{t.counterpartyName ? t.description : "Karşı taraf bilgisi yok"}</div>
    </>
  );
}
