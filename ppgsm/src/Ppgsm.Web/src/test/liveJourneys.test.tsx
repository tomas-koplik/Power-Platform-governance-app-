import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

const mocks = vi.hoisted(() => ({
  callback: vi.fn(),
  workspace: vi.fn(),
  connection: vi.fn(),
  capabilities: vi.fn(),
  revoke: vi.fn(),
  reconsent: vi.fn(),
  listSnapshots: vi.fn(),
  listEvidence: vi.fn(),
  getTenantSettings: vi.fn(),
  getEnvironments: vi.fn(),
  getDlpPolicies: vi.fn(),
  getEvidence: vi.fn(),
  compareSnapshots: vi.fn(),
  createExport: vi.fn(),
  getExport: vi.fn(),
  downloadExport: vi.fn(),
  eligibility: vi.fn(),
  createProposal: vi.fn(),
  reviewProposal: vi.fn(),
}));

vi.mock("../api", async () => {
  const { mockWorkspace } = await import("../mockData");
  return {
    ApiProblem: class extends Error {},
    logout: vi.fn(),
    adapter: {
      kind: "live",
      capabilities: { exceptions: false, exports: false, approvals: false, offboarding: false },
      loadWorkspace: mocks.workspace,
      submitConsentCallback: mocks.callback,
      getConnection: mocks.connection,
      getTenantCapabilities: mocks.capabilities,
      getDeletion: vi.fn(),
      getConsentUrl: vi.fn(),
      revokeConnection: mocks.revoke,
      reconsentConnection: mocks.reconsent,
      requestOffboarding: vi.fn(),
      approveOffboarding: vi.fn(),
      listSnapshots: mocks.listSnapshots,
      listEvidence: mocks.listEvidence,
      getTenantSettings: mocks.getTenantSettings,
      getEnvironments: mocks.getEnvironments,
      getDlpPolicies: mocks.getDlpPolicies,
      compareSnapshots: mocks.compareSnapshots,
      createExport: mocks.createExport,
      getExport: mocks.getExport,
      downloadExport: mocks.downloadExport,
      createException: vi.fn(),
      startSnapshot: vi.fn(),
      getEvidence: mocks.getEvidence,
      getRemediationEligibility: mocks.eligibility,
      createRemediationProposal: mocks.createProposal,
      reviewRemediationProposal: mocks.reviewProposal,
    },
    __workspace: mockWorkspace,
  };
});

import { App } from "../App";
import { mockWorkspace } from "../mockData";

function renderAt(path: string) {
  window.history.replaceState({}, "", path);
  return render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><MemoryRouter initialEntries={[path]}><App /></MemoryRouter></QueryClientProvider>);
}

describe("live onboarding journeys", () => {
  beforeEach(() => {
    mocks.workspace.mockResolvedValue({ ...mockWorkspace, capabilities: { ...mockWorkspace.capabilities, onboarding: true, connections: true } });
    mocks.callback.mockReset();
    mocks.connection.mockResolvedValue({ connectionId: "connection", customerId: mockWorkspace.customers[0].customerId, mode: "Delegated", status: "Active", lastValidatedAt: "2026-07-14T09:42:00Z" });
    mocks.capabilities.mockResolvedValue([]);
    mocks.revoke.mockResolvedValue({ connectionId: "connection", customerId: mockWorkspace.customers[0].customerId, mode: "Delegated", status: "Revoked" });
    mocks.reconsent.mockImplementation(() => new Promise(() => undefined));
  });

  it("does not activate a denied callback", async () => {
    mocks.callback.mockRejectedValue(new Error("Administrator denied consent."));
    renderAt("/onboarding/callback?state=signed&error=access_denied");
    expect(await screen.findByRole("heading", { name: "Consent was not activated" })).toBeInTheDocument();
  });

  it.each([
    ["Degraded", false, "Degraded connection"],
    ["Active", true, "Active connection"],
  ])("renders %s verification without overstating evidence", async (status, verified, heading) => {
    mocks.callback.mockResolvedValue({ customerId: "customer", entraTenantId: "tenant", operation: "delegated-admin-consent", status, enterpriseApplicationPresent: true, delegatedScopeGranted: true, powerPlatformRoleAssigned: verified, detail: verified ? "All probes verified." : "Power Platform role is unavailable." });
    renderAt("/onboarding/callback?state=signed&tenant=tenant&admin_consent=True");
    expect(await screen.findByRole("heading", { name: heading })).toBeInTheDocument();
    expect(screen.getByText(verified ? "Verified" : "Unavailable", { exact: true })).toBeInTheDocument();
  });

  it("keeps consent and connection operations gated by server capabilities", async () => {
    mocks.workspace.mockResolvedValue(mockWorkspace);
    renderAt("/onboarding");
    expect(await screen.findByRole("button", { name: /Continue to administrator consent/ })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Revoke connection" })).toBeDisabled();
  });

  it("calls authenticated revoke and reconsent routes without administrator input", async () => {
    const user = userEvent.setup();
    renderAt("/onboarding");
    await user.click(await screen.findByRole("button", { name: "Revoke connection" }));
    expect(mocks.revoke).toHaveBeenCalledWith(mockWorkspace.customers[0].customerId);
    await user.click(screen.getByRole("button", { name: "Reconsent" }));
    expect(mocks.reconsent).toHaveBeenCalledWith(mockWorkspace.customers[0].customerId);
    expect(screen.queryByLabelText(/administrator.*object/i)).not.toBeInTheDocument();
  });
});

describe("live evidence, export, and remediation journeys", () => {
  const evidence = {
    evidenceId: "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee",
    section: "tenantSettings",
    mediaType: "application/json",
    contentHash: "sha256:verified",
    capturedAt: "2026-07-14T09:42:00Z",
    confidence: "Verified",
    pageNumber: 1,
    collectorId: "tenant-settings",
    collectorVersion: "1.0.0",
    parserSchemaVersion: "1",
    completenessRationale: "All published fields were captured.",
  };

  beforeEach(() => {
    vi.clearAllMocks();
    mocks.workspace.mockResolvedValue({ ...mockWorkspace, capabilities: { ...mockWorkspace.capabilities, evidence: true, dlp: true, compare: true, exports: true } });
    mocks.listSnapshots.mockResolvedValue(mockWorkspace.snapshots);
    mocks.listEvidence.mockResolvedValue({ snapshotId: mockWorkspace.snapshots[0].snapshotId, coverage: "Full", confidence: "Verified", evidenceIds: [evidence.evidenceId], items: [evidence], page: 1, pageSize: 20, total: 1 });
    mocks.getTenantSettings.mockResolvedValue({ snapshotId: mockWorkspace.snapshots[0].snapshotId, state: "Complete", coverage: "Full", confidence: "Verified", evidenceIds: [evidence.evidenceId], items: [{ key: "trialEnvironmentsDisabled", value: true, evidenceId: evidence.evidenceId }], detail: "Complete." });
    mocks.getEnvironments.mockResolvedValue({ snapshotId: mockWorkspace.snapshots[0].snapshotId, state: "Complete", coverage: "Full", confidence: "Verified", evidenceIds: [evidence.evidenceId], items: [], detail: "Complete." });
    mocks.getDlpPolicies.mockResolvedValue({ snapshotId: mockWorkspace.snapshots[0].snapshotId, state: "Complete", coverage: "Full", confidence: "Verified", evidenceIds: [evidence.evidenceId], items: [], detail: "Complete." });
    mocks.getEvidence.mockResolvedValue({ rawEvidenceReferenceId: evidence.evidenceId, sectionKey: "tenantSettings", mediaType: "application/json", content: { trialEnvironmentsDisabled: "[REDACTED]" } });
    mocks.compareSnapshots.mockResolvedValue({ customerId: mockWorkspace.customers[0].customerId, baselineSnapshotId: mockWorkspace.snapshots[1].snapshotId, currentSnapshotId: mockWorkspace.snapshots[0].snapshotId, addedFindings: 1, resolvedFindings: 0, changedFindings: 1 });
  });

  it("drills through normalized evidence to the redacted source document", async () => {
    const user = userEvent.setup();
    renderAt("/evidence");
    await user.click(await screen.findByRole("button", { name: "Inspect source evidence" }));
    expect(await screen.findByRole("heading", { name: "Tenant settings" })).toBeInTheDocument();
    expect(screen.getByText(/REDACTED/)).toBeInTheDocument();
    expect(mocks.getEvidence).toHaveBeenCalledWith(mockWorkspace.customers[0].customerId, mockWorkspace.snapshots[0].snapshotId, evidence.evidenceId, false);
  });

  it("creates an export for the selected snapshot with the explicit format and PII contract", async () => {
    const user = userEvent.setup();
    mocks.createExport.mockResolvedValue({ exportJobId: "export", customerId: mockWorkspace.customers[0].customerId, snapshotId: mockWorkspace.snapshots[1].snapshotId, includesPii: false, format: "Json", status: "Queued", createdAt: "2026-07-14T10:00:00Z" });
    mocks.getExport.mockResolvedValue({ exportJobId: "export", customerId: mockWorkspace.customers[0].customerId, snapshotId: mockWorkspace.snapshots[1].snapshotId, includesPii: false, format: "Json", status: "Running", createdAt: "2026-07-14T10:00:00Z" });
    renderAt("/compare");
    await user.selectOptions(await screen.findByLabelText("Export snapshot"), mockWorkspace.snapshots[1].snapshotId);
    await user.click(screen.getByRole("button", { name: "Create JSON export" }));
    expect(mocks.createExport).toHaveBeenCalledWith(mockWorkspace.customers[0].customerId, { snapshotId: mockWorkspace.snapshots[1].snapshotId, format: "Json", includePii: false });
  });

  it("displays completed export download integrity and expiry", async () => {
    const user = userEvent.setup();
    mocks.createExport.mockResolvedValue({ exportJobId: "export", customerId: mockWorkspace.customers[0].customerId, snapshotId: mockWorkspace.snapshots[0].snapshotId, includesPii: false, format: "Json", status: "Completed", createdAt: "2026-07-14T10:00:00Z", artifactContentHash: "sha256:artifact", artifactContentLength: 2048, artifactMediaType: "application/json", downloadExpiresAt: "2099-07-14T11:00:00Z" });
    renderAt("/compare");
    await user.click(await screen.findByRole("button", { name: "Create JSON export" }));
    expect(await screen.findByText("sha256:artifact")).toBeInTheDocument();
    expect(screen.getByText("2,048 bytes")).toBeInTheDocument();
    expect(screen.getByText("application/json")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Download JSON" })).toBeEnabled();
  });

  it("gates remediation eligibility by membership and capability", async () => {
    mocks.workspace.mockResolvedValue({ ...mockWorkspace, session: { ...mockWorkspace.session, role: "Reader" }, capabilities: { ...mockWorkspace.capabilities, evidence: true, remediation: true, approvals: true } });
    renderAt("/remediation");
    expect(await screen.findByText("Customer administrator membership required")).toBeInTheDocument();
    expect(mocks.eligibility).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Create server proposal" })).toBeDisabled();
  });

  it("keeps proposal creation separate from independent approval", async () => {
    const user = userEvent.setup();
    const adminWorkspace = { ...mockWorkspace, session: { ...mockWorkspace.session, role: "CustomerAdmin" as const }, capabilities: { ...mockWorkspace.capabilities, evidence: true, remediation: true, approvals: true } };
    mocks.workspace.mockResolvedValue(adminWorkspace);
    mocks.eligibility.mockResolvedValue({ eligible: true, findingId: mockWorkspace.findings[0].findingId, snapshotId: mockWorkspace.snapshots[0].snapshotId, templateId: "tenant-settings.restrict-production-creation.v1", templateVersion: 1, allowedParameters: ["disabled"], evidenceCapturedAt: evidence.capturedAt, evidenceValidUntil: "2099-07-15T09:42:00Z", target: mockWorkspace.findings[0].scope, verification: "Recollect settings.", rollback: "Manual rollback required." });
    mocks.createProposal.mockResolvedValue({ proposalId: "proposal", customerId: mockWorkspace.customers[0].customerId, findingId: mockWorkspace.findings[0].findingId, snapshotId: mockWorkspace.snapshots[0].snapshotId, proposedBy: "subject-a", proposedAt: "2026-07-14T10:00:00Z", evidenceValidUntil: "2099-07-15T09:42:00Z", templateId: "tenant-settings.restrict-production-creation.v1", targetScope: mockWorkspace.findings[0].scope, verification: "Recollect settings.", rollback: "Manual rollback required.", status: "Proposed" });
    renderAt("/remediation");
    await user.click(await screen.findByRole("button", { name: "Create server proposal" }));
    expect(await screen.findByRole("heading", { name: "Proposal Proposed" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Approve as independent reviewer/ })).toBeDisabled();
    expect(screen.queryByRole("textbox", { name: /script/i })).not.toBeInTheDocument();
    expect(mocks.reviewProposal).not.toHaveBeenCalled();
  });
});