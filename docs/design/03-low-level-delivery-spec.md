# Power Platform Governance Snapshot Manager (PPGSM)
## Díl 3: Low-level delivery specifikace

**Verze:** 1.0 | **Datum:** 8. 7. 2026 | Cílovka: delivery tým (2× backend, 1× frontend, 0.5× DevOps)
Stack: .NET 8, React 18 + TypeScript, Azure SQL, Bicep IaC.

---

## 1. Struktura řešení (monorepo)

```
ppgsm/
├─ infra/                      # Bicep: rg, container apps env, sql, kv, sb, storage, appinsights
│  ├─ main.bicep
│  └─ modules/*.bicep
├─ src/
│  ├─ Ppgsm.Api/               # ASP.NET Core minimal API + auth
│  ├─ Ppgsm.Worker/            # Service Bus consumer, joby
│  ├─ Ppgsm.Core/              # domain, rules engine, scoring
│  ├─ Ppgsm.Collectors/        # kolektory (viz kap. 3)
│  ├─ Ppgsm.Data/              # EF Core, migrace, RLS
│  └─ Ppgsm.Web/               # React SPA (Vite, MSAL.js, TanStack Query)
├─ rules/                      # YAML rule katalog (verzovaný obsah)
│  ├─ tenant-settings/*.yaml
│  ├─ dlp/*.yaml
│  └─ ...
├─ scripts/poc/                # PoC PowerShell skripty (kap. 8)
└─ tests/
   ├─ Ppgsm.Core.Tests/        # unit: evaluátory, scoring
   ├─ Ppgsm.Collectors.Tests/  # contract testy proti nahraným fixture JSON
   └─ Ppgsm.E2E/               # Playwright + test tenant (nightly)
```

---

## 2. Autentizace — implementační detail

### 2.1 App registrace (v našem home tenantu)

**AR-1 `PPGSM-Web` (multi-tenant, user sign-in + delegated data access):**
- `signInAudience: AzureADMultipleOrgs`, SPA redirect `https://app.../auth`, web redirect pro admin consent callback `https://app.../onboarding/consent-callback`.
- Delegated permissions: Power Platform API (`8578e004-a5c6-46e7-913e-12f58912df43` — first-party PPAPI appId; ověřit v tenantu, pokud chybí SP, provést force-refresh dle [Auth v2 docs](https://learn.microsoft.com/power-platform/admin/programmability-authentication-v2)) — scopes minimálně `EnvironmentManagement.Environments.Read`, `AppManagement.ApplicationPackages.Read` + další `.Read` scopes dle PoC; Microsoft Graph `User.Read`, `User.Read.All` (orphan detection).
- Pro legacy BAP volání delegated token na resource `https://service.powerapps.com/` (scope `https://service.powerapps.com//.default`). Pozor na dvojité lomítko — je záměrné.
- Token flow: auth code + PKCE v SPA → API používá OBO (`Microsoft.Identity.Web` `ITokenAcquisition.GetAccessTokenForUserAsync`) pro PPAPI/BAP/Graph. Uživatelské tokeny se **neukládají** (jen MSAL token cache in-memory, per session).

**AR-2 `PPGSM-Daemon` (multi-tenant, app-only):**
- Certifikát z Key Vault (self-signed 90d, rotace Azure Automation/Function).
- Žádné application permissions na PPAPI (nepodporováno ✅) — přístup výhradně přes Power Platform RBAC assignment v zákaznickém tenantu; fallback legacy `adminApplications` registrace.
- Token: client credentials na `https://api.powerplatform.com/.default` (RBAC cesta) a `https://service.powerapps.com//.default` (legacy BAP cesta) — kolektor si říká o správný resource dle endpointu.

### 2.2 Onboarding sekvence (kód)

```
1. POST /api/customers                          → Customer record (Status=Pending)
2. GET  /api/customers/{id}/consent-url         → https://login.microsoftonline.com/organizations/v2.0/adminconsent
                                                   ?client_id={AR1}&redirect_uri=...&state={id}
   (druhý consent-url pro AR-2)
3. callback → uložit tenantId z `tenant` query param, ověřit state, TenantConnection(Delegated, Active)
4. Wizard krok "app-only": v kontextu přihlášeného PP Admina zákazníka
   a) Graph GET /servicePrincipals?$filter=appId eq '{AR2}'   → objectId lokálního SP
   b) POST https://api.powerplatform.com/authorization/roleAssignments?api-version=2024-10-01
      body: { roleDefinitionId: "c886ad2e-27f7-4874-8381-5849b8d8a090",  // PP reader
              principalObjectId: {spObjectId}, principalType: "ApplicationUser",
              scope: "/tenants/{customerTenantId}" }
      (delegated token uživatele; vyžaduje PP Admin nebo PP RBAC Admin ✅)
   c) fallback (feature flag): PUT https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform
        /adminApplications/{AR2-appId}?api-version=2020-10-01   (delegated token) ✅
   d) verifikační app-only call → TenantConnection(AppOnly, Active)
5. Enqueue SnapshotRun job
```

---

## 3. Kolektory — kontrakt a endpointy

Společný interface:

```csharp
public interface ISnapshotCollector
{
    string SectionKey { get; }                    // "tenantSettings", "environments", ...
    CollectorRequirement Requirement { get; }     // DelegatedOnly | AppOnlyCapable
    Task<SectionResult> CollectAsync(TenantContext ctx, SnapshotSink sink, CancellationToken ct);
}
// SectionResult: Coverage (Full|Partial|Failed|Skipped), ItemCount, RawBlobPath, Warnings[]
```

Zásady: každý raw response se ukládá do Blob (`{customer}/{snapshotId}/{section}/{n}.json.gz`) **před** parsováním; parser je tolerantní (unknown fields → passthrough); HTTP klient s Polly (retry 429/5xx, respekt `Retry-After`, jitter, circuit breaker); paginace přes `nextLink`/`skiptoken` do vyčerpání; per-tenant souběžnost max 4 requesty.

| Kolektor | Endpoint(y) | Token resource | Pozn. |
|---|---|---|---|
| `TenantSettingsCollector` | `POST https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/listtenantsettings?api-version=2020-10-01` ✅ | service.powerapps.com | preview; uložit celé, mapovat známé klíče do SettingRecord |
| `EnvironmentsCollector` | `GET https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/scopes/admin/environments?api-version=2020-10-01&$expand=properties` (a/nebo PPAPI `GET /environmentmanagement/environments?api-version=...` — zvolit dle PoC T-04 výsledku) | dle endpointu | extrahovat isDefault, governanceConfiguration.protectionLevel, linkedEnvironmentMetadata |
| `DlpPolicyCollector` | `GET https://api.bap.microsoft.com/providers/PowerPlatform.Governance/v2/policies` 🟡 (V2 DLP; přesnou URL potvrdit v PoC T-02 — alternativně `Get-DlpPolicy` tvar) | service.powerapps.com | vč. connectorGroups, environments scope, connector action rules, endpoint rules |
| `ConnectorsCollector` | `GET https://api.powerapps.com/providers/Microsoft.PowerApps/apis?api-version=2020-10-01&$filter=environment eq '{envId}'` + unblockable/virtual listy ✅ | service.powerapps.com | jen pro environmenty v MVP scope; join s DLP → classification |
| `TenantIsolationCollector` | PS ekvivalent `Get-PowerAppTenantIsolationPolicy`; REST: `.../scopes/admin/tenantIsolationPolicy` 🟡 (T-03) | service.powerapps.com | |
| `AppsCollector` | `GET https://api.powerapps.com/providers/Microsoft.PowerApps/scopes/admin/environments/{env}/apps?api-version=2020-10-01` + roleAssignments per app | service.powerapps.com | roleAssignments jen pro apps v default env + sample (výkon); full volitelně |
| `FlowsCollector` | `GET https://api.flow.microsoft.com/providers/Microsoft.ProcessSimple/scopes/admin/environments/{env}/v2/flows?api-version=2016-11-01` 🟡 | service.flow / service.powerapps 🟡 T-05 | app-only může být omezené ✅ → Coverage=Partial |
| `EnvGroupsCollector` (MVP-light) | PPAPI governance/environmentGroups (preview) 🟡 T-04 | api.powerplatform.com | jen list + membership |
| `GraphEnrichmentCollector` | Graph `GET /users/{id}?$select=id,accountEnabled` (batch `$batch`, 20/req) | graph.microsoft.com | OwnerStatus pro orphan detection |

---

## 4. Databáze — DDL jádro (Azure SQL)

```sql
CREATE TABLE Customer (
  CustomerId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
  Name NVARCHAR(200) NOT NULL,
  EntraTenantId UNIQUEIDENTIFIER NOT NULL UNIQUE,
  Region NVARCHAR(50) NOT NULL DEFAULT 'westeurope',
  Status TINYINT NOT NULL DEFAULT 0,           -- 0 Pending,1 Active,2 Suspended,3 Offboarded
  DefaultRuleProfileId UNIQUEIDENTIFIER NULL,
  CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME());

CREATE TABLE TenantConnection (
  ConnectionId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
  CustomerId UNIQUEIDENTIFIER NOT NULL REFERENCES Customer,
  Mode TINYINT NOT NULL,                       -- 1 Delegated, 2 AppOnly
  SpObjectIdInCustomerTenant UNIQUEIDENTIFIER NULL,
  RbacRoleAssignmentId NVARCHAR(100) NULL,
  LegacyManagementAppRegistered BIT NOT NULL DEFAULT 0,
  ConsentGrantedBy NVARCHAR(256) NULL, ConsentGrantedAt DATETIME2 NULL,
  Status TINYINT NOT NULL DEFAULT 1, LastValidatedAt DATETIME2 NULL);

CREATE TABLE Snapshot (
  SnapshotId UNIQUEIDENTIFIER PRIMARY KEY DEFAULT NEWID(),
  CustomerId UNIQUEIDENTIFIER NOT NULL REFERENCES Customer,
  StartedAt DATETIME2 NOT NULL, CompletedAt DATETIME2 NULL,
  TriggeredBy NVARCHAR(256) NOT NULL, Mode TINYINT NOT NULL,
  SchemaVersion INT NOT NULL, Status TINYINT NOT NULL,   -- 0 Running,1 Done,2 Failed,3 PartialDone
  CoverageJson NVARCHAR(MAX) NOT NULL DEFAULT '{}',
  RawBlobPrefix NVARCHAR(400) NOT NULL,
  CapturedIdentity NVARCHAR(256) NULL, CapturedRolesJson NVARCHAR(MAX) NULL,
  ApiVersionsJson NVARCHAR(MAX) NULL);
CREATE INDEX IX_Snapshot_Customer ON Snapshot(CustomerId, StartedAt DESC);

CREATE TABLE Environment (
  EnvironmentRecordId BIGINT IDENTITY PRIMARY KEY,
  SnapshotId UNIQUEIDENTIFIER NOT NULL REFERENCES Snapshot,
  EnvGuid NVARCHAR(100) NOT NULL, DisplayName NVARCHAR(400) NULL,
  EnvType NVARCHAR(50) NULL, Region NVARCHAR(50) NULL,
  IsDefault BIT NOT NULL DEFAULT 0, IsManaged BIT NOT NULL DEFAULT 0,
  ProtectionLevel NVARCHAR(50) NULL, HasDataverse BIT NOT NULL DEFAULT 0,
  SecurityGroupId UNIQUEIDENTIFIER NULL, CreatedTime DATETIME2 NULL,
  PropertiesJson NVARCHAR(MAX) NULL);
CREATE INDEX IX_Env_Snap ON Environment(SnapshotId);

-- SettingRecord, PolicyRecord, ConnectorRecord, ResourceRecord analogicky dle Dílu 2 kap. 5
-- (všechny se SnapshotId FK + index, JSON payload sloupce NVARCHAR(MAX))

CREATE TABLE RuleDefinition (
  RuleId NVARCHAR(30) NOT NULL, Version INT NOT NULL,
  Name NVARCHAR(300) NOT NULL, Area NVARCHAR(40) NOT NULL,
  Severity TINYINT NOT NULL,                    -- 4 Crit,3 High,2 Med,1 Low,0 Info
  Weight DECIMAL(6,2) NOT NULL DEFAULT 1,
  DefinitionYaml NVARCHAR(MAX) NOT NULL,
  AutoRemediable NVARCHAR(10) NOT NULL, RemediationType NVARCHAR(10) NOT NULL,
  DocsUrl NVARCHAR(500) NULL, Deprecated BIT NOT NULL DEFAULT 0,
  PublishedAt DATETIME2 NOT NULL,
  PRIMARY KEY (RuleId, Version));

CREATE TABLE Finding (
  FindingId BIGINT IDENTITY PRIMARY KEY,
  SnapshotId UNIQUEIDENTIFIER NOT NULL REFERENCES Snapshot,
  RuleId NVARCHAR(30) NOT NULL, RuleVersion INT NOT NULL,
  Scope TINYINT NOT NULL, EnvGuid NVARCHAR(100) NULL,
  Status TINYINT NOT NULL,                      -- 0 Pass,1 Fail,2 Partial,3 NotEvaluated,4 Excepted
  MeasuredValueJson NVARCHAR(MAX) NULL, EvidenceJson NVARCHAR(MAX) NULL,
  Message NVARCHAR(1000) NULL);
CREATE INDEX IX_Finding_Snap ON Finding(SnapshotId, Status);

CREATE TABLE Score (
  ScoreId BIGINT IDENTITY PRIMARY KEY,
  SnapshotId UNIQUEIDENTIFIER NOT NULL UNIQUE REFERENCES Snapshot,
  Overall DECIMAL(5,2) NOT NULL, TierName NVARCHAR(30) NOT NULL,
  AreaScoresJson NVARCHAR(MAX) NOT NULL,
  EvaluatedRules INT NOT NULL, TotalRules INT NOT NULL,
  CriticalFailCount INT NOT NULL, CapApplied BIT NOT NULL);

CREATE TABLE RemediationAction (...);  -- dle Dílu 2 kap. 5
CREATE TABLE RuleException (...);
CREATE TABLE AuditEvent (
  AuditId BIGINT IDENTITY PRIMARY KEY,
  CustomerId UNIQUEIDENTIFIER NULL, Actor NVARCHAR(256) NOT NULL,
  Action NVARCHAR(100) NOT NULL, TargetType NVARCHAR(50) NULL,
  TargetId NVARCHAR(200) NULL, Ts DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
  Ip NVARCHAR(45) NULL, DetailsJson NVARCHAR(MAX) NULL,
  CorrelationId UNIQUEIDENTIFIER NULL);

-- Row-level security (izolace per tenant pro customer-facing role):
CREATE FUNCTION dbo.fn_tenantAccess(@CustomerId UNIQUEIDENTIFIER)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS ok WHERE
  CAST(SESSION_CONTEXT(N'IsInternal') AS BIT) = 1
  OR @CustomerId = CAST(SESSION_CONTEXT(N'CustomerId') AS UNIQUEIDENTIFIER);
CREATE SECURITY POLICY TenantIsolationPolicy
  ADD FILTER PREDICATE dbo.fn_tenantAccess(CustomerId) ON dbo.Snapshot,
  ADD FILTER PREDICATE dbo.fn_tenantAccess(CustomerId) ON dbo.Customer
  WITH (STATE = ON);
-- API vrstva nastavuje SESSION_CONTEXT z ověřeného tokenu při otevření spojení.
```

---

## 5. Rules engine

- Evaluátory jsou pojmenované C# strategie registrované v DI: `dlp.coverage`, `tenant.settingEquals`, `tenant.settingIn`, `env.allProductionManaged`, `resources.orphanCount`, `sharing.everyoneInDefault`, `isolation.enabled`, `connectors.unclassifiedCount`, `composite.and/or` (skládání).
- Vstup evaluátoru = typed snapshot model + params z YAML; výstup = `EvalResult {status, measured, evidencePaths[]}`.
- Deterministický běh: `RuleEvaluation` job iteruje aktivní RuleProfile (globální default + per-customer overrides), zapíše Findings + Score v jedné transakci; re-run nad starým snapshotem s novým katalogem = nová sada findings s vyšší RuleVersion (historii nemazat).
- Scoring přesně dle Dílu 2 kap. 2.2 (jednotkové testy: cap pravidlo, výjimky mimo jmenovatel, NotEvaluated mimo jmenovatel + coverage výpočet).

Ukázka evaluátoru:

```csharp
public sealed class DlpCoverageEvaluator : IRuleEvaluator
{
    public string Key => "dlp.coverage";
    public EvalResult Evaluate(SnapshotModel s, JsonElement p)
    {
        var min = p.GetProperty("minCoverage").GetDouble();
        var covered = s.Environments.Count(e => s.DlpPolicies.Any(pl => pl.Covers(e)));
        var ratio = s.Environments.Count == 0 ? 1 : (double)covered / s.Environments.Count;
        return ratio >= min
            ? EvalResult.Pass(ratio)
            : EvalResult.PartialOrFail(ratio, evidence: s.Environments
                 .Where(e => !s.DlpPolicies.Any(pl => pl.Covers(e)))
                 .Select(e => $"environments/{e.EnvGuid}"));
    }
}
```

---

## 6. API povrch aplikace (interní REST, verze v1)

```
POST   /api/v1/customers                       Consultant+        vytvoření zákazníka
GET    /api/v1/customers                       role-scoped        portfolio
GET    /api/v1/customers/{id}/consent-url      Consultant+
POST   /api/v1/customers/{id}/connections/app-only   CustomerAdmin|Consultant (delegated ctx)
POST   /api/v1/customers/{id}/snapshots        Consultant|CustomerAdmin   {sections?, envIds?}
GET    /api/v1/customers/{id}/snapshots        Reader+
GET    /api/v1/snapshots/{sid}                 Reader+            vč. coverage
GET    /api/v1/snapshots/{sid}/findings?area=&severity=&status=   Reader+
GET    /api/v1/snapshots/{sid}/score           Reader+
GET    /api/v1/snapshots/{a}/compare/{b}       Reader+            diff struktura
POST   /api/v1/snapshots/{sid}/export          Reader+            {format: pdf|xlsx|json, template}
GET    /api/v1/findings/{fid}                  Reader+
POST   /api/v1/findings/{fid}/exceptions       CustomerAdmin      {justification, expiresAt}
POST   /api/v1/findings/{fid}/remediations     Consultant|CustomerAdmin   návrh akce (vygeneruje dry-run job)
POST   /api/v1/remediations/{aid}/approve      CustomerAdmin (≠ navrhovatel)
POST   /api/v1/remediations/{aid}/execute      fáze 3
GET    /api/v1/customers/{id}/audit            Auditor|InternalAdmin
GET    /api/v1/rules                            Reader+            katalog + profily
```

Konvence: problém+json chyby, ETag na snapshot zdrojích, correlation id header, rate limit per user.

---

## 7. Implementační roadmapa

| Fáze | Délka | Obsah | Exit kritérium |
|---|---|---|---|
| **0 – Research PoC** | 2–3 týdny | skripty kap. 8 proti test tenantu; API coverage matrix (aktualizace matice z Dílu 1 o naměřené výsledky); rozhodnutí RBAC vs. legacy | Vyplněná coverage matrix, potvrzený auth model |
| **1 – Snapshot MVP** | 6–8 týdnů | infra (Bicep), auth (AR-1/AR-2, onboarding wizard), kolektory MVP, snapshot pipeline, základní dashboard, export JSON/CSV + jednoduché PDF | Smoke-test z Dílu 2 kap. 1 |
| **2 – Assessment engine** | 4–6 týdnů | rule katalog ≥ 35 pravidel, engine + scoring, findings UI, výjimky, plný assessment report, snapshot compare | Score reprodukovatelné, report schválen pre-sales týmem |
| **3 – Remediation** | 6 týdnů | dry-run diff, skript generátor (PowerShell/PAC šablony per pravidlo), schvalovací workflow, vybrané auto-akce (tenant settings toggles), verifikační snapshot | 4-eyes workflow e2e, rollback runbook |
| **4 – Produktizace** | průběžně | scheduled snapshoty, trend dashboardy, multi-customer škálování, GDAP research, pricing/komerční model, SLA monitoring, cert rotace automat | první 3 platící zákazníci na scheduled režimu |

### Prvních 10 implementačních úkolů (fáze 0→1)

1. Založit test tenant (M365 developer / vlastní trial) + naplnit testovací data (5 environmentů vč. managed, 2 DLP policies, orphaned app, custom connector).
2. Vytvořit AR-1 a AR-2 app registrace + Bicep modul pro jejich reprodukci; ověřit přítomnost PPAPI first-party SP v tenantu.
3. PoC skripty T-01–T-06 (kap. 8) a vyplnit coverage matrix.
4. Rozhodnout auth model (RBAC reader vs. legacy) na základě T-04; zapsat ADR (architecture decision record).
5. Bicep infra: RG, Container Apps env, SQL (RLS), Key Vault, Blob, Service Bus, App Insights; CI/CD (GitHub Actions, OIDC federated credentials — žádné secrets).
6. Skeleton Ppgsm.Api + Ppgsm.Web s Entra sign-in a multi-tenant onboarding wizardem (kroky 1–3 sekvence kap. 2.2).
7. `TenantSettingsCollector` + `EnvironmentsCollector` end-to-end (job → Blob raw → SQL parsed → API → UI výpis) — vertikální řez, referenční vzor pro ostatní kolektory.
8. Zbylé MVP kolektory (DLP, konektory, isolation, apps, flows, Graph enrichment) + coverage reporting.
9. Snapshot detail UI + settings explorer s CZ vysvětlivkami (katalog popisů jako obsahový soubor).
10. Export JSON/CSV + první PDF šablona (executive summary) + AuditEvent middleware na všech čteních.

---

## 8. PoC testovací scénář (fáze 0)

Prostředí: test tenant T, uživatelé `poc-gr@T` (Global Reader), `poc-ppa@T` (Power Platform Administrator, bez licence — pozor: musí se 1× přihlásit do PPAC ✅), SP `PPGSM-Daemon`.

| Test | Kroky | Očekávání / co měříme |
|---|---|---|
| **T-01 Tenant settings** | Pod `poc-ppa`: `POST /listtenantsettings` (BAP). Pod `poc-gr`: totéž. | ppa: 200 + kompletní JSON (uložit jako fixture). gr: očekáváme chybu/prázdno → potvrzení Global Reader gap. Zaznamenat přesný error tvar. |
| **T-02 DLP** | `Get-DlpPolicy` + zachytit podkladové REST volání (Fiddler/`-Verbose`); zopakovat čisté REST volání. | Potvrzená V2 URL + tvar (connectorGroups, environments, action rules, endpoint rules). |
| **T-03 Tenant isolation** | `Get-PowerAppTenantIsolationPolicy` + odchytit REST. | Potvrzená REST cesta pro kolektor. |
| **T-04 RBAC reader (klíčový test)** | Pod `poc-ppa` vytvořit role assignment `Power Platform reader` pro SP (Authorization API 2024-10-01). Pak app-only tokenem (resource api.powerplatform.com i service.powerapps.com) volat: environments list, listtenantsettings, DLP, connectors, apps. | Matice endpoint × (200/403). Rozhoduje: stačí RBAC reader pro celý MVP scope? Kde 403 → sekce zůstává delegated-only, nebo fallback legacy. |
| **T-05 Legacy SP fallback** | Pod `poc-ppa` PUT `adminApplications/{AR2}`; app-only zopakovat celý MVP scope vč. flows. | Potvrdit pokrytí + zdokumentovat Flow omezení pro SP. |
| **T-06 Cross-tenant** | Z druhého tenantu T2: admin consent obou AR, pak T-04/T-05 postup v T2. | End-to-end důkaz multi-tenant modelu; zapsat přesné kroky pro onboarding runbook. |
| **T-07 Environment Admin scope** | Uživatel jen Environment Admin jednoho env: environments + DLP čtení. | Potvrdit „partial snapshot" chování (vidí jen svá data ✅ dle connector docs — změřit reálně). |
| **T-08 Throttling** | Skript: 500 rychlých volání apps list. | Zaznamenat 429 práh + Retry-After hodnoty → konfigurace Polly. |

Výstup fáze 0 = **API Coverage Matrix v2** (tabulka z Dílu 1 kap. 3 doplněná o naměřené výsledky per identita) + ADR-001 Auth model + fixtures pro contract testy.

### PoC skript kostra (T-01/T-04, PowerShell 7)

```powershell
# T-01 delegated (poc-ppa)
Install-Module Microsoft.PowerApps.Administration.PowerShell -Scope CurrentUser
Add-PowerAppsAccount
Get-TenantSettings | ConvertTo-Json -Depth 20 > fixtures/tenantsettings-ppa.json
Get-AdminPowerAppEnvironment | ConvertTo-Json -Depth 20 > fixtures/envs-ppa.json
Get-DlpPolicy | ConvertTo-Json -Depth 20 > fixtures/dlp-ppa.json
Get-PowerAppTenantIsolationPolicy -TenantId $tid | ConvertTo-Json -Depth 20 > fixtures/isolation-ppa.json

# T-04 app-only přes RBAC reader
$body = @{ client_id=$appId; scope='https://api.powerplatform.com/.default';
           client_assertion_type='urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
           client_assertion=(New-ClientAssertion -CertThumbprint $thumb -AppId $appId -TenantId $tid);
           grant_type='client_credentials' }
$tok = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tid/oauth2/v2.0/token" -Body $body).access_token
$h = @{ Authorization = "Bearer $tok" }
# matice endpointů:
$eps = @(
 'https://api.powerplatform.com/environmentmanagement/environments?api-version=2022-03-01-preview',
 'https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/listtenantsettings?api-version=2020-10-01'
 # + DLP, connectors, apps ...
)
foreach ($e in $eps) {
  try { $r = Invoke-WebRequest -Uri $e -Headers $h -Method ($e -like '*listtenantsettings*' ? 'POST' : 'GET')
        "{0}`t{1}" -f $r.StatusCode, $e }
  catch { "{0}`t{1}" -f $_.Exception.Response.StatusCode.value__, $e }
}
```

---

## 9. Co je nutné ověřit ručním PoC (checklist)

- [ ] Přesné pokrytí RBAC role **Power Platform reader** pro: listtenantsettings, DLP read, connectors, apps, flows, environment groups (T-04) — **rozhodující pro auth model**.
- [ ] Přesná V2 REST URL a payload tvar pro DLP policies a tenant isolation (T-02, T-03).
- [ ] Cross-tenant průchod: consent → SP objectId → RBAC assignment / legacy registrace v cizím tenantu (T-06).
- [ ] Chování PPAPI vs. BAP environments endpointu (který vrací governanceConfiguration/protectionLevel kompletně).
- [ ] Rozsah Flow admin API pro SP (T-05) a jeho error signatury.
- [ ] Environment Admin partial-scope chování (T-07).
- [ ] Throttling prahy a Retry-After (T-08).
- [ ] Tvar environment groups rules objektů (preview) — jen pokud T-04 projde.
- [ ] Zda unlicencovaný PP Admin musí navštívit PPAC před API voláními (dokumentováno ✅ — reprodukovat a zapsat do onboarding runbooku).
- [ ] Chování `listtenantsettings` u tenantu bez jakéhokoli Power Platform provisioningu (edge case nových zákazníků).
