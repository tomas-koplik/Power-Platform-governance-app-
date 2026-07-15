import type { ConsentCallbackInput, ConsentCallbackResult, ConsentUrl, CreateExportInput, DeletionRecord, DlpPolicyEvidence, EnvironmentEvidence, EvidenceDocument, EvidenceIndex, EvidenceMetadata, ExportJob, GovernanceAdapter, GovernanceException, ProjectedEvidence, RemediationEligibility, RemediationProposal, Snapshot, SnapshotComparison, StartSnapshotInput, TenantCapability, TenantConnection, TenantSettingEvidence, WorkspaceData } from "./domain";
import { mockWorkspace } from "./mockData";
import { createAuthRuntime, type AuthRuntime } from "./auth";

type ActionCapabilities = Pick<WorkspaceData["capabilities"], "exceptions" | "exports" | "approvals">;
const unavailableActions: ActionCapabilities = { exceptions: false, exports: false, approvals: false };

export class ApiProblem extends Error {
  constructor(message: string, readonly status: number, readonly correlationId?: string) {
    super(message);
  }
}

let authRuntime: Promise<AuthRuntime> | undefined;
const runtimeConfig = window.__PPGSM_CONFIG__ ?? {};
const apiBaseUrl = (runtimeConfig.apiBaseUrl ?? import.meta.env.VITE_API_BASE_URL ?? "").replace(/\/$/, "");

export async function logout(): Promise<void> {
  await (authRuntime ??= createAuthRuntime()).then((auth) => auth.logout());
}

export async function request<T>(path: string, init?: RequestInit, auth = authRuntime ??= createAuthRuntime()): Promise<T> {
  const accessToken = await (await auth).getAccessToken();
  const response = await fetch(`${apiBaseUrl}${path}`, { ...init, headers: { Accept: "application/json", "Content-Type": "application/json", Authorization: `Bearer ${accessToken}`, ...init?.headers } });
  if (!response.ok) {
    const problem = await response.json().catch(() => ({})) as { title?: string; detail?: string; correlationId?: string };
    throw new ApiProblem(problem.detail ?? problem.title ?? `Request failed (${response.status})`, response.status, problem.correlationId ?? response.headers.get("X-Correlation-ID") ?? undefined);
  }
  return response.json() as Promise<T>;
}

async function download(path: string, filename: string, auth = authRuntime ??= createAuthRuntime()): Promise<void> {
  const accessToken = await (await auth).getAccessToken();
  const response = await fetch(`${apiBaseUrl}${path}`, { headers: { Accept: "application/octet-stream", Authorization: `Bearer ${accessToken}` } });
  if (!response.ok) throw new ApiProblem(`Download failed (${response.status})`, response.status, response.headers.get("X-Correlation-ID") ?? undefined);
  const url = URL.createObjectURL(await response.blob());
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  link.click();
  URL.revokeObjectURL(url);
}

class LiveAdapter implements GovernanceAdapter {
  readonly kind = "live" as const;
  capabilities: ActionCapabilities = unavailableActions;

  async loadWorkspace(signal?: AbortSignal): Promise<WorkspaceData> {
    const workspace = await request<WorkspaceData>("/api/v1/session/workspace", { signal });
    workspace.findings = workspace.findings.map((finding) => ({ ...finding, id: finding.id ?? finding.ruleId ?? finding.findingId ?? "Unknown finding" }));
    this.capabilities = workspace.capabilities;
    return workspace;
  }

  startSnapshot(input: StartSnapshotInput): Promise<Snapshot> {
    return request<Snapshot>(`/api/v1/customers/${input.customerId}/snapshots`, {
      method: "POST",
      headers: { "Idempotency-Key": input.idempotencyKey },
      body: JSON.stringify({ mode: input.mode, sections: input.sections, environmentIds: input.environmentIds }),
    });
  }

  getConsentUrl(customerId: string) { return request<ConsentUrl>(`/api/v1/customers/${customerId}/onboarding/consent-url`, { method: "POST" }); }
  submitConsentCallback(input: ConsentCallbackInput) { return request<ConsentCallbackResult>("/api/v1/onboarding/consent-callback", { method: "POST", body: JSON.stringify(input) }); }
  getConnection(customerId: string) { return request<TenantConnection>(`/api/v1/customers/${customerId}/connection`); }
  getTenantCapabilities(customerId: string) { return request<TenantCapability[]>(`/api/v1/customers/${customerId}/capabilities`); }
  revokeConnection(customerId: string) { return request<TenantConnection>(`/api/v1/customers/${customerId}/connection/revoke`, { method: "POST" }); }
  reconsentConnection(customerId: string) { return request<TenantConnection>(`/api/v1/customers/${customerId}/connection/reconsent`, { method: "POST" }); }
  requestOffboarding(customerId: string, retentionExpiresAt: string) { return request<DeletionRecord>(`/api/v1/customers/${customerId}/offboarding`, { method: "POST", body: JSON.stringify({ retentionExpiresAt }) }); }
  approveOffboarding(customerId: string) { return request<DeletionRecord>(`/api/v1/customers/${customerId}/offboarding/approve`, { method: "POST" }); }
  getDeletion(customerId: string) { return request<DeletionRecord>(`/api/v1/customers/${customerId}/deletion`); }
  listSnapshots(customerId: string) { return request<Snapshot[]>(`/api/v1/customers/${customerId}/snapshots`); }
  listEvidence(customerId: string, snapshotId: string, page: number, pageSize: number) { return request<EvidenceIndex>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/evidence?page=${page}&pageSize=${pageSize}`); }
  getTenantSettings(customerId: string, snapshotId: string) { return request<ProjectedEvidence<TenantSettingEvidence>>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/evidence/tenant-settings`); }
  getEnvironments(customerId: string, snapshotId: string) { return request<ProjectedEvidence<EnvironmentEvidence>>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/evidence/environments`); }
  getDlpPolicies(customerId: string, snapshotId: string) { return request<ProjectedEvidence<DlpPolicyEvidence>>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/evidence/dlp-policies`); }
  getEvidence(customerId: string, snapshotId: string, evidenceId: string, raw = false) { return request<EvidenceDocument>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/evidence/${evidenceId}?raw=${raw}`); }
  compareSnapshots(customerId: string, baselineSnapshotId: string, currentSnapshotId: string) { return request<SnapshotComparison>(`/api/v1/customers/${customerId}/comparisons`, { method: "POST", body: JSON.stringify({ baselineSnapshotId, currentSnapshotId }) }); }
  createExport(customerId: string, input: CreateExportInput) { return request<ExportJob>(`/api/v1/customers/${customerId}/exports`, { method: "POST", body: JSON.stringify(input) }); }
  getExport(customerId: string, exportJobId: string) { return request<ExportJob>(`/api/v1/customers/${customerId}/exports/${exportJobId}`); }
  downloadExport(customerId: string, exportJobId: string) { return download(`/api/v1/customers/${customerId}/exports/${exportJobId}/download`, `ppgsm-evidence-${exportJobId}.json`); }
  createException(customerId: string, findingId: string, reason: string, expiresAt: string) { return request<GovernanceException>(`/api/v1/customers/${customerId}/findings/${findingId}/exceptions`, { method: "POST", body: JSON.stringify({ reason, expiresAt }) }); }
  getRemediationEligibility(customerId: string, snapshotId: string, findingId: string, evidence: EvidenceMetadata, evidenceValidUntil: string) {
    const query = new URLSearchParams({ evidenceHash: evidence.contentHash, evidenceCapturedAt: evidence.capturedAt, evidenceValidUntil });
    return request<RemediationEligibility>(`/api/v1/customers/${customerId}/snapshots/${snapshotId}/findings/${findingId}/remediation-eligibility?${query}`);
  }
  createRemediationProposal(customerId: string, input: { findingId: string; snapshotId: string; templateId: string; parameters: Record<string, unknown>; evidenceHash: string; targetScope: string; evidenceCapturedAt: string; evidenceValidUntil: string }) { return request<RemediationProposal>(`/api/v1/customers/${customerId}/remediation/proposals`, { method: "POST", body: JSON.stringify(input) }); }
  reviewRemediationProposal(customerId: string, proposalId: string, approved: boolean, latestSnapshotId: string, reason?: string) { return request<RemediationProposal>(`/api/v1/customers/${customerId}/remediation/proposals/${proposalId}/review`, { method: "POST", body: JSON.stringify({ approved, reason, latestSnapshotId }) }); }
}

class MockAdapter implements GovernanceAdapter {
  readonly kind = "mock" as const;
  capabilities: ActionCapabilities = unavailableActions;

  async loadWorkspace(): Promise<WorkspaceData> {
    await new Promise((resolve) => window.setTimeout(resolve, 120));
    const workspace = structuredClone(mockWorkspace);
    this.capabilities = workspace.capabilities;
    return workspace;
  }

  async startSnapshot(input: StartSnapshotInput): Promise<Snapshot> {
    await new Promise((resolve) => window.setTimeout(resolve, 250));
    return { snapshotId: crypto.randomUUID(), customerId: input.customerId, status: "Queued", mode: input.mode, requestedAt: new Date().toISOString(), sections: [] };
  }

  private unavailable(): never { throw new ApiProblem("This operation is unavailable in the hypothetical mock scenario.", 503); }
  async getConsentUrl(): Promise<ConsentUrl> { return this.unavailable(); }
  async submitConsentCallback(): Promise<ConsentCallbackResult> { return this.unavailable(); }
  async getConnection(): Promise<TenantConnection> { return this.unavailable(); }
  async getTenantCapabilities(): Promise<TenantCapability[]> { return this.unavailable(); }
  async revokeConnection(): Promise<TenantConnection> { return this.unavailable(); }
  async reconsentConnection(): Promise<TenantConnection> { return this.unavailable(); }
  async requestOffboarding(): Promise<DeletionRecord> { return this.unavailable(); }
  async approveOffboarding(): Promise<DeletionRecord> { return this.unavailable(); }
  async getDeletion(): Promise<DeletionRecord> { return this.unavailable(); }
  async listSnapshots(): Promise<Snapshot[]> { return structuredClone(mockWorkspace.snapshots); }
  async listEvidence(): Promise<EvidenceIndex> { return this.unavailable(); }
  async getTenantSettings(): Promise<ProjectedEvidence<TenantSettingEvidence>> { return this.unavailable(); }
  async getEnvironments(): Promise<ProjectedEvidence<EnvironmentEvidence>> { return this.unavailable(); }
  async getDlpPolicies(): Promise<ProjectedEvidence<DlpPolicyEvidence>> { return this.unavailable(); }
  async getEvidence(): Promise<EvidenceDocument> { return this.unavailable(); }
  async compareSnapshots(): Promise<SnapshotComparison> { return this.unavailable(); }
  async createExport(): Promise<ExportJob> { return this.unavailable(); }
  async getExport(): Promise<ExportJob> { return this.unavailable(); }
  async downloadExport(): Promise<void> { return this.unavailable(); }
  async createException(): Promise<GovernanceException> { return this.unavailable(); }
  async getRemediationEligibility(): Promise<RemediationEligibility> { return this.unavailable(); }
  async createRemediationProposal(): Promise<RemediationProposal> { return this.unavailable(); }
  async reviewRemediationProposal(): Promise<RemediationProposal> { return this.unavailable(); }
}

const adapterMode = runtimeConfig.dataAdapter ?? import.meta.env.VITE_DATA_ADAPTER;
if (import.meta.env.PROD && import.meta.env.MODE !== "demo" && adapterMode === "mock") throw new Error("Mock data adapter is prohibited in production.");
export const adapter: GovernanceAdapter & { capabilities: ActionCapabilities } = adapterMode === "mock" ? new MockAdapter() : new LiveAdapter();