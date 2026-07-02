# NeoReports — Blazor Server web app (starter)

A runnable **.NET 8 Blazor Server** starting point for the NeoReports web app, mounted at
`/neoreports`. The design system is already applied (tokens, components, app shell) and
**all 17 prototype screens are implemented** in en-US using pure design-system CSS
(no MudBlazor), Geist font, and Tabler icons.

## Run

```bash
cd NeoReports.Web
dotnet run
```

Opens at **http://localhost:5000/neoreports** (mounted via `UsePathBase`).
Requires the **.NET 8 SDK**: https://dotnet.microsoft.com/download

## Screens (17)

| Route | Page | |
|---|---|---|
| `/` | `Dashboard` | metric strip, recent jobs, sources/destinations |
| `/reports` | `Reports` | count strip, filter bar, report cards |
| `/reports/{slug}` | `ReportDetail` | config, schedule + mini calendar, history, permissions |
| `/pipeline` | `PipelineView` | shared source/columns + variant rows |
| `/sources` | `SourcesList` | health-badged source cards |
| `/sources/{name}/explore` | `SourceExplorer` | column picker + data preview |
| `/builder` | `Builder` | step 1 · source picker |
| `/builder/configure` | `BuilderConfigure` | step 2 · query, params, pagination, resilience |
| `/builder/format` | `BuilderFormat` | step 3 · formats + live config |
| `/builder/destination` | `BuilderDestination` | step 4 · destinations + config |
| `/builder/review` | `BuilderReview` | step 5 · save + schedule |
| `/jobs/{id}` | `JobRunning` | live progress (timer), phases, timeline |
| `/jobs/completed` | `JobCompleted` | files, delivered destinations |
| `/jobs/failed` | `JobFailed` | error hero, recovery options, stack trace |
| `/settings/alerts` | `Alerts` | channels + rules |
| `/settings/authentication` | `Authentication` | filter chain, permission matrix, signed URLs |
| `/settings/plugins` | `Plugins` | grouped plugins + marketplace |
| `/settings/retention`, `/settings/audit` | `Retention`, `Audit` | scaffold stubs (keep SubNav from 404ing) |

## Structure

```
NeoReports.Web/
├─ Program.cs · App.razor · _Imports.razor
├─ Pages/               # _Host.cshtml + 19 .razor pages (17 screens + 2 stubs)
├─ Layout/              # AppLayout, Topbar
├─ Components/UI/       # 22 reusable components (see below)
├─ Models/Models.cs     # records + StatusMaps
├─ Services/            # BuilderState (scoped), SampleData
└─ wwwroot/
   ├─ css/tokens.css    # design-system tokens (colors, type, spacing, dark mode)
   ├─ css/neoreports.css# component styles (same classes as the prototype)
   ├─ fonts/README.md   # how to self-host Geist + Tabler
   └─ assets/logo-mark.svg
```

### Components/UI (22)
`Button` `Badge` `JobStatusBadge` `Card` `MetricCard` `CatTile` `Switch` `WizardStepper`
`ProgressBar` `PhaseStepper` `FilterBar` `Pill` `Chip` `ChipGroup` `Dropdown` `Banner`
`EmptyState` `SubNav` `Timeline` `DataGrid` `SelectableCard` `ReportCard` `SourceCard`
`FormatCard` `DestinationCard`

## Wiring to the real engine

See **`../handoff.md`** for the screen → route → components → endpoint → states table,
responsive breakpoints, and the full Tabler icon inventory. `SampleData` is the only
mock layer — replace it with calls to the NeoReports API / EF Core.

## Fidelity notes

- Forms are cosmetic (no validation/persistence) — this is a UI starter, not production logic.
- Job running progress is a decorative timer (60→70% loop); wire `GET /api/jobs/{id}`.
- Logo is a placeholder; Geist + Tabler load from CDN until self-hosted (see `wwwroot/fonts/README.md`).
- I could not run `dotnet build` in the design environment — build locally and report any
  compile errors; the Razor is written to standard .NET 8 conventions.

## Style rules (non-negotiable)
Sentence case · en-US copy, English technical terms · mono font for technical data ·
primary CTAs black (blue = info/selection) · triple status (color + icon + text) ·
flat (no shadows/gradients; depth via 0.5px borders + `--bg2`) · no emoji · `·` separator ·
dark mode via the existing tokens.
