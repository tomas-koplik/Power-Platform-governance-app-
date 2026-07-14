export type Role = "Reader" | "Auditor" | "CustomerAdmin" | "Consultant" | "InternalAdmin";
export type Coverage = "Full" | "Partial" | "Failed" | "Skipped";
export type SnapshotStatus = "Queued" | "Running" | "Completed" | "Partial" | "Failed";
export type Severity = "Critical" | "High" | "Medium" | "Low";
export type FindingStatus = "Pass" | "Fail" | "Partial" | "NotEvaluated" | "Excepted" | "NotApplicable";

export interface Session {
  displayName: string;
  role: Role;
  authMode: "msal" | "mock";
}

export interface Customer {
  customerId: string;
  name: string;
  entraTenantId: string;
  region: string;
  status: "Pending" | "Active" | "Suspended" | "Offboarded";
  connection: { mode: "Delegated" | "AppOnly"; status: ConnectionStatus; detail: string };
}

export type ConnectionStatus = "Pending" | "Active" | "Degraded" | "Revoked";
export interface ConsentUrl { url: string; entraTenantId: string }
export interface ConsentCallbackInput { state: string; tenant?: string; adminConsent?: string; error?: string; errorDescription?: string }
export interface ConsentCallbackResult {
  customerId: string;
  entraTenantId: string;
  operation: string;
  status: ConnectionStatus;
  enterpriseApplicationPresent: boolean;
  delegatedScopeGranted: boolean;
  powerPlatformRoleAssigned: boolean;
  detail: string;
}
export interface TenantConnection {
  connectionId: string;
  customerId: string;
  mode: "Delegated" | "AppOnly";
  status: ConnectionStatus;
  consentGrantedAt?: string;
  lastValidatedAt?: string;
}
export interface TenantCapability {
  tenantCapabilityId: string;
  customerId: string;
  connectionId: string;
  endpoint: string;
  identity: string;
  available: boolean;
  detail: string;
  verifiedAt: string;
}
export interface DeletionRecord {
  jobId: string;
  customerId: string;
  status: "Requested" | "Approved" | "Executing" | "PendingRetentionExpiry" | "Completed" | "Failed";
  requestedBy: string;
  requestedAt: string;
  approvedBy?: string;
  approvedAt?: string;
  retentionExpiresAt: string;
  completedAt?: string;
  detail?: string;
  certificateId?: string;
}

export interface SnapshotSection {
  snapshotSectionId: string;
  snapshotId: string;
  sectionKey: string;
  coverage: Coverage;
  itemCount: number;
  reason?: string;
  recordedAt: string;
}

export interface Snapshot {
  snapshotId: string;
  customerId: string;
  status: SnapshotStatus;
  mode: "Delegated" | "AppOnly";
  requestedAt: string;
  startedAt?: string;
  completedAt?: string;
  sections: SnapshotSection[];
}

export interface EnvironmentRecord {
  id: string;
  name: string;
  type: string;
  region: string;
  managed: boolean;
  dlpCovered: boolean;
  apps: number;
  flows: number;
  ownerUpn?: string;
  risk: "Controlled" | "Review" | "High";
}

export interface SettingRecord {
  key: string;
  label: string;
  value: string;
  source: "API" | "Derived";
  coverage: Coverage;
  meaning: string;
}

export interface Finding {
  id: string;
  findingId?: string;
  ruleId?: string;
  title: string;
  area: string;
  severity: Severity;
  status: FindingStatus;
  scope: string;
  observed: string;
  interpretation: string;
  proposedAction: string;
  ownerUpn?: string;
  remediation: "Script" | "Manual" | "Informational";
}

export interface DlpPolicy {
  id: string;
  name: string;
  scope: string;
  environments: string[];
  business: string[];
  nonBusiness: string[];
  blocked: string[];
}

export interface WorkspaceData {
  capabilities: Capabilities;
  session: Session;
  customers: Customer[];
  snapshots: Snapshot[];
  score: { overall: number; tier: string; evaluated: number; total: number; confidence: Coverage; areas: Record<string, number> };
  settings: SettingRecord[];
  environments: EnvironmentRecord[];
  findings: Finding[];
  dlpPolicies: DlpPolicy[];
}

export interface Capabilities {
  portfolio: boolean;
  onboarding: boolean;
  connections: boolean;
  snapshots: boolean;
  evidence: boolean;
  findings: boolean;
  score: boolean;
  dlp: boolean;
  compare: boolean;
  exports: boolean;
  exceptions: boolean;
  remediation: boolean;
  approvals: boolean;
}

export interface SnapshotComparison {
  customerId: string;
  baselineSnapshotId: string;
  currentSnapshotId: string;
  addedFindings: number;
  resolvedFindings: number;
  changedFindings: number;
}
export interface ExportJob {
  exportJobId: string;
  customerId: string;
  format: "Pdf" | "Xlsx" | "Json";
  status: "Queued" | "Running" | "Completed" | "Failed";
  createdAt: string;
  downloadUrl?: string;
  failureReason?: string;
}
export interface EvidenceDocument {
  rawEvidenceReferenceId: string;
  sectionKey: string;
  mediaType: string;
  content: unknown;
}
export interface GovernanceException {
  exceptionId: string;
  customerId: string;
  findingId: string;
  reason: string;
  approvedAt: string;
  expiresAt: string;
}

export interface StartSnapshotInput {
  customerId: string;
  idempotencyKey: string;
  mode: "Delegated" | "AppOnly";
  sections?: string[];
  environmentIds?: string[];
}

export interface GovernanceAdapter {
  readonly kind: "live" | "mock";
  loadWorkspace(signal?: AbortSignal): Promise<WorkspaceData>;
  startSnapshot(input: StartSnapshotInput): Promise<Snapshot>;
  getConsentUrl(customerId: string): Promise<ConsentUrl>;
  submitConsentCallback(input: ConsentCallbackInput): Promise<ConsentCallbackResult>;
  getConnection(customerId: string): Promise<TenantConnection>;
  getTenantCapabilities(customerId: string): Promise<TenantCapability[]>;
  revokeConnection(customerId: string): Promise<TenantConnection>;
  reconsentConnection(customerId: string): Promise<TenantConnection>;
  requestOffboarding(customerId: string, retentionExpiresAt: string): Promise<DeletionRecord>;
  approveOffboarding(customerId: string): Promise<DeletionRecord>;
  getDeletion(customerId: string): Promise<DeletionRecord>;
  listSnapshots(customerId: string): Promise<Snapshot[]>;
  getEvidence(customerId: string, snapshotId: string, evidenceId: string, raw?: boolean): Promise<EvidenceDocument>;
  compareSnapshots(customerId: string, baselineSnapshotId: string, currentSnapshotId: string): Promise<SnapshotComparison>;
  createExport(customerId: string): Promise<ExportJob>;
  getExport(customerId: string, exportJobId: string): Promise<ExportJob>;
  downloadExport(customerId: string, exportJobId: string): Promise<void>;
  createException(customerId: string, findingId: string, reason: string, expiresAt: string): Promise<GovernanceException>;
}

export const canViewPii = (role: Role) => role === "CustomerAdmin" || role === "Consultant" || role === "InternalAdmin";

export function displayIdentity(value: string | undefined, role: Role): string {
  if (!value) return "Not recorded";
  if (canViewPii(role)) return value;
  const [local, domain] = value.split("@");
  return domain ? `${local.slice(0, 2)}***@${domain}` : "Identity restricted";
}