# Backup, restore, and disaster recovery

## Objectives

Initial objectives are RPO 24 hours and RTO 8 hours for production metadata, subject to a successful restore exercise. Raw evidence is write-once and reproducible only while customer consent and upstream APIs remain available, so Blob protection is part of recovery, not an optional archive.

The current production template is single-region with zone-redundant SQL compute, ZRS Blob, Premium Service Bus, and two API replicas. This protects against many zonal faults but is not regional disaster recovery.

## Protection

- Azure SQL: 14-day point-in-time retention and 4 weekly/12 monthly long-term backups.
- Blob: ZRS, versioning, 14-day soft delete, container soft delete, lifecycle retention, and locked-time policy when immutability is enabled.
- Key Vault: soft delete and purge protection for 90 days. Certificates must also exist in the approved issuance/rotation system; Key Vault recovery is not a certificate backup strategy.
- Infrastructure and runtime settings: Bicep and immutable image digests in GitHub artifacts.
- Service Bus: transient work only. Messages are replayable from SQL snapshot requests; the queue is not a system of record.

## Quarterly restore test

1. Select a non-production point in time and restore Azure SQL to a new database name.
2. Deploy a disposable Container Apps revision/job against the restored database using a disposable managed identity.
3. Run table counts, referential checks, RLS cross-tenant denial tests, and append-only audit checks.
4. Restore representative versioned blobs into a disposable container or read them in place using an isolated identity. Verify gzip/JSON integrity and SQL-to-blob references.
5. Recreate a queue and replay synthetic pending jobs; never replay customer jobs in the test.
6. Record achieved RPO/RTO, evidence links, failures, and corrective owner. Delete disposable data after approval.

A restore test is a production onboarding requirement, not just an annual exercise.

## Regional disaster

Before production onboarding, add a paired EU region approved by Security and Privacy. The target design is:

- Azure SQL failover group with customer-managed failover decision.
- Storage GZRS/RA-GZRS where immutable-policy and residency requirements permit it, or application-level replication to an approved immutable secondary account.
- A warm secondary Container Apps environment, Key Vault, ACR import path, private DNS/networking, and disabled scheduler.
- Replicated dashboards/alerts and documented DNS/custom-domain failover.

During failover: freeze writes and scheduling, assess primary consistency, fail SQL according to the approved mode, validate Blob availability, deploy the recorded digests to secondary, run liveness and tenant-isolation smoke tests, then enable API traffic followed by workers and scheduler. Do not allow both schedulers to dispatch simultaneously.

## Residual risk

Until the paired-region design is implemented and tested, a regional outage can exceed the stated RTO. Service Bus messages may be lost in a regional event, so operators must reconcile queued/running snapshots from SQL and re-enqueue idempotently after recovery.
