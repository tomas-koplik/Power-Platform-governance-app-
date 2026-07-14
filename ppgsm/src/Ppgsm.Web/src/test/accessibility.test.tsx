import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

vi.mock("../api", async () => {
  const { mockWorkspace } = await import("../mockData");
  return {
    ApiProblem: class extends Error {},
    adapter: {
      kind: "mock",
      capabilities: { exceptions: false, exports: false, approvals: false },
      loadWorkspace: async () => mockWorkspace,
      startSnapshot: vi.fn(),
    },
  };
});

import { App } from "../App";

describe("application accessibility structure", () => {
  it("exposes landmarks and a focused page heading", async () => {
    render(<QueryClientProvider client={new QueryClient()}><MemoryRouter initialEntries={["/findings"]}><App /></MemoryRouter></QueryClientProvider>);
    expect(await screen.findByRole("heading", { level: 1, name: "Findings" })).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Primary navigation" })).toBeInTheDocument();
    expect(screen.getByRole("main")).toHaveAttribute("id", "main-content");
  });

  it("labels observed, interpreted, and proposed content separately", async () => {
    render(<QueryClientProvider client={new QueryClient()}><MemoryRouter initialEntries={["/findings"]}><App /></MemoryRouter></QueryClientProvider>);
    expect(await screen.findByRole("heading", { name: "Observed evidence" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Interpretation" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Proposed action" })).toBeInTheDocument();
  });

  it("keeps simulated and executed remediation states distinct", async () => {
    render(<QueryClientProvider client={new QueryClient()}><MemoryRouter initialEntries={["/remediation"]}><App /></MemoryRouter></QueryClientProvider>);
    expect(await screen.findByRole("heading", { name: "Simulated action" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Executed state" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Submit server proposal/ })).toBeDisabled();
  });
});