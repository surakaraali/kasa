import type { Transaction } from "./types";
import { tl } from "./format";

export function Amount({ value }: { value: number }) {
  return <span className={`num ${value > 0 ? "in" : "out"}`}>{value > 0 ? "+" : ""}{tl(value)}</span>;
}

const ruleText = { Iban: "IBAN ile", TaxNumber: "vergi no ile", Manual: "elle" } as const;

export function MatchBadge({ t }: { t: Transaction }) {
  if (t.matchStatus === "Unmatched") return <span className="badge warn">Eşleşmedi</span>;
  if (t.matchStatus === "Ignored") return <span className="badge">ERP&apos;ye gitmeyecek</span>;
  return <span className="badge good">Eşleşti{t.matchRule ? `, ${ruleText[t.matchRule]}` : ""}</span>;
}

export function ExportBadge({ t }: { t: Transaction }) {
  switch (t.exportStatus) {
    case "Pending": return <span className="badge">ERP&apos;ye gidiyor</span>;
    case "Exported": return <span className="badge good">Fiş {t.erpVoucherNo}</span>;
    case "Failed": return <span className="badge bad" title={t.exportError ?? undefined}>Aktarılamadı</span>;
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
