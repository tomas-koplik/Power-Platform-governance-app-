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
}));

vi.mock("../api", async () => {
  const { mockWorkspace } = await import("../mockData");
  return {
    ApiProblem: class extends Error {},
    logout: vi.fn(),
    adapter: {
      kind: "live",
      capabilities: { exceptions: false, exports: false, approvals: false },
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
      listSnapshots: vi.fn(),
      compareSnapshots: vi.fn(),
      createExport: vi.fn(),
      getExport: vi.fn(),
      downloadExport: vi.fn(),
      createException: vi.fn(),
      startSnapshot: vi.fn(),
      getEvidence: vi.fn(),
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