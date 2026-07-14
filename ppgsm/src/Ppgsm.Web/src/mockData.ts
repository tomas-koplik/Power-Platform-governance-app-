import type { WorkspaceData } from "./domain";

const now = "2026-07-14T09:42:00Z";
const previous = "2026-06-30T08:15:00Z";

export const mockWorkspace: WorkspaceData = {
  capabilities: {
    portfolio: true, onboarding: false, connections: false, snapshots: true, evidence: true,
    findings: true, score: true, dlp: true, compare: false, exports: false,
    exceptions: false, remediation: false, approvals: false,
  },
  session: { displayName: "Alex Morgan", role: "Consultant", authMode: "mock" },
  customers: [{ customerId: "11111111-1111-4111-8111-111111111111", name: "Northstar Manufacturing", entraTenantId: "22222222-2222-4222-8222-222222222222", region: "West Europe", status: "Active", connection: { mode: "AppOnly", status: "Degraded", detail: "Reader RBAC coverage is unverified; app-only collection is incomplete." } }],
  snapshots: [
    { snapshotId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", customerId: "11111111-1111-4111-8111-111111111111", status: "Partial", mode: "AppOnly", requestedAt: now, startedAt: now, completedAt: "2026-07-14T09:49:00Z", sections: [
      ["tenantSettings", "Partial", 42], ["environments", "Partial", 4], ["dlpPolicies", "Partial", 2], ["flows", "Partial", 280], ["environmentGroups", "Skipped", 0], ["tenantIsolation", "Skipped", 0]
    ].map(([sectionKey, coverage, itemCount], index) => ({ snapshotSectionId: `00000000-0000-4000-8000-00000000000${index}`, snapshotId: "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", sectionKey: String(sectionKey), coverage: coverage as "Partial" | "Skipped", itemCount: Number(itemCount), reason: coverage === "Partial" ? "App-only Reader RBAC and endpoint coverage are unverified." : "Collector is unavailable in the app-only PoC path.", recordedAt: now })) },
    { snapshotId: "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", customerId: "11111111-1111-4111-8111-111111111111", status: "Completed", mode: "Delegated", requestedAt: previous, completedAt: previous, sections: [] },
  ],
  score: { overall: 0, tier: "Not evaluated", evaluated: 0, total: 4, confidence: "Partial", areas: {} },
  settings: [
    { key: "disableEnvironmentCreationByNonAdminUsers", label: "Restrict production environment creation", value: "Enabled", source: "API", coverage: "Full", meaning: "Only administrators can create production environments." },
    { key: "disableTrialEnvironmentCreationByNonAdminUsers", label: "Restrict trial environments", value: "Disabled", source: "API", coverage: "Full", meaning: "Non-admin users can currently create trial environments." },
    { key: "defaultEnvironmentGovernance", label: "Default environment posture", value: "Needs attention", source: "Derived", coverage: "Partial", meaning: "Composite interpretation from environment, DLP, and sharing evidence." },
  ],
  environments: [
    { id: "default", name: "Northstar Default", type: "Default", region: "Europe", managed: false, dlpCovered: true, apps: 86, flows: 142, ownerUpn: "maria.santos@northstar.example", risk: "High" },
    { id: "sales", name: "Sales Production", type: "Production", region: "Europe", managed: true, dlpCovered: true, apps: 24, flows: 51, ownerUpn: "liam.chen@northstar.example", risk: "Controlled" },
    { id: "ops", name: "Operations Production", type: "Production", region: "Europe", managed: false, dlpCovered: true, apps: 37, flows: 74, ownerUpn: "ana.kovac@northstar.example", risk: "Review" },
    { id: "trial", name: "Innovation Trial", type: "Trial", region: "United States", managed: false, dlpCovered: false, apps: 9, flows: 13, risk: "High" },
  ],
  findings: [
    { id: "PPG-SHR-001", findingId: "10000000-0000-4000-8000-000000000001", title: "Apps are shared tenant-wide in the default environment", area: "Sharing and ownership", severity: "Critical", status: "NotEvaluated", scope: "Northstar Default", observed: "Four apps use the Everyone principal; two include Dataverse connections.", interpretation: "Advisory only: no customer control profile exists, so this observation is not a pass or fail.", proposedAction: "Review sharing intent and define an approved control profile before generating any change.", ownerUpn: "maria.santos@northstar.example", remediation: "Script" },
    { id: "PPG-DLP-002", findingId: "10000000-0000-4000-8000-000000000002", title: "One environment is outside DLP coverage", area: "DLP policies", severity: "High", status: "NotEvaluated", scope: "Innovation Trial", observed: "Three of four observed environments map to at least one of the two observed DLP policies.", interpretation: "Advisory only: no customer control profile exists, so uncovered scope is not a compliance failure.", proposedAction: "Confirm intended policy scope, then model extension of the tenant baseline for review.", remediation: "Script" },
    { id: "PPG-ISO-001", title: "Outbound tenant isolation was not evaluated", area: "Tenant isolation", severity: "High", status: "NotEvaluated", scope: "Tenant", observed: "Collector failed before an authoritative isolation value was captured.", interpretation: "No compliance conclusion can be drawn from this snapshot.", proposedAction: "Run a delegated refresh after the endpoint PoC is confirmed.", remediation: "Manual" },
    { id: "PPG-TEN-006", title: "Production environment creation is restricted", area: "Tenant settings", severity: "Low", status: "NotEvaluated", scope: "Tenant", observed: "disableEnvironmentCreationByNonAdminUsers is true.", interpretation: "Advisory only: a customer control profile has not been configured.", proposedAction: "Confirm the intended environment creation policy before evaluating this observation.", remediation: "Informational" },
  ],
  dlpPolicies: [
    { id: "baseline", name: "Tenant baseline", scope: "Tenant", environments: ["sales", "ops"], business: ["Microsoft 365 Users", "SharePoint", "Dataverse"], nonBusiness: ["RSS", "Twitter"], blocked: ["FTP", "Consumer Gmail"] },
    { id: "default", name: "Default guardrail", scope: "Default environment", environments: ["default"], business: ["Microsoft 365 Users"], nonBusiness: ["Teams"], blocked: ["HTTP", "SQL Server", "Dropbox"] },
  ],
};