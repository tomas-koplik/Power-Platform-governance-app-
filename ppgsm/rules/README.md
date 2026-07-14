# Versioned governance rule catalog

`v1/catalog.yaml` and `v1/default-profile.yaml` are JSON-compatible YAML. This keeps the artifacts readable by YAML tooling while the backend uses the platform JSON parser and adds no parser supply-chain dependency.

## Publication contract

The Score capability is enabled only when all of these checks pass:

- catalog and default profile files are present and parse successfully;
- schema version is `1` and catalog version equals `RuleCatalog:TrustedVersion`;
- the SHA-256 digest of `catalog.yaml.manifest.json` appears in the semicolon-separated `RuleCatalog:TrustedManifestDigests` allowlist;
- the manifest contains SHA-256 digests for the exact catalog and profile bytes, and either byte sequence changing invalidates publication;
- stable IDs are unique and every required rule contract field validates;
- every evidence requirement requires `Full` section coverage;
- the default profile contains every catalog rule with `enabled`, `disabled`, or `advisory` mode.

Any failure returns no published catalog and keeps Score unavailable. The publication attestation is a release allowlist value, not a cryptographic signature or proof of author identity. Production release automation should inject both trusted values from an approved immutable release artifact. Cryptographic signing remains a separate hardening item.

## Published and gated rules

| Rule | Default mode | Source confidence | Control intent | Data source and required identity | Evaluator and scoring effect | Remediation | Open PoC question | Acceptance evidence |
|---|---|---|---|---|---|---|---|---|
| PPG-TEN-006 | Advisory | Setting documented; identity-specific retrieval requires PoC | Restrict non-admin production creation when customer policy requires it | Full `tenantSettings`; delegated PP Admin/Reader coverage to prove | `tenant.settingEquals`; Low, scores only when customer enables | Review-first script; tenant-wide blast radius; refresh to verify | Is the property stable for each supported identity/API version? | Pass and Fail fixtures; Partial evidence returns NotEvaluated |
| PPG-DLP-002 | Enabled, evidence-gated | DLP behavior documented; complete policy-scope retrieval is PoC-required | Identify environments outside all applicable DLP policy scopes | Full `environments` plus Full `dlpPolicies`; delegated PP Admin initially | `dlp.coverage`; High, DLP area weight 1.2; measured ratio supports Partial | Policy diff/script after dependency review; workload disruption possible | Does collection prove tenant-wide scope for delegated, Reader, app-only, and partial access? | Pass, Partial, NotEvaluated fixtures |
| PPG-ENV-002 | Disabled | Managed Environments documented; authoritative fields unproven | Require production environments to be Managed when customer adopts policy | Full `environments`; identity/endpoint pending PoC | `env.allProductionManaged`; Medium; no name heuristics | Manual enablement after license/owner review | Which stable fields prove production and managed state? | NotApplicable fixture for no production environments |
| PPG-SHR-001 | Disabled | Admin inventory/assignments documented in parts; completeness unproven | Detect broad default-environment sharing under customer policy | Full environments/apps/flows and all role assignments; PP Admin initially | `sharing.everyoneInDefault`; Critical, DefaultEnv weight 1.1; cap applies only after enablement | Manual assignment review; access interruption risk | Are broad principals and all assignments complete for every supported identity? | Profile-disabled path and full-evidence requirement |
| PPG-SHR-002 | Advisory | Graph user state documented; resource owner completeness PoC-required | Find resources owned by disabled or deleted users without conflating lookup failure | Full apps/flows and Full owner directory enrichment; PP read identity plus Graph `User.Read.All` | `resources.orphanCount`; High; `Disabled`/`NotFound` fail, `Unresolved` NotEvaluated | Review-first reassignment script with approved replacement | Are owner IDs complete, and are 404, disabled, and lookup failure distinct? | Disabled and NotFound Fail fixtures; Unresolved NotEvaluated fixture |
| PPG-AI-001 | Advisory | Product behavior and settings are evolving | Surface AI data-movement posture for customer Security/Privacy review | Full `tenantSettings`; exact identity/field pending PoC | `tenant.settingIn`; Informational, zero score effect | Information only; no change proposed | Which stable fields and current docs support a customer-specific policy? | Advisory NotEvaluated fixture |

## Evaluator acceptance behavior

The fixtures in `v1/fixtures/acceptance-cases.yaml` define Pass, Fail, Partial, NotEvaluated, NotApplicable, and Excepted behavior. `NotEvaluated`, `NotApplicable`, `Excepted`, and Informational findings never enter the score denominator. The exact score uses severity weights `10/6/3/1/0`, area weights, applicability factor, and evaluator ratio; tiers are Excellent `90-100`, Good `75-89`, Needs Attention `60-74`, and At Risk below `60`, with a failed scored Critical rule capped at `59`.