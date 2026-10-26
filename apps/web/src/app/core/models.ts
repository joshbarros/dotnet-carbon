export interface Batch {
  id: string;
  code: string;
  name: string;
  standard: string;
  country: string;
  issuedOn: string;
  priceCents: number;
  tonnesIssued: number;
  tonnesLeft: number;
}

export type PurchaseStatus = 'Requested' | 'Held' | 'Allocated' | 'Reversed' | 'Rejected';

export interface Purchase {
  id: string;
  batchCode: string;
  ownerId: string;
  reference: string;
  tonnes: number;
  totalCents: number;
  status: PurchaseStatus;
  reason: string | null;
  certificateNumber: string | null;
  createdAt: string;
  updatedAt: string;
  tonnesReleasedAt: string | null;
}

export interface LogEntry {
  id: string;
  type: string;
  module: string;
  occurredAt: string;
}

export interface UserInfo {
  id: string;
  email: string;
  roles: string[];
}

export interface AuthResponse {
  accessToken: string;
  expiresAt: string;
  user: UserInfo;
}
