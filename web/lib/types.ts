export type Role = "Admin" | "Accountant" | "Viewer";

export interface Me { userId: string; fullName: string; email: string; role: Role; companyId: string; companyName: string; avatarVersion: number | null }

export interface Account { id: string; connectionId: string; bank: "AcikBank" | "KlasikBank"; iban: string; name: string; currency: string; balance: number | null; balanceAt: string | null }

export interface Connection {
  id: string; bank: "AcikBank" | "KlasikBank"; status: "Pending" | "Active" | "ReauthRequired" | "Rejected" | "Removed";
  createdAt: string; consentExpiresAt: string | null; lastSyncAt: string | null; lastSyncOutcome: "Succeeded" | "Failed" | null;
  lastSyncError: string | null; accounts: Account[];
}

export type MatchStatus = "Unmatched" | "Matched" | "Ignored";
export type ExportStatus = "None" | "Pending" | "Exported" | "Failed";

export interface Transaction {
  id: number; accountId: string; bank: string; accountName: string; bankReference: string; bookedAt: string; amount: number;
  description: string; counterpartyName: string | null; counterpartyIban: string | null; counterpartyTaxNumber: string | null;
  matchStatus: MatchStatus; matchRule: "Iban" | "TaxNumber" | "Manual" | null; currentAccountId: string | null;
  currentAccountCode: string | null; currentAccountTitle: string | null; exportStatus: ExportStatus; erpVoucherNo: string | null;
  exportError: string | null; rowVersion: string;
}

export interface TransactionPage { items: Transaction[]; nextCursor: string | null }

export interface CurrentAccount { id: string; code: string; title: string; taxNumber: string | null; iban: string | null }

export interface Dashboard {
  totalBalance: number; accounts: Account[]; todayIn: number; todayOut: number; daily: { date: string; in: number; out: number }[];
  matched: number; unmatched: number; ignored: number; exported: number; exportPending: number; exportFailed: number; lastSyncAt: string | null;
  matchedByIban: number; matchedByTaxNumber: number; matchedManually: number;
}
