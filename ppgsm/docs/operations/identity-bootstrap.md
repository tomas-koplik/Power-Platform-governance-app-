# Identity and environment bootstrap

PPGSM infrastructure does not create Entra app registrations or assume tenant identifiers. A tenant administrator must complete this bootstrap once per Azure/GitHub environment and record the approval.

## Azure deployment identity

1. Create one Entra application or user-assigned managed identity per GitHub environment: `ppgsm-dev`, `ppgsm-test`, `ppgsm-staging`, and `ppgsm-prod`.
2. Add a federated identity credential for the exact repository and GitHub environment subject, for example `repo:OWNER/REPOSITORY:environment:ppgsm-prod`. Do not create a client secret.
3. Grant the deployment principal at subscription scope only the roles needed to deploy the template: `Contributor`, `User Access Administrator`, `Resource Policy Contributor`, and `Cost Management Contributor`. Replace `User Access Administrator` with a custom role limited to managed-identity RBAC assignments after validating the deployment operations.
4. Configure protected GitHub environments named `ppgsm-dev`, `ppgsm-test`, `ppgsm-staging`, and `ppgsm-prod`. Require reviewers for staging and production; disallow self-review for production.
5. Add environment variables, not credentials: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `PPGSM_DEPLOYMENT_LOCATION`, `PPGSM_SQL_ADMIN_LOGIN`, `PPGSM_SQL_ADMIN_OBJECT_ID`, `PPGSM_API_CLIENT_ID`, `PPGSM_AUTHORITY`, `PPGSM_AUDIENCE`, `PPGSM_API_SCOPE`, `PPGSM_AUTHORIZED_SPA_CLIENT_IDS_JSON`, `PPGSM_ONBOARDING_WEB_CLIENT_ID`, `PPGSM_CONSENT_CALLBACK_URI`, `PPGSM_ONBOARDING_SIGNING_SECRET_URI`, `PPGSM_RULE_CATALOG_VERSION`, `PPGSM_RULE_CATALOG_ATTESTATION`, `PPGSM_GRAPH_VERIFIER_BASE_URI`, `PPGSM_GRAPH_VERIFIER_SCOPES_JSON`, `PPGSM_CORS_ALLOWED_ORIGINS_JSON`, `PPGSM_OWNER_TAG`, `PPGSM_COST_CENTER`, and `PPGSM_ALERT_EMAIL`. JSON values must be non-empty arrays. Optional variables are `PPGSM_CUSTOM_DOMAIN` and the versioned `PPGSM_CERTIFICATE_SECRET_ID`.
6. Create protected environments named `ppgsm-ENV-post-deploy`. Require a reviewer independent from the deployer for staging and production. Store only short-lived smoke JWTs as environment secrets and rotate them after each release; store test object IDs and contract URLs as environment variables.

GitHub receives a short-lived Azure token through OIDC. No Azure credential is stored in GitHub.

## Application registrations

Create registrations only after the Security Architect approves scopes and redirect URIs.

- `PPGSM-Web`: multi-tenant user sign-in and approved delegated Power Platform scopes. Expose the API audience consumed by `Authentication__Audience`.
- After deployment, register the `spaRedirectUri` and `spaLogoutUri` outputs on `PPGSM-Web`. Set `PPGSM_CORS_ALLOWED_ORIGINS_JSON` to the exact `webUrl` origin; wildcard origins and credentials are not enabled.
- Store a random onboarding state signing value of at least 32 characters in the environment daemon Key Vault. Set `PPGSM_ONBOARDING_SIGNING_SECRET_URI` to its versioned secret URI; the API resolves it through managed identity and the value never enters GitHub.
- `PPGSM-Daemon`: multi-tenant workload identity for customer Power Platform access. Do not add unsupported Power Platform application permissions. Prefer the customer-assigned Power Platform Reader RBAC role; legacy management-app registration stays disabled by feature flag.
- Store only daemon and ingress certificate material in the dedicated environment daemon Key Vault. The migration identity has no vault role. The API and worker can read daemon secrets; a separate TLS certificate identity is attached to the Container Apps environment for ingress import.
- The daemon registration is shared across onboarded customers. Key Vault isolation prevents unrelated platform identities from reading its certificate, but compromise of the daemon certificate can still affect every customer that granted that registration access. Per-customer Power Platform RBAC, audit correlation, rapid global revocation, and customer notification are mandatory compensating controls. Use per-customer daemon registrations where contractual isolation requires a smaller blast radius.

Customer tenant IDs, local service-principal object IDs, Power Platform RBAC assignment IDs, and consent evidence are onboarding data. They are not Bicep constants.

## SQL identity bootstrap

The Bicep template enables Entra-only authentication and sets an approved Entra group as SQL administrator. After deployment, an administrator must create contained users for the API, worker, and migration managed identities.

```sql
CREATE USER [ppgsm-prod-api-id] FROM EXTERNAL PROVIDER;
CREATE USER [ppgsm-prod-worker-id] FROM EXTERNAL PROVIDER;
CREATE USER [ppgsm-prod-migration-id] FROM EXTERNAL PROVIDER;

ALTER ROLE db_datareader ADD MEMBER [ppgsm-prod-api-id];
ALTER ROLE db_datawriter ADD MEMBER [ppgsm-prod-api-id];
ALTER ROLE db_datareader ADD MEMBER [ppgsm-prod-worker-id];
ALTER ROLE db_datawriter ADD MEMBER [ppgsm-prod-worker-id];
ALTER ROLE db_ddladmin ADD MEMBER [ppgsm-prod-migration-id];
ALTER ROLE db_datareader ADD MEMBER [ppgsm-prod-migration-id];
ALTER ROLE db_datawriter ADD MEMBER [ppgsm-prod-migration-id];
GRANT EXECUTE ON sys.sp_set_session_context TO [ppgsm-prod-api-id];
GRANT EXECUTE ON sys.sp_set_session_context TO [ppgsm-prod-worker-id];
```

Replace `db_ddladmin` with a custom migration role once the exact migration DDL is known. The runtime identities must not be database owners and must not receive `ALTER ANY SECURITY POLICY`.

## Daemon certificate rotation and revocation

1. Create a new certificate version in the daemon vault at least 14 days before expiry. Never export its private key to a workstation or GitHub.
2. Add the new public credential to `PPGSM-Daemon`, deploy the versioned secret URI, and run an authenticated collection in the test tenant.
3. Verify Key Vault access logs identify only the API/worker managed identities and that the old and new credential IDs are recorded in release evidence without secret values.
4. After the overlap window, remove the old public credential from Entra, disable the old Key Vault version, test token acquisition again, then delete the old version subject to purge protection and retention policy.
5. For suspected compromise, suspend scheduler/worker processing, remove the compromised public credential from Entra first, disable its vault version, revoke customer assignments or consent as required, rotate to a clean credential, and notify affected customers. Do not wait for normal overlap.
6. Record actor, time, app object ID, certificate thumbprints, vault version IDs, affected customer IDs, validation results, and incident/release correlation ID. Never record private key material or tokens.

## Private access validation

From a Container Apps console or disposable job using the relevant managed identity, verify DNS resolves private addresses for SQL, Blob, Service Bus, and Key Vault. Confirm public network access fails from an external host. Verify API Service Bus send, raw read/delete, and exports access; worker Service Bus receive, raw write, and daemon-certificate read; TLS certificate identity secret read; and migration SQL access. Negative tests must prove the worker cannot access exports, the API cannot write raw evidence, and migration cannot read the daemon vault.
