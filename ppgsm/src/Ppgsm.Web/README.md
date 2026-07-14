# PPGSM Web

Production React and TypeScript frontend for the PPGSM API. The root static prototype is independent and is not used at runtime.

## Runtime modes

Live mode uses MSAL authorization code flow with PKCE. Configure the public SPA client ID, tenant authority, and exactly one delegated API scope through `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY`, and `VITE_API_SCOPE`. Missing client ID or scope fails before the first live request. Each API request uses `acquireTokenSilent` and sends `Authorization: Bearer <access-token>`; interaction-required expiry triggers an MSAL redirect. Logout uses `logoutRedirect`.

The browser stores only the MSAL session cache in `sessionStorage`. It never receives or stores downstream Power Platform tokens. Those tokens and delegated/app-only collector credentials remain server-side. BFF mode and `/auth/*` calls were removed because the current API does not implement them.

`VITE_DATA_ADAPTER=mock` is an explicit local review mode. It displays a persistent `Hypothetical PoC scenario · review only` label. It must not be set in a production build.

## API contract assumptions

The existing API currently supports customer creation and snapshot request/list/detail/evidence. The frontend calls `POST /api/v1/customers/{customerId}/snapshots` with `Idempotency-Key` and handles both `202` creation and `200` replay through the same typed response.

The authenticated shell needs one additive API endpoint:

```text
GET /api/v1/session/workspace
```

It must derive the user and available customer contexts from the bearer identity and server-side membership and return the `WorkspaceData` contract in `src/domain.ts`, including `capabilities`. It must not accept a tenant ID from a client header. Until this exists, live mode shows an API error and does not fall back to mock data.

The exact scope assumption is `api://<API-APPLICATION-CLIENT-ID>/ppgsm.read`. The API must validate signature, issuer for the configured tenant policy, audience equal to its application ID URI, expiry/not-before, and delegated `scp` containing `ppgsm.read`. The UI expects server-derived display name, application role, customer memberships, and capabilities; it does not authorize from browser claims. No Power Platform scope belongs in the SPA token request.

Later journeys remain capability-gated by server-returned workspace capabilities pending the specification endpoints for consent, connections, findings, score, compare, exports, exceptions, remediation, and approvals. The unimplemented write controls remain fail-closed even in mock mode. UI presence is not a claim those endpoints work.

Enum JSON must use the string values represented in `src/domain.ts` (for example `Partial` and `AppOnly`). If ASP.NET remains on numeric enum serialization, publish OpenAPI-generated numeric unions or add `JsonStringEnumConverter` consistently before integration.

## Hosting contract

This project uses separate static hosting by default. `npm run build` emits immutable assets to `dist/`. Host them on Azure Static Web Apps, Front Door-backed storage, or a dedicated web container. Route `/api/*` to `Ppgsm.Api`, register `/auth` as an SPA redirect URI, and rewrite all other non-asset routes to `index.html`.

Do not copy `dist` into `Ppgsm.Api` until the API explicitly enables `UseStaticFiles` and SPA fallback. No such hosting behavior is claimed today.

## Local commands

```powershell
Copy-Item .env.example .env.local
# Set VITE_DATA_ADAPTER=mock only for local UX review.
npm install
npm run typecheck
npm test
npm run test:a11y
npm run test:e2e
npm run dev
```

The Vite development server proxies `/api` to `http://localhost:5080`. Change `vite.config.ts` if the local API uses another port.