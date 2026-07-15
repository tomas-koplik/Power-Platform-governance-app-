# Power Platform Governance Snapshot Manager (PPGSM)
## Díl 2: Rozsah fází, Architektura, Datový model, UX, Bezpečnost

**Verze:** 1.0 | **Datum:** 8. 7. 2026

---

## 1. Rozsah fáze 1 — read-only snapshot (MVP)

| Oblast | MVP? | Zdůvodnění |
|---|---|---|
| Tenant-level Power Platform settings | ✅ MVP | Jedno API volání, největší poměr hodnota/úsilí |
| Environments (inventář + properties) | ✅ MVP | Základ všeho ostatního |
| Default environment governance (composite) | ✅ MVP | Nejčastější zdroj problémů u zákazníků |
| Managed environments (protectionLevel per env) | ✅ MVP | Čte se z environment properties, žádné extra volání |
| Environment groups + rules | 🟡 MVP-light | Zobrazit seznam skupin a členství; vyhodnocování rules až fáze 2 (preview API) |
| DLP policies + connector classification | ✅ MVP | Klíčová governance oblast; vč. výpočtu unclassified konektorů a DLP coverage per environment |
| Custom connectors | ✅ MVP | Inventář + zda jsou pokryty DLP |
| Tenant isolation / cross-tenant restrictions | ✅ MVP | Jedno volání, vysoká bezpečnostní hodnota |
| Environment roles & admins (BAP permissions) | ✅ MVP | Bez Dataverse deep-dive |
| Dataverse security roles kontext | ❌ Fáze 2 | Per-environment Dataverse volání, jiný auth, násobí složitost |
| Apps, flows, makers, owners | ✅ MVP (apps ano; flows s poznámkou o SP limitu) | Inventář + agregace (počty, top makers) |
| Orphaned apps/flows | ✅ MVP | Odvozeno z inventáře + Graph lookup ownerů |
| Sharing model (share with Everyone, počty sdílení) | ✅ MVP pro apps | Role assignments per app; flows fáze 2 |
| Copilot Studio / AI governance | 🟡 MVP-light | Jen to, co je v tenant settings + DLP virtual konektorech; dedikovaná agent pravidla fáze 2 |
| Power Pages | ❌ Fáze 2 | Menší penetrace, samostatný stack |
| Audit / admin activity logy (Purview) | ❌ Fáze 2–3 | Jiný auth stack, licence |
| Capacity / licensing | ❌ Fáze 2 | API pokrytí nejisté; pro score není nutné |

**MVP smoke-test definice hotovo:** proti test tenantu proběhne snapshot < 10 min pro tenant se ~100 environmenty / ~2000 apps, dashboard zobrazí score, gap list a export PDF.

---

## 2. Rozsah fáze 2 — best practices, gap analýza, score

### 2.1 Datový model doporučení (rule katalog)

Pravidla jsou **verzovaný obsah oddělený od kódu** (YAML v git repu → publikace do DB). Atributy:

```yaml
ruleId: PPG-DLP-001            # stabilní ID, nikdy nerecyklovat
version: 3                      # verze pravidla
name: "Tenant-wide DLP policy exists and covers all environments"
area: DLP                       # enum: TenantSettings|Environments|DefaultEnv|ManagedEnv|DLP|Connectors|TenantIsolation|Sharing|Makers|AI|Audit|Capacity
description: >
  Každý environment musí být pokryt alespoň jednou DLP policy...
rationale: >                    # proč je pravidlo důležité (business + security jazyk pro zákazníka)
  Bez DLP mohou makeři kombinovat firemní data s libovolnými konektory...
desiredState:                   # deklarativní očekávání (vyhodnocuje engine)
  evaluator: dlp.coverage       # jméno evaluátoru v enginu
  params: { minCoverage: 1.0, includeDefault: true }
severity: Critical              # Critical|High|Medium|Low|Informational
impactDescription: "Exfiltrace dat přes neomezené konektory"
remediationEffort: Medium       # Low|Medium|High
autoRemediable: partial         # yes|no|partial
remediation:
  type: script                  # auto|script|manual|info
  scriptTemplate: "New-DlpPolicy ... "   # šablona PowerShell/PAC
docsUrl: https://learn.microsoft.com/power-platform/admin/wp-data-loss-prevention
exceptionsAllowed: true
customerNotes: ""               # volný text plněný per zákazník
appliesTo: tenant               # tenant|environment (env pravidla se vyhodnocují N×)
weight: 10                      # základní váha do score (viz 2.2)
minSnapshotSchema: 1            # od jaké verze snapshot schématu lze vyhodnotit
```

Výsledek vyhodnocení = **finding**: `{ruleId, ruleVersion, scope (tenant|envId), status: Pass|Fail|Partial|NotEvaluated|Excepted, evidence (JSON path do snapshotu + hodnoty), measuredValue, message}`.

### 2.2 Metodika governance/security score

Ne prostý poměr splněných pravidel. Návrh:

1. **Váha pravidla** `w = severityWeight × areaWeight × applicabilityFactor`
   - severityWeight: Critical=10, High=6, Medium=3, Low=1, Informational=0 (informativní pravidla score neovlivňují).
   - areaWeight: konfigurovatelné per oblast (default: DLP 1.2, TenantIsolation 1.2, DefaultEnv 1.1, ostatní 1.0).
   - applicabilityFactor: pravidlo neaplikovatelné (např. Power Pages nejsou) → vyřazeno ze jmenovatele (ne penalizace).
2. **Skóre pravidla** `s ∈ <0;1>`: Pass=1, Fail=0, Partial=naměřený poměr (např. DLP coverage 0.8), Excepted=vyřazeno ze jmenovatele + evidováno.
3. **Skóre oblasti** `AreaScore = 100 × Σ(w·s)/Σ(w)` v rámci oblasti.
4. **Celkové skóre** = vážený průměr oblastí **s cap pravidlem**: existuje-li nesplněné **Critical** pravidlo, celkové score se zastropuje na 59 (tier „At Risk"). Tiery: 90–100 Excellent, 75–89 Good, 60–74 Needs Attention, <60 At Risk.
5. **Trend** = delta score mezi snapshoty + rozpad na „co se zlepšilo/zhoršilo" (diff findings).
6. Score vždy doprovázet **coverage indikátorem** („vyhodnoceno 42/48 pravidel; 6 NotEvaluated kvůli chybějícím oprávněním") — jinak je číslo zavádějící.

### 2.3 Výchozí rule katalog (MVP ~35 pravidel, výběr)

- **PPG-TEN-001..015:** tenant settings (zakázané trial environmenty pro ne-adminy Critical? ne — High; vypnuté self-service developer env dle politiky; sharing s Everyone omezen; production environment creation restricted; AI/Copilot data movement dle geo politiky…)
- **PPG-DLP-001..008:** existence tenant DLP; 100% coverage environmentů; default env pod nejpřísnější policy; new connectors default = Blocked/Non-business; endpoint filtering pro HTTP/SQL; custom konektory klasifikovány; žádné prázdné „allow-all" policy.
- **PPG-ISO-001..002:** tenant isolation zapnutá (inbound i outbound) s explicitní allowlist.
- **PPG-ENV-001..006:** default env přejmenován + omezen; produkční env = Managed; env mají security group; žádné trial env starší 30 dní; DEV/TEST/PROD strategie (heuristika dle názvů — Informational).
- **PPG-SHR-001..003:** žádné appky sdílené na celý tenant v default env (Critical); orphaned apps/flows = 0 (High); appky bez aktivity > 180 dní review (Informational).
- **PPG-AI-001..004:** Copilot přepínače dle politiky zákazníka; agent publishing omezeno (dle dostupnosti dat 🟡).

---

## 3. Rozsah fáze 3 — řízená remediation (design)

### 3.1 Co lze měnit programově (ověřený write přehled)

| Změna | Mechanismus | Rollback |
|---|---|---|
| Tenant settings | `Set-TenantSettings` / BAP PATCH ✅ | Ano — snapshot obsahuje původní hodnotu → inverse operace |
| DLP policy create/update | `New/Set-DlpPolicy`, BAP V2 ✅ | Částečně — návrat k předchozí definici; dopad na běžící flows okamžitý ⚠️ |
| Tenant isolation | `Set-PowerAppTenantIsolationPolicy` ✅ | Ano (konfigurace), dopad na integrace ⚠️ |
| Enable Managed Environment | environment governance update ✅ | Ano (disable), licencování ⚠️ |
| Reassign owner orphaned app | `Set-AdminPowerAppOwner` ✅ | Ano |
| Disable flow | admin flow API 🟡 (SP limit ✅) | Ano (enable) |
| Delete environment | API existuje ✅ | ❌ nevratné → **nikdy auto**, pouze manuální kategorie |

### 3.2 Bezpečný model změn

Pipeline každé remediace: **Finding → Návrh akce → Dry-run (diff: current vs. target JSON) → Schválení (jiná osoba než navrhovatel, 4-eyes) → Provedení / Export skriptu → Verifikační mini-snapshot → Audit event.**

- Žádná změna bez explicitního schválení; auto-apply pouze pro whitelisted pravidla kategorie „automaticky opravitelné" a i tak s approval krokem.
- **Export PowerShell/PAC skriptu** je první-třídní výstup (zákazník provede sám ve svém change managementu) — snižuje požadovaná write oprávnění aplikace na nulu.
- Výjimky: finding lze označit `Excepted` s povinným odůvodněním, platností (expiry) a schvalovatelem; výjimky se reportují zvlášť.

Kategorizace doporučení: **auto** (tenant settings toggle, DLP default group), **skript** (DLP restructuring, isolation allowlist), **manuální** (licenční rozhodnutí, environment strategie, mazání), **informativní** (adopce, hygiena).

---

## 4. Cílová Azure architektura

```
                        ┌────────────────────────────────────────────────┐
                        │                 Zákaznický tenant               │
                        │  Entra ID ── enterprise app (consent)           │
                        │  Power Platform ── SP: RBAC "PP reader"         │
                        └───────────────▲───────────────▲────────────────┘
                                        │ delegated      │ app-only (cert)
┌──────────────┐   HTTPS   ┌────────────┴────────────────┴──────────────┐
│  Uživatel     │─────────▶│  Azure Container Apps (env)                 │
│ (browser)     │          │  ┌──────────────┐  ┌───────────────────┐   │
└──────────────┘          │  │ Web/API app   │  │ Worker (jobs)      │   │
        Entra ID login     │  │ .NET 8 API +  │  │ snapshot collector │   │
                           │  │ React SPA     │  │ rules engine       │   │
                           │  └──────┬───────┘  └────────┬──────────┘   │
                           └─────────┼───────────────────┼──────────────┘
                                     │ managed identity   │
        ┌───────────────┬────────────┼───────────────┬────┼──────────────┐
        │ Azure SQL      │  Key Vault │ Blob Storage  │ Service Bus       │
        │ (metadata,     │ (SP certy, │ (raw snapshot │ (job queue)       │
        │ findings,      │ per-tenant │ JSON, exporty │                   │
        │ score, audit)  │ šifr.klíče)│ PDF/XLSX)     │                   │
        └───────────────┴────────────┴───────────────┴───────────────────┘
                           App Insights + Log Analytics (audit sink, alerts)
```

**Komponenty a rozhodnutí:**

- **Compute: Azure Container Apps** (místo App Service) — jednotný model pro API i worker, škálování na 0 u workeru, snadný přesun na dedikované prostředí per velký zákazník. API: .NET 8 (Microsoft.Identity.Web, Polly retry, System.Text.Json). Frontend: React SPA (MSAL.js).
- **Identita aplikace:** jedna **multi-tenant app registration** (`signInAudience=AzureADMultipleOrgs`) pro sign-in + delegated PPAPI scopes; **druhá app registration** pro app-only daemon (certifikát) — oddělení user-facing a machine identit zjednodušuje consent vysvětlení i revokaci.
- **Storage:**
  - **Azure SQL** — relační metadata, findings, score, audit (viz datový model). Per-tenant izolace: `TenantId` diskriminátor + row-level security policy + oddělené šifrovací klíče; u enterprise zákazníků volitelně dedikovaná DB (sharding pattern „database per customer" připravit v connection factory od začátku).
  - **Blob Storage** — immutable raw snapshoty (JSON, komprimované), exporty; kontejner per zákazník; immutability policy (WORM) pro audit-grade snapshoty; lifecycle management (retention).
  - Cosmos DB není nutná — snapshot je write-once/read-few, Blob+SQL je levnější a jednodušší.
- **Key Vault:** SP certifikáty (per náš daemon, ne per zákazník — zákazník consentuje náš SP), per-tenant data encryption keys (envelope encryption citlivých polí), přístup výhradně přes managed identity, RBAC, soft-delete + purge protection.
- **Background jobs:** Service Bus queue + worker; typy jobů: `SnapshotRun`, `RuleEvaluation`, `ReportExport`, `GraphEnrichment` (orphan detection), `ScheduledSnapshot` (timer). Checkpointing v SQL (`SnapshotRun.Progress`) kvůli throttlingu a resumu.
- **Observabilita:** Application Insights (per-request TenantId dimension), Log Analytics; alerty: fail rate snapshotů, 429 poměr, cert expirace, anomální objem čtení.
- **Export reportů:** PDF (QuestPDF) + XLSX (ClosedXML) generované workerem do Blob, podepsané SAS s krátkou platností.
- **Aplikační role (Entra app roles):** `InternalAdmin` (SoftwareOne ops), `Consultant` (běh snapshotů, návrhy remediace), `CustomerAdmin` (svůj tenant, schvalování remediace, správa výjimek), `Reader` (čtení dashboardů svého tenantu), `Auditor` (read + audit log, bez PII detailů makerů). Autorizace: policy-based, každý request nese TenantContext.

---

## 5. Logický datový model

```
Customer (CustomerId PK, Name, EntraTenantId UNIQUE, Region, DataResidency,
          CreatedAt, Status, DefaultRuleProfileId)

TenantConnection (ConnectionId PK, CustomerId FK, Mode: Delegated|AppOnly,
          AppRegistrationId, SpObjectIdInCustomerTenant, RbacRoleAssignmentId NULL,
          LegacyManagementAppRegistered BIT, CertThumbprint NULL,
          ConsentGrantedBy, ConsentGrantedAt, Status: Active|Revoked|Degraded,
          LastValidatedAt)

Snapshot (SnapshotId PK, CustomerId FK, StartedAt, CompletedAt,
          TriggeredBy (UserId|'scheduler'), Mode, SchemaVersion,
          CoverageJson  -- per sekce: Full|Partial|Failed|Skipped + důvod,
          RawBlobUri, Status, ApiVersionsJson, CapturedIdentity, CapturedRoles)

Environment (EnvironmentRecordId PK, SnapshotId FK, EnvGuid, DisplayName, Type,
          Region, IsDefault, IsManaged, ProtectionLevel, HasDataverse,
          SecurityGroupId NULL, CreatedTime, StateJson, PropertiesJson)

SettingRecord (SettingId PK, SnapshotId FK, Scope: Tenant|Environment,
          EnvGuid NULL, SettingKey, SettingValueJson, Source: API|PS|Derived)

PolicyRecord (PolicyRecordId PK, SnapshotId FK, PolicyGuid, PolicyType: DLP|Isolation|EnvGroupRule,
          DisplayName, Scope, EnvironmentsJson, DefinitionJson)

ConnectorRecord (ConnectorRecordId PK, SnapshotId FK, EnvGuid, ConnectorId,
          DisplayName, Tier, IsCustom, Publisher, DlpClassification: Business|NonBusiness|Blocked|Unclassified,
          CoveringPolicyGuid NULL)

ResourceRecord (ResourceId PK, SnapshotId FK, EnvGuid, Kind: App|Flow|Bot|CustomConnector|Page,
          ResourceGuid, DisplayName, OwnerObjectId, OwnerUpn, OwnerStatus: Active|Disabled|NotFound,
          SharedWithCount, SharedWithEveryone BIT, LastModified, PropertiesJson)

RuleDefinition (RuleId PK, Version PK, Name, Area, Severity, Weight, DefinitionYaml,
          AutoRemediable, RemediationType, DocsUrl, PublishedAt, Deprecated BIT)

RuleProfile (ProfileId PK, Name, CustomerId NULL,  -- NULL = globální default
          OverridesJson  -- per-rule: disabled / změna severity / params)

Finding (FindingId PK, SnapshotId FK, RuleId+Version FK, Scope, EnvGuid NULL,
          Status: Pass|Fail|Partial|NotEvaluated|Excepted, MeasuredValueJson,
          EvidenceJson, Message)

Score (ScoreId PK, SnapshotId FK, Overall, TierName, AreaScoresJson,
          EvaluatedRules, TotalRules, CriticalFailCount, CapApplied BIT)

RemediationAction (ActionId PK, CustomerId FK, FindingId FK, Type: Auto|Script|Manual,
          ProposedByUserId, ProposedAt, DryRunDiffJson, ScriptBlobUri NULL,
          Status: Proposed|Approved|Rejected|Executed|Verified|Failed|RolledBack,
          ApprovedByUserId NULL, ExecutedAt NULL, ResultJson, RollbackScriptUri NULL)

Exception (ExceptionId PK, CustomerId FK, RuleId, Scope, EnvGuid NULL,
          Justification, ApprovedBy, CreatedAt, ExpiresAt, Status)

AuditEvent (AuditId PK, CustomerId NULL, ActorUserId|ActorApp, Action, TargetType,
          TargetId, Timestamp, IpAddress, DetailsJson, CorrelationId)
          -- append-only; replika do Log Analytics
```

Zásady: snapshot data jsou **immutable** (opravy = nový snapshot); PII (UPN makerů) šifrovat sloupcově per-tenant klíčem; `RawBlobUri` drží úplný surový stav pro budoucí re-evaluaci starých snapshotů novými pravidly.

---

## 6. UX — hlavní obrazovky

1. **Tenant portfolio (home, interní role):** karty zákazníků — score, tier, trend šipka, datum posledního snapshotu, stav connection (Active/Degraded/Revoked), CTA „Run snapshot".
2. **Onboarding wizard:** krok 1 údaje zákazníka → krok 2 „Přihlaste se účtem PP Admina zákazníka" (admin consent URL) → krok 3 volitelné zřízení app-only (checkbox „vytvořit RBAC assignment Power Platform reader", zobrazit přesně co se provede) → krok 4 první snapshot s live progress → krok 5 souhrn + consent dokument ke stažení.
3. **Spuštění snapshotu:** výběr rozsahu (full / jen vybrané sekce / jen vybrané environmenty), odhad doby, progress per kolektor, coverage výsledek.
4. **Historie snapshotů:** timeline se score sparkline, badge coverage, akce Compare/Export/Detail.
5. **Dashboard skóre:** celkové score gauge + tier, radar/bar per oblast, top 5 kritických findings, coverage disclaimer, delta vs. minulý snapshot.
6. **Seznam zjištění:** tabulka s filtrací (oblast, severity, status, environment), bulk akce (navrhnout remediaci, označit výjimku), export CSV.
7. **Detail zjištění:** popis + rationale (zákaznický jazyk), evidence (aktuální vs. doporučená hodnota, JSON path), dopad, náročnost, tlačítka: „Navrhnout opravu" / „Vygenerovat skript" / „Výjimka"; odkaz na MS Learn.
8. **Detail nastavení (settings explorer):** strom tenant settings a environment properties s vysvětlivkami (lokalizovaný katalog popisů CZ/EN), search, „co to znamená" tooltips — plní edukativní roli nástroje.
9. **Porovnání snapshotů:** side-by-side diff (settings změny, nové/zaniklé environmenty, DLP změny, score delta), zvýraznění změn mimo nástroj („unmanaged drift").
10. **Export assessment reportu:** volba šablony (executive 2 str. / technický plný), jazyk CZ/EN, branding, generování na pozadí + notifikace.
11. **Remediation centrum:** kanban Proposed → Approved → Executed → Verified; detail akce s dry-run diffem, schvalování (4-eyes), download skriptu, audit trail.

---

## 7. Bezpečnostní a compliance požadavky

- **Least privilege:** delegated = pouze role, kterou uživatel reálně má (aplikace nezvyšuje práva); app-only = RBAC „Power Platform reader" na tenant scope; write oprávnění se zřizují až při adopci fáze 3, samostatným consentem/rolí (Contributor), nikdy defaultně.
- **Consent model:** dvouvrstvý a explicitní — (1) Entra admin consent (vyjmenované delegated scopes + Graph `User.Read`, volitelně `Directory.Read.All` pro orphan detection — zvážit místo toho jen `User.Read.All`), (2) Power Platform RBAC assignment. Onboarding generuje **„Co aplikace čte a proč" dokument**: tabulka endpoint → účel → příklad dat → kde se ukládá → retention. Tento dokument je i prodejní nástroj proti security námitkám.
- **Secrets:** žádné client secrets; certifikáty v Key Vault, rotace 90 dní automatizovaná, managed identity všude, žádné credentials v env proměnných/CI.
- **Minimalizace dat:** neukládat obsah aplikací/flows (definice), pouze metadata; UPN pseudonymizovat v exportech pro role Auditor/Reader; konfigurovatelný „no-PII mód" (ukládat jen objectId).
- **Šifrování:** TLS 1.2+; at rest platform encryption + sloupcové šifrování PII per-tenant klíčem (envelope, klíče v KV); Blob s customer-managed keys volitelně pro enterprise.
- **Audit přístupů:** každé čtení zákaznických dat = AuditEvent (kdo, co, kdy, odkud, correlation id); append-only + export do Log Analytics s retention ≥ 1 rok; přístup interních rolí k zákaznickým datům podléhá JIT schválení.
- **Oddělení zákazníků:** TenantId row-level security v SQL, kontejner per zákazník v Blob, cache klíčované tenantem, testy na cross-tenant leakage v CI; možnost dedikované DB.
- **Rizika multi-tenant SaaS:** kompromitace daemon certifikátu = přístup ke všem consentnutým tenantům → mitigace: cert v KV s HSM, Conditional Access pro SP (workload identity policies), alerting na anomální použití, možnost okamžité revokace per zákazník (smazání role assignment na jejich straně dokumentovat v off-boarding runbooku).
- **GDPR / data residency:** hosting v EU regionu (West Europe / Sweden Central + Germany West Central pár), data zákazníka nepřesouvat mimo EU; DPA se zákazníkem; retention default 24 měsíců snapshotů, konfigurovatelné; právo na výmaz = smazání Customer kaskádou + purge Blob; ROPA záznam. Pozn.: snapshoty obsahují osobní údaje zaměstnanců zákazníka (jména adminů/makerů) — zákazník je správce, my zpracovatel.
