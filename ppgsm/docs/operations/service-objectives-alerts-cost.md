# Service objectives, alerts, and cost

## Initial service objectives

These are onboarding targets pending load and restore evidence.

## Custom metric contracts

The worker must emit `ppgsm.queue.oldest_message_age_seconds` at least every five minutes with numeric seconds in the metric value and low-cardinality string properties `queue_name` and `environment`. Emit zero when the queue is empty. The queue-age alert requires two consecutive samples above 900 seconds; missing telemetry must be covered by the existing worker availability and collection-failure alerts.

The certificate monitor must emit `ppgsm.certificate.days_to_expiry` at least hourly for every configured ingress and app-only certificate. Use numeric days in the metric value and properties `certificate_id` (stable secret/certificate identifier, never secret content) and `environment`. The alert fires below 30 days. Alert verification must inject test metrics in a disposable environment and confirm action-group delivery.

| Signal | Objective | Measurement |
|---|---:|---|
| API availability | 99.9% monthly | Successful authenticated API requests, excluding approved maintenance |
| API latency | 95% under 1 second | Application Insights request duration, excluding exports |
| Snapshot dispatch | 99% starts within 15 minutes | SQL request timestamp to worker start |
| Snapshot completion | 95% within 10 minutes for the MVP reference tenant | Snapshot started/completed timestamps with coverage status |
| Queue age | Oldest active job under 15 minutes | Application custom metric from enqueued timestamp |
| Recovery | RPO 24 hours, RTO 8 hours | Quarterly restore evidence |

A snapshot with failed/unknown coverage is not counted as successful. Tenant IDs may be telemetry dimensions; customer payloads and PII may not.

## Alert catalog

| Alert | Severity | Response |
|---|---:|---|
| Collection failure | 1 | Inspect correlation ID and per-section coverage; retry idempotently after fixing auth/upstream faults |
| Dead-lettered snapshot job | 1 | Quarantine payload metadata, identify delivery count/root cause, replay from SQL request only |
| API restart count | 2 | Inspect revision/container logs and roll back if release-related |
| Queue backlog/age | 2 | Check worker replicas, lock loss, throttling, and oldest enqueue time |
| Upstream 429 burst | 2 | Honor `Retry-After`, reduce tenant concurrency, pause scheduler if sustained |
| Certificate near expiry | 1 | Add overlapping certificate, validate daemon auth, then remove old credential |
| Anomalous tenant access | 1 | Disable identity/session, preserve audit evidence, validate tenant boundary |
| Budget 80% actual / 100% forecast | 3 | Attribute SQL, Logs, Service Bus, Blob, egress, and compute growth |

The template creates baseline alerts and routes them to `PPGSM_ALERT_EMAIL`. Queue age and certificate expiry require application custom metrics/Event Grid wiring that the current runtime does not yet emit. Treat the backlog and Key Vault log queries as temporary coverage, and do not approve production onboarding until the exact age/expiry signals are integrated and fired successfully.

## Alert verification

In a disposable environment:

1. Send a synthetic application trace containing `collector failed`; verify the collection alert and common alert schema email.
2. Publish a poison test message with a low test-only delivery threshold; verify dead-letter alert, then delete the message.
3. Stop the worker and enqueue enough synthetic jobs to cross backlog/age thresholds; verify scale-out and alert resolution.
4. Use a test dependency endpoint that returns 429; verify retries respect `Retry-After` and the throttling alert fires without logging response bodies.
5. Use a short-lived test certificate and the approved expiry Event Grid/custom metric path; verify both warning and resolution.
6. Emit synthetic audit telemetry for one test identity across more than ten synthetic tenant IDs; verify anomalous-access routing.
7. Use Azure Monitor test notifications for the action group and record receiver acknowledgement.

Store alert rule ID, test time, incident/notification ID, result, and owner with deployment evidence.

## Cost drivers

Major fixed drivers are production Premium Service Bus, provisioned General Purpose Azure SQL, Log Analytics ingestion/retention, ACR, private endpoints, and two minimum API replicas. Variable drivers are Container Apps vCPU/memory, SQL compute/storage/backups, snapshot blob volume/versioning/immutability retention, dependency telemetry, exports, image storage, and outbound data transfer.

Dev uses scale-to-zero API/worker and serverless SQL. Staging approximates production messaging but has one API replica and lower retention. Production starts with a EUR/USD-neutral budget value of `1500` in the subscription billing currency; Finance must replace it with an approved forecast after a representative load test.

Cost controls: sampling and PII-safe telemetry, per-environment retention, export expiry, tenant-level usage dimensions, snapshot concurrency limits, budgets, and quarterly rightsizing. Do not reduce immutable or contractual retention merely to resolve a budget alert.
