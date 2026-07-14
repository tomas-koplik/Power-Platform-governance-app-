# V1 evaluator runtime status

## Runtime controls

| Control | Source confidence | Control intent | Data source | Required identity | Evaluator behavior | Severity and scoring | Remediation guidance | Open PoC question | Acceptance test |
|---|---|---|---|---|---|---|---|---|---|
| Catalog publication | Implemented contract | Prevent execution of unreviewed or incompatible rules | Trusted catalog and profile files | Service identity, local file read | Unknown key, evaluator-version mismatch, catalog-version mismatch, or attestation mismatch returns no publication and disables Score | No score is available | Correct and re-attest the catalog; no tenant write | None | Publication tests reject unknown keys and version 2 |
| Evidence gate | Catalog-declared; endpoint confidence remains collector-specific | Prevent conclusions from incomplete, lower-confidence, or absent paths | Completed section coverage plus immutable raw evidence references | Collector identity recorded per evidence reference | Requires every section, exact path, Full coverage, and declared confidence | NotEvaluated has no denominator effect | Re-run collection only after identity and endpoint prerequisites are met | T-01 through T-05 below | Acceptance partial-evidence fixture returns NotEvaluated |
| Coverage aggregation | Deterministic implementation | Report collection confidence without converting failed/skipped evidence to success | Required section coverage | None | Full only when all required sections are Full; Partial for mixed evaluable coverage; Failed/Skipped when no evidence is evaluable | Confidence only; no direct penalty | Investigate failed collectors or collect missing sections | None | Core theory covers Full, Partial, Failed, and Skipped |
| Zero denominator | Deterministic implementation | Avoid a numeric posture claim when no rule is evaluable | Persisted findings | None | Returns tier `Not evaluated`, score 0, and no area scores | No score claim | Resolve evidence/profile/PoC gates before interpreting posture | None | Core zero-denominator test |

## Evaluator registry

| Evaluator | Runtime status | Rule status in default profile | Evidence and applicability | Scoring effect | Remaining gate |
|---|---|---|---|---|---|
| `tenant.settingEquals` v1 | Enabled in registry | `PPG-TEN-006` advisory | Full documented `tenantSettings`; customer profile required | Excluded while advisory | Customer-approved provisioning profile and T-01 property stability |
| `dlp.coverage` v1 | Enabled in registry | `PPG-DLP-002` enabled | Full environments and DLP policy scope; measured environment ratio | Pass/Fail/Partial after PoC validation; ratio is measured, not fixed | T-02 tenant-wide DLP scope |
| `env.allProductionManaged` v1 | Enabled in registry | `PPG-ENV-002` disabled | Machine-readable production and protection level | Excluded while disabled | T-03 authoritative Managed Environment fields |
| `sharing.everyoneInDefault` v1 | Enabled in registry | `PPG-SHR-001` disabled | Full default environment plus app/flow assignments and customer sharing profile | Excluded while disabled | T-04 complete role assignments across identities |
| `resources.orphanCount` v1 | Enabled in registry | `PPG-SHR-002` advisory | Full app/flow owners and owner directory; unresolved lookup is NotEvaluated | Excluded while advisory | T-05 owner ID completeness and Graph outcome distinction |
| `tenant.settingIn` v1 | Enabled in registry | `PPG-AI-001` advisory | Full AI setting evidence and customer policy | Informational and zero score effect | Stable setting/documentation and customer Security/Privacy decision |

## Identity and scope

- PPAC Power Platform Administrator verification is persisted as a `TenantCapability` with tenant ID, principal object ID, source, and role. PPGSM `TenantMembership` remains a separate application authorization record.
- Snapshot jobs persist the user-requested environment filter. Snapshot completion separately persists discovered environment IDs and marks discovery authoritative only when the environments section is Full.
- Findings persist deterministic ID, rule version, catalog version, evaluator key/version, measured ratio, and exact raw evidence reference IDs. Raw settings remain immutable for reevaluation by newer catalogs.

## Remaining T-test dependencies

- **T-01:** Verify tenant-setting property shape and identity/API-version consistency for delegated and supported app-only paths.
- **T-02:** Prove complete DLP policy and environment scope for delegated, Reader RBAC, legacy app-only, and partial environment access.
- **T-03:** Identify stable authoritative production classification and Managed Environment state fields.
- **T-04:** Prove complete app/flow role assignments, broad principals, and environment visibility for each supported identity.
- **T-05:** Prove owner IDs are complete and Graph reliably distinguishes Disabled, NotFound, and Unresolved transport/permission failures.

Write remediation remains review-first. Any future execution path must state tenant/environment blast radius, identity and licensing prerequisites, post-change snapshot verification, and rollback limitations from the catalog before approval.