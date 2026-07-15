# Power Platform Governance Snapshot Manager (PPGSM)

PPGSM captures read-only governance snapshots of a customer's Microsoft Power Platform tenant (tenant settings, environments, DLP policies, apps, flows, sharing, ownership), evaluates them against an attested rule catalog, and produces an auditable governance score with evidence-backed findings, exports, and review-first remediation proposals.

**Live demo:** <https://tomas-koplik.github.io/Power-Platform-governance-app-/> — the web UI running on the mock data adapter. No real tenant data; the API, collectors, and Azure infrastructure are not part of the demo.

## Repository layout

| Path | Contents |
|---|---|
| [`ppgsm/`](ppgsm/) | The application: .NET 8 API/Worker/Collectors, EF Core + Azure SQL RLS data layer, React web app, Bicep infrastructure, versioned rule catalog, and tests. See [`ppgsm/README.md`](ppgsm/README.md). |
| [`docs/design/`](docs/design/) | Research, feasibility analysis, architecture, and the low-level delivery specification (including the T-01–T-08 PoC gate scenarios). |
| [`.github/workflows/`](.github/workflows/) | CI (build/test/publish, Bicep lint, image provenance), gated Azure deploy/rollback, and the GitHub Pages demo publish. |

## Quick start

```powershell
# Backend (requires .NET 8 SDK)
cd ppgsm
dotnet test .\Ppgsm.sln
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project .\src\Ppgsm.Api\Ppgsm.Api.csproj   # Swagger at /swagger

# Web app (requires Node 22)
cd ppgsm\src\Ppgsm.Web
npm install
npm run dev   # proxies /api to http://localhost:5080
```

The static demo is built with `npm run build -- --mode demo`, which is the only mode allowed to ship the mock data adapter; production builds reject it.

## Status

The solution builds and its test suites pass on the pinned .NET 8 SDK. Production deployment remains gated on live-tenant verification work: the collector PoC gates (T-01–T-08) must be evidenced in a disposable tenant, and Entra app registrations, SQL role bootstrap, and DR evidence are manual prerequisites — see the operational runbooks under [`ppgsm/docs/operations/`](ppgsm/docs/operations/).
