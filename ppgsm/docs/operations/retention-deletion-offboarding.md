# Retention, deletion, and customer offboarding

Blob object names must use tenant and snapshot prefixes: `raw-snapshots/{customerId}/{snapshotId}/{section}/{page}.json.gz` and `exports/{customerId}/{exportId}`. Do not create storage containers dynamically per customer; tenant isolation is enforced by application authorization, prefix-scoped processing, SQL RLS, and monitored managed identities.

Azure RBAC is container-scoped, not prefix-scoped. The worker has Blob Data Contributor only on `raw-snapshots`; the API has a custom raw read/delete role without raw write plus Blob Data Contributor on `exports`. Tenant-prefix authorization and deletion approval remain application controls and must be covered by cross-tenant negative tests. The worker cannot access exports.

## Default retention

- Production raw evidence: 730 days, immutable when enabled.
- Non-production raw evidence: 30-180 days by environment.
- Exports: 30 days.
- Production operational logs: 365 days; never log customer payloads, UPNs, tokens, or certificate material. Tenant ID, customer ID, snapshot ID, correlation ID, endpoint class, status, and duration are permitted.
- SQL snapshots/findings/audit: 24 months by default, subject to customer contract and legal hold.

Lifecycle deletion does not override legal hold or a locked immutability interval. A customer deletion request may therefore enter `PendingRetentionExpiry`; communicate the earliest deletion date rather than claiming immediate erasure.

## Offboarding

1. Authenticate the request and obtain CustomerAdmin plus internal approval. Record contract/legal-hold status and an offboarding correlation ID.
2. Set the customer to suspended, disable scheduled jobs, reject new snapshots, and wait for active jobs to reach a terminal state or cancel them idempotently.
3. In the customer tenant, use the configured external revocation adapter to remove delegated grants and apply the explicit `Preserve`, `Disable`, or `Remove` enterprise-application policy. Remove Power Platform RBAC only through the configured verified allowlisted endpoint. `PendingManualAction`, partial, or failed evidence blocks physical deletion; a customer administrator removes and evidences Power Platform RBAC manually when no supported endpoint and concrete assignment ID are configured. Never activate the legacy management-app fallback.
4. Revoke/delete customer-specific certificates or keys. Do not delete shared daemon credentials while other customers remain onboarded.
5. Produce the agreed final export using short-lived access; record receipt, then expire the export.
6. Delete SQL tenant rows in a controlled order using an internal tenant context and an audited stored procedure. Verify every tenant table count is zero and RLS remains enabled.
7. Delete blobs under both customer prefixes. If immutable, mark the request pending and schedule deletion at policy expiry; do not weaken or unlock policy to accelerate deletion.
8. Remove customer-specific alert routing, memberships, cache entries, and onboarding records while retaining the minimal legally required offboarding audit record.
9. Verify no queue messages, active jobs, SQL rows, blobs, certificate references, or Power Platform role assignments remain. A second operator signs the evidence.

## Deletion safeguards

Production deletion tooling must require exact `CustomerId`, display the Entra tenant ID and blob prefix, support dry-run counts, require a second approver, and write before/after counts to an append-only audit sink. Bulk wildcard deletion and direct portal deletion are prohibited.

Key Vault purge protection means a deleted shared vault cannot be recreated with the same name for 90 days. Environment teardown must account for that delay and must never be used as a customer-level deletion mechanism.
