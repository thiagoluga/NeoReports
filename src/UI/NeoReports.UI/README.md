# NeoReports.UI — the NeoReports UI (Razor Class Library)

The NeoReports web UI as a **.NET 8 Blazor Server Razor Class Library** (see D31/D32 in
`DECISIONS.md`). The design system is applied (tokens, components, app shell) and
**all 17 prototype screens are implemented** in en-US using pure design-system CSS
(no MudBlazor), Geist font, and Tabler icons.

## Mounting in a host

```csharp
builder.Services.AddNeoReportsUI();
...
app.UseNeoReportsUI("/neoreports");   // any non-root base path works
```

UI routes, `_content` static assets and the Blazor hub all live under the base path,
so the host's own endpoints are untouched. `<base href>` is derived from the request
`PathBase` — no extra configuration when the path changes.

## Run

The library has no entry point; use the sample host:

```bash
dotnet run --project samples/08-web-ui
# custom URL:
dotnet run --project samples/08-web-ui -- --NeoReports:UIPath=/reports-admin
```

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

Settings screens (Alerts/Authentication/Plugins/Retention/Audit) were removed — no
accounts/RBAC/notification/plugin system exists to back them. See
`docs/ui-removed-mock-content.md` (D36) for what they showed and what a real version would need.

## Structure

```
NeoReports.UI/
├─ NeoReportsUIExtensions.cs  # AddNeoReportsUI() + UseNeoReportsUI(basePath)
├─ App.razor · _Imports.razor
├─ Pages/               # _Host.cshtml + 19 .razor pages (17 screens + 2 stubs)
├─ Layout/              # AppLayout, Topbar
├─ Components/UI/       # 22 reusable components (see below)
├─ Models/Models.cs     # records + StatusMaps
├─ Services/            # BuilderState (scoped), SampleData
└─ wwwroot/
   ├─ css/tokens.css    # design-system tokens (colors, type, spacing, dark mode)
   ├─ css/neoreports.css# component styles (same classes as the prototype)
   ├─ fonts/            # self-hosted Geist/Geist Mono/Tabler binaries + README.md
   └─ assets/logo-mark.svg
```

### Components/UI (22)
`Button` `Badge` `JobStatusBadge` `Card` `MetricCard` `CatTile` `Switch` `WizardStepper`
`ProgressBar` `PhaseStepper` `FilterBar` `Pill` `Chip` `ChipGroup` `Dropdown` `Banner`
`EmptyState` `Timeline` `DataGrid` `SelectableCard` `ReportCard` `SourceCard`
`FormatCard` `DestinationCard`

## Wiring to the real engine

See **`docs/ui-handoff.md`** for the screen → route → components → endpoint → states table,
responsive breakpoints, and the full Tabler icon inventory. `INeoReportsApiClient`
(`Services/NeoReportsApiClient.cs`) calls the real endpoints where the handoff table says
one exists — reports list, report detail, run/cancel/download, all three job states, plus
the Epic D dynamic-registration endpoints (`capabilities`, `reports` POST/validate/DELETE,
`jobs` list, `jobs/{id}/artifacts`) — falling back to `SampleData` when no engine is mounted
or a call fails. Cells the handoff marks `mock/future` with no real backing were removed rather
than left as `SampleData` (D36) — see `docs/ui-removed-mock-content.md` for what and why.

The **Builder wizard** (`/builder` → `/builder/review`) is the one screen that writes back
to the engine, not just reads it (ADR D33). `BuilderState` (Scoped) holds the real fields —
report name, source type, connection string variable, SQL query, key column, page size,
output columns, and an engine destination — alongside the wizard's cosmetic fields (schedule,
template metadata) that have no `ReportConfig` equivalent and are never sent.
`BuilderConfigMapper` (`Services/BuilderConfigMapper.cs`) turns that state into the same JSON
document shape `POST /api/reports` accepts, deliberately without referencing the engine
assemblies (the UI only ever talks to NeoReports over HTTP). Step 1 checks
`GET /api/capabilities`; if nothing comes back the whole wizard drops into demo mode
(browsable, but Save/Run are disabled) — this only happens when the host never mounted an
engine, so the sample host (`samples/08-web-ui`, UI-only, no `/api`) always demos this way.

## Fidelity notes

- Most form fields (parameters table, resilience knobs, template metadata, schedule) are
  cosmetic (no validation/persistence) — this is a UI starter, not production logic.
- The progress *percentage* on the running-job screen stays a decorative animation (no
  server-side row total to compute a real one against); the counters below it (read/written/
  retries) and the status badge/cancel button are wired to `GET /api/jobs/{id}`.
- Logo is a placeholder. Geist, Geist Mono, and Tabler icons are self-hosted binaries in
  `wwwroot/fonts/` (see that folder's `README.md`) — no CDN or Google Fonts calls at runtime.

## Style rules (non-negotiable)
Sentence case · en-US copy, English technical terms · mono font for technical data ·
primary CTAs black (blue = info/selection) · triple status (color + icon + text) ·
flat (no shadows/gradients; depth via 0.5px borders + `--bg2`) · no emoji · `·` separator ·
dark mode via the existing tokens.
