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
| Dashboard | `/` `Dashboard` | MetricCard, Card, DataGrid, JobStatusBadge, ProgressBar, CatTile, Button | jobs strip: **mock/future** (no jobs-list endpoint yet; only `GET /api/jobs/{id}`). Metrics/sources/destinations: **mock/future** | loading (skeleton rows), empty (no recent jobs), error (engine down) |
| Reports list | `/reports` `Reports` | ReportCard, FilterBar, Pill, EmptyState, Button | `GET /api/reports` (client-side filter) | loading, empty (no reports → EmptyState CTA), error |
| Report detail | `/reports/{slug}` `ReportDetail` | Card, MetricCard, Chip, Banner, DataGrid, Timeline, JobStatusBadge, CatTile | `GET /api/reports` (find by name client-side; no per-report endpoint yet). History/metrics/perms/changes: **mock/future** | not-found (bad slug → EmptyState), loading, empty history |
| Pipeline + variants | `/pipeline` `PipelineView` | Card, MetricCard, Badge, CatTile, Button | `GET /api/reports` (pipeline shape from the list; no per-report endpoint yet). Variant runs: **mock/future** | loading, per-variant error/paused |
| Sources list | `/sources` `SourcesList` | SourceCard, FilterBar, Pill, Button | source list + health: **mock/future** (engine exposes reports/jobs, not source registry yet) | loading, empty, per-source error (Diagnose action) |
| Source explorer | `/sources/{name}/explore` `SourceExplorer` | Card, CatTile, Chip, DataGrid-like preview | schema + preview: **mock/future** | loading (preview skeleton), empty (0 columns), error (source unreachable) |
| Builder · 1 Source | `/builder` `Builder` | WizardStepper, SelectableCard, FilterBar, CatTile, Button, BuilderState | source list: **mock/future** | loading, empty (no sources → register CTA) |
| Builder · 2 Configure | `/builder/configure` `BuilderConfigure` | WizardStepper, Card, ChipGroup, Dropdown, Switch, Pill | validate: `POST /api/reports/{name}/run?mode=sync` (dry-run) — **future** | validation error inline on query |
| Builder · 3 Format | `/builder/format` `BuilderFormat` | WizardStepper, FormatCard, ChipGroup, Dropdown, Switch, CatTile | client-side only | none (pure config) |
| Builder · 4 Destination | `/builder/destination` `BuilderDestination` | WizardStepper, DestinationCard, Dropdown, Switch, CatTile | client-side only | none (pure config) |
| Builder · 5 Review | `/builder/review` `BuilderReview` | WizardStepper, Card, Chip, Switch, Banner, Dropdown, CatTile, BuilderState | save: `POST /api/reports` (**future**); run now: `POST /api/reports/{name}/run` | save error (toast), schedule preview |
| Job running | `/jobs/{id}` `JobRunning` | Card, MetricCard, PhaseStepper, Timeline, ProgressBar, CatTile, Badge | `GET /api/jobs/{id}` (poll/stream); cancel: `POST /api/jobs/{id}/cancel` | live updates, connection lost, cancel-in-progress |
| Job completed | `/jobs/completed` `JobCompleted` | Card, MetricCard, PhaseStepper, Timeline, CatTile, Badge, Button | `GET /api/jobs/{id}`; download: `GET /api/jobs/{id}/download` | download error, expired files |
| Job failed | `/jobs/failed` `JobFailed` | Card, MetricCard, PhaseStepper, CatTile, Badge, Button | `GET /api/jobs/{id}`; resume/retry: `POST /api/reports/{name}/run` | partial-output present/absent, resume unavailable |
| Alerts | `/settings/alerts` `Alerts` | SubNav, Card, CatTile, Switch, Badge, Timeline | alert config + activity: **mock/future** | loading, empty (no rules → CTA), channel-disconnected |
| Authentication | `/settings/authentication` `Authentication` | SubNav, Card, CatTile, Switch, Badge, Dropdown | filter chain + policy matrix: **mock/future** | loading, save error |
| Plugins | `/settings/plugins` `Plugins` | SubNav, Card, CatTile, Badge, Pill, Button | plugin registry: **mock/future** | loading, update-available, license-error (Resolve) |
| Retention* | `/settings/retention` `Retention` | SubNav, Card, EmptyState | **future** | scaffold only |
| Audit* | `/settings/audit` `Audit` | SubNav, Card, EmptyState | **future** | scaffold only |

\* Retention/Audit are scaffold stubs so the settings SubNav never 404s — not in the original prototype spec.

### Real API surface (given)
```
POST /api/reports/{name}/run        → async, returns jobId
POST /api/reports/{name}/run?mode=sync → streams the result
GET  /api/reports
GET  /api/jobs/{id}
POST /api/jobs/{id}/cancel
GET  /api/jobs/{id}/download
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
