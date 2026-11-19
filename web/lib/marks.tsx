/** Kasa's mark: the dial of a safe, a "kasa" in Turkish, its pointer at the top like a needle on a gauge. */
export function KasaMark({ size = 24, title = "Kasa" }: { size?: number; title?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" role="img" aria-label={title}>
      <rect width="24" height="24" rx="6" fill="#0f5c4c" />
      <circle cx="12" cy="12" r="6.6" fill="none" stroke="#fff" strokeWidth="1.8" />
      <circle cx="12" cy="12" r="2" fill="#fff" />
      <rect x="11.2" y="3.4" width="1.6" height="3.4" rx="0.8" fill="#d9f0e4" />
      <rect x="17.4" y="11.2" width="2.2" height="1.6" rx="0.8" fill="#fff" />
      <rect x="4.4" y="11.2" width="2.2" height="1.6" rx="0.8" fill="#fff" />
      <rect x="11.2" y="17.4" width="1.6" height="2.2" rx="0.8" fill="#fff" />
    </svg>
  );
}

/**
 * Sayman, Kasa's assistant: a treasurer that is a small machine. A screen for a face, two calm eyes in the ledger's
 * green, one antenna. Flat, so it sits beside text like a person's initial would.
 */
export function SaymanMark({ size = 24, title = "Sayman" }: { size?: number; title?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" role="img" aria-label={title}>
      <line x1="12" y1="2.6" x2="12" y2="5.4" stroke="#171717" strokeWidth="1.6" strokeLinecap="round" />
      <circle cx="12" cy="2.4" r="1.5" fill="#16a34a" />
      <rect x="3" y="5.4" width="18" height="15" rx="5" fill="#171717" />
      <rect x="5.6" y="8.2" width="12.8" height="9.2" rx="3" fill="#232325" />
      <rect x="8" y="11" width="2.4" height="3.4" rx="1.2" fill="#4cc38a" />
      <rect x="13.6" y="11" width="2.4" height="3.4" rx="1.2" fill="#4cc38a" />
      <rect x="1.2" y="11" width="1.8" height="4.4" rx="0.9" fill="#171717" />
      <rect x="21" y="11" width="1.8" height="4.4" rx="0.9" fill="#171717" />
    </svg>
  );
}
