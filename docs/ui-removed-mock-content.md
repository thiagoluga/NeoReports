# Removed mock/hardcoded UI content

Maintainer decision (2026-07, recorded as `DECISIONS.md` D36): the shipped UI must not present
fabricated data as if it were real. Everything catalogued here was part of the original Claude
Design handoff (`docs/ui-handoff.md`) but had **no real backing in the engine and no realistic
path to get one without a new feature decision** — so it's removed from what ships, not just
hidden. This file is the map back to what existed, why it's gone, and what a real implementation
would need, for whoever picks any of this up later.

This is distinct from the **demo-mode fallback** pattern (screens that show real data when the
engine is reachable and an honest empty/error state otherwise) — that pattern stays; see
`docs/ui-handoff.md` for the current real/mock-per-screen table. Nothing in this file was ever
real; it was permanently fictional regardless of engine state.

---

## Whole pages removed

### Settings screens — Alerts, Authentication, Plugins, Retention, Audit

**Where:** `src/UI/NeoReports.UI/Pages/{Alerts,Authentication,Plugins,Retention,Audit}.razor`,
routes `/settings/{alerts,authentication,plugins,retention,audit}`, shared
`Components/UI/SubNav.razor`, "Settings" entry in `Layout/Topbar.razor`.

**What it showed:** Alerts — notification channel config (Slack/email/webhook rules) + a fake
activity log. Authentication — an auth filter chain + permission matrix. Plugins — a
marketplace-style grid of installed/available plugins with fake versions and license states.
Retention/Audit were empty scaffold stubs that existed only so the SubNav's 5 tabs didn't 404.

**Why removed:** All five require a subsystem that doesn't exist at all: user accounts, RBAC,
notification integrations, a plugin registry, an audit trail store. This is the "auth chain"
CLAUDE.md already lists as out of v1 scope — not a wiring gap like D34's resilience, a genuinely
unbuilt product surface.

**What a real version would need:** An identity/auth story for the whole product first (accounts,
sessions, permissions) — Alerts and Plugins could follow once that exists and once there's a
notification-integration and plugin-loading design. Not a small follow-up.

### Pipeline + variants (`PipelineView.razor`, route `/pipeline`)

**Where:** `src/UI/NeoReports.UI/Pages/PipelineView.razor`; entry point was the "regional-sales"
demo report card in Reports list (`ReportSummary.IsPipeline`); `Layout/Topbar.razor` special-cased
`/pipeline` to highlight the "Reports" nav item.

**What it showed:** A single fixed "regional-sales" pipeline with 3 fictional variants (full BI
dataset, filtered cut, leadership alert), fake per-variant metrics and pause/resume controls.

**Why removed:** Already flagged as 100% mock in Epic D (D8) — a single hardcoded pipeline with no
route parameter to select a real report, and variants are explicitly post-MVP (D23, multi-source).
Kept illustrative through D8 because removing it wasn't in scope then; now it is.

**What a real version would need:** D23 (multi-source reports) shipped first, then a real concept
of "a report with several output variants" in the engine, then a route/UI to pick which report's
pipeline to view — variants don't exist for any report today, so there's nothing to point this at.

### Source explorer (`SourceExplorer.razor`, route `/sources/{name}/explore`)

**Where:** `src/UI/NeoReports.UI/Pages/SourceExplorer.razor`; entry point was the "Explore" button
on `Components/UI/SourceCard.razor`, wired from the decorative source catalog on `SourcesList.razor`.

**What it showed:** A fake column list + data preview grid for a selected source.

**Why removed:** Was already flagged as needing its own ADR — schema/data introspection from a
UI is a security-sensitive surface (arbitrary read access to whatever the connection string can
see) that was never designed, just mocked as a static screenshot-like preview.

**What a real version would need:** A design decision on what "preview" is allowed to touch (row
limit, column masking, auth), plus an actual introspection endpoint. Needs an ADR before any code.

---

## Decorative catalogs (source/destination pickers with no real backing)

### Sources list decorative catalog + health count-strip (`SourcesList.razor`)

**What it showed:** A grid of fake registered sources (`sales-db`/SQL Server, `billing-pg`/
PostgreSQL, `audit-mongo`/MongoDB, etc. from `SampleData.Sources`) with fabricated health status,
p95 latency, and "used in N reports" counts; a health-aggregate strip ("4 healthy · 1 high
latency · 1 unavailable").

**Why removed:** The engine has no source *registry* at all — `GET /api/capabilities` only
reports which `IConfigSourceProvider` *types* are registered (e.g. "sql", "inmemory"), never named
source instances with health/latency, because there's no such concept to track. The real "Engine
source types" section (additive since D9) is what stays.

**What a real version would need:** A source-registry concept in the engine (named, persisted
source instances distinct from provider types) plus actual health-check plumbing — a genuinely new
feature, not a UI gap.

### Dashboard "Sources" card ("Most used")

**What it showed:** The same `SampleData.Sources` list, ranked as if by usage frequency.

**Why removed:** Same root cause as above (no source registry) plus no usage-frequency tracking
exists anywhere.

### Builder step 1 decorative source catalog + "Quick source" card

**What it showed:** The same fake source cards as a pickable grid, plus a "Quick source · ad-hoc
SQL" card with a non-functional "Create" button.

**Why removed:** The real "Engine source type" selector (added as a D6 follow-up fix) is what
actually drives `POST /api/reports`; the decorative grid below it could never affect the report
being built. The "Create" button had no `OnClick` handler — pure decoration.

### Builder step 4 decorative destination catalog + SharePoint/Email per-destination panels

**What it showed:** A grid of destination types (SharePoint, Email, S3-styled cards, etc.) backed
by the cosmetic `Wizard.Destinations` set (never sent to the engine — the real field is
`Wizard.DestinationType`, wired since D6), plus deep per-destination configuration forms for
SharePoint (site, library, folder template, Azure AD auth) and Email (recipients, subject,
delivery mode).

**Why removed:** v1 only ships `local` and `s3` destination factories; SharePoint and Email
destinations don't exist in the engine at all (SharePoint is explicitly listed "Out" in
`CLAUDE.md`'s v1 scope). Building detailed config UI for destination types that can never be
selected in the real "Engine destination" dropdown was building for nothing.

**What a real version would need:** A `NeoReports.Destinations.SharePoint`/`.Email` package
(new `IDestinationFactory` implementations) — real engine work, not a UI wiring gap. SharePoint is
explicitly out of v1 scope; would need its own scope decision first.

---

## Cards / sections removed from otherwise-real screens

### `ReportDetail.razor`

- **Permissions card** (3 fake users + 1 group, with roles) and **Recent changes card** (a fake
  edit-history timeline). No accounts/audit-trail system exists — see Settings/Authentication
  above, same root cause.
- **Schedule card** ("First Monday · 06:00 BRT", a fake 4-week calendar heatmap). See D35 — v1 has
  no recurring-execution concept; this stayed "illustrative" after D35, now removed entirely per
  the no-mock-in-release decision (D36).
- **Configuration card's "Parameters" row** (`@since`/`@until`/`@tenant` name=value pills). Dynamic
  runtime parameters need an expression evaluator (JsonLogic/DynamicLinq), explicitly out of v1
  scope in `CLAUDE.md`.
- **"Edit" header button** (found broken during a UI audit, 2026-07): navigated straight to a
  blank `/builder` wizard, silently discarding the report being viewed instead of loading it —
  `Wizard.ReportName`/`SqlQuery`/etc. all reset to their defaults. Not a wiring gap: there is no
  `PUT /api/reports/{name}` (only create + delete), and `GET /api/reports/{name}` deliberately never
  returns the raw source properties (SQL text, connection string) at all — same write-only-property-
  bag boundary D33 already applies to `ApiSourceView`. A true edit would need a new update endpoint
  and a decision on whether/how secrets get echoed back to prefill it; removed rather than left
  misleading in the meantime.

### `BuilderConfigure.razor` (Builder step 2)

- **Parameters card** (editable table: name, default value, type, source). Same root cause as
  ReportDetail's Parameters row — needs the banned expression evaluator.
- **Query preview card** (fixed syntax-highlighted SQL snippet, unrelated to the real `SqlQuery`
  field right above it). Could theoretically become a live-highlighted echo of the real field, but
  that needs a SQL syntax highlighter (no such dependency in the project) — cosmetic-only value,
  not worth the new dependency for v1.
- **Pagination card** (Strategy/Key column/Ordering controls, and a second "Page size" input that
  duplicated — and could silently disagree with — the real one in the Engine configuration card
  above). The engine's pagination is keyset-only, driven entirely by `KeyColumn`/`PageSize`
  (already real fields); this card's "Strategy" (offset/keyset/cursor/token) and "Ordering"
  (ASC/DESC) chips had no engine equivalent to select from.
- **Resilience card's "Retry on errors" pills** (deadlock/lock-timeout/network/5xx/429/custom) and
  **"Abort when" threshold switches** (consecutive/total/rate). The Max attempts/Backoff/Base
  delay/Jitter/On failure controls in the same card are real (D34) and stay; these two sub-parts
  don't, because Polly retries any exception uniformly (no per-exception-type filter exists) and
  threshold-based abort escalation (`FailureStrategyBuilder.AbortIf`) is a predicate with no
  config-document representation (same reason dynamic filters are JsonLogic, not code — see D34).

### `BuilderReview.razor` (Builder step 5)

- **Configuration summary's "Estimate" row** (`~42,000 rows · ~2.8 MB · typical run ~3m 30s`).
  Fabricated — the engine can't estimate output size without running the query (same reasoning
  D33 already used to reject a real progress percentage).
- **Configuration summary's "Parameters" row** — same as ReportDetail/BuilderConfigure above.
- **"Save as template" extra fields**: the Description textarea (always the same fixed lorem
  about "Monthly snapshot of sales..." regardless of the report being built, and never sent to
  `POST /api/reports` — there's no `Description` field in `ReportConfig` at all), the Visibility
  dropdown, and the "Allow other users to run"/"Lock editing" toggles (both tie to the same
  nonexistent auth/accounts system as Permissions above).
- **Schedule card** (Don't schedule / Run once / Schedule recurring radio options, frequency/
  timezone pickers, day-of-week chips). Same as ReportDetail's Schedule card — D35, deferred.

### Job pages — `JobRunning.razor`, `JobCompleted.razor`, `JobFailed.razor`

- **"Memory" metric card** (`JobRunning`, fixed "412 MB · limit 1.2 GB"). The engine tracks no
  per-job memory usage — would need new instrumentation in the pipeline runner, not a UI gap.
- **"Processing rate" sparkline chart** (`JobRunning`, a fixed 20-point fake dataset drawn as an
  SVG line/area chart). No time-series rate data is tracked anywhere; `Rate/s` (the single current
  number, D6 follow-up) is real, but a historical series would need the engine to record samples
  over time.
- **"Timeline" event list** (`JobRunning` and `JobCompleted`, a fixed sequence like "Connection
  established · 142ms", "Retry after lock-timeout", "Page 26 written"). `JobView`/`Stats` carry
  only aggregate counters, no discrete lifecycle events — would need structured event logging in
  the pipeline runner.
- **"Retries this job" card** (`JobRunning`, one hardcoded fake retry entry with a specific
  exception message). `Stats.Retries` (the count) is already real and shown in the metric strip;
  per-retry detail (which page, which exception, which delay) isn't tracked.
- **"Alerts fired" card** (`JobFailed`, 3 fixed fake alert rules with channels). Depends on the
  same nonexistent notification/alerting system as Settings/Alerts above.
- **Partial-output "Download partial" card** (`JobFailed`, a fixed fake filename/size shown
  regardless of whether the failed job actually produced partial output). There's no
  `GET /api/jobs/{id}/artifacts`-equivalent for a failed job's partial output — only completed jobs
  expose artifacts.

---

## What stays: the demo-mode fallback pattern

Screens with a real data path (Dashboard, Reports, Report detail, Builder, Sources' capability
section, Job pages) still show demo content **only** when the engine is unreachable — but per D36
that fallback no longer shows fabricated numbers; it shows an honest empty state / "engine
unreachable" banner instead. See `docs/ui-handoff.md`'s per-screen table for the current state of
each.
