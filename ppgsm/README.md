# PPGSM production foundation

This directory is an isolated .NET 8 production foundation for Power Platform Governance Snapshot Manager. The static workshop prototype in the repository root remains independent and unchanged.

## Projects

- `src/Ppgsm.Core`: tenant domain, immutable snapshot lifecycle, typed evidence, collector contracts, authorization, idempotency, and audit abstractions.
- `src/Ppgsm.Data`: EF Core 8 SQL Server mappings, tenant write guard, SQL session context interceptor, and Azure SQL RLS script.
- `src/Ppgsm.Api`: minimal REST API, OpenAPI, problem details, ETags, correlation IDs, local stores, membership authorization, and audit middleware.
- `src/Ppgsm.Worker`: queue-neutral worker host placeholder. It does not collect tenant data.
- `src/Ppgsm.Collectors`: assembly placeholder only. Collector implementations belong to the Backend Collectors Engineer.
- `tests/Ppgsm.Core.Tests`: focused executable domain and tenant-isolation tests.
- `tests/Ppgsm.Collectors.Tests`: skipped fixture contract placeholder for the collector handoff.

## Local development

Prerequisite: .NET 8 SDK. Package versions are pinned in project files and the SDK is pinned by `global.json` with patch roll-forward.

```powershell
dotnet restore .\Ppgsm.sln
dotnet test .\Ppgsm.sln
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\Ppgsm.Api\Ppgsm.Api.csproj
```

In Development, the API uses `X-Development-Subject` only as a local identity selector and defaults to `local-consultant`. It does not accept a customer ID from a header. Customer access is always resolved from the server-side membership store. Swagger is available at `/swagger` after launch.

Outside Development, startup validates bearer tokens and requires live onboarding verification configuration. Development intentionally uses an unavailable verifier and cannot activate a connection.

## Production consent verification

The authenticated API initiates onboarding without accepting a customer administrator object ID from the browser. The signed, one-time state binds the initiating PPGSM subject and customer tenant. After Entra redirects to the configured authenticated SPA/BFF callback, that client sends the consent result to `POST /api/v1/onboarding/consent-callback` with its PPGSM bearer token. The API derives the customer administrator exclusively from validated `tid` and `oid` claims. The callback tenant, authenticated claim tenant, signed customer tenant, Graph evidence tenant, local enterprise application, and verified administrator must all agree.

Configure `Onboarding:Verification` with:

- `ClientApplicationId`: the PPGSM web/API application ID whose local enterprise application must exist and be enabled in the customer tenant.
- `DelegatedResourceApplicationId`: the resource service principal application ID for the expected delegated grant.
- `ExpectedDelegatedScopes`: every required delegated scope; only tenant-wide `AllPrincipals` grants count.
- `CapabilityProbes`: fixed HTTPS GET endpoints on `api.powerplatform.com`, `api.bap.microsoft.com`, `api.powerapps.com`, or `api.flow.microsoft.com`, with the matching collector resource scope. Mark preview endpoints with `Preview: true`; preview-only evidence always degrades the connection.

The verifier uses the configured Key Vault certificate through `Collectors:AppOnlyCertificate`; no delegated user token is stored. Each probe persists state, HTTP status, request ID, raw-response SHA-256, and detail through `TenantCapability`. Unknown status, preview-only evidence, any missing probe, disabled/revoked enterprise application, missing scope, or inconclusive administrator evidence results in `Degraded`.

Required Microsoft Graph **application permissions** for the verifier service principal are:

- `Application.Read.All` to resolve local service principals.
- `DelegatedPermissionGrant.Read.All` to read tenant-wide OAuth2 permission grants.
- `RoleManagement.Read.Directory` to read directory role assignments and definitions.

All require customer-tenant admin consent. The authenticated callback token also needs the existing PPGSM API delegated scope and must issue immutable `tid` and `oid` claims. Power Platform probes require the corresponding resource application permissions and an approved Power Platform service-principal/RBAC assignment. The verifier recognizes Global Administrator, Power Platform Administrator, or Dynamics 365 Administrator directory-role evidence; endpoint probes independently test effective app-only access.

PoC dependencies remain: confirm each configured Power Platform endpoint and API version in a disposable tenant; prove the least-privilege RBAC assignment for each endpoint/resource identity; record 200, 401/403, 404, 429, revocation, and role-removal fixtures; and confirm whether a documented non-preview API exposes Power Platform role assignments directly. Until that final API is available, directory role plus successful allowlisted app-only probes is the explicit evidence boundary, and preview-only results cannot activate a connection.

## API v1 foundation

- `POST /api/v1/customers` requires the server-issued `Consultant` or `InternalAdmin` application role.
- `POST /api/v1/customers/{customerId}/snapshots` requires membership and `Idempotency-Key`; returns `202` when created and `200` for a replay.
- `GET /api/v1/customers/{customerId}/snapshots` lists only that authorized customer's snapshots.
- `GET /api/v1/customers/{customerId}/snapshots/{snapshotId}` returns status, coverage, and an ETag.
- `GET /api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence/{evidenceId}` reads evidence only after membership and ownership checks.

Local snapshot requests remain `Queued`; no collector or queue implementation is included in this ownership slice.

## SQL and RLS

`PpgsmDbContext` maps every current tenant-owned entity with `CustomerId`. `TenantSessionConnectionInterceptor` writes an authorized tenant context into read-only SQL `SESSION_CONTEXT`, and the context rejects cross-tenant writes before SQL execution.

Run the worker `migrate` command to apply the ordered EF migrations. The consolidated RLS migration creates one `PpgsmTenantIsolationPolicy` after all referenced tables exist, adds filter and INSERT/UPDATE block predicates to every tenant-owned table, and denies public update/delete on append-only audit events. Production migration automation must complete this command before customer traffic is enabled.

## Compatibility and handoffs

- Schema additions are additive. Snapshot records and evidence are never updated as corrections; create a new snapshot/version.
- The local in-memory adapter is development-only and is not durable or horizontally scalable.
- Backend Collectors Engineer: implement `ISnapshotCollector` and `ISnapshotEvidenceSink` using raw-first writes, then add recorded fixture contract tests.
- Security Architect: verify Entra app-role issuance, tenant membership administration, SQL principal permissions, RLS bypass resistance, and production auth tests.
- Platform Engineer: wire EF migration execution, Azure SQL, immutable blob storage, Service Bus, Key Vault certificates, and deployment configuration.
- Frontend Engineer: consume the OpenAPI contract and handle `202`, idempotent `200`, RFC 9457-style problem details, ETags, and correlation IDs.
- Governance Specialist: confirm section keys and typed setting interpretation before collector parsers or rules are published.

Unresolved cloud rules remain the PoC items from the delivery specification: Power Platform Reader endpoint coverage, DLP and tenant-isolation endpoint shapes, cross-tenant consent, Flow service-principal limitations, and partial Environment Admin visibility.

## Production platform foundation

`infra/main.bicep` deploys an isolated dev, test, staging, or production resource group at subscription scope. Modules provision a delegated Container Apps virtual network, user-assigned API/worker/migration identities, ACR, Container Apps API/worker/scheduled and migration jobs, Azure SQL, private Blob/Service Bus/Key Vault/SQL endpoints, private DNS, Log Analytics/Application Insights, diagnostic settings, budgets, and baseline alerts. Root prototype files remain outside this deployment.

Environment policy is defined in `infra/environments/*.bicepparam`. Runtime images must be digest references. Production enables zone-redundant SQL, Premium Service Bus, two API replicas, 730-day immutable raw evidence, and 365-day logs. Public access is disabled for data services; API ingress remains public HTTPS and validates Entra bearer tokens.

Workflows:

- `.github/workflows/ppgsm-ci.yml`: .NET restore/build/test/publish, root web integrity, Bicep build/lint, Checkov policy checks, and provenance/SBOM-enabled API/worker images.
- `.github/workflows/ppgsm-deploy.yml`: protected-environment OIDC deployment, digest import/promotion, what-if, candidate revision smoke test, controlled migration gate, deployment evidence, and API revision rollback.

Operational entry points:

- `docs/operations/identity-bootstrap.md`
- `docs/operations/deployment-and-rollback.md`
- `docs/operations/backup-restore-dr.md`
- `docs/operations/retention-deletion-offboarding.md`
- `docs/operations/service-objectives-alerts-cost.md`

Production remains blocked until the worker implements queue consumption, scheduler and `migrate` commands; SQL contained users/custom roles are bootstrapped and tested; app registrations/consent/RBAC are approved and manually configured; custom queue-age and certificate-expiry signals are verified; a disposable deployment, revision rollback, restore test, and regional DR design are evidenced.