# NeoReports — Decision Record (consolidated ADR)

> Document to start the next conversation with the decisions locked in.
> Entry context: **v1 typed code-first only · single worker (vertical scale) · lean MVP · solo founder (part time)**.

## The principle that resolves the tension

**The contract (`NeoReports.Abstractions`) is designed to close no door — dynamic path, multi-worker and UI are all possible without rework. The v1 implementation delivers only the minimum that is demonstrable.** Stable, small abstraction; lean implementation.

Corollary for a solo founder: every public interface in `Abstractions` is a liability (it locks SemVer, breaks external plugins if it changes). Every interface the MVP doesn't use **leaves** v1.

---

## D1 — Typed registration: pipeline generic over `T`, projection only at the writer edge

**Decision.** v1 is **exclusively typed code-first**. The pipeline is generic over `T`; the registration *is* the POCO. There is no positional `ReportRecord` nor a per-row dictionary in v1.

- **Read and processing** (`IBatchSource<T>`, `map`, `filter`): everything operates on `T`. Zero boxing during processing.
- **Projection to columns** happens **only at the writer edge**: Core compiles `Func<T, object?>` per column (from the `ReportSchema` declared in the builder) and projects each row to `object?[]` in schema order, immediately before writing. Boxing only here, and it is unavoidable (CSV/XLSX are weakly typed outputs).
- **Writers stay non-generic**: they consume `(object?[] row, ReportSchema)`. A format plugin does not need to know about `T`.

**Why.** A per-row dictionary kills constant memory. Processing typed and projecting only at the output gives maximum perf on the hot path and keeps writers simple. The dynamic path (positional `ReportRecord` + JsonLogic filter) **comes back post-MVP** without breaking the writer (the edge already speaks `object?[]` + schema).

---

## D2 — Single worker (vertical scale)

**Decision.**
- **One worker process**, vertical scale. No distributed queue, no multi-machine coordination in v1.
- Default: **Hangfire single-server** (SQL/SQLite storage) — gains job-state persistence across restarts and a dashboard for free, with a single server. `InMemory` for dev/tests.
- A job is an **atomic unit** that runs entirely on that worker. If the process dies mid-way, the job **restarts from zero** (idempotent re-execution). Output to local temp, upload at the end, mark `completed`.
- `ICheckpointStore` **exists as a contract** but is a no-op in v1.

**Why.** A vertical worker is the simplest form and serves the MVP. Hangfire single-server costs almost nothing and already opens the path to multi-server later (just spin up more instances) without changing the contract. Mid-job resume and multi-worker are post-MVP — the contract (`IJobStore`, `ICheckpointStore`) is already ready for both.

---

## D3 — The cursor is an opaque serializable token, not `object?`

**Decision.** The pagination cursor is an **opaque serializable token** (`string?`, encoded by the source itself). No `object? Cursor`.

**Why.** Even with a single worker, the cursor is the keyset-pagination mechanism (opens/closes the connection per page). An opaque `string?` is the right type and costs zero — and already makes checkpoint/multi-worker viable in the future without rework.

**Changes in code.** `BatchResult<T>.NextCursor`, `BatchContext.Cursor` and `Checkpoint.LastCursor` are `string?`. The source owns the encode/decode of its internal typed cursor.

---

## D4 — Batch is the canonical model; streaming is adapted

**Decision.** `IBatchSource<T>` is the primary contract and the internal model of the pipeline. `IStreamingSource<T>` (`IAsyncEnumerable<T>`) exists as an authoring option, but is sliced into batches by a `StreamingToBatchAdapter` (configurable size).

**Why.** Retry, threshold and writing operate on a batch. A single model to reason about.

---

## D5 — Variants and coalescing: out of v1

**Decision.** Cut variants, config inheritance and coalescing from the MVP. Independent reports evolve to pipeline+variants later without breaking the contract.

---

## D6 — Don't re-abstract Polly

**Decision.** Use `Polly v8` (`ResiliencePipeline`) directly in the batch-read loop. Remove `IRetryPolicy` and `IExceptionClassifier` from `Abstractions`. Keep only `IFailureStrategy` (decision after retries are exhausted) + a threshold monitor.

**Changes in code.** Retry config compiles into a `ResiliencePipeline`. `IFailureStrategy` in v1: only `AbortReport()` and `SkipBatchAndLog()`.

---

## D7 — Rename `ExecutionContext` → `ReportExecutionContext`

**Decision.** Rename. It collides with `System.Threading.ExecutionContext`.

---

## D8 — Report trigger and `sync` mode

**Decision.** No dynamic-config endpoint in v1. Reports are registered in code (`.AddReport("name", b => b.From<T>()...)`). The endpoint triggers a report **registered by name** with parameters:

```
POST /api/reports/{name}/run        # async → jobId
POST /api/reports/{name}/run?mode=sync   # direct streaming in the response
```

`mode=sync` is **single-output** (one format, response body, no compression of multiple files). The compiler validates and rejects multi-output in sync with `400`.

---

## D9 — Minimal and frozen `Abstractions` (typed-only)

**Decision.** Strict SemVer; treat as an ABI. v1 surface:

```
Schema/        ColumnType · ReportColumn · ReportSchema
Data/          ReportBatch<T>
Execution/     ReportExecutionContext · JobPriority
Sources/       IReportSource · IBatchSource<T> · IStreamingSource<T>
               BatchContext · BatchResult<T>     (Cursor = string?)
Formats/       IReportWriter · WriterContext     (non-generic writer; receives object?[] + schema)
Destinations/  IReportDestination · ReportFile · DestinationContext · UploadResult
Resilience/    IFailureStrategy · BatchFailureContext · FailureDecision · FailureAction
Jobs/          IReportJobScheduler · IJobStore · ReportJob · ReportJobRequest
               ReportJobStatus · JobStats · ICheckpointStore (contract, no-op v1)
Extensibility/ ISourceFactory · IWriterFactory · IDestinationFactory
Exceptions/    NeoReportsException · BatchFailedException · SourceFailedException
               · ThresholdExceededException · ConfigurationException
```

Removed from v1 vs. Ch. 16: `IRetryPolicy`, `IExceptionClassifier`, `IAuthProvider*` (host auth is enough), `IReportConfigParser` + config/variant DTOs, positional `ReportRecord`, the full `JobEvent`/`JobEventType`, public `IPaginationStrategy` (internal for now).

---

## D10 — Typed filter/transform; no dynamic expressions in v1

**Decision.** Filter and transform are **typed C# delegates** (`Func<T,bool>`, `Func<T,T>`) declared in the builder. JsonLogic and DynamicLinq leave v1 (they were part of the dynamic path, cut in D1).

**Why.** Code-first does not need an expression evaluator — the filter *is* compiled C# code, fast and safe. Dynamic expressions come back together with the dynamic path, post-MVP.

---

## Concrete v1 scope (what's in)

| Layer | In v1 | Later |
|---|---|---|
| Paradigm | Typed code-first (`.AddReport` + `.From<T>`) | Dynamic-config endpoint, visual builder, UI |
| Sources | SQL (`IBatchSource<T>`, keyset) | HTTP, File, Mongo, Custom |
| Formats | CSV, XLSX | PDF, JSON, XML, Parquet |
| Destinations | Local, S3 | SharePoint, Azure, GDrive, FTP, Email, Webhook |
| Jobs | Hangfire single-server, InMemory | Multi-worker, Quartz, MassTransit, Azure Functions |
| Resilience | Polly + Abort/SkipAndLog + threshold | Pause/Review, FallbackToCache, dead-letter |
| Auth | Inherits from the host | Filter chain, signed URLs, per-area/action |
| Structure | Independent reports | Pipeline + variants + coalescing |
| Checkpoint | No-op contract; restart-from-zero | Mid-job resume; multi-worker |
| Filter | Typed C# delegates | JsonLogic / DynamicLinq (dynamic path) |

**Not in v1:** Blazor UI, dynamic path, variants/coalescing, multi-worker, auth chain, SharePoint, `dotnet new` templates, PDF, YAML/TOML config, metrics dashboard.

---

## Claude Design handoff (already done) → Claude Code

> **Superseded by D31 (2026-07):** the handoff was ultimately delivered as a **runnable Blazor
> Server starter** (all 17 screens + components + `handoff.md`), now in `src/UI/NeoReports.UI`
> and `docs/ui-handoff.md`, with **no MudBlazor** and the **Geist** font. The section below is
> kept as the historical spec of what was asked.

**State:** the screen design is already done in the Claude Design project (Claude Design System — Anthropic Sans, CSS variables, official palette, Tabler outline icons, flat). **The UI remains post-MVP** — this handoff is preparation for the UI phase, not for v1.

**Target UI stack:** Blazor Server + MudBlazor (+ ApexCharts).

**Ask the design project to export, in this priority order:**

1. **`tokens.css`** — all Design System tokens (colors, typography, spacing, radii, shadows) as named CSS custom properties, single file. Becomes the MudBlazor theme.
2. **`components.html`** — a catalog of each reusable component in **all variants and states** (default/hover/active/disabled/loading/empty/error). Minimum: MetricCard, StatusBadge (queued/running/completed/failed/paused/retrying/cancelled), ProgressBar, PhaseStepper, WizardStepper, FilterBar, ReportCard, SourceCard, DestinationCard, FormatCard, DataGrid (header+rows), Timeline/EventRow, EmptyState, Banner/Alert, NavBar, SubNav, Chip/Tag, Switch. Named, stable classes, **no inline style**.
3. **One `.html` per screen** (the 17) — semantic markup that **references** the catalog classes (does not recopy style); only layout/composition/grid.
4. **`handoff.md`** — a table `screen → route → components used → feeding endpoint → states to handle`, plus responsive breakpoints and the list of Tabler icons.

**Format rules (minimize work in Claude Code):** external CSS only, zero inline style; classes that map 1:1 to a component name; semantic HTML (`button`/`table`/`nav`/headings, no div-soup); no assumption about JS behavior (interactivity belongs to Blazor). **Avoid:** screenshots/PNG, Figma, giant HTML with inline style.

---

## Suggested sequence (solo, part time)

1. Minimal `Abstractions` (D9) + `Core` (generic `<T>` fluent builder + batch pipeline + compiled projection + Polly).
2. `Sources.Sql` (keyset) + `Formats.Csv` + `Destinations.Local`. **First typed end-to-end report running.**
3. `Formats.Xlsx` (ClosedXML) + `Destinations.S3` (all-or-nothing upload).
4. `Jobs.Hangfire` (single-server) + `Jobs.InMemory` + `IJobStore`.
5. `AspNetCore`: async/sync trigger endpoints for registered reports. **Demonstrable MVP.**
6. Validate with real users before UI / dynamic path / variants / multi-worker.

---

## Decisions summary table

| # | Topic | Decision |
|---|---|---|
| D1 | Registration | Typed generic `<T>` pipeline; projection to `object?[]` only at the writer edge; no dictionary; dynamic post-MVP |
| D2 | Worker | Single / vertical; Hangfire single-server; atomic job; restart-from-zero; multi-worker and resume post-MVP |
| D3 | Cursor | Opaque serializable token (`string?`) |
| D4 | Stream vs Batch | Batch canonical; streaming adapted |
| D5 | Variants/coalescing | Out of v1 |
| D6 | Resilience | Polly directly; only `IFailureStrategy` + threshold as an owned abstraction |
| D7 | Naming | `ExecutionContext` → `ReportExecutionContext` |
| D8 | Trigger/sync | Reports registered by name; no dynamic config; sync = single-output |
| D9 | Abstractions | Minimal typed-only, frozen, strict SemVer |
| D10 | Filter | Typed C# delegates; JsonLogic/DynamicLinq post-MVP |
| D11 | Retry/Skip | Retry (Polly) wraps the batch read; a read failure is not "skippable" (no cursor to advance) → becomes Abort; a projection/write failure is skippable (cursor already known) |
| D12 | Map in the builder | `Map` is not a step that changes the builder's type; mapping is expressed by `From(source, map)`, keeping `ReportBuilder<TRow>` single-generic and compatible with `AddReport<TRow>(Action<...>)` |
| D13 | SQL keyset source | `Source.Sql(connString, sql).Keyset(key, pageSize)`; the query carries `@cursor` (`(@cursor IS NULL OR Id > @cursor)`) and `ORDER BY`; connection per page; cursor = last key as `string?`; binds only the parameters the query references; connection-by-name is post-MVP |
| D14 | In-memory XLSX | The XLSX writer uses ClosedXML, which materializes the whole sheet in memory before saving — a conscious exception to "constant memory" (rule 8). Acceptable for v1 sizes; streaming OpenXML is post-MVP. CSV stays truly streaming |
| D15 | All-or-nothing S3 | `Destination.S3(bucket, keyTemplate)` uses `PutObject` (atomic per object): a failure leaves no partial object. Client from DI (`IAmazonS3`) or AWS defaults. Multipart for large objects is post-MVP |
| D16 | Format entry point | Each format package exposes a `static class Format` with a `Csv()`/`Xlsx()` method. To use two formats together (the spec does `Format.Csv(...).Format.Xlsx(...)`), the consumer uses `using static ...Csv.Format;` + `using static ...Xlsx.Format;` and calls `Csv(...)`/`Xlsx(...)` — avoiding the `Format` name collision between the two assemblies |
| D17 | Assertion lib | Tests use **Shouldly** (MIT). FluentAssertions left because v8 went commercial-license (Xceed); being stuck on 7.x would block updates. Dependabot keeps the test-tooling group up to date without FA |
| D18 | Jobs packaging | Base package `NeoReports.Jobs` (shared worker `ReportJobWorker` + `InMemoryJobStore` + `InMemoryJobScheduler` + `NoOpCheckpointStore` + DI) and `NeoReports.Jobs.Hangfire` extending it. Avoids a 3rd package just to share the worker; the plan's "InMemory" lives in the base package |
| D19 | Worker and cancellation | `ReportJobWorker` is the single lifecycle core (running→completed/failed/cancelled), used by both schedulers. Idempotent restart (AC-16) comes from the pipeline (per-job temp + upload only at the end; temp cleanup is best-effort and never changes the status). InMemory: cancels via a per-job `CancellationTokenSource`. Hangfire: `CancellationToken` injected into the invoker; `CancelAsync` deletes the job; in-process id↔hangfire-id map (single-server; cross-restart is post-MVP, D2). Parameters travel as JSON (`JobParameters`, dates as ISO-8601 round-trip) |
| D20 | Endpoints + artifact store | `MapNeoReports("/api")` (Minimal API). Download/sync need to retain the file beyond the pipeline's temp: `IReportArtifactStore` (+ `FileSystemArtifactStore`) in **Core** (an engine concern, not a plugin contract → outside `Abstractions`); the `ReportRunner` saves into it only if registered (opt-in via DI). Sync = single-output (multi → 400, AC-10); multi-output on download becomes a zip. Job status serialized as a **string** (`JsonStringEnumConverter` on the DTOs, without touching the enum in `Abstractions`). Auth inherits from the host (`RequireAuthorization` optional; no auth chain). Outputs with the same extension (e.g. two CSVs) get a **disambiguated** file name in the pipeline (`name.csv`, `name-2.csv`) so they don't collide on disk/artifact and can go together in the zip |
| D21 | Dynamic path | Reopened in v2. Row type = positional `ReportRecord` (`object?[]` + `ReportSchema`), not a dictionary; reuses the whole v1 pipeline because the writer edge already speaks `object?[]` + schema. Config (JSON) + `IReportConfigParser` + JsonLogic filter return additively to `Abstractions` (SemVer-minor) |
| D22 | Multi-sheet XLSX | **First v2 paid feature (maintainer-locked).** One workbook, several named sheets, each from a different filter over the same source (different sources per sheet = B2). Single pass preserved via a generic "multi-section output" hook in OSS Core; the XLSX workbook writer + fluent API ship in the commercial `NeoReports.Xlsx.Pro` package (D27). Blueprint: `docs/epic-b1-multisheet-pro.md`. Some sub-decisions still open there |
| D27 | Pro package model | **Open-core** (forced by the already-MIT core). Core stays MIT; advanced features ship in **`NeoReports.Xlsx.Pro`** — **source-available, Option A (QuestPDF-style): free under USD 1M annual revenue, paid above** (use **PolyForm Small Business 1.0.0**; fetch canonical text at B1.2). **No runtime enforcement** for now (contractual, like QuestPDF). Pro plugs in via existing extensibility; the OSS core never depends on Pro. Pro packages are excluded from the OSS NuGet release; commercial sales terms are the maintainer's/lawyer's. The **multi-view hook is MIT** (one file per view); Pro adds the single-workbook (sheets) writer |
| D28 | Multi-source join (B2) | **Two explicit, user-chosen strategies** (not auto-detected): (a) **keyset merge-join** — an `IStreamingSource<TResult>` that merges two sources ordered by the same key, constant memory when per-key multiplicity is bounded, inner + left-outer; (b) **enrichment/lookup** — an `IBatchSource<TResult>` that, per page of a primary source, makes ONE batched lookup call and maps it in (O(pageSize), structurally no N+1). Both produce a source the existing pipeline consumes unchanged. Blueprint: `docs/epic-b2-multisource.md` |
| D29 | Multi-source packaging | **Resolved: Pro.** B2's value is the join sources themselves, so there is no natural MIT/Pro split like B1 — it is a straight monetization call, settled as **Pro** for consistency with the B1 decision (`NeoReports.Sources.Join.Pro`, same model as D27: PolyForm Small Business, `IsPackable=false`, no runtime enforcement). v1 join types are **inner + left-outer**; dynamic-config support for multi-source is deferred (B2.4, optional) |
| D30 | Pro distribution | Pro packages are **not published** to any feed for now (maintainer decision, B1.4). A dedicated `pack-pro.yml` workflow packs `NeoReports.Xlsx.Pro` and `NeoReports.Sources.Join.Pro` as CI **build artifacts** — per-project `IsPackable=true` override, versioned like the OSS release tag, also runnable on demand (`workflow_dispatch`) — keeping them continuously packable (metadata + LICENSE.txt validated) and one click away for a first customer. The OSS release pipeline is untouched. A private feed (e.g. GitHub Packages) is deferred until there are paying customers |
| D31 | Epic C kickoff (UI) | Maintainer decisions (2026-07): the **validation gate is waived** — Epic C starts now. The Claude Design handoff arrived as a **runnable Blazor Server starter** (all 17 prototype screens + reusable components + `handoff.md`) instead of the static-HTML deliverables — **accepted**, it skips the HTML→Blazor translation step; it lives in `src/UI/NeoReports.UI` with the screen→route→endpoint map in `docs/ui-handoff.md`. **No MudBlazor** (supersedes the D24-era stack note): pure design-system CSS (`tokens.css` + `neoreports.css`) is the theming layer. Font is **Geist** (+ Geist Mono), Tabler outline icons; both self-hosted from `wwwroot/fonts/` since C4 (zero CDN/Google Fonts calls at runtime — see `wwwroot/fonts/README.md`). UI copy is **en-US**. Screens run on `SampleData` mocks; wiring the real endpoints is the next Epic C step, guided by the handoff table (endpoints the engine doesn't expose are marked `mock/future` — never invented) |
| D32 | UI hosting model | The UI is a **Razor Class Library**, not a standalone app (maintainer ask, C5). A host mounts it with `services.AddNeoReportsUI()` + `app.UseNeoReportsUI("<base path>")`; the base path is **configurable** (default `/neoreports`) and everything — UI routes, `_content` static assets, the Blazor hub — is branched under it (`app.Map`), leaving the host's own routes untouched. `_Host.cshtml` derives `<base href>` from the request `PathBase`, so no per-path configuration is needed. Sample `08-web-ui` is the runnable host and demos the custom URL (`--NeoReports:UIPath=/reports-admin`). Whether `NeoReports.UI` ships on NuGet (and MIT vs Pro) is deferred — `IsPackable=false` for now |
| D33 | Dynamic registration API (Epic D) | Maintainer decision (2026-07): the **runtime-registration slice of the dynamic path enters scope** to power the UI Builder — `POST /api/reports` (register a `ReportConfig` document at runtime), `POST /api/reports/validate` (dry-run compile), `DELETE /api/reports/{name}`, `GET /api/capabilities`, plus read endpoints the UI needs (`GET /api/jobs`, `GET /api/reports/{name}`, `GET /api/jobs/{id}/artifacts`). Key sub-decisions: (a) **file-backed** `IReportConfigStore` (one JSON per report, single-server consistent, rehydrated at startup; corrupt files are logged and skipped, never crash the host); (b) dynamic report names validated with `^[a-zA-Z][a-zA-Z0-9_-]{0,99}$` — they become filenames, so invalid names are **rejected**, not sanitized; (c) GET responses **never echo property bags** (source/output/destination `Properties` may hold secrets) — only type/format ids and columns; (d) `${VAR}` whole-value env placeholders resolved at compile time so secrets stay out of persisted configs; (e) only config-origin reports are deletable (code-first → 409); running jobs of a deleted report finish normally; (f) **no edit** (`PUT`) in this epic — needs a secrets round-trip story, future ADR; (g) real progress **percentage rejected** (would need a source `COUNT`); counters remain the truth. `Abstractions` stays frozen — everything lands in Core/AspNetCore/UI. Blueprint: `docs/epic-d-dynamic-api.md` |
| D34 | Dynamic-path resilience (Epic D follow-up) | The hardcoded-UI audit found the Builder's Resilience card (step 2) was 100% cosmetic because `ReportConfig` (D33) never carried retry/failure-strategy fields — an oversight, not an intentional omission: `RetryOptions`/`FailureStrategyBuilder` are plain scalar builders already in scope (v1 spec: "Polly resilience + `IFailureStrategy`"), just never mapped into the dynamic-path schema. Closed by adding an **optional** `ReportConfig.Resilience` (`ResilienceConfig`: `MaxAttempts`, `Backoff` "Constant"/"Exponential", `BaseDelaySeconds`, `Jitter`, `OnFailure` "abort"/"skip-and-log") — additive on the frozen `Abstractions` record (new trailing optional parameter), applied by `ReportConfigCompiler` via the same `builder.Retry(...)`/`builder.OnFailure(...)` the fluent path uses; omitting it keeps the engine's existing defaults unchanged. **Explicitly not exposed**: `FailureStrategyBuilder.AbortIf`'s threshold escalation is a `Func<ThresholdContext,bool>` predicate — no config-document equivalent, same reason dynamic filters are JsonLogic and not arbitrary code (stays out until there's a JsonLogic-style threshold expression, if ever). Per-exception-type retry filtering also has no engine equivalent (Polly retries any exception uniformly) — the Builder's "Retry on errors" pills and "Abort when" threshold switches stay cosmetic, clearly labeled as illustrative-only rather than removed, since the underlying idea may still get a real design later. |
| D35 | Scheduling (recurring runs) | Maintainer decision (2026-07): **deferred, not implemented**. The hardcoded-UI audit flagged the Schedule cards (`ReportDetail`, Builder step 5 "Review") as cosmetic — unlike D34's resilience gap, this one is a genuinely **new capability**: neither the typed nor the dynamic path has any recurring-execution concept today (confirmed: no `IRecurringJobManager`/`RecurringJob` usage anywhere in `NeoReports.Jobs.Hangfire`, only the plain `IBackgroundJobClient.Enqueue` one-shot pattern in `HangfireJobScheduler`). **Sketch for whenever this enters scope** (not committed to, just so the shape is on record): a `Schedule` (cron string) field on `ReportConfig`/the code-first builder; `HangfireJobScheduler` gains a `RegisterRecurringAsync` using `IRecurringJobManager.AddOrUpdate<HangfireReportJobInvoker>(reportName, invoker => invoker.ExecuteAsync(...), cronExpression)` — Hangfire already provides the primitive, so this is additive to the existing scheduler, not a new subsystem; `InMemoryJobScheduler` would need either a `Timer`-based equivalent or to explicitly not support it (single-server default). Until then: the Schedule cards **stay illustrative** (not removed — the underlying feature is plausible future scope, just not now) and their code comments point at this entry instead of describing invented behavior as if it were real. **Superseded in part by D36**: the Schedule cards are removed from the shipped UI (not merely left illustrative) — this entry's design sketch still stands for whenever scheduling is actually built. |
| D36 | No mocked UI in the release | Maintainer decision (2026-07): the shipped UI must not present fabricated data as if it were real. Everything catalogued during the hardcoded-UI audit (#94–#101) that has **no real backing and no realistic path to one without a new feature decision** is removed from what ships — not hidden behind a flag, actually deleted — while being documented in `docs/ui-removed-mock-content.md` (what it was, why it's gone, what a real version would need) so it can be picked up later without re-deriving the reasoning. Removed: the Settings screens (Alerts/Authentication/Plugins/Retention/Audit — no accounts/RBAC/notification/plugin system exists), Pipeline+variants (D8, D23), Source explorer (needs its own ADR, D8), the decorative source/destination catalogs (no source registry or SharePoint/Email factories exist), and the Permissions/Recent changes/Schedule/Parameters/Query preview/Estimate/fake-telemetry cards embedded in otherwise-real screens (ReportDetail, Builder, Job pages). **Explicitly not removed**: the demo-mode fallback pattern itself (screens showing real data when the engine is reachable) — only its content changes, from fabricated numbers to an honest empty state / "engine unreachable" indication when the engine is down (screens: Dashboard, Reports, Report detail, Builder, Sources, Job pages). `SampleData.cs` is reduced or removed accordingly as each screen's fallback is reworked. |
| D37 | Abort thresholds as config; per-exception retry filtering rejected | Maintainer decision (2026-07), closing D34's two punted ends — one ships, one is rejected for good. (a) Threshold escalation ships: `ResilienceConfig` gains an optional, additive `AbortWhen` (`AbortThresholdConfig`: `ConsecutiveFailures`, `TotalFailures`, `FailureRate` — OR semantics, at least one field set, counts >= 1, rate in (0,1], legal only with `skip-and-log`), a 1:1 transcription of the three predicate helpers `ThresholdContext` already exposes — a closed vocabulary, so D34's "predicates aren't data" objection doesn't apply (same logic that made filters JsonLogic). The compiler builds the same `AbortIf(...)` the fluent path uses; `FailureStrategyBuilder` gains a data-based `AbortIf(AbortThresholdConfig)` overload so both paths are introspectable, and `GET /reports/{name}` exposes the thresholds (null = none or custom code predicate). (b) Retry-on-error-type is **rejected**, not deferred: any faithful design needs per-plugin exception classification, which is the `IExceptionClassifier` D6 explicitly removed (CLAUDE.md rule 5); type-name matching in config is a worse classifier, and a BCL-only "transient" toggle (`DbException.IsTransient` et al.) has provider-dependent coverage that can silently disable expected retries. Polly keeps retrying any non-cancellation exception uniformly; the Builder's "Retry on errors" pills stay removed (D36). Revisiting this means revisiting D6 itself, in its own ADR. Blueprint: `docs/epic-e-real-backing.md` (E1). |
| D38 | Per-job event log (timeline, retry detail, rate history — one mechanism) | Maintainer decision (2026-07): the three job-telemetry gaps the audit removed (timeline, per-retry detail, rate sparkline) are one feature: a bounded, structured, per-job event log. `JobEvent` returns **in Core, not the frozen `Abstractions`** (D9 removed it from the ABI, not from existence — same placement logic as `IReportArtifactStore`, D20): `IJobEventStore` + InMemory and JSONL-file stores, **opt-in** via `AddJobEvents()` like the artifact store — unregistered means the runner emits nothing and behaves byte-identically to today. Bounds are **user-configurable** (`JobEventOptions`: per-job cap, default 1000, closed with an `events-truncated` marker; optional TTL retention pruned on append — which also cleans up orphan event files from `?mode=sync` runs that have no job record). `ReportRunner` emits a closed vocabulary of lifecycle events (`run-started/-restarted/-completed/-failed/-cancelled`, `page-completed` with cumulative counters, `retry` via a new optional `OnRetry` hook on `ResiliencePipelineFactory`, `batch-skipped`, `outputs-finalized`, `upload-completed`); appends are fire-and-forget and can never change a run's outcome. Exposure is a single sub-resource, `GET /jobs/{id}/events` (type filter + paging), keeping `JobView` IO-free per D5; retry detail is `?type=retry`, and the processing-rate series is **derived** from `page-completed` events rather than a second sampling mechanism — every sparkline point is a real page completion, nothing interpolated or invented (D36). Exception messages in events are truncated and newline-sanitized; property bags never appear. `JobStats` counters remain the aggregate truth. Blueprint: E2/E3. |
| D39 | No per-job memory metric; a process-level Memory screen instead | Maintainer decision (2026-07): the removed "Memory: 412 MB" card on the running-job page stays dead — a single worker process runs jobs concurrently, so a per-job memory number cannot be measured honestly and would be exactly the fabricated-looking telemetry D36 removed. Instead the UI gains a **Memory screen** backed by `GET /api/system/memory`: process working set (`Environment.WorkingSet`), GC heap and committed bytes (`GC.GetGCMemoryInfo()`), a measurement timestamp, and the count of currently running jobs; the running-jobs *list* is composed client-side from the existing `GET /jobs?status=Running`, not duplicated into a new endpoint. The screen's copy states the measurement is process-wide (including the UI when co-hosted) and that isolating one job's footprint means running it alone — that guidance, not a fake per-job number, is the feature. This is a deliberate, narrow exception adjacent to CLAUDE.md's "no general metrics dashboard": one endpoint, one reading per request, no time series, no counters registry; anything more is a new decision. Blueprint: E4. |
| D40 | Partial output of failed/cancelled jobs: dedicated store, never the destination or artifacts list | Maintainer decision (2026-07): when a run ends `Failed` **or `Cancelled`**, the staged temp output is best-effort finalized and copied into a new, **separate** `IPartialArtifactStore` (Core, opt-in DI, own directory — a distinct interface rather than a flag on `IReportArtifactStore`, so no consumer can ever list a partial as a real artifact by omission), with files renamed **`{name}.partial.{ext}`** so the label survives the download leaving the browser. Exposure is only `GET /jobs/{id}/partial-artifacts` (+ its own `/download`), returning `[]` for any other status; `GET /jobs/{id}/artifacts` and the completed `/download` never learn partials exist. Eligibility: `SkipBatchAndLog` runs ending `CompletedPartial` are completed jobs whose file legitimately publishes to the real destinations (D11), so the partial store never engages for them. D2/D15's all-or-nothing guarantee at the configured destination is untouched — this feature exists to relieve pressure on that guarantee, not to weaken it. Partials get their own TTL-based retention (options, pruned on save) since they bypass the normal artifact lifecycle; capture is fire-and-forget and never changes the run's outcome. Format fidelity is documented best-effort: CSV partials contain all fully-written batches (D11's batch-atomic writer assumption); XLSX partials are whatever ClosedXML finalizes from memory. Blueprint: E5. |
| D41 | Scheduling (recurring runs) — implements and supersedes D35 | Maintainer decision (2026-07): recurring execution enters scope, following D35's sketch. `ReportConfig` gains an optional, additive `Schedule` (`ScheduleConfig`: `Cron` only — **UTC-only** evaluation, no timezone field; the UI converts the computed next run to the viewer's local time) and the typed builder gains `.Schedule(cron)`. Recurrence is a **Core capability interface** (`IRecurringReportScheduler`), not a change to the frozen `IReportJobScheduler`: `HangfireJobScheduler` implements it via `IRecurringJobManager.AddOrUpdate` (recurring-job id `neoreports:{name}`; a new invoker entry creates the `IJobStore` record per firing so recurring runs appear in `GET /jobs` like any other job), and `InMemoryJobScheduler` implements a Cronos + `PeriodicTimer` equivalent (Cronos approved into CPM as its own top-level package — Hangfire.Core bundles its own copy internally rather than exposing it as a NuGet dependency, so Core needs a direct reference for cron validation independent of Hangfire), schedules dying at process exit like everything else in-memory. Overlapping firings **run concurrently** — no skip-if-running; the engine already isolates concurrent jobs. Schedules are **runtime-overridable for both origins** (code-first included) via a uniform `IScheduleOverrideStore` (file-backed, with an explicit "unscheduled" tombstone; effective schedule = override if present, else the declared one) — `PUT/DELETE /reports/{name}/schedule` writes overrides and **never patches the stored config document**, so secrets never round-trip and D33(f)'s punted full edit stays punted. Because dynamic reports hydrate lazily, a hosted service forces hydration at startup and reconciles recurring registrations (applying effective schedules, removing orphaned `neoreports:*` entries for deleted reports). `GET /capabilities` gains `Scheduling`; `GET /reports/{name}` exposes the cron and a `NextRunAt` computed via Cronos from the real registration — never a fabricated date (D36); hosts without a recurring scheduler reject schedule input (400/409) rather than silently dropping it. Delete removes the recurring registration and any override before unregistering; running jobs finish normally. Blueprint: E6. |
| D42 | Source registry (named source instances + on-demand health) — Epic F | Maintainer decision (2026-07): the engine gains its first *instance*-level source concept — a named, persisted `SourceDefinition` (name under the same regex as dynamic report names; provider type id; property bag reusing D33's `${VAR}` placeholder mechanism; description) in a file-backed `ISourceRegistryStore` (Core), hydrated like dynamic reports and hydrated **before** them. **Packaging: MIT** (maintainer call — not Pro). Reports reference one via a new additive `Ref` on `SourceConfig` (supersedes D13's "connection-by-name is post-MVP" deferral): the compiler checks existence at compile time but resolves properties **at run time** (definition base, report-local overlay, `${VAR}` substitution on the merged bag), so rotating a connection string takes effect on the next run of every referencing report without recompiles; inline sources remain fully supported, and `IConfigSourceProvider` implementations are untouched (they receive an ordinary merged `SourceConfig`). CRUD is `GET/POST/PUT/DELETE /api/sources` — GET never returns properties (the D33 property-bag rule at its most literal), which is precisely why **`PUT` full-replace is allowed** while report edit stayed punted: secrets never round-trip because the client always re-sends placeholders. Delete is blocked (409) while any registered report references the source; "used in N reports" is computed from `CompiledReport.SourceRef`, never tracked. Health is `ISourceHealthCheck` (Core), resolved per provider type from DI like `IConfigSourceProvider` — nothing SQL-specific in Core; `Sources.Sql` ships open-and-ping. Checks run **on demand** (`POST /sources/{name}/health`) with the last result cached and timestamped — no background poller, because a stale reading presented as current is the fabricated-telemetry pattern D36 removed; "never checked" is a first-class state. **Typed-path by-name authoring is in scope** (`Source.SqlNamed("sales-db", sql)`): the Core compile step injects a per-run registry resolver, since sources have no `IServiceProvider` on the read path. Blueprint: `docs/epic-f-source-registry.md`. |
| D43 | Additional relational sources: PostgreSQL, MySQL/MariaDB, Oracle — via a shared ADO.NET keyset engine — Epic G | Maintainer decision (2026-07, requested directly, not derived from an audit): v1's source scope ("SQL source (keyset)") expands to cover the other mainstream relational engines, reusing `SqlKeysetSource<T>`'s design rather than reinventing it three times — its internals already type everything through `System.Data.Common` (`DbCommand`/`DbDataReader`/`DbParameter`), so only connection creation is SQL-Server-specific. A new **MIT** package, `NeoReports.Sources.Common`, extracts that engine as `AdoKeysetSource<T>` (parametrized by a `Func<DbConnection>` connection factory instead of a hardcoded `SqlConnection`) plus the existing `RecordMaterializer<T>`; three new sibling packages — `NeoReports.Sources.Postgres` (Npgsql), `NeoReports.Sources.MySql` (MySqlConnector), `NeoReports.Sources.Oracle` (Oracle.ManagedDataAccess.Core) — each ship the same shape `Sources.Sql` already established: `Source.<Provider>(connString, sql)` / `Source.<Provider>Named(name, sql)` typed entry points, `<Provider>ConfigSourceProvider` (dynamic path, `type: "postgres"|"mysql"|"oracle"`), `<Provider>SourceHealthCheck` (open-and-ping), and `Add<Provider>ConfigSource()` DI registration — all built on the shared engine. Only `SqlKeysetSource<T>` itself — already-published (v1.2.0) public API — is left untouched, to avoid a needless break for existing consumers for zero behavioral gain; everything *around* it that was never part of that public contract (`SqlConfigSourceProvider`'s property parsing, `SqlSourceHealthCheck`'s ping body, `Source.cs`'s member-selector helper) is unified onto the same `AdoConfigProperties`/`AdoSourceHealth`/`MemberSelector` helpers the new providers use, closing the duplication Sonar's quality gate correctly flagged on the first attempt at this decision rather than accepting it as a permanent tradeoff. Each provider's integration tests use Testcontainers (`Testcontainers.PostgreSql`/`.MySql`/`.Oracle`), `[SkippableFact]`-gated like the existing SQL Server suite so CI without Docker still passes. Blueprint: `docs/epic-g-more-sources.md`. |
| D44 | MongoDB source (non-relational, own pagination strategy) — Epic G | Maintainer decision (2026-07): MongoDB cannot reuse D43's ADO.NET engine — no `DbConnection`/`DbDataReader`, no relational cursor model — so it gets its own **MIT** package, `NeoReports.Sources.MongoDb` (`MongoDB.Driver`), implementing the same *outcome* as keyset pagination (opaque `string?` cursor, no offset/skip drift under concurrent writes) via a sort-and-range-filter query: page N+1 is `Find(keyField > cursor).Sort(keyField).Limit(pageSize)`, cursor = the last returned document's key field serialized to its string form. Typed entry point `Source.MongoDb(connectionString, database, collection).Keyset(keySelector, pageSize)`; dynamic path `type: "mongodb"`; health check is `RunCommandAsync({ ping: 1 })`. D45's structured filter translation is **SQL-family only in this first cut** — Mongo previews run unfiltered with an honest UI note (D36's degrade-honestly pattern) until a follow-up teaches it BSON filter translation. Integration tests use `Testcontainers.MongoDb`, `[SkippableFact]`-gated. Blueprint: `docs/epic-g-more-sources.md`. |
| D45 | Report preview: paginated read-only sample, plus structured (non-expression) filter editing for SQL-family dynamic sources — Epic G | Maintainer decision (2026-07): a **new, narrower** capability than the "Source explorer" D36 flagged as needing its own ADR — this previews one already-registered report's own configured source (not ad-hoc browsing of any source's schema/data), and it is a genuinely new mechanism, **not** a return of the removed JsonLogic/DynamicLinq expression evaluator (CLAUDE.md's "Out" list stands unchanged). `POST /reports/{name}/preview` runs the source for a bounded page (server-capped page size, no output writing, no upload, no job record) and returns rows + schema; every source type supports the *unfiltered* sample. Filter editing is additive and scoped tightly: a closed list of structured rows — `Column` (must be one of the report's declared columns), `Operator` (`equals`/`notEquals`/`gt`/`gte`/`lt`/`lte`/`contains`/`startsWith` — a fixed enum, never a free-form expression), `Value` — translated by SQL-family sources (`Sql`/`Postgres`/`MySql`/`Oracle`, D43) into a parameterized `WHERE` fragment appended to the keyset query (always via `DbParameter`, never string-concatenated); Mongo (D44) and any source without a translator ignore filters and the UI says so honestly rather than silently dropping them. **Filters are ephemeral, never persisted**: `POST /reports/{name}/preview` and the additive `Filters` field on `RunReportRequest` both apply for that one call only — no new `PUT`, no config-document mutation, no override store, mirroring the precedent `RunReportRequest.Parameters` already set for run-time values. Typed (code-first) reports are preview-only, **not** filter-editable — their `Filter(Func<T,bool>)` is a compiled predicate with no structured representation to edit and re-submit; only dynamic (config-registered) SQL-family reports expose the filter editor. Blueprint: `docs/epic-g-more-sources.md`. **Implementation refinement (G5):** `IFilterTranslator.TryTranslate(string sql, IReadOnlyList<PreviewFilter> filters, out string translatedSql, out IReadOnlyDictionary<string, object?> parameters)` — no `DbCommand` parameter, as originally sketched. `CompiledReport` erases its row type behind an internal `ReaderFactory`, so a `DbCommand` already bound to the report's compiled source was never reachable from the preview endpoint; the translator instead returns the filtered SQL text plus a name→value dictionary that flows into the *existing* `ReportExecutionContext.Parameters` mechanism `AdoKeysetSource` already merges into its bound query — filter values are still always parameter-bound, never string-concatenated, just via a seam that already existed rather than a new one. A dynamic report's filtered preview re-reads its *stored* config document (`IReportConfigStore`) rather than the compiled, type-erased source, and — for a `Ref`-based source — merges the registry definition's properties with the report's own overlay the same way `RefBatchSource` does (definition base, report-local overlay wins). `RunReportRequest.Filters` is additive on the contract now, but a full filtered *run* (not just preview) is deferred — it needs a temporary re-compiled report threaded through the job/scheduler pipeline, a separate piece of work; `POST /run` returns 400 on a non-empty `Filters` until then. **Implementation refinement (G6):** two scope cuts, both consequences of G5's already-merged contract rather than new decisions. No "Load more" pagination — `PreviewResponse` carries `hasMore` but no next-page cursor (G5 always reads page 1 only), so the UI shows an honest "more rows exist" note instead of a button that couldn't actually fetch them; deferred alongside filtered-run support, since both need the same kind of follow-up to G5. The filter editor isn't hidden *upfront* for a dynamic source with no translator (only for typed/code-first reports, which the UI can determine from `ApiReportDetail.Origin`) — there's no "does this source support filters" capability query on the wire, only `filtersApplied` on a preview *response*, so the honest note can only appear after the user's first filtered attempt, not before it. **Fix (G7, before first release):** `PreviewFilterRequest.Value` was `object?`, and without an explicit converter `System.Text.Json` leaves an `object?`-typed property as a boxed `JsonElement` on minimal-API model binding — no ADO.NET provider can bind that as a `DbParameter` value, so **every** filtered preview against **every** relational provider was broken regardless of column type. A first pass fixed this by applying the existing `PrimitiveObjectConverter` (used for `ReportConfig`/source-registry property bags) to `PreviewFilterRequest.Value` — but that converter also recovers a round-tripped ISO-8601 string as a CLR `DateTime`, which is exactly wrong for a filter value: an ordinary decimal a user might type, e.g. `"12.25"`, parses as December 25 under `DateTime.TryParse`'s lenient rules, silently corrupting both `Contains`/`StartsWith` patterns (built from the reformatted date instead of the literal text) and typed casts (chosen from the column's declared type, now mismatched against the value's silently-changed runtime type) — caught by an automated multi-angle code review before merge, with a concrete repro. The actual fix goes further: `PreviewFilter.Value` (Core) and `PreviewFilterRequest.Value` are now `string?`, not `object?` — a filter value is always its literal text form, matching exactly what the preview UI's plain text input sends, checked by the compiler rather than merely documented. A new `FilterValueConverter` (AspNetCore) stringifies any JSON scalar (string verbatim, number as its exact written digits, boolean as `"true"`/`"false"`) with no date-sniffing. This, plus schema-aware casting once every value is guaranteed text, surfaced three more real, distinct bugs once exercised against actual Testcontainers-backed databases (not just string/dictionary unit tests) for all four relational providers: (1) Postgres has no implicit `text`→typed conversion in a comparison — the same class of gap D43 hit for keyset cursors, but here there is no report-author-controlled SQL text to hand-write a cast into, so `AdoFilterTranslator` now takes an optional `castParameter` delegate and Postgres registers `AdoFilterTranslator.PostgresCast` (`{token}::{type}`); (2) SQL Server rejects a bare `ORDER BY` inside a derived table (every keyset query already ends with one) unless followed by `TOP`/`OFFSET`/`FOR XML` — `AdoFilterTranslator` now takes an optional `innerQuerySuffix` and the `sql` (SQL Server) registration appends `" OFFSET 0 ROWS"`; (3) Oracle's implicit `VARCHAR2`→`NUMBER` conversion is session-NLS-dependent, so a value like `"2000.00"` can fail with `ORA-01722` against a session that doesn't treat `.` as the decimal separator — the oracle registration now casts numeric columns via `AdoFilterTranslator.OracleCast` (`TO_NUMBER` with an explicit format model and an `NLS_NUMERIC_CHARACTERS` override; verified empirically that this plain format model — deliberately with no `S`/`MI`/`PR` sign element — already parses a leading `-` correctly, since adding an explicit sign element instead broke ordinary *positive* values by then requiring an explicit leading `+`). `IFilterTranslator.TryTranslate` gained a `ReportSchema schema` parameter (not yet released, so not a SemVer break) so a translator can look up each filter column's declared `ColumnType`. A `Contains`/`StartsWith` filter against a non-`String` column previously emitted an uncastable `LIKE` comparison that crashed with a raw provider error (an unhandled 500, e.g. Postgres's "operator does not exist: numeric ~~ unknown") — `AdoFilterTranslator.TryTranslate` now declines to translate (returns `false`, surfacing the existing honest 400 `ReportPreviewRunner` already gives a translator that can't handle a request) instead of emitting broken SQL. **Known gap, not fixed here:** filtering Oracle's `Date` column (or any column colliding with an Oracle reserved word) still fails with `ORA-01747` — `AdoFilterTranslator` interpolates `t.{Column}` unquoted, and Oracle's case-folding of the unquoted reference doesn't match the quoted alias a reserved-word column needs (`AS "Date"`); needs its own per-provider identifier-quoting design, tracked as a follow-up rather than folded into this fix (closed by G8, below). `OracleCast` also only covers `Integer`/`Decimal`/`Money` — `Boolean`/`Uuid`/`Date`/`DateTime`/`Timestamp` are left uncast for the same reason (no single safe cast to guess without knowing the report author's actual underlying Oracle column representation) — remains open. **Fix (G8):** `AdoFilterTranslator` gained an optional per-provider `quoteIdentifier` delegate (`Func<string, string>?`, applied to the filtered column when building the outer `t.{...}` comparison). Oracle registers `AdoFilterTranslator.OracleQuoteIdentifier`, which wraps a column name in double quotes only when it matches a curated Oracle reserved-word/datatype list (`Date`, and others in the same category); every other column stays bare, matching Oracle's default case-folding of the report author's own unquoted inner-query SQL, so non-colliding columns are unaffected. |
| D23 | Multi-source | Planned for v2 (Epic B2). Any report assembled from several sources (join/enrich). Likely Pro. **Two explicit, user-chosen strategies** (not auto-detected): keyset **merge-join** of two ordered sources (constant memory) and per-row **enrichment/lookup** (batched per page). Reuses the workbook writer for multi-source-per-sheet. Design recorded before coding |
| D24 | UI ordering | Blazor UI is the **last** v2 epic (after dynamic path + multi-source + a user-validation gate), per the maintainer. Always built from the Claude Design handoff, never invented |
| D25 | v2 additivity | Every v2 addition is additive and SemVer-minor on `Abstractions`; v1's frozen surface is never broken, only extended. Removing anything stays SemVer-major |
| D26 | Config trigger | Config reports are registered at startup (`AddReportFromConfig`/`File`/`Directory`) and compiled lazily on first registry resolution, then run **by name** through the standard runner/endpoints. v2 does **not** add an endpoint that runs arbitrary config (connection string + SQL) from a request body — that is an injection/SSRF surface; ad-hoc config execution stays out until there's a vetted, authorized design |
| — | Design | Already done in Claude Design; export per the handoff; UI post-MVP |

---

## D11 — Retry, skip and threshold semantics (Core / PR 2)

**Decision.**
- The unit of resilience is the **read of a batch**. The `ResiliencePipeline` (Polly v8) wraps `reader.ReadAsync` (read + filter + projection). `MaxAttempts` includes the first attempt (`MaxRetryAttempts = MaxAttempts - 1`). Cancellation (`OperationCanceledException`) is never retried.
- A **read failure** after retries are exhausted **is not "skippable"**: without a read batch there is no `NextCursor` to advance the keyset pagination, so silently skipping would truncate data. In that case, even in skip mode, the report **aborts** (status `Failed`).
- A **projection/write failure** of an already-read batch **is skippable**: the `NextCursor` is already known, so `SkipBatchAndLog` discards that batch, logs a structured warning and marks the report as **partial** (`CompletedPartial`), moving on to the next cursor.
- `IFailureStrategy` receives counters (consecutive/total/ratio) via `BatchFailureContext`; `SkipBatchAndLog().AbortIf(t => t.ConsecutiveFailures(n))` escalates to Abort when the threshold is reached.
- **Writer atomicity assumption:** writers must write a batch atomically (buffer and flush) so that a skip leaves no partial row. Output goes to a per-execution temp file; publishing (upload) happens only at the end (aligns with D2: restart-from-zero, atomic publish).

**Why.** Retry handles transient read failures (AC-11); skip + threshold give resilience to definitive failures without corrupting keyset ordering (AC-12/13/14). Separating reading (retryable, idempotent) from writing (not re-written) avoids double-writing into the output stream.

---

## D12 — `Map` via a `From` overload, single-generic builder (Core / PR 2)

**Decision.** `ReportBuilder<TRow>` is generic **only** over the final row type `TRow`. Mapping from a different source type is expressed by overloads `From<TSource>(IBatchSource<TSource>, Func<TSource,TRow>)` / `From<TSource>(IStreamingSource<TSource>, Func<TSource,TRow>)`, which adapt the source via `MappingBatchSource`/`MappingStreamingSource`.

**Why.** A `Map<TOut>` step that changes the builder's type would break the `AddReport<TRow>("name", Action<ReportBuilder<TRow>>)` registration pattern (the lambda would stay on a builder of another type while the registration built the original). The `From` overload delivers the same capability (the spec's "Map to an output type") without that trap and without a second generic parameter on the builder. Columns are declared with `.Column(v => v.X, "Header")` (infers `ColumnType` from the member type) or `Columns(Col(...))`.

---

## D13 — SQL Server keyset source (Sources / PR 3)

**Decision.**
- Fluent entry: `Source.Sql(connectionString, sql).Keyset<T,TKey>(v => v.Id, pageSize: 1000)`. The first parameter is the **connection string** in v1; resolution by **connection name** (config/DI, like the spec's `"sales-db"`) is post-MVP.
- The query is the author's responsibility and **must** expose a `@cursor` parameter on the key column and order by it — recommended pattern `WHERE (@cursor IS NULL OR Id > @cursor) ORDER BY Id`. The first page sends `@cursor = NULL`.
- **Connection opened/closed per page** (D2/D3). The **cursor** is the page's last key serialized as `string?` (`BatchResult.NextCursor`); `HasMore` is true only when the page filled up (`Count == pageSize`) and there is a last key.
- **Defensive parameter binding:** only the parameters the query actually references are added to the command (text scan for `@name`), avoiding "too many parameters"; run-time parameters override the static ones without duplicating.
- **Materialization** (`RecordMaterializer<T>`): prefer the POCO's longest constructor (positional records), matching parameters↔columns by name (case-insensitive); fall back to a parameterless constructor + settable properties. Column ordinals mapped by name.
- The `Schema` declared by the source is a minimal placeholder — the pipeline's projection uses the builder's columns (D1), not the source's schema.

**Why.** Keyset with an opaque `@cursor` and connection-per-page satisfies AC-2 (reads all pages in order, without skipping/repeating) and keeps constant memory, leaving checkpoint/multi-worker viable later without rework. Connection-by-name is configuration sugar that doesn't change the contract — cut from v1.

---

# v2 decisions (post-MVP — reopening scope)

> v1 (1.0.0) is published. The decisions below reopen scope **deliberately and additively**.
> Locked order with the maintainer: **Epic A (dynamic path) → Epic B (multi-source / multi-sheet) → validation gate → Epic C (Blazor UI) last.** Nothing here breaks v1's frozen `Abstractions` (D25) — it only extends it.

## D21 — Dynamic path: positional `ReportRecord`, config + JsonLogic (v2 / Epic A)

**Decision.** The dynamic (config-driven) path returns in v2. It does **not** revisit rule 1 ("never a dictionary as the row type"): the dynamic row is a **positional `ReportRecord`** — an `object?[]` aligned to a declared `ReportSchema`, exactly the shape the writer edge already consumes (D1). The pipeline stays generic over `T`; the dynamic path simply runs with `T = ReportRecord`. Writers, destinations, jobs and resilience are **untouched**.

- `ReportRecord` and a `ReportRecord` `IBatchSource` return to `Abstractions` (additive, SemVer-minor — D25).
- Config is **JSON**, parsed by `IReportConfigParser` into a runnable registration (the same internal model `AddReport<T>` produces). Columns/source/outputs/destinations/retry/onFailure mirror the fluent builder one-to-one.
- The dynamic **filter** is a **JsonLogic** expression compiled to `Func<ReportRecord,bool>` (D10's deferred half). Typed delegates remain the code-first option; JsonLogic is the dynamic one. DynamicLinq stays out unless a concrete need appears.
- No new execution path: a config report is just another registration. Same `ReportRunner`, same jobs, same endpoints.

**Why.** Reusing the positional edge means the dynamic path is *configuration on top of the existing engine*, not a parallel engine. Constant memory, resilience and the writer contract all carry over for free. This is the single biggest reason `Abstractions` was kept minimal-but-open in v1.

## D22 — Multi-sheet XLSX (v2 / Epic B — design before code)

**Decision (directional).** A single XLSX workbook with several **named sheets**, each fed by a different filter (and later a different source), without breaking the single-pass read (D14's ClosedXML in-memory model already holds the whole workbook, so multi-sheet fits naturally). Exact API and whether it is a **paid** capability are **TBD with the maintainer**; a concrete decision is recorded here before coding. Tracked in `memory/open-questions.md`.

## D23 — Multi-source reports (v2 / Epic B — design before code)

**Decision (directional).** Any report assembled from **several sources** (join/enrich) into one output — likely the headline **paid** feature. Monetization model (free core vs paid) and the join semantics are **TBD with the maintainer**; recorded before coding. Tracked in `memory/open-questions.md`.

## D24 — UI is the last v2 epic (Blazor + Claude Design handoff)

**Decision.** The Blazor Server + MudBlazor UI is built **last** in v2, after the dynamic path, multi-source, and a real user-validation gate — by the maintainer's explicit ordering. It is built **only** from the Claude Design handoff (`tokens.css`, `components.html`, per-screen `.html`, `handoff.md`); design is never invented or diverged from the Design System tokens.

## D25 — v2 additivity / SemVer discipline

**Decision.** Every v2 addition to `Abstractions` is **additive** (new types/members) and ships as **SemVer-minor**. v1's published surface is never changed in place; removing or changing a signature would be SemVer-major and is avoided. External plugins built against 1.x keep compiling against any 1.y.
