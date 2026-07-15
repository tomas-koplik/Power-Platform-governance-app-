# Power Platform Governance Snapshot Manager (PPGSM)
## Díl 1: Executive Summary, Feasibility analýza, Matice nastavení, Oprávnění, Architektonické varianty

**Verze:** 1.0 | **Datum research:** 8. 7. 2026 | **Status:** Podklad pro rozhodnutí + zadání PoC
**Autor:** Solution Architecture / Governance research

> Legenda věrohodnosti tvrzení:
> ✅ **Ověřeno** – potvrzeno v oficiální MS dokumentaci k datu 8. 7. 2026, odkaz uveden.
> 🟡 **Částečně ověřeno** – dokumentace existuje, ale chování je preview / nejednoznačné, nutný praktický test.
> 🔴 **Neověřeno** – nutný praktický test v PoC, dokumentace nepotvrzuje.

---

## 1. Executive Summary

### 1.1 Verdikt proveditelnosti

**Aplikace je proveditelná.** Fáze 1 (read-only snapshot) je realizovatelná s vysokou jistotou — všechny klíčové oblasti (tenant settings, environments, DLP, tenant isolation, apps/flows inventory) mají zdokumentovaný programový přístup přes kombinaci:

1. **Power Platform API** (`api.powerplatform.com`) – moderní API, oficiálně podporované, s novým RBAC modelem pro service principals (preview),
2. **legacy BAP API** (`api.bap.microsoft.com`) – pokrývá tenant settings, DLP, tenant isolation; stejné API, které pohání Power Platform admin center,
3. **PowerShell moduly** (`Microsoft.PowerApps.Administration.PowerShell`) a **PAC CLI** jako fallback a jako zdroj pravdy pro remediation skripty.

### 1.2 Doporučená cesta pro MVP

- **Varianta B (multi-tenant Entra ID aplikace + admin consent zákazníka) s app-only přístupem přes Power Platform RBAC roli „Power Platform reader"** je strategicky správný cíl, ale RBAC pro service principals je **v preview** → pro MVP doporučuji **variantu C (hybrid)**:
  - **Onboarding + první snapshot: delegated login** uživatele s rolí **Power Platform Administrator** v zákaznickém tenantu (delegated permissions Power Platform API). Během onboardingu tento uživatel zároveň zaregistruje service principal aplikace do Power Platformy a přiřadí mu RBAC roli.
  - **Opakované/plánované snapshoty: app-only** service principal (client credentials, certifikát v Key Vault).
- Backend: **Azure Container Apps + Azure SQL (per-tenant izolace) + Key Vault + Entra ID multi-tenant app registration**.
- MVP rozsah: tenant settings, environments (vč. managed environments a governance konfigurace), default environment, DLP policies + klasifikace konektorů, tenant isolation, seznam apps/flows/makers s detekcí orphaned resources, základní score. Detailně kap. 6 v Dílu 2.

### 1.3 Největší nejistoty (top 5)

| # | Nejistota | Dopad | Mitigace |
|---|---|---|---|
| 1 | **RBAC role „Power Platform reader" pro SP je preview** — rozsah čtecích operací, které reálně pokrývá, není taxativně vyjmenován pro každý endpoint. ✅ existence ověřena, 🔴 pokrytí per-endpoint neověřeno | Vysoký — určuje, zda app-only režim může běžet least-privilege, nebo musí SP dostat de facto PP Admin práva (legacy model) | PoC test scénář T-04 (Díl 4) |
| 2 | **`listtenantsettings` je preview endpoint** (BAP API) ✅ | Střední — Microsoft může měnit tvar odpovědi; nutná verzovaná deserializace snapshotu | Ukládat raw JSON + verzi API |
| 3 | **Legacy SP registrace (`New-PowerAppManagementApp`) dává SP práva ekvivalentní Power Platform Administrator, bez granularity** ✅ | Vysoký — bezpečnostní námitka zákazníků („read-only nástroj s write právy") | Preferovat nový RBAC model; u legacy cesty transparentně komunikovat + certifikát místo secretu + monitoring |
| 4 | **Cross-tenant použití jedné multi-tenant app** pro BAP/Dataverse — dokumentované scénáře jsou převážně single-tenant; cross-tenant chování registrace management app 🔴 | Vysoký pro SaaS model | PoC test T-06: consent + registrace v druhém (test) tenantu |
| 5 | Audit logy (Purview / Management Activity API) vyžadují **samostatné licence a jiná oprávnění** (Graph `AuditLogsQuery.Read.All` / O365 Management API) 🟡 | Nízký pro MVP (odsunout do fáze 2+) | Mimo MVP |

### 1.4 Co lze číst read-only vs. co vyžaduje vyšší oprávnění

**Read-only dosažitelné (fáze 1):**
- Tenant settings (BAP `listtenantsettings`), environments + properties (vč. governanceConfiguration → managed environment), DLP policies vč. connector groups a endpoint filtering pravidel, unblockable/virtual konektory, tenant isolation policy, seznam apps/flows/makers per environment, environment permissions, custom konektory.
- **ALE:** v Power Platformě prakticky neexistuje tenant-wide „reader" role mimo preview RBAC. **Global Reader NENÍ v Power Platform admin centru podporován** ✅ ([MS Learn – Entra built-in roles](https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference): „Power Platform admin center – Global Reader is not yet supported"). Delegated čtení tedy dnes reálně znamená účet s rolí **Power Platform Administrator** (read/write), nebo per-environment Environment Admin (jen jeho environmenty).

**Vyžaduje write/vyšší oprávnění:**
- Jakákoli remediation (Set-TenantSettings, update DLP, update environment) → Power Platform Administrator (delegated) nebo SP registrovaný legacy cestou / RBAC Contributor+.
- Registrace service principalu do Power Platformy → musí provést člověk-administrátor (SP se nemůže registrovat sám) ✅ ([MS Learn](https://learn.microsoft.com/power-platform/admin/powerplatform-api-create-service-principal)).
- Purview audit → samostatný consent (Graph application permission) + odpovídající licence.
- Dataverse obsah (security roles uvnitř environmentu) → System Administrator/Reader v daném Dataverse, resp. application user.

---

## 2. Feasibility analýza

### 2.1 Přehled programových přístupových cest

| Cesta | Base URL / nástroj | Auth model | Stav | Poznámka |
|---|---|---|---|---|
| **Power Platform API (v2, „PPAPI")** | `https://api.powerplatform.com` | Entra ID; **pouze delegated permissions**; pro SP se místo application permissions přiřazuje **Power Platform RBAC role** (Reader/Contributor/Owner) ✅ | GA API, RBAC pro SP preview | [Authentication v2](https://learn.microsoft.com/power-platform/admin/programmability-authentication-v2), [RBAC tutorial](https://learn.microsoft.com/power-platform/admin/programmability-tutorial-rbac-role-assignment) |
| **Legacy BAP API** | `https://api.bap.microsoft.com/providers/Microsoft.BusinessAppPlatform/...` (+ `api.powerapps.com`, `api.flow.microsoft.com`) | Delegated (scope `https://service.powerapps.com//.default`) nebo SP registrovaný přes `adminApplications` PUT ✅ | Funkční, částečně preview endpoints | Stejné API jako PPAC; SP = implicitní PP Admin, bez granularity ✅ |
| **PowerShell** `Microsoft.PowerApps.Administration.PowerShell` | cmdlety `Get-AdminPowerAppEnvironment`, `Get-TenantSettings`, `Get-DlpPolicy`, `Get-AdminPowerApp`, `Get-AdminFlow`, `Get-PowerAppTenantIsolationPolicy`, ... | Interaktivní user, user+password, nebo SP (po registraci `New-PowerAppManagementApp`) ✅ | GA | SP podpora: environment management, tenant settings, Power Apps management; Flow cmdlety jen tam, kde není potřeba licence ✅ ([docs](https://learn.microsoft.com/power-platform/admin/powershell-create-service-principal)) |
| **PAC CLI** | `pac admin list`, `pac env ...`, `pac admin create-service-principal` | user / SP | GA | Vhodné pro remediation skripty a DevOps, ne jako runtime backend |
| **Power Platform for Admins connector** | wrapper nad BAP API | connection v kontextu uživatele | GA | Relevantní jen pokud by řešení běželo v Power Platformě (CoE-style); pro Azure web app nepoužívat ✅ ([connector docs](https://learn.microsoft.com/connectors/powerplatformforadmins/)) |
| **Microsoft Graph** | `graph.microsoft.com` | delegated/application | GA | Rozlišení identit (users, SP), licence, Entra role; audit (beta `security/auditLog/queries`) |
| **Office 365 Management Activity API / Purview** | `manage.office.com` | application | GA | Power Platform admin activity logy; fáze 2+ |
| **Dataverse Web API** | `https://{org}.crm*.dynamics.com/api/data/v9.2` | delegated / application user | GA | Security roles, systemusers, audit uvnitř environmentu; fáze 2+ |

### 2.2 Kategorizace položek podle dostupnosti

**A. Dostupné programově (read i potenciálně write):**
tenant settings ✅, environments (list/detail/properties/lifecycle) ✅, DLP policies (tenant i environment scope, V2) ✅, unblockable + virtual konektory ✅, tenant isolation policy ✅ (PowerShell `Get-PowerAppTenantIsolationPolicy`; REST 🟡 ověřit), apps/flows/connections/custom connectors per environment ✅, environment permissions ✅, RBAC role definitions/assignments (PPAPI preview) ✅.

**B. Částečně dostupné:**
- **Managed environments / environment groups** – governanceConfiguration je součástí environment objektu; environment groups mají PPAPI endpoints (🟡 preview, ověřit rozsah čtení pravidel skupiny v PoC).
- **Copilot Studio governance** – část je v tenant settings (např. vypnutí Copilot funkcí, generative AI data movement), část jako virtual konektory v DLP ✅ ([Data policies – virtual connectors](https://learn.microsoft.com/power-platform/admin/wp-data-loss-prevention)); dedikovaná agent governance (Agent 365 éra) se rychle vyvíjí 🔴 → snapshot ukládat raw, pravidla přidávat postupně.
- **Kapacita/licence** – PPAPI licensing endpoints (currency allocation, ISV contract) existují 🟡; add-on kapacita per environment částečně v environment properties; kompletní capacity report jako v PPAC UI není plně veřejné API 🔴.
- **Audit logy** – dostupné, ale přes jiný stack (Purview / Management Activity API / Graph beta) s vlastními licencemi a consentem 🟡.

**C. Dostupné pouze nepřímo:**
- **Orphaned apps/flows** – neexistuje přímý endpoint; odvozuje se: owner objectId z app/flow → Graph lookup → user neexistuje/disabled ⇒ orphaned. (Stejný princip používá CoE Starter Kit.)
- **Sharing model / „shared with Everyone"** – z permissions objektů app (`Get-AdminPowerAppRoleAssignment`, resp. BAP permissions endpoint); interpretace „Everyone" vyžaduje logiku nad principal typem.
- **Skutečný efektivní stav default environmentu** – kombinace více zdrojů (environment properties + DLP coverage + maker count).

**D. Pravděpodobně nedostupné / nezdokumentované:**
- Některé UI-only přepínače PPAC (nové preview funkce se často objeví v UI dřív než v `listtenantsettings` odpovědi) 🔴 → princip: **snapshot ukládá kompletní raw JSON tenant settings**, engine hodnotí jen známé klíče; neznámé klíče reportuje jako „nehodnoceno".
- Historická telemetrie PPAC (analytics/usage dashboardy) – není veřejné API v použitelné podobě 🔴.
- Advanced connector policies (ACP) – nové, dokumentace se vyvíjí 🔴 → fáze 2+.

---

## 3. Matice nastavení / API / oprávnění (fáze 1 jádro)

Sloupce: **RO** = read-only dostupnost, **RW** = write/remediation, **Deleg.** = delegated access, **App-only** = SP podpora.

| Oblast | Položka | Proč je důležitá | Zdroj dat | RO | RW | Min. role (delegated) | API permission / consent | Deleg. | App-only | Omezení / poznámky | Dokumentace |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Tenant settings | Kompletní `listtenantsettings` (disable* přepínače, sharing, trials, developer env, Copilot/AI přepínače…) | Základ governance posture celého tenantu | BAP API `POST /providers/Microsoft.BusinessAppPlatform/listtenantsettings?api-version=2020-10-01`; PowerShell `Get-TenantSettings` | ✅ | ✅ (`Set-TenantSettings`) | **Power Platform Admin** (PP Admin); Global Reader nefunguje ✅ | Delegated scope `https://service.powerapps.com//.default`; app-only: SP registrace nebo PPAPI RBAC | ✅ | ✅ (SP auth „tenant settings" explicitně podporováno ✅) | Endpoint je **preview** ✅; tvar odpovědi se může měnit → ukládat raw | [list-tenantsettings](https://learn.microsoft.com/power-platform/admin/list-tenantsettings) |
| Environments | Seznam + detail environmentů (typ, region, Dataverse, security group, states) | Inventarizace, environment strategie | PPAPI `GET /environmentmanagement/environments` (nebo BAP `/scopes/admin/environments`); PS `Get-AdminPowerAppEnvironment` | ✅ | ✅ (create/delete/update) | PP Admin (vše) / Environment Admin (jen své) | dtto | ✅ | ✅ | Environment Admin uvidí jen podmnožinu → snapshot označit jako „partial" | [Environments overview](https://learn.microsoft.com/power-platform/admin/environments-overview) |
| Managed Env. | `governanceConfiguration.protectionLevel`, usage insights, sharing limits, maker welcome | Rozlišení managed/unmanaged = základ mnoha pravidel | properties environment objektu (BAP/PPAPI) | ✅ | ✅ (enable/disable ME) | PP Admin | dtto | ✅ | 🟡 ověřit v PoC | Sharing limits detaily mohou být v samostatných sub-objektech | [Managed environments](https://learn.microsoft.com/power-platform/admin/managed-environment-overview) |
| Environment groups | Skupiny + pravidla (rules) skupin | Centralizovaná governance, AI/data movement pravidla | PPAPI governance/environmentGroups endpoints (preview) | 🟡 | 🟡 | PP Admin | PPAPI delegated scope / RBAC role | ✅ | 🟡 | Preview; ověřit čtení rules per group | [Environment groups](https://learn.microsoft.com/power-platform/admin/environment-groups) |
| Default environment | Identifikace + governance stav | Nejrizikovější prostor tenantu | environment list (`isDefault=true`) + kombinace DLP/ME | ✅ | částečně | PP Admin | dtto | ✅ | ✅ | Odvozená oblast (composite) | [Secure default env](https://learn.microsoft.com/power-platform/guidance/adoption/secure-default-environment) |
| DLP | Tenant + environment DLP policies, connector groups (Business/Non-business/Blocked), connector action rules, endpoint filtering | Klíčový bezpečnostní guardrail | BAP API `apiPolicies` (V2); PS `Get-DlpPolicy`; connector „Power Platform for Admins" (jen info) | ✅ | ✅ (`New/Set/Remove-DlpPolicy`) | PP Admin (tenant-scope); Env Admin jen env-scope policies ✅ | dtto | ✅ | ✅ 🟡 (SP docs explicitně jmenují env mgmt, tenant settings, Power Apps; DLP ověřit v PoC) | V2 operace; staré DLP operace deprecated ✅ | [DLP](https://learn.microsoft.com/power-platform/admin/wp-data-loss-prevention), [PS](https://learn.microsoft.com/power-platform/admin/powerapps-powershell) |
| Konektory | Seznam konektorů per env, custom konektory, unblockable list, virtual konektory | Klasifikační pokrytí DLP; shadow-IT custom konektory | BAP/PowerApps API `GET apis?$filter=environment eq '{env}'`; unblockable/virtual list endpoints ✅ | ✅ | n/a | PP Admin / Env Admin | dtto | ✅ | 🟡 | Nutné pro výpočet „unclassified connectors" | [connector docs](https://learn.microsoft.com/connectors/powerplatformforadmins/) |
| Tenant isolation | Inbound/outbound cross-tenant restrikce + allowlist | Ochrana proti exfiltraci přes cizí tenanty | PS `Get-PowerAppTenantIsolationPolicy`; BAP REST 🟡 | ✅ | ✅ (`Set-...`) | PP Admin | dtto | 🟡 | 🟡 | REST tvar ověřit v PoC (PS je jistota) | [Cross-tenant restrictions](https://learn.microsoft.com/power-platform/admin/cross-tenant-restrictions) |
| Apps/Flows | Inventář, vlastníci, sdílení, last modified | Detekce orphaned, over-shared, stale resources | PS `Get-AdminPowerApp`, `Get-AdminFlow`, `Get-AdminPowerAppRoleAssignment`; BAP/PowerApps+Flow API | ✅ | částečně (reassign owner, disable flow) | PP Admin | dtto; Flow API pro SP jen bez-licenční operace ✅ | ✅ | 🟡 (Flow část omezená ✅) | Velké tenancy → paginace + throttling; plánovat inkrementální sběr | [PowerShell for admins](https://learn.microsoft.com/power-platform/admin/powerapps-powershell) |
| Env role/adminí | Environment permissions, admin listy | Least privilege kontrola | BAP environment permissions; Dataverse `systemuserroles` pro Dataverse env | ✅ | ✅ | PP Admin; uvnitř Dataverse System Admin | Dataverse: `user_impersonation` delegated / application user | ✅ | 🟡 | Dataverse část = per-environment volání, fáze 2 | [Security roles](https://learn.microsoft.com/power-platform/admin/security-roles-privileges) |
| Copilot Studio / AI | Tenant-level AI přepínače, generative AI data movement, Copilot virtual konektory v DLP, agent governance | Rychle rostoucí compliance téma (EU AI Act argumentace) | tenant settings + DLP virtual connectors + environment group rules | 🟡 | 🟡 | PP Admin | dtto | ✅ | 🟡 | Oblast se rychle mění → raw snapshot + postupná pravidla | [Copilot Studio security & governance](https://learn.microsoft.com/microsoft-copilot-studio/security-and-governance) |
| Power Pages | Seznam webů, security stav | Veřejná expozice dat | PS `Get-AdminPowerPage*` / Power Pages API 🟡 | 🟡 | 🟡 | PP Admin | dtto | ✅ | 🔴 | Fáze 2; jen pokud zákazník Power Pages používá | [Power Pages admin](https://learn.microsoft.com/power-pages/admin/admin-overview) |
| Audit | Power Platform administrative logs, activity logging | Forenzní stopa, detekce změn mimo nástroj | O365 Management Activity API / Purview Audit Search (Graph beta) | ✅ (s licencí) | n/a | Purview role / Graph app permission `AuditLogsQuery.Read.All` 🟡 | samostatný consent | ✅ | ✅ | Jiný auth stack + licence; **fáze 2+** | [Purview auditing](https://learn.microsoft.com/purview/audit-search) |
| Kapacita/licence | Storage kapacita, add-ons, currency allocations | Kontext doporučení (ME vyžaduje premium licence) | PPAPI licensing endpoints 🟡; environment addons | 🟡 | 🟡 | PP Admin | PPAPI scope | ✅ | 🟡 | Kompletní capacity report jako v UI není veřejný 🔴; **fáze 2** | [Licensing API](https://learn.microsoft.com/power-platform/admin/programmability-overview) |

---

## 4. Ověření oprávnění (role deep-dive)

| Role / identita | Co reálně umožňuje pro náš scénář | Verdikt pro fázi 1 (read-only snapshot) |
|---|---|---|
| **Global Reader** | ❌ **Není podporován v Power Platform admin centru ani pro BAP data** ✅ ([Entra permissions reference](https://learn.microsoft.com/entra/identity/role-based-access-control/permissions-reference); potvrzeno i MS Q&A). Nevidí environments, capacity, settings. | **Nepoužitelný.** Toto je zásadní zjištění: „read-only auditor s Global Reader" scénář dnes nefunguje. |
| **Global Administrator** | Plný přístup; automaticky Power Platform admin práva. | Funguje, ale **nedoporučovat** — zbytečně vysoká privilegia, špatný signál zákazníkovi. |
| **Power Platform Administrator** | Plný admin nad Power Platform: všechny environmenty, tenant settings, DLP, kapacita; může spravovat bez licence (administrative access mode) ✅ ([docs](https://learn.microsoft.com/power-platform/admin/global-service-administrators-can-administer-without-license)). Pozn.: unlicencovaný admin se musí min. 1× přihlásit do PPAC, než fungují admin API/konektory ✅. | **Minimální prakticky funkční role pro kompletní tenant-wide snapshot v delegated režimu.** Je read/write — least-privilege čistě čtecí delegated role dnes neexistuje (mimo preview RBAC). |
| **Dynamics 365 Administrator** | Správa environmentů souvisejících s D365; historicky viděl podmnožinu; Microsoft roli postupně omezuje vůči Power Platform管理 scope 🟡 | Nepoužívat jako cílovou roli; chování ověřit v PoC jen pokud to zákazník vyžaduje. |
| **Environment Admin** | Jen environmenty, kde je adminem: environment properties, env-scope DLP, permissions ✅ (connector docs: „Environment admins will only have access to data and operations on their environments") | Použitelný pro **scoped assessment jednoho environmentu**, ne pro tenant snapshot. Aplikace musí umět „partial snapshot" režim. |
| **System Administrator (Dataverse)** | Práva uvnitř konkrétního Dataverse (security roles, users, audit). Nedává tenant-level přístup. | Relevantní až pro fázi 2 (Dataverse security deep-dive). |
| **Service principal – legacy registrace** (`New-PowerAppManagementApp` / BAP `adminApplications` PUT) | SP je „treated like a user with Power Platform Administrator role assigned; granular roles can't be assigned" ✅. Funguje pro environment management, tenant settings, Power Apps management; Flow jen bez-licenční operace ✅. Registraci musí provést lidský admin (user context), SP se nemůže registrovat sám ✅. | Funkční app-only cesta, ale **fakticky admin-level** → komunikačně citlivé. |
| **Service principal – Power Platform RBAC (preview)** | PPAPI: „don't use application permissions; assign RBAC role (Contributor or Reader)" ✅. Role: **Power Platform reader** (`c886ad2e-27f7-4874-8381-5849b8d8a090`), contributor, owner, RBAC administrator; scope tenant / environment group / environment ✅. Přiřazení provádí PP Admin nebo PP RBAC Administrator ✅ ([RBAC tutorial](https://learn.microsoft.com/power-platform/admin/programmability-tutorial-rbac-role-assignment), [RBAC overview](https://learn.microsoft.com/power-platform/admin/security/role-based-access-control)). Reader = permissions končící `.Read` ✅. | **Strategicky nejlepší cesta pro app-only read-only.** Riziko: preview + reálné pokrytí read endpointů (tenant settings? DLP?) není taxativně potvrzeno per-endpoint 🔴 → PoC test T-04. |
| **Delegated admin / GDAP** | GDAP (Graph delegatedAdminRelationships) řeší M365/Entra role cross-tenant pro CSP partnery. Mapování GDAP → Power Platform Administrator role v zákaznickém tenantu je možné (PP Admin je Entra role), ale interakce GDAP identit s BAP API není v PP dokumentaci explicitně popsána 🔴. | **Neblokovat MVP na GDAP.** MVP = přímý consent + účet/SP v zákaznickém tenantu. GDAP prozkoumat ve fázi 4 (produktizace pro CSP partnery), s praktickým testem. |

**Závěr — minimální oprávnění pro fázi 1:**
- **Delegated:** Power Platform Administrator (nic nižšího tenant-wide dnes nefunguje; Global Reader vyloučen ✅).
- **App-only:** preferenčně SP s **Power Platform reader** RBAC rolí na tenant scope (preview, ověřit pokrytí); fallback legacy management app registrace (= admin-level, transparentně deklarovat).

---

## 5. Architektonické varianty

### Varianta A — Delegated user login

Uživatel (konzultant SoftwareOne nebo zákazníkův admin) se přihlásí do aplikace svým účtem v zákaznickém tenantu; aplikace volá API v jeho kontextu (OBO/auth code flow).

- **Potřebné role:** Power Platform Administrator pro tenant-wide snapshot ✅. Global Reader **nestačí** ✅. Environment Admin stačí jen pro partial snapshot vlastních environmentů ✅.
- **Delegated permissions:** Power Platform API delegated scopes (admin consent doporučen, jinak per-user consent prompt) + `User.Read` (Graph, profil) ✅.
- **Výhody:** nejrychlejší onboarding (žádná registrace SP), přirozený consent model, ideální pro one-shot assessment workshop.
- **Limity:** vyžaduje přítomnost člověka + MFA (žádné plánované snapshoty), token lifetime, výsledky závisí na rolích konkrétního uživatele → snapshot musí ukládat „captured-as" identitu a role; auditovatelnost vázaná na osobní účet.

### Varianta B — Multi-tenant Entra ID app + tenant-wide admin consent (app-only)

- **Application permissions:** Power Platform API **application permissions nepodporuje** — oficiální model je **RBAC role assignment pro SP** ✅ („For service principal identities, don't use application permissions. Instead… assign it an RBAC role (Contributor or Reader)" — [Authentication v2](https://learn.microsoft.com/power-platform/admin/programmability-authentication-v2)).
- **App-only funguje:** ano, dvěma cestami: (1) nový RBAC model (preview), (2) legacy `adminApplications` registrace = SP jako PP Admin ✅.
- **Explicitní registrace v Power Platformě:** **ano, je nutná** (legacy cesta) — SP musí být zaregistrován jako management app lidským adminem; u RBAC cesty musí PP Admin vytvořit role assignment ✅. Multi-tenant nuance: consent vytvoří enterprise application v zákaznickém tenantu; registraci/role assignment pak provádí zákazníkův admin proti **object ID lokálního service principalu** — 🔴 cross-tenant průchod ověřit v PoC (T-06).
- **Secrets:** výhradně **certifikát** (ne client secret) uložený v **Azure Key Vault**, ideálně s managed identity přístupem backendu; rotace certifikátu automatizovaná; žádné secrets v konfiguraci.
- **Výhody:** plánované snapshoty, drift detection, nezávislost na lidech, škáluje na desítky zákazníků.
- **Limity:** těžší onboarding (dva kroky: Entra consent + PP registrace/role), preview RBAC, u legacy cesty admin-level práva SP; Flow API omezení pro SP ✅.

### Varianta C — Hybrid (doporučeno pro MVP)

1. **Onboarding wizard (delegated):** zákazníkův PP Admin se přihlásí → udělí Entra consent → aplikace jeho tokenem provede **první snapshot** a zároveň (s jeho potvrzením) **zaregistruje SP / vytvoří RBAC assignment „Power Platform reader"** pro budoucí app-only běhy.
2. **Scheduled snapshoty (app-only):** background job s certifikátem z Key Vault; pokud RBAC reader nepokryje některé endpointy, tyto sekce se označí „vyžaduje delegated refresh" a doplní se při příštím lidském přihlášení.
- **Proveditelnost:** technicky ano — přesně tento vzor dokumentace předpokládá (registrace SP vyžaduje lidského admina ✅; poté SP běží samostatně ✅).
- **Onboarding zákazníka:** 15–30 min řízený wizard; výstupem je i „consent dokument" (co přesně bylo uděleno, viz Díl 2 kap. bezpečnost).
- **Bezpečnostní/provozní dopady:** dva credential typy (user token krátkodobě v paměti, SP certifikát v KV); degradační režim (SP odvolán → aplikace přejde do „delegated-only"); jasný off-boarding (smazání role assignmentu + enterprise app + dat).

### Doporučení

| Fáze | Varianta |
|---|---|
| PoC + MVP (fáze 0–1) | **C** — delegated pro onboarding/ad-hoc, app-only (RBAC reader, fallback legacy) pro scheduled |
| Cílový produkt (fáze 3–4) | **B jako primární** (jakmile RBAC reader vyjde z preview), delegated ponechat pro onboarding a remediation approval |

---

## 6. Rizika a otevřené otázky (konsolidace)

1. **Preview API drift** — `listtenantsettings`, RBAC, environment groups jsou preview ✅ → mitigace: raw JSON storage, verzování kolektorů, contract testy proti test tenantu v CI.
2. **Global Reader gap** ✅ — zákazníci budou očekávat čistě čtecí delegated roli; neexistuje → nutná komunikační příprava (one-pager „proč PP Admin / proč RBAC reader SP").
3. **SP = implicitní admin (legacy)** ✅ — security review zákazníka může blokovat; mitigace: RBAC reader preferovaně, certifikát, Conditional Access na SP, transparentní seznam volaných endpointů.
4. **Throttling** — BAP/PowerApps API mají rate limity (429 + Retry-After); velké tenancy (tisíce apps/flows) → kolektor musí mít backoff, checkpointing, inkrementální režim. Konkrétní limity nejsou plně publikované 🔴 → změřit v PoC.
5. **Flow API pro SP omezené** ✅ — flows inventář v app-only režimu může být neúplný → označovat coverage per sekce snapshotu.
6. **Cross-tenant consent flow** 🔴 — end-to-end test nutný (T-06).
7. **Interpretace best practices** — některá pravidla jsou kontextová (např. „default environment jen pro personal productivity") → engine musí podporovat výjimky s odůvodněním a per-zákazník profil přísnosti.
8. **Rollback** — část změn (smazání environmentu, DLP změny s okamžitým dopadem na běžící flows) je fakticky nevratná nebo disruptivní → remediation model „export skriptu > přímé provedení" pro rizikové akce (Díl 3).
9. **GDPR / data residency** — snapshoty obsahují osobní údaje (jména/UPN makerů a adminů) → minimalizace, EU region, retention policy, DPA (Díl 2).
10. **Rychlý vývoj AI governance** (Copilot Studio, agenti) — pravidla stárnou; rule katalog musí být verzovaný a aktualizovatelný bez release aplikace.
