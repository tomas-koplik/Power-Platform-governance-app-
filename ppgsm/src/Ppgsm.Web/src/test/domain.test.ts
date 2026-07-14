import { describe, expect, it } from "vitest";
import { canViewPii, displayIdentity } from "../domain";

describe("role-aware identity display", () => {
  it("shows PII only to operational roles", () => {
    expect(canViewPii("CustomerAdmin")).toBe(true);
    expect(displayIdentity("maria.santos@northstar.example", "Consultant")).toBe("maria.santos@northstar.example");
  });

  it("pseudonymizes PII for readers and auditors", () => {
    expect(canViewPii("Auditor")).toBe(false);
    expect(displayIdentity("maria.santos@northstar.example", "Auditor")).toBe("ma***@northstar.example");
  });
});