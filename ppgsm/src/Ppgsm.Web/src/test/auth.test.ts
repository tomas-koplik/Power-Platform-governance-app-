import { InteractionRequiredAuthError, type AccountInfo, type AuthenticationResult, type IPublicClientApplication } from "@azure/msal-browser";
import { describe, expect, it, vi } from "vitest";
import { createAuthRuntime, type BrowserAuthConfig } from "../auth";
import { request } from "../api";

const config: BrowserAuthConfig = {
  clientId: "web-client-id",
  authority: "https://login.microsoftonline.com/tenant-id",
  apiScope: "api://api-client-id/ppgsm.read",
  redirectUri: "http://localhost/auth",
};
const account = { homeAccountId: "home", environment: "login.microsoftonline.com", tenantId: "tenant-id", username: "reader@example.com", localAccountId: "local" } as AccountInfo;

function client(overrides: Partial<IPublicClientApplication> = {}): IPublicClientApplication {
  return {
    initialize: vi.fn().mockResolvedValue(undefined),
    handleRedirectPromise: vi.fn().mockResolvedValue(null),
    getActiveAccount: vi.fn().mockReturnValue(account),
    getAllAccounts: vi.fn().mockReturnValue([account]),
    setActiveAccount: vi.fn(),
    loginRedirect: vi.fn().mockResolvedValue(undefined),
    logoutRedirect: vi.fn().mockResolvedValue(undefined),
    acquireTokenSilent: vi.fn().mockResolvedValue({ accessToken: "api-token" } as AuthenticationResult),
    acquireTokenRedirect: vi.fn().mockResolvedValue(undefined),
    ...overrides,
  } as unknown as IPublicClientApplication;
}

describe("browser authentication adapter", () => {
  it("requests only the explicit API scope", async () => {
    const msal = client();
    const runtime = await createAuthRuntime(config, msal);

    await expect(runtime.getAccessToken()).resolves.toBe("api-token");
    expect(msal.acquireTokenSilent).toHaveBeenCalledWith({ account, scopes: [config.apiScope] });
  });

  it("uses an MSAL redirect when an expired session requires interaction", async () => {
    const msal = client({ acquireTokenSilent: vi.fn().mockRejectedValue(new InteractionRequiredAuthError("interaction_required")) });
    const runtime = await createAuthRuntime(config, msal);

    await expect(runtime.getAccessToken()).rejects.toThrow("Authentication refresh redirect started.");
    expect(msal.acquireTokenRedirect).toHaveBeenCalledWith({ account, scopes: [config.apiScope] });
  });

  it("attaches the bearer token without persisting downstream tokens", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(JSON.stringify({ ok: true }), { status: 200 })));
    const auth = Promise.resolve({ login: vi.fn(), logout: vi.fn(), getAccessToken: vi.fn().mockResolvedValue("api-token") });

    await request("/api/v1/session/workspace", undefined, auth);

    expect(fetch).toHaveBeenCalledWith("/api/v1/session/workspace", expect.objectContaining({ headers: expect.objectContaining({ Authorization: "Bearer api-token" }) }));
    expect(sessionStorage.length).toBe(0);
  });
});