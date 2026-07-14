import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";

test("R1-R3 portfolio, snapshot coverage, and findings journey", async ({ page }) => {
  await page.goto("/portfolio");
  await expect(page.getByText("Hypothetical PoC scenario · review only")).toBeVisible();
  await page.getByRole("link", { name: /Run snapshot/ }).click();
  await expect(page.getByRole("heading", { name: "Snapshot progress and coverage" })).toBeVisible();
  await expect(page.getByText("Skipped", { exact: true }).first()).toBeVisible();
  await page.getByRole("link", { name: "Findings" }).click();
  await page.getByRole("button", { name: /Apps are shared tenant-wide/ }).click();
  await expect(page.getByRole("heading", { name: "Observed evidence" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Interpretation" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Proposed action" })).toBeVisible();
});

test("R4 evidence and DLP map remain keyboard operable", async ({ page }) => {
  await page.goto("/evidence");
  await expect(page.getByLabel("Snapshot")).toContainText("Partial");
  await expect(page.getByRole("heading", { name: "Tenant settings" })).toBeVisible();
  await expect(page.getByText("Snapshot evidence index/list endpoint not available in the current API")).toBeVisible();
  await page.getByRole("tab", { name: "Environments" }).press("Enter");
  await expect(page.getByRole("heading", { name: "Northstar Default" })).toBeVisible();
  await page.getByRole("link", { name: "DLP map" }).click();
  await page.getByRole("button", { name: /Default guardrail/ }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("heading", { name: "Default guardrail" })).toBeVisible();
});

test("R6 capability gating does not imply unsupported execution and passes axe", async ({ page }) => {
  await page.goto("/remediation");
  await expect(page.getByRole("heading", { name: "Simulated action" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Executed state" })).toBeVisible();
  await expect(page.getByRole("button", { name: /Submit server proposal/ })).toBeDisabled();
  await expect(page.getByText("The browser never accepts or constructs script text.")).toBeVisible();
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations).toEqual([]);
});