# PPGSM — Power Platform Governance Snapshot Manager
## Research, feasibility a delivery dokumentace — index

**Datum research:** 8. 7. 2026 | Ověřováno proti oficiální Microsoft dokumentaci (Microsoft Learn) k tomuto datu.

| Soubor | Obsah |
|---|---|
| `01-executive-summary-feasibility.md` | Executive summary, feasibility analýza, matice nastavení/API/oprávnění, deep-dive rolí (Global Reader, PP Admin, SP, RBAC, GDAP), architektonické varianty A/B/C, rizika |
| `02-architektura-datovy-model.md` | Rozsah fází 1–3, metodika score, Azure architektura, logický datový model, UX obrazovky, bezpečnost & GDPR |
| `03-low-level-delivery-spec.md` | Struktura řešení, auth implementace (app registrace, onboarding sekvence), kolektory + endpointy, SQL DDL, rules engine, REST API povrch, roadmapa, prvních 10 úkolů, PoC testovací scénář T-01–T-08, PoC checklist |

## TL;DR rozhodnutí

1. **Proveditelné.** Fáze 1 (read-only snapshot) stojí na zdokumentovaných API.
2. **MVP = hybridní model (varianta C):** delegated login PP Admina pro onboarding + první snapshot; app-only service principal s Power Platform RBAC rolí **„Power Platform reader"** (preview) pro plánované snapshoty; fallback legacy management-app registrace (= admin-level, transparentně deklarovat).
3. **Klíčová zjištění:**
   - Global Reader **není** v Power Platform admin centru podporován → čistě čtecí delegated tenant-wide role dnes neexistuje.
   - Power Platform API nepoužívá application permissions; app-only přístup se řídí RBAC rolemi (Reader/Contributor/Owner) přiřazenými service principalu — přiřazení musí provést PP Admin zákazníka.
   - Legacy SP registrace dává službě implicitní práva Power Platform Administratora bez granularity — použitelné, ale komunikačně citlivé.
   - `listtenantsettings` a RBAC jsou preview → snapshoty ukládat jako raw JSON + verzované parsování.
4. **Nejdřív fáze 0 (PoC, 2–3 týdny):** rozhodující je test T-04 — zda RBAC role „reader" pokryje celý MVP čtecí rozsah.
