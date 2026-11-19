const money = new Intl.NumberFormat("tr-TR", { style: "currency", currency: "TRY", minimumFractionDigits: 2 });
const date = new Intl.DateTimeFormat("tr-TR", { day: "2-digit", month: "2-digit", year: "numeric", timeZone: "Europe/Istanbul" });
const dateTime = new Intl.DateTimeFormat("tr-TR", { day: "2-digit", month: "2-digit", year: "numeric", hour: "2-digit", minute: "2-digit", timeZone: "Europe/Istanbul" });
const shortDay = new Intl.DateTimeFormat("tr-TR", { day: "2-digit", month: "short", timeZone: "UTC" });

export const tl = (value: number | null | undefined) => (value == null ? "—" : money.format(value));
export const day = (iso: string) => date.format(new Date(iso));
export const when = (iso: string | null | undefined) => (iso ? dateTime.format(new Date(iso)) : "—");
/** "2026-11-19" → "19 Kas" (a calendar day, so no time zone shift). */
export const calendarDay = (isoDate: string) => shortDay.format(new Date(`${isoDate}T00:00:00Z`));
/** TR09 0090 0010 0000 0000 0000 01 */
export const iban = (value: string | null | undefined) => (value ? value.replace(/(.{4})/g, "$1 ").trim() : "—");
