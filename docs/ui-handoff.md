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
| Dashboard | `/` `Dashboard` | MetricCard, Card, DataGrid, JobStatusBadge, ProgressBar, CatTile, Button | jobs strip: `GET /api/jobs?limit=8`; metric cards (jobs today, success rate, records exported, avg duration) computed client-side from `GET /api/jobs?since=<today-utc>&limit=200` (Epic D / D7); "Recent files" from `GET /api/jobs/{id}/artifacts` (D6 follow-up). "Sources" card removed (D36) | empty (no recent jobs → EmptyState), "Engine unreachable" (D36 — no demo fallback) |
| Reports list | `/reports` `Reports` | ReportCard, FilterBar, Pill, EmptyState, Button | `GET /api/reports` (client-side filter, `null` = unreachable vs empty list = really empty, D36); count strip (running/failed) computed from `GET /api/jobs?limit=200` when live, "active schedules"/"paused" dropped (Epic D / D6, follow-up fix) | loading, empty (no reports → EmptyState CTA), "Engine unreachable" (D36) |
| Report detail | `/reports/{slug}` `ReportDetail` | Card, MetricCard, Chip, Banner, DataGrid, JobStatusBadge, CatTile | `GET /api/reports/{name}` is now the *only* data path — no more sample-slug lookup (D36). Columns/formats/destinations/retry/origin (Epic D / D4). History: `GET /api/jobs?report={name}&limit=200` (Epic D / D7), table shows the first 10, status/route/progress via `JobRowFormatter`. Metric strip (total runs/success rate/avg duration) computed from the same call; "Next run" always shows "Not scheduled". Delete: `DELETE /api/reports/{name}` (Epic D / D8, only when `deletable`). Edit (`Deletable` only): navigates to `/builder?edit={name}` — see Builder · 1 Source (2026-07 UI audit). Permissions, Recent changes, and Schedule cards, and the Parameters row, were removed (D36) | "Report not found" (bad slug or engine unreachable — same EmptyState, D36), empty history (→ EmptyState), delete error (danger banner) |
| Sources list | `/sources` `SourcesList` | Card, EmptyState | provider type cards: `GET /api/capabilities` (Epic D / D9) — the engine only reports registered provider *types* (e.g. "sql"), not named source instances with health/latency. The decorative source catalog was removed (D36); see `docs/ui-removed-mock-content.md` | loading, empty (no capabilities → EmptyState) |

**Removed (D36):** Pipeline + variants (`/pipeline`) and Source explorer (`/sources/{name}/explore`)
— both were already flagged fully `mock/future` (D8, needs an ADR); see
`docs/ui-removed-mock-content.md` for what they showed and what a real version would need.
| Builder · 1 Source | `/builder` `Builder` | WizardStepper, Card, Banner, BuilderState | "Engine source type" card (real `<select>` bound to `Wizard.SourceType`, options from `GET /api/capabilities`) + demo-mode gate (Epic D / D6). Decorative source catalog removed (D36). `?edit={name}` (2026-07 UI audit, `docs/ui-removed-mock-content.md` "Builder — edit mode"): resets and re-hydrates `BuilderState` from `GET /api/reports/{name}` for `Deletable` reports; SQL/connection/destination path stay blank (never returned) with a re-enter banner on the relevant steps. Without `?edit`, resets `BuilderState` by default (D69) — the wizard's own internal "Back"/"Change"/"edit" links (steps 2-5 back to step 1) pass `?resume=true` instead, the only case that skips the reset | demo-mode banner when capabilities is empty |
| Builder · 2 Configure | `/builder/configure` `BuilderConfigure` | WizardStepper, Card, ChipGroup | "Engine configuration" card (report name, connection string variable, SQL query, key column, page size, output columns) bound to `BuilderState`; validate: `POST /api/reports/validate` (Epic D / D6). Recap card shows the real `Wizard.SourceType`/connection variable (Epic D / D6, follow-up fix). Resilience card's max attempts/backoff/base delay/jitter/on-failure are real, sent as `resilience.*` (D34); its "retry on errors" and "abort when" controls, plus the Query preview and Parameters cards and the duplicate Pagination card, were removed (D36 — no per-exception filter or expression evaluator in the engine) | validation error inline (danger banner) |
| Builder · 3 Format | `/builder/format` `BuilderFormat` | WizardStepper, FormatCard, EmptyState, Banner | Catalog driven by `GET /api/capabilities` (D36 follow-up) instead of a fixed 6-option list (CSV/XLSX/PDF/JSON/TXT/XML — v1 only ships CSV/XLSX writers); `Wizard.Formats` feeds `outputs[]` directly. Per-format configuration (delimiter/encoding, sheet name/auto-filter, previews) removed — none of it was ever sent to the engine (`OutputConfig` only carries the format id) | empty (no formats registered → EmptyState), demo-mode banner |
| Builder · 4 Destination | `/builder/destination` `BuilderDestination` | WizardStepper, Card | "Engine destination" card (type from `GET /api/capabilities`, path template) bound to `BuilderState`, shown only when the engine is available (Epic D / D6). Decorative catalog (SharePoint/email/…) and per-destination config panels removed (D36) | none (pure config) |
| Builder · 5 Review | `/builder/review` `BuilderReview` | WizardStepper, Card, Chip, CatTile, BuilderState | save: `POST /api/reports` (`201`, `409` if the name exists); run now: `POST /api/reports/{name}/run` then `GET /api/jobs/{id}` (Epic D / D6). Summary's Source/Columns/Destinations rows read real `BuilderState` fields instead of a stale `Wizard.Destinations` set and fixed placeholders (Epic D / D6, follow-up fix). Parameters/Estimate summary rows, the "Save as template" card, and the Schedule card were removed (D36) | save error (danger banner, incl. `409` name conflict); Save/Run disabled in demo mode |
| Jobs list | `/jobs` `Jobs` | Card, DataGrid, JobStatusBadge, CatTile, EmptyState | `GET /api/jobs?status=<filter>&limit=200`, refetched on status-dropdown change (found missing during a 2026-07 UI audit — the Topbar's "Jobs" nav link and Dashboard's "View all →" both used to point at a single hardcoded/first job instead of a real list). Row status/route/progress mapping shared with Dashboard's "Recent jobs" card and ReportDetail's history table via `JobRowFormatter` (Services) | empty (no jobs match the filter → EmptyState), "Engine unreachable" |
| Job running | `/jobs/{id}` `JobRunning` | Card, MetricCard, PhaseStepper, ProgressBar, CatTile, Badge | `GET /api/jobs/{id}` (poll/stream); cancel: `POST /api/jobs/{id}/cancel`. Configuration card (formats/buffer/retry policy) and Destinations card read `GET /api/reports/{name}` once resolved, fetched once per report name rather than every poll tick; "Worker" row dropped (Epic D / D6, follow-up fix). Rate/s computed from `Stats.RecordsWritten` / elapsed since `StartedAt`, peak tracked across polls (Epic D / D6, follow-up). "Memory" card, the Processing-rate sparkline, the Timeline card, and the "Retries this job" card were removed (D36); all remaining fields show "—" instead of fabricated numbers while a poll hasn't resolved a real job yet | live updates, connection lost, cancel-in-progress, unresolved poll → "—" placeholders (not fake data) |
| Job completed | `/jobs/completed` `JobCompleted` | Card, MetricCard, PhaseStepper, CatTile, Badge, Button | `GET /api/jobs/{id}` is now the *only* data path (D36 — no more demo fallback content). Files: `GET /api/jobs/{id}/artifacts` (Epic D / D5); download: `GET /api/jobs/{id}/download`. Configuration/Destinations cards read `GET /api/reports/{name}`; "Worker" and "Run by" rows dropped (Epic D / D6, follow-up fix). Timeline card removed (D36) | "Job not found" (bad/missing id or engine unreachable — EmptyState, D36), download error, expired files |
| Job failed | `/jobs/failed` `JobFailed` | Card, MetricCard, PhaseStepper, CatTile, Badge, Button | `GET /api/jobs/{id}` is now the *only* data path (D36). Resume/retry: `POST /api/reports/{name}/run`. "Alerts fired" and the fake partial-output download card were removed (D36); "Resume from checkpoint" stays disabled with an honest label (no fabricated page/row numbers) since v1 has no checkpoint store | "Job not found" (bad/missing id or engine unreachable — EmptyState, D36), resume unavailable |

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
