import { InteractionRequiredAuthError, PublicClientApplication, type Configuration, type IPublicClientApplication } from "@azure/msal-browser";

export interface AuthRuntime {
  login(): Promise<void>;
  logout(): Promise<void>;
  getAccessToken(): Promise<string>;
}

export interface BrowserAuthConfig {
  clientId: string;
  authority: string;
  apiScope: string;
  redirectUri: string;
}

export function readBrowserAuthConfig(): BrowserAuthConfig {
  const runtimeConfig = window.__PPGSM_CONFIG__ ?? {};
  const clientId = runtimeConfig.entraClientId ?? import.meta.env.VITE_ENTRA_CLIENT_ID;
  const apiScope = runtimeConfig.apiScope ?? import.meta.env.VITE_API_SCOPE;
  const missing = [!clientId && "VITE_ENTRA_CLIENT_ID", !apiScope && "VITE_API_SCOPE"].filter(Boolean);
  if (!clientId || !apiScope) throw new Error(`Live mode requires Entra configuration: ${missing.join(", ")}.`);
  return {
    clientId,
    apiScope,
    authority: runtimeConfig.entraAuthority ?? import.meta.env.VITE_ENTRA_AUTHORITY ?? "https://login.microsoftonline.com/organizations",
    redirectUri: `${window.location.origin}/auth`,
  };
}

function createMsalConfiguration(config: BrowserAuthConfig): Configuration {
  return {
    auth: {
      clientId: config.clientId,
      authority: config.authority,
      redirectUri: config.redirectUri,
      postLogoutRedirectUri: window.location.origin,
    },
    cache: { cacheLocation: "sessionStorage" },
  };
}

export async function createAuthRuntime(
  config = readBrowserAuthConfig(),
  client: IPublicClientApplication = new PublicClientApplication(createMsalConfiguration(config)),
): Promise<AuthRuntime> {
  await client.initialize();
  const redirectResult = await client.handleRedirectPromise();
  if (redirectResult?.account) client.setActiveAccount(redirectResult.account);

  const request = () => ({ scopes: [config.apiScope], account: client.getActiveAccount() ?? client.getAllAccounts()[0] });
  return {
    login: async () => { await client.loginRedirect({ scopes: [config.apiScope] }); },
    logout: async () => { await client.logoutRedirect(); },
    getAccessToken: async () => {
      const tokenRequest = request();
      if (!tokenRequest.account) {
        await client.loginRedirect({ scopes: [config.apiScope] });
        throw new Error("Authentication redirect started.");
      }
      try {
        return (await client.acquireTokenSilent({ ...tokenRequest, account: tokenRequest.account })).accessToken;
      } catch (error) {
        if (error instanceof InteractionRequiredAuthError) {
          await client.acquireTokenRedirect({ ...tokenRequest, account: tokenRequest.account });
          throw new Error("Authentication refresh redirect started.");
        }
        throw error;
      }
    },
  };
}