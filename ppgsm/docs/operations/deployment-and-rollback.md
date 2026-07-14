# Deployment, migration, and rollback

## Release model

`PPGSM CI` builds API and worker images once, publishes SBOM/provenance, and records each `sha256` digest. `PPGSM Deploy and Rollback` imports those exact digests into each environment ACR. Rebuilding or promoting mutable tags is not permitted.

Dev, test, staging, and production are separate resource groups and protected GitHub environments. Staging and production require release approval. Production requires `run_migration=true`, successful migration execution, durable adapter registration, and a separately approved `ppgsm-prod-post-deploy` authenticated smoke gate before traffic promotion. The workflow intentionally blocks production while API or worker source still registers Local/InMemory adapters.

## Deploy

1. Verify the source SHA passed `PPGSM CI`; obtain the API and worker digest artifacts.
2. Review the Bicep/checkov results and unresolved policy exceptions.
3. Run `PPGSM Deploy and Rollback` with action `deploy`. The workflow bootstraps identities/ACR, imports digests, runs Azure what-if, and deploys a zero-traffic candidate when a stable revision already exists.
4. If the release contains schema changes, confirm they are backward-compatible with the currently stable API and start the controlled migration job. Destructive changes require an expand/migrate/contract sequence across at least two releases.
5. The workflow health-checks the zero-traffic candidate revision. The protected post-deploy job requires supplied primary and cross-tenant-negative JWTs plus durable test object IDs. It verifies JWT rejection/acceptance, SQL persistence and RLS denial, queue/blob/export ownership, audit, revocation, and deletion contracts.
6. Only after every authenticated contract passes does the promotion job label the candidate `stable`, move 100% traffic, and probe the public API URL. Missing test identities, unsupported audit/deletion/revocation endpoints, or failed contracts block promotion.

The web app is promoted as its own digest-pinned image and receives environment-specific public configuration only at container startup. Its candidate `/healthz` check runs before API promotion. API rollback moves traffic to a retained API revision. Web rollback is an immutable redeployment using the prior `web.digest` from deployment evidence; do not rebuild the old source or retag a mutable image. Database rollback remains forward-compatible: schema migrations must support both the candidate and retained API revisions until the rollback window closes.

## Release evidence

Retain the CI test results, Bicep output, Checkov result, source SHA, immutable API/worker digests, Azure what-if, deployment record, migration execution name/status/log correlation, candidate and prior stable revision names, resource inventory, and authenticated smoke `contracts.tsv` for at least 90 days. The release approver records the GitHub run URL, approval identity, change ticket, schema compatibility decision, and rollback revision. Evidence must contain identifiers and status only, never JWTs, customer payloads, certificate material, or PII.

Azure deployment, migration, RBAC negative tests, private networking, revision rollback, restore, and alert verification require Azure credentials and disposable test identities. CI compilation alone is not deployment evidence.

## Database compatibility

Allowed in the pre-traffic migration: additive nullable columns, new tables/indexes created online where supported, new compatible views/procedures, and RLS predicates for newly introduced tenant tables.

Not allowed in the same release as code promotion: dropping or renaming columns, tightening nullability before backfill, changing enum meaning, disabling RLS, deleting evidence, or changing a message contract without dual-read support.

The worker image exposes a dedicated `migrate` command that applies the ordered, idempotent EF migration script. Tenant RLS is owned by the final EF migration, which recreates the single `PpgsmTenantIsolationPolicy` only after all referenced tables exist. The release workflow waits for a successful job execution and fails on error or timeout. Do not onboard production data until the command and RLS negative path pass in a disposable Azure environment.

## Export and governance acceptance gates

The scheduled `${namePrefix}-exports` Container Apps job runs the immutable worker image with the `exports` command every five minutes. It atomically claims queued SQL rows, writes tenant-scoped blobs under `exports/{customerId}/{exportJobId}/`, and stores only the authenticated API download route. The API has Blob Data Reader plus Storage Blob Delegator; only the worker has Blob Data Contributor. A successful authenticated download exchanges the API route for a read-only user-delegation SAS valid for five minutes.

Before production promotion, deploy the same image digests to a disposable environment and record:

1. `az deployment sub validate` and `az deployment sub what-if` results for the target parameter file, plus policy/checkov results.
2. Effective role assignments proving API read/delegation, worker write, no shared-key access, no public container access, and cross-tenant API denial.
3. One export moving `Queued -> Running -> Completed`, a tenant-prefixed blob, a five-minute read-only SAS, successful download, expiry denial, and denial using the secondary tenant token.
4. Evidence-list, consent-metadata, consent callback, deletion status, and deletion-certificate owner/cross-tenant smoke rows from `scripts/post-deploy-smoke.sh`.
5. Consent metadata showing `AuthoritativeDataRegion` equals the deployment location and the approved consent-document/deletion-certificate retention periods.
6. Lifecycle verification showing export deletion after `exportRetentionDays`; use a disposable container with a shortened policy rather than waiting against production data.
7. Manual alert tests for export failure and stale queued export, with action-group receipt and recovery recorded. Never place export payloads or customer PII in test telemetry.

Deletion certificates remain in the RLS-protected `CustomerDeletions` record after tenant payload rows and export blobs are removed. The certificate endpoint is restricted to CustomerAdmin/InternalAdmin and returns verification references and before/after counts. This is the authoritative certificate; a duplicate certificate blob is intentionally not created.

The export processor adds one five-minute Consumption job execution and Blob transactions/storage. Normal cost is low and workload-dependent; confirm the Azure what-if cost delta and budget forecast before approval. The largest variable is retained export bytes for `exportRetentionDays`, not the SAS exchange.

Export rollback is to disable the scheduled export job, allow or fail any claimed row deliberately, and redeploy the previous worker digest. Existing completed blobs remain readable through the compatible API route until lifecycle deletion. Do not remove API Blob Reader/Delegator RBAC until the previous API revision is retired. Database compatibility is additive; queued rows can be processed after rollback only if the prior worker understands the same export contract.

The current JSON artifact is a signed-in-request manifest containing export/customer IDs, format, creation time, and requester identity. It does not yet package snapshot evidence. Production onboarding must treat evidence-package content as a residual functional risk until the backend export specification identifies the authoritative snapshot and redaction rules and a package builder is added.

## API revision rollback

1. Identify the last healthy retained revision from the deployment evidence or `az containerapp revision list`.
2. Run `PPGSM Deploy and Rollback` with action `rollback` and that full revision name.
3. The workflow activates the revision, assigns 100% traffic, and probes `/health/live`.
4. Verify authentication and one read-only tenant request. Watch failure, restart, and dependency telemetry for 30 minutes.

Worker rollback requires redeploying the prior worker digest through the deploy workflow. Pause scheduled snapshots first if the previous worker cannot read newly emitted messages.

Before promotion, rollback is automatic: failure leaves traffic on the prior stable revision. After promotion, use the rollback action for API traffic and redeploy the previous worker digest. Runtime configuration and identity/RBAC changes are rolled back by redeploying the prior Bicep/source SHA only after confirming that doing so will not reintroduce excess access. Certificate rotation rollback means re-enabling the previous still-valid vault version and Entra public credential; never restore a credential suspected of compromise.

## Database rollback

Prefer forward repair. Do not automatically reverse a successful schema migration after new code has written data. For an incompatible failed migration, stop API traffic and scheduler, preserve the database, restore a point-in-time copy under a new database name, validate row counts/RLS/audit events, then change the deployment to the restored database during an approved incident. Never overwrite the source database before verification.
