# PPGSM production foundation

This directory is an isolated .NET 8 production foundation for Power Platform Governance Snapshot Manager. The static workshop prototype in the repository root remains independent and unchanged.

## Projects

- `src/Ppgsm.Core`: tenant domain, immutable snapshot lifecycle, typed evidence, collector contracts, authorization, idempotency, and audit abstractions.
- `src/Ppgsm.Data`: EF Core 8 SQL Server mappings, tenant write guard, SQL session context interceptor, and Azure SQL RLS script.
- `src/Ppgsm.Api`: minimal REST API, OpenAPI, problem details, ETags, correlation IDs, local stores, membership authorization, and audit middleware.
- `src/Ppgsm.Worker`: hosted worker with `worker`, `scheduler`, `exports`, and `migrate` commands; consumes Service Bus snapshot jobs, processes offboarding and export jobs, and applies EF migrations.
- `src/Ppgsm.Collectors`: collector HTTP pipeline (pagination, checkpoint/resume, retry, destination allowlisting), Azure Blob/Service Bus adapters, app-only/delegated token acquisition, and section collectors for tenant settings, environments, DLP, connectors, apps, flows, owner enrichment, and environment groups. Most collectors remain feature-flag-disabled pending their PoC gates.
- `tests/Ppgsm.Core.Tests`: focused executable domain and tenant-isolation tests.
- `tests/Ppgsm.Collectors.Tests`: collector pipeline, capability, onboarding-security, and runtime adapter tests.

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

## Production external consent revocation

External offboarding is disabled unless `Offboarding:ExternalConsentRevocation:Enabled` is `true`. A disabled deployment returns capability unavailable from the offboarding request and approval routes. An enabled production deployment fails startup unless `ClientApplicationId` is a valid application ID and every configured Power Platform removal endpoint is an HTTPS assignment endpoint template on the fixed `api.powerplatform.com` or `api.bap.microsoft.com` allowlist with an approved resource scope. There is no legacy management-app fallback.

Set `EnterpriseApplicationPolicy` explicitly to:

- `Preserve` to remove delegated grants but retain the local enterprise application.
- `Disable` to remove grants and set the verified local service principal's `accountEnabled` property to `false`.
- `Remove` to remove grants and delete the verified local service principal.

The adapter resolves the customer tenant from the durable customer record and requires Graph to uniquely resolve the configured application ID to the service-principal object ID persisted by verified onboarding. It removes every matching `oauth2PermissionGrant` with Microsoft Graph `DELETE /v1.0/oauth2PermissionGrants/{id}`. A missing grant or service principal is idempotent success only after the tenant-bound lookups are conclusive. Wrong tenant, wrong/non-unique service principal, wrong grant client, throttling exhaustion, authorization failure, or an inconclusive response fails closed before physical customer-data deletion.

In addition to the verifier's read permissions, the app-only service principal requires these Microsoft Graph **application permissions**, with customer-tenant admin consent:

- `DelegatedPermissionGrant.ReadWrite.All` to enumerate and delete delegated OAuth2 permission grants.
- `Application.ReadWrite.All` to resolve and, only under the configured policy, disable or delete the local enterprise application. If policy is `Preserve`, validate whether the customer's least-privilege permission review permits retaining only the existing read permission instead.

Each external response is represented by operation, endpoint, HTTP status, Microsoft request ID, and SHA-256 of the raw response. The retained consent reference is a SHA-256 over that canonical evidence manifest; tokens and response bodies are not logged or placed in the deletion certificate.

Power Platform RBAC removal is automated only when onboarding has persisted the concrete assignment ID and operations has configured a verified, supported endpoint template containing `{assignmentId}` on the allowlist. The adapter uses the configured app-only Power Platform resource identity and accepts only a successful or already-absent response. The current onboarding verifier stores `verified` rather than a concrete assignment ID, so deployments using that evidence boundary receive `PendingManualAction` and offboarding does not proceed to physical deletion.

For `PendingManualAction`, a customer administrator must remove the PPGSM assignment in the Power Platform admin center, capture the tenant ID, assignment/role identifier, UTC time, operator, and portal/audit request reference, and provide that evidence to the offboarding operator. Do not enable a preview, guessed, or legacy endpoint to bypass this step. After a supported endpoint and concrete assignment identity have been independently verified and configured, retry the same offboarding job; Graph grant deletion is idempotent.

## API v1 foundation

- `POST /api/v1/customers` requires the server-issued `Consultant` or `InternalAdmin` application role.
- `POST /api/v1/customers/{customerId}/snapshots` requires membership and `Idempotency-Key`; returns `202` when created and `200` for a replay.
- `GET /api/v1/customers/{customerId}/snapshots` lists only that authorized customer's snapshots.
- `GET /api/v1/customers/{customerId}/snapshots/{snapshotId}` returns status, coverage, and an ETag.
- `GET /api/v1/customers/{customerId}/snapshots/{snapshotId}/evidence/{evidenceId}` reads evidence only after membership and ownership checks.

In local Development, snapshot requests remain `Queued`; live collection runs only in deployments configured with the production Service Bus/Blob/SQL adapters and enabled collector feature flags.

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

Production remains blocked until SQL contained users/custom roles are bootstrapped and tested; app registrations/consent/RBAC are approved and manually configured; the collector PoC gates (T-01 through T-08) are evidenced and approved per tenant; custom queue-age and certificate-expiry signals are verified; a disposable deployment, revision rollback, restore test, and regional DR design are evidenced.