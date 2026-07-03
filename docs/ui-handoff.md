# NeoReports Web App — implementation handoff

Companion to `README.md`. This is the map a developer (or Claude Code) needs to wire
the starter to the real NeoReports engine: which screen calls which endpoint, which
states each must handle, responsive behavior, and the icon inventory.

All 17 prototype screens are implemented as `.razor` pages. Copy assumes en-US UI copy,
pure design-system CSS (no MudBlazor), Geist font, Tabler icons.

---

## (a) Screen → route → components → endpoint → states

`mock/future` = data not yet exposed by the engine; the page renders sample data and must
degrade gracefully once a real endpoint exists.

| Screen | Route (`.razor`) | Components used | Endpoint (real API) | States to handle |
|---|---|---|---|---|
| Dashboard | `/` `Dashboard` | MetricCard, Card, DataGrid, JobStatusBadge, ProgressBar, CatTile, Button | jobs strip: `GET /api/jobs?limit=8`; metric cards (jobs today, success rate, records exported, avg duration) computed client-side from `GET /api/jobs?since=<today-utc>&limit=200` (Epic D / D7). Sources/destinations: **mock/future** | empty (no recent jobs → EmptyState), demo fallback (engine down) |
| Reports list | `/reports` `Reports` | ReportCard, FilterBar, Pill, EmptyState, Button | `GET /api/reports` (client-side filter); count strip (running/failed) computed from `GET /api/jobs?limit=200` when live, "active schedules"/"paused" dropped (Epic D / D6, follow-up fix) | loading, empty (no reports → EmptyState CTA), error |
| Report detail | `/reports/{slug}` `ReportDetail` | Card, MetricCard, Chip, Banner, DataGrid, Timeline, JobStatusBadge, CatTile | `GET /api/reports/{name}` (Epic D / D4 — columns, formats, destinations, retry/failure strategy, origin chip). History: `GET /api/jobs?report={name}&limit=200` (Epic D / D7), table shows the first 10. Metric strip (total runs/success rate/avg duration) computed from the same call; "Next run" shows "Not scheduled" when live (Epic D / D6, follow-up fix). Delete: `DELETE /api/reports/{name}` (Epic D / D8, only when `deletable`). Perms/changes: **mock/future** | not-found (bad slug → EmptyState), empty history (→ EmptyState), delete error (danger banner) |
| Pipeline + variants | `/pipeline` `PipelineView` | Card, MetricCard, Badge, CatTile, Button | **mock/future** — the whole screen (single fixed "regional-sales" pipeline, no route param to select a report). Variants are explicitly post-MVP (D23); wiring the shared source/columns section alone isn't meaningful without a way to pick which report's pipeline to show, so this stays fully SampleData (Epic D / D8 decision) | loading, per-variant error/paused |
| Sources list | `/sources` `SourcesList` | SourceCard, FilterBar, Pill, Button | provider type cards: `GET /api/capabilities` (Epic D / D9 — additive above the decorative catalog; no per-source name/health/latency since the engine has no source registry, only provider *types*). Decorative source list + health: **mock/future** | loading, empty, per-source error (Diagnose action) |
| Source explorer | `/sources/{name}/explore` `SourceExplorer` | Card, CatTile, Chip, DataGrid-like preview | schema + preview: **mock/future** (introspection is security-sensitive — needs its own ADR) | loading (preview skeleton), empty (0 columns), error (source unreachable) |
| Builder · 1 Source | `/builder` `Builder` | WizardStepper, SelectableCard, FilterBar, CatTile, Button, BuilderState | source catalog: **mock/future**; "Engine source type" card (real `<select>` bound to `Wizard.SourceType`, options from `GET /api/capabilities`) + demo-mode gate (Epic D / D6, follow-up fix) | loading, empty (no sources → register CTA), demo-mode banner when capabilities is empty |
| Builder · 2 Configure | `/builder/configure` `BuilderConfigure` | WizardStepper, Card, ChipGroup, Dropdown, Switch, Pill | "Engine configuration" card (report name, connection string variable, SQL query, key column, page size, output columns) bound to `BuilderState`; validate: `POST /api/reports/validate` (Epic D / D6). Recap card shows the real `Wizard.SourceType`/connection variable, not a fixed "SQL Server" string (Epic D / D6, follow-up fix). Resilience card's max attempts/backoff/base delay/jitter/on-failure are real, sent as `resilience.*` (D34) | validation error inline (danger banner); parameters/pagination stay cosmetic; Resilience's "retry on errors" and "abort when" threshold stay cosmetic (no per-exception filter or expression evaluator in the engine) |
| Builder · 3 Format | `/builder/format` `BuilderFormat` | WizardStepper, FormatCard, ChipGroup, Dropdown, Switch, CatTile | `Wizard.Formats` (already real format ids — csv/xlsx) feeds `outputs[]` directly | none (pure config) |
| Builder · 4 Destination | `/builder/destination` `BuilderDestination` | WizardStepper, DestinationCard, Dropdown, Switch, CatTile | "Engine destination" card (type from `GET /api/capabilities`, path template) bound to `BuilderState`, shown only when the engine is available (Epic D / D6); the decorative catalog (SharePoint/email/…) stays cosmetic | none (pure config) |
| Builder · 5 Review | `/builder/review` `BuilderReview` | WizardStepper, Card, Chip, Switch, Banner, Dropdown, CatTile, BuilderState | save: `POST /api/reports` (`201`, `409` if the name exists); run now: `POST /api/reports/{name}/run` then `GET /api/jobs/{id}` (Epic D / D6). Summary's Source/Columns/Destinations rows read real `BuilderState` fields instead of a stale `Wizard.Destinations` set and fixed placeholders (Epic D / D6, follow-up fix) | save error (danger banner, incl. `409` name conflict); Save/Run disabled in demo mode; schedule (D35, deferred)/parameters/estimate stay cosmetic |
| Job running | `/jobs/{id}` `JobRunning` | Card, MetricCard, PhaseStepper, Timeline, ProgressBar, CatTile, Badge | `GET /api/jobs/{id}` (poll/stream); cancel: `POST /api/jobs/{id}/cancel`. Configuration card (formats/buffer/retry policy) and Destinations card read `GET /api/reports/{name}` once resolved, fetched once per report name rather than every poll tick; "Worker" row dropped (Epic D / D6, follow-up fix). Rate/s computed from `Stats.RecordsWritten` / elapsed since `StartedAt`, peak tracked across polls (Epic D / D6, follow-up); "Memory" stays a fixed placeholder (no per-job memory tracking in the engine) | live updates, connection lost, cancel-in-progress |
| Job completed | `/jobs/completed` `JobCompleted` | Card, MetricCard, PhaseStepper, Timeline, CatTile, Badge, Button | `GET /api/jobs/{id}`; files: `GET /api/jobs/{id}/artifacts` (Epic D / D5); download: `GET /api/jobs/{id}/download`. Configuration/Destinations cards read `GET /api/reports/{name}`; "Worker" and "Run by" rows dropped (Epic D / D6, follow-up fix) | download error, expired files |
| Job failed | `/jobs/failed` `JobFailed` | Card, MetricCard, PhaseStepper, CatTile, Badge, Button | `GET /api/jobs/{id}`; resume/retry: `POST /api/reports/{name}/run` | partial-output present/absent, resume unavailable |

**Removed (D36):** Alerts, Authentication, Plugins, Retention, Audit (`/settings/*`) — no
accounts/RBAC/notification/plugin system exists to back them; see
`docs/ui-removed-mock-content.md` for what they showed and what a real version would need.

### Real API surface (given)
```
POST /api/reports/{name}/run        → async, returns jobId
POST /api/reports/{name}/run?mode=sync → streams the result
GET  /api/reports
GET  /api/reports/{name}            → full report definition (Epic D / D4)
POST /api/reports                   → register a dynamic report (Epic D / D2)
POST /api/reports/validate          → dry-run compile, never registers (Epic D / D2)
DELETE /api/reports/{name}          → remove a dynamic report; 409 for code-registered (Epic D / D2)
GET  /api/capabilities              → registered source/format/destination type ids (Epic D / D2)
GET  /api/jobs?status=&report=&since=&limit=&offset= → list jobs (Epic D / D3)
GET  /api/jobs/{id}
POST /api/jobs/{id}/cancel
GET  /api/jobs/{id}/download
GET  /api/jobs/{id}/artifacts        → finished output files, name/mime/size (Epic D / D5)
```
Everything else (aggregate dashboard metrics, source health, alert/auth/plugin config)
is `mock/future` — do **not** invent endpoints; wire these once the engine exposes them.

---

## (b) Responsive breakpoints

The prototype is desktop-first (dense technical UI). Recommended breakpoints — none are
implemented in the starter yet; add them as `@media` blocks in `neoreports.css`:

| Token | Width | Behavior |
|---|---|---|
| `xl` | ≥ 1280px | Full layout. `grid-4` = 4 cols, `grid-2`/`grid-1-5`/`grid-1-4` as declared. |
| `lg` | 1024–1279px | `grid-4` → 2 cols; charts stack under 1.5fr/1fr where cramped. |
| `md` | 768–1023px | Two-column grids (`grid-2`, `grid-1-5`, `grid-1-4`) collapse to 1 col. Topbar nav stays. |
| `sm` | < 768px | Topbar nav → hamburger menu; report/source card body grids stack to 1 col; tables get horizontal scroll (`overflow-x:auto` wrapper); wizard stepper becomes a compact "Step 3 of 5" label. |

Fixed minimums to preserve: hit targets ≥ 44px on touch; table cells keep `white-space:nowrap`
on mono/numeric columns and scroll horizontally rather than wrap.

---

## (c) Tabler icons used

Self-hosted (`_Host.cshtml` links `wwwroot/css/tabler-icons.min.css`; see `wwwroot/fonts/README.md`
for the binaries and how to refresh them). Full set
referenced across pages + components:

**Nav / chrome:** `search` · `bell` · `dots-vertical` · `chevron-down` · `arrow-right` · `arrow-left` · `arrow-up` · `external-link` · `x` · `plus` · `check` · `minus`

**Status / semantic:** `player-play` · `player-pause` · `player-stop` · `circle-check` · `alert-triangle` · `alert-octagon` · `clock` · `clock-play` · `clock-exclamation` · `loader-2` (spin) · `flag-check` · `refresh` · `refresh-alert`

**Category tiles:** `database` · `table` · `file-text` · `file-spreadsheet` · `file` · `file-alert` · `file-certificate` · `braces` · `code` · `cloud-upload` · `stack-2` · `bolt` · `bell-ringing` · `mail` · `webhook` · `puzzle` · `cpu` · `arrows-transfer-up` · `git-branch`

**Brand glyphs:** `brand-aws` · `brand-azure` · `brand-office` · `brand-google-drive` · `brand-postgresql` · `brand-mysql` · `brand-mongodb` · `brand-kafka` · `brand-slack` · `brand-teams`

**Forms / detail:** `pencil` · `trash` · `download` · `upload` · `eye` · `link` · `settings` · `tool` · `filter` · `tag` · `arrows-sort` · `heart-rate-monitor` · `grip-vertical` · `math-function` · `arrow-bear-right` · `files` · `info-circle` · `calendar` · `calendar-month` · `calendar-x` · `columns` · `adjustments-horizontal` · `calculator` · `shield` · `shield-check` · `lock` · `key` · `user-search` · `sparkles` · `bookmark` · `rotate` · `activity` · `report` · `report-analytics` · `report-off` · `layout-dashboard` · `tools` · `history` · `clock-cog` · `clock-off` · `shopping-bag`

If an icon name drifts between Tabler versions, pin `@tabler/icons-webfont@3.30.0`
(the version the starter references) or check https://tabler.io/icons.
