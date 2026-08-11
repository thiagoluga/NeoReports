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
| D14 | Streaming XLSX (superseded 2026-07-30) | **Originally:** the XLSX writer used ClosedXML, which materializes the whole workbook in memory — a conscious exception to "constant memory" (rule 8), accepted for v1 sizes. **Now resolved:** both the MIT single-sheet writer and the Pro multi-sheet workbook writer are rewritten on `DocumentFormat.OpenXml`'s SAX `OpenXmlWriter`, streaming each worksheet's XML straight to a per-sheet temp file (0600 on Unix) and hand-assembling the `.xlsx` with `System.IO.Compression.ZipArchive` in Create mode written directly to the pipeline's write-only output stream — bypassing `System.IO.Packaging`, whose `ZipPackage` (Update mode) buffers every part in RAM. Measured live memory is flat (~1.5 MB) writing 100k→2.4M rows while the output grows to 60+ MB; a regression test enforces it. Strings are inline (no shared-string table). The only behavioural change is the dropped column auto-fit (`AdjustToContents` is O(rows×cols) and can't stream). ClosedXML is removed from both writer packages. CSV was already truly streaming |
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
| D29 | Multi-source packaging | **Resolved: Pro.** B2's value is the join sources themselves, so there is no natural MIT/Pro split like B1 — it is a straight monetization call, settled as **Pro** for consistency with the B1 decision (`NeoReports.Sources.Join.Pro`, same model as D27: PolyForm Small Business, `IsPackable=false`, no runtime enforcement — ~~superseded by D70~~, see below). v1 join types are **inner + left-outer**; dynamic-config support for multi-source is deferred (B2.4, optional) |
| D30 | Pro distribution | Pro packages are **not published** to any feed for now (maintainer decision, B1.4). A dedicated `pack-pro.yml` workflow packs `NeoReports.Xlsx.Pro` and `NeoReports.Sources.Join.Pro` as CI **build artifacts** — per-project `IsPackable=true` override, versioned like the OSS release tag, also runnable on demand (`workflow_dispatch`) — keeping them continuously packable (metadata + LICENSE.txt validated) and one click away for a first customer. The OSS release pipeline is untouched. A private feed (e.g. GitHub Packages) is deferred until there are paying customers. **Superseded by `## D70`** (2026-07-22): once the Pro packages are published publicly, "no runtime enforcement" is replaced by an offline signed-license check (Epic Q) |
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
| D42 | Source registry (named source instances + on-demand health) — Epic F | Maintainer decision (2026-07): the engine gains its first *instance*-level source concept — a named, persisted `SourceDefinition` (name under the same regex as dynamic report names; provider type id; property bag reusing D33's `${VAR}` placeholder mechanism; description) in a file-backed `ISourceRegistryStore` (Core), hydrated like dynamic reports and hydrated **before** them. **Packaging: MIT** (maintainer call — not Pro). Reports reference one via a new additive `Ref` on `SourceConfig` (supersedes D13's "connection-by-name is post-MVP" deferral): the compiler checks existence at compile time but resolves properties **at run time** (definition base, report-local overlay, `${VAR}` substitution on the merged bag), so rotating a connection string takes effect on the next run of every referencing report without recompiles; inline sources remain fully supported, and `IConfigSourceProvider` implementations are untouched (they receive an ordinary merged `SourceConfig`). CRUD is `GET/POST/PUT/DELETE /api/sources` — GET never returns properties (the D33 property-bag rule at its most literal), which is precisely why **`PUT` full-replace is allowed** while report edit stayed punted: secrets never round-trip because the client always re-sends placeholders. Delete is blocked (409) while any registered report references the source; "used in N reports" is computed from `CompiledReport.SourceRef`, never tracked. Health is `ISourceHealthCheck` (Core), resolved per provider type from DI like `IConfigSourceProvider` — nothing SQL-specific in Core; `Sources.Sql` ships open-and-ping. Checks run **on demand** (`POST /sources/{name}/health`) with the last result cached and timestamped — no background poller, because a stale reading presented as current is the fabricated-telemetry pattern D36 removed; "never checked" is a first-class state. **Typed-path by-name authoring is in scope** (`Source.SqlNamed("sales-db", sql)`): the Core compile step injects a per-run registry resolver, since sources have no `IServiceProvider` on the read path. Blueprint: `docs/epic-f-source-registry.md`. **Fix (2026-07-15, reported directly by the maintainer):** `RefBatchSource`'s lazy per-run resolution — necessary so a rotated connection string takes effect without recompiling, per this entry's own design — depends on the `IServiceProvider` it was compiled with still being valid whenever a run actually happens, potentially long after compilation. `POST /reports` (the only *dynamic* — i.e. runtime, non-typed — path that creates a `Ref`-based report) compiled with `http.RequestServices`, a provider scoped to the triggering HTTP request; the compiled report is registered into the singleton registry and a real run happens on an async job's own background task, so by the time `RefBatchSource.ResolveAsync` first ran, the request (and its DI scope) was long gone — `Cannot access a disposed object. Object name: 'IServiceProvider'.` on every single async run of every `Ref`-based dynamic report, 100% reproducible, never triggered by `?mode=sync` (which completes inside the same still-open request). The typed by-name path (`Source.SqlNamed`) was never affected — its "per-run registry resolver," per this entry's original text, is injected at Core compile time against the host's stable root provider, not a request scope. Fixed by resolving through `IEndpointRouteBuilder.ServiceProvider` (the app's root provider, captured once when `MapNeoReports` runs) in both `POST /reports` and `POST /reports/validate`, never `http.RequestServices`, for exactly this reason: anything a compiled report's reader might resolve *lazily, after this request ends* must come from a provider that outlives the request. |
| D43 | Additional relational sources: PostgreSQL, MySQL/MariaDB, Oracle — via a shared ADO.NET keyset engine — Epic G | Maintainer decision (2026-07, requested directly, not derived from an audit): v1's source scope ("SQL source (keyset)") expands to cover the other mainstream relational engines, reusing `SqlKeysetSource<T>`'s design rather than reinventing it three times — its internals already type everything through `System.Data.Common` (`DbCommand`/`DbDataReader`/`DbParameter`), so only connection creation is SQL-Server-specific. A new **MIT** package, `NeoReports.Sources.Common`, extracts that engine as `AdoKeysetSource<T>` (parametrized by a `Func<DbConnection>` connection factory instead of a hardcoded `SqlConnection`) plus the existing `RecordMaterializer<T>`; three new sibling packages — `NeoReports.Sources.Postgres` (Npgsql), `NeoReports.Sources.MySql` (MySqlConnector), `NeoReports.Sources.Oracle` (Oracle.ManagedDataAccess.Core) — each ship the same shape `Sources.Sql` already established: `Source.<Provider>(connString, sql)` / `Source.<Provider>Named(name, sql)` typed entry points, `<Provider>ConfigSourceProvider` (dynamic path, `type: "postgres"|"mysql"|"oracle"`), `<Provider>SourceHealthCheck` (open-and-ping), and `Add<Provider>ConfigSource()` DI registration — all built on the shared engine. Only `SqlKeysetSource<T>` itself — already-published (v1.2.0) public API — is left untouched, to avoid a needless break for existing consumers for zero behavioral gain; everything *around* it that was never part of that public contract (`SqlConfigSourceProvider`'s property parsing, `SqlSourceHealthCheck`'s ping body, `Source.cs`'s member-selector helper) is unified onto the same `AdoConfigProperties`/`AdoSourceHealth`/`MemberSelector` helpers the new providers use, closing the duplication Sonar's quality gate correctly flagged on the first attempt at this decision rather than accepting it as a permanent tradeoff. Each provider's integration tests use Testcontainers (`Testcontainers.PostgreSql`/`.MySql`/`.Oracle`), `[SkippableFact]`-gated like the existing SQL Server suite so CI without Docker still passes. Blueprint: `docs/epic-g-more-sources.md`. |
| D44 | MongoDB source (non-relational, own pagination strategy) — Epic G | Maintainer decision (2026-07): MongoDB cannot reuse D43's ADO.NET engine — no `DbConnection`/`DbDataReader`, no relational cursor model — so it gets its own **MIT** package, `NeoReports.Sources.MongoDb` (`MongoDB.Driver`), implementing the same *outcome* as keyset pagination (opaque `string?` cursor, no offset/skip drift under concurrent writes) via a sort-and-range-filter query: page N+1 is `Find(keyField > cursor).Sort(keyField).Limit(pageSize)`, cursor = the last returned document's key field serialized to its string form. Typed entry point `Source.MongoDb(connectionString, database, collection).Keyset(keySelector, pageSize)`; dynamic path `type: "mongodb"`; health check is `RunCommandAsync({ ping: 1 })`. D45's structured filter translation is **SQL-family only in this first cut** — Mongo previews run unfiltered with an honest UI note (D36's degrade-honestly pattern) until a follow-up teaches it BSON filter translation. Integration tests use `Testcontainers.MongoDb`, `[SkippableFact]`-gated. Blueprint: `docs/epic-g-more-sources.md`. |
| D45 | Report preview: paginated read-only sample, plus structured (non-expression) filter editing for SQL-family dynamic sources — Epic G | Maintainer decision (2026-07): a **new, narrower** capability than the "Source explorer" D36 flagged as needing its own ADR — this previews one already-registered report's own configured source (not ad-hoc browsing of any source's schema/data), and it is a genuinely new mechanism, **not** a return of the removed JsonLogic/DynamicLinq expression evaluator (CLAUDE.md's "Out" list stands unchanged). `POST /reports/{name}/preview` runs the source for a bounded page (server-capped page size, no output writing, no upload, no job record) and returns rows + schema; every source type supports the *unfiltered* sample. Filter editing is additive and scoped tightly: a closed list of structured rows — `Column` (must be one of the report's declared columns), `Operator` (`equals`/`notEquals`/`gt`/`gte`/`lt`/`lte`/`contains`/`startsWith` — a fixed enum, never a free-form expression), `Value` — translated by SQL-family sources (`Sql`/`Postgres`/`MySql`/`Oracle`, D43) into a parameterized `WHERE` fragment appended to the keyset query (always via `DbParameter`, never string-concatenated); Mongo (D44) and any source without a translator ignore filters and the UI says so honestly rather than silently dropping them. **Filters are ephemeral, never persisted**: `POST /reports/{name}/preview` and the additive `Filters` field on `RunReportRequest` both apply for that one call only — no new `PUT`, no config-document mutation, no override store, mirroring the precedent `RunReportRequest.Parameters` already set for run-time values. Typed (code-first) reports are preview-only, **not** filter-editable — their `Filter(Func<T,bool>)` is a compiled predicate with no structured representation to edit and re-submit; only dynamic (config-registered) SQL-family reports expose the filter editor. Blueprint: `docs/epic-g-more-sources.md`. **Implementation refinement (G5):** `IFilterTranslator.TryTranslate(string sql, IReadOnlyList<PreviewFilter> filters, out string translatedSql, out IReadOnlyDictionary<string, object?> parameters)` — no `DbCommand` parameter, as originally sketched. `CompiledReport` erases its row type behind an internal `ReaderFactory`, so a `DbCommand` already bound to the report's compiled source was never reachable from the preview endpoint; the translator instead returns the filtered SQL text plus a name→value dictionary that flows into the *existing* `ReportExecutionContext.Parameters` mechanism `AdoKeysetSource` already merges into its bound query — filter values are still always parameter-bound, never string-concatenated, just via a seam that already existed rather than a new one. A dynamic report's filtered preview re-reads its *stored* config document (`IReportConfigStore`) rather than the compiled, type-erased source, and — for a `Ref`-based source — merges the registry definition's properties with the report's own overlay the same way `RefBatchSource` does (definition base, report-local overlay wins). `RunReportRequest.Filters` is additive on the contract now, but a full filtered *run* (not just preview) is deferred — it needs a temporary re-compiled report threaded through the job/scheduler pipeline, a separate piece of work; `POST /run` returns 400 on a non-empty `Filters` until then. **Implementation refinement (G6):** two scope cuts, both consequences of G5's already-merged contract rather than new decisions. No "Load more" pagination — `PreviewResponse` carries `hasMore` but no next-page cursor (G5 always reads page 1 only), so the UI shows an honest "more rows exist" note instead of a button that couldn't actually fetch them; deferred alongside filtered-run support, since both need the same kind of follow-up to G5. The filter editor isn't hidden *upfront* for a dynamic source with no translator (only for typed/code-first reports, which the UI can determine from `ApiReportDetail.Origin`) — there's no "does this source support filters" capability query on the wire, only `filtersApplied` on a preview *response*, so the honest note can only appear after the user's first filtered attempt, not before it. **Fix (G7, before first release):** `PreviewFilterRequest.Value` was `object?`, and without an explicit converter `System.Text.Json` leaves an `object?`-typed property as a boxed `JsonElement` on minimal-API model binding — no ADO.NET provider can bind that as a `DbParameter` value, so **every** filtered preview against **every** relational provider was broken regardless of column type. A first pass fixed this by applying the existing `PrimitiveObjectConverter` (used for `ReportConfig`/source-registry property bags) to `PreviewFilterRequest.Value` — but that converter also recovers a round-tripped ISO-8601 string as a CLR `DateTime`, which is exactly wrong for a filter value: an ordinary decimal a user might type, e.g. `"12.25"`, parses as December 25 under `DateTime.TryParse`'s lenient rules, silently corrupting both `Contains`/`StartsWith` patterns (built from the reformatted date instead of the literal text) and typed casts (chosen from the column's declared type, now mismatched against the value's silently-changed runtime type) — caught by an automated multi-angle code review before merge, with a concrete repro. The actual fix goes further: `PreviewFilter.Value` (Core) and `PreviewFilterRequest.Value` are now `string?`, not `object?` — a filter value is always its literal text form, matching exactly what the preview UI's plain text input sends, checked by the compiler rather than merely documented. A new `FilterValueConverter` (AspNetCore) stringifies any JSON scalar (string verbatim, number as its exact written digits, boolean as `"true"`/`"false"`) with no date-sniffing. This, plus schema-aware casting once every value is guaranteed text, surfaced three more real, distinct bugs once exercised against actual Testcontainers-backed databases (not just string/dictionary unit tests) for all four relational providers: (1) Postgres has no implicit `text`→typed conversion in a comparison — the same class of gap D43 hit for keyset cursors, but here there is no report-author-controlled SQL text to hand-write a cast into, so `AdoFilterTranslator` now takes an optional `castParameter` delegate and Postgres registers `AdoFilterTranslator.PostgresCast` (`{token}::{type}`); (2) SQL Server rejects a bare `ORDER BY` inside a derived table (every keyset query already ends with one) unless followed by `TOP`/`OFFSET`/`FOR XML` — `AdoFilterTranslator` now takes an optional `innerQuerySuffix` and the `sql` (SQL Server) registration appends `" OFFSET 0 ROWS"`; (3) Oracle's implicit `VARCHAR2`→`NUMBER` conversion is session-NLS-dependent, so a value like `"2000.00"` can fail with `ORA-01722` against a session that doesn't treat `.` as the decimal separator — the oracle registration now casts numeric columns via `AdoFilterTranslator.OracleCast` (`TO_NUMBER` with an explicit format model and an `NLS_NUMERIC_CHARACTERS` override; verified empirically that this plain format model — deliberately with no `S`/`MI`/`PR` sign element — already parses a leading `-` correctly, since adding an explicit sign element instead broke ordinary *positive* values by then requiring an explicit leading `+`). `IFilterTranslator.TryTranslate` gained a `ReportSchema schema` parameter (not yet released, so not a SemVer break) so a translator can look up each filter column's declared `ColumnType`. A `Contains`/`StartsWith` filter against a non-`String` column previously emitted an uncastable `LIKE` comparison that crashed with a raw provider error (an unhandled 500, e.g. Postgres's "operator does not exist: numeric ~~ unknown") — `AdoFilterTranslator.TryTranslate` now declines to translate (returns `false`, surfacing the existing honest 400 `ReportPreviewRunner` already gives a translator that can't handle a request) instead of emitting broken SQL. **Known gap, not fixed here:** filtering Oracle's `Date` column (or any column colliding with an Oracle reserved word) still fails with `ORA-01747` — `AdoFilterTranslator` interpolates `t.{Column}` unquoted, and Oracle's case-folding of the unquoted reference doesn't match the quoted alias a reserved-word column needs (`AS "Date"`); needs its own per-provider identifier-quoting design, tracked as a follow-up rather than folded into this fix (closed by G8, below). `OracleCast` also only covers `Integer`/`Decimal`/`Money` — `Boolean`/`Uuid`/`Date`/`DateTime`/`Timestamp` are left uncast for the same reason (no single safe cast to guess without knowing the report author's actual underlying Oracle column representation) — remains open. **Fix (G8):** `AdoFilterTranslator` gained an optional per-provider `quoteIdentifier` delegate (`Func<string, string>?`, applied to the filtered column when building the outer `t.{...}` comparison). Oracle registers `AdoFilterTranslator.OracleQuoteIdentifier`, which wraps a column name in double quotes only when it matches a curated Oracle reserved-word/datatype list (`Date`, and others in the same category); every other column stays bare, matching Oracle's default case-folding of the report author's own unquoted inner-query SQL, so non-colliding columns are unaffected. |
| D46 | Samples standardization + Aspire-orchestrated multi-DB samples — Epic H | Maintainer decision (2026-07, requested directly): the 9 existing samples grew organically with no shared conventions — a scoping pass (see `docs`/agent survey) found `Sale` (an identical 4-field record) copy-pasted across 01/02/03/06, two independently-invented in-memory fake-data providers (04's fixed-to-`Sale`, 09's generic/schema-driven — 09's is strictly more capable), a csproj-naming split (01-06 name the file after the folder, 07-09 after the assembly), and 01-03 having no README at all. **Standardize first** (H1): a new non-packable `NeoReports.Samples.Shared` project holds one canonical `Sale` and promotes 09's generic schema-driven `InMemoryBatchSource<T>` as the one fake-data provider, migrated into by 01/02/03/04/06; csproj naming unifies onto the assembly-name style (07-09's), since it scales better once several same-shaped Aspire samples are added; 01-03 get minimal READMEs; the `Nullable`/`ImplicitUsings` re-declarations already covered by `samples/Directory.Build.props` are removed. **Then add Aspire samples** (H2/H3): four new self-contained samples — one Aspire AppHost + report project per already-shipped relational/document provider (`10-aspire-postgres-wide`, `11-aspire-mysql-wide`, `12-aspire-sqlserver-wide`, `13-aspire-mongodb-wide`, using `Aspire.Hosting.PostgreSQL`/`.MySql`/`.SqlServer`/`.MongoDB`, CPM-versioned) — deliberately **not** one shared AppHost orchestrating all four, so a reader interested in one provider isn't forced to pull Docker images for the other three. Seed scale is standardized via a shared H2 generator in `NeoReports.Samples.Shared`: a wide POCO (~50 columns spanning every `ColumnType`) and a deterministic (seeded `Random`) bulk generator targeting ~500,000 rows — large enough to make the engine's constant-memory streaming a genuine, visible selling point (the same order of magnitude as the `NeoReports.Benchmarks` 1M-row memory-diagnoser target), without making a sample take unreasonably long to seed/run on a contributor's machine or in CI. **Implementation (H2/H3):** the wide POCO (`WideTransaction`) ended up at 51 columns spanning six CLR types with an unambiguous `ColumnType` inference (`string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) rather than literally every `ColumnType` — the typed builder's `Col<T,TProp>` infers `ColumnType` purely from `TProp` with no override, and no CLR type maps to `ColumnType.Money` at all (`decimal`/`double`/`float` all infer `Decimal`), so `Money` is reachable only via the dynamic/config path, out of scope for a typed-path sample. Aspire is pinned to the 9.x line (9.5.2): the `dotnet new aspire-apphost` template's default (13.x) forces `TargetFramework=net10.0` on the AppHost, inconsistent with every other sample's `net8.0`; 9.5.2's `Aspire.AppHost.Sdk` targets `net8.0` cleanly. Each provider surfaced its own real gotcha under real-container verification — Postgres needs an explicit keyset-cursor cast and UTC-Kind stripping for `TIMESTAMP` columns; MySQL needs `GuidFormat=Char36` (no native UUID type) and has no bulk-copy ADO.NET API, so seeding is batched parameterized `INSERT`s; SQL Server's `UNIQUEIDENTIFIER` sorts in its own byte-group order (a different row order than the other three providers, not a correctness bug) and its 2,100-parameter-per-query limit rules out a naive batched-insert approach at this table's width, so seeding uses `SqlBulkCopy`; MongoDB's driver refuses to serialize a `Guid` at all without an explicit process-wide `GuidRepresentation` registration. Full detail in PLAN.md's H3 entry. **Follow-up (2026-07-13):** each sample's headless `ReportRunner` (seeded the DB, ran the report once, exited) was replaced by a `Web` ASP.NET Core host that mounts the full NeoReports UI (same pattern as `09-web-ui-live`) with `wide-transactions` already registered typed against the matching source — Aspire now only provisions the database and starts the UI; running the report, watching progress, and downloading the file happen by clicking through it. Also fixed: the shipped `AppHost.cs` files had never actually been launched end to end before H3 merged (only `ReportRunner`'s logic was verified against manually-started containers) — both `Properties/launchSettings.json` (missing entirely; without it `DistributedApplication.Build().Run()` throws on startup) and `UserSecretsId` (missing; without it Aspire can't persist the generated DB password across runs, so a `WithDataVolume()`-reused container fails auth forever after the first run) were absent from every AppHost project. Full detail in PLAN.md's H3 entry. |
| D47 | Real progress percentage via an opt-out source row count — refines D33(g) | Maintainer decision (2026-07): the running-job page's percentage was a client-side timer animation with no connection to real progress — the last decorative number left on that screen after D36 — and is replaced by a real one. D33(g) rejected a real percentage because it "would need a source `COUNT`", i.e. a forced cost on every run; this entry **refines rather than reverses** it by making the count a per-report, opt-out setting: `ReportConfig` gains an optional, additive `TrackProgress` (`bool?`, omitted = engine default) and the typed builder gains `.TrackProgress(bool enabled = true)` — **default enabled** in both paths and pre-checked in the Builder UI (the original objection was the unconditional cost, not the count itself). Counting is a **Core capability interface**, `ISourceRowCounter` (`Task<long> CountAsync(ReportExecutionContext, CancellationToken)`), detected by `is`-pattern-matching on the source instance exactly like `INamedSourceResolver` — **not** an addition to the frozen `IBatchSource<T>`/`IStreamingSource<T>` (same placement logic as D41's `IRecurringReportScheduler` and D38's `IJobEventStore`: an engine concern, not an ABI obligation). `AdoKeysetSource<T>` implements it by wrapping the report's own query — `SELECT COUNT(*) FROM (<sql><suffix>) q` with the cursor parameter bound null (first-page semantics) and all static/run-time parameters bound as on a real read, so the count counts exactly what a full run would read; SQL Server's wrapper suffix is `" OFFSET 0 ROWS"`, the same derived-table fix G7 established for the filter translator (both `Source.Sql` and `Source.SqlNamed` set it). `SqlKeysetSource`/`AdoNamedKeysetSource` delegate (the named source resolves its connection through the registry at count time, same freshness rule as reads); `MongoDbKeysetSource` uses **exact** `CountDocumentsAsync` — an estimated count that lets the bar end at 96% or 104% is precisely the fabricated-telemetry pattern D36 removed, and the opt-out already covers anyone who finds the exact count too expensive. `MappingBatchSource`/`MappingStreamingSource`/`RefBatchSource` forward the capability to their inner source, throwing `NotSupportedException` (degrading to indeterminate) when it can't count. The count runs **once per run** in `ReportRunner.ExecuteAsync`, after the reader is built and before `run-started` is emitted; it is **best-effort**: a disabled toggle, a source with no counter, or a count that throws all degrade identically to **indeterminate** progress (log a warning, run unaffected) — telemetry never changes a run's outcome (D38 ground rule). A restarted run re-counts (restart-from-zero, D2 — the fresh total is the honest one). The total travels on both channels: `JobStats` gains a trailing nullable `TotalRecords` (aggregate truth, additive/SemVer-minor per D25; flows through `JobView` untouched since it embeds `JobStats` wholesale), and the `run-started`/`run-restarted` **and every** `page-completed` event gain a `totalRecords` datum — the live channel, since `JobStats` is only populated once, at job completion, so a poller needs the event log for a live number. **Percentage is `recordsRead / TotalRecords`, not `recordsWritten`** — the count totals source rows before any `Filter(...)`, so a written-based percentage would never reach 100% on a filtered report; read-based always closes at 100%, and is clamped to `[0,100]` regardless (no snapshot isolation between the count and the read finishing). UI (follow-up PR): `ProgressBar` gains an `Indeterminate` mode (sliding-segment animation on the existing bar — flat, no gradients, matching the design system); `JobRunning` computes the real percentage from polled events and falls back to the indeterminate bar with an honest note; Builder's Configure step gains the default-on "Track progress" switch whose off state warns that only counters/rates will be shown, no completion percentage. **Consequence to flag loudly in the CHANGELOG:** default-on means every existing report — typed and dynamic, with no code change required — starts issuing one extra `COUNT` query per run after upgrading; `.TrackProgress(false)` / `"trackProgress": false` restores the old (uncounted) behavior. |
| D48 | Combined all-sources Aspire demo (`14-aspire-all-sources-demo`) — intentional exception to D46 | Maintainer decision (2026-07, requested directly: "quero um demo 100% funcional com varias sources, tudo etc" — a single demo with every source type and every feature working, explicitly **no** "Demo mode" fallback anywhere in the flow). D46 deliberately kept the four Aspire samples (10-13) as one-provider-each so a reader interested in only Postgres isn't forced to pull three other Docker images — this decision does not reverse that: 10-13 are untouched, and sample 14 is a **new, additive** fifth sample whose whole purpose is the opposite tradeoff, orchestrating all four databases (Postgres/MySQL/SQL Server/MongoDB) from one `AppHost` and mounting one `Web` UI in front of all of them, for a maintainer who explicitly wants to exercise everything from one running app rather than four. **Killing "Demo mode" for good:** `GET /api/capabilities`'s `Sources` list (and therefore the Builder wizard's "Save" gate) is populated solely from DI-registered `IConfigSourceProvider` instances — the four single-provider samples never call `Add<Provider>ConfigSource()` because their one typed report doesn't need the dynamic/config path, which is exactly why they show "Demo mode" today. Sample 14's `Web/Program.cs` calls all four (`AddSqlConfigSource`/`AddPostgresConfigSource`/`AddMySqlConfigSource`/`AddMongoDbConfigSource`), so capabilities are never empty and Save is never disabled. **Everything working, not just sources:** `AddSourceRegistry()` pre-registers all four Aspire-provisioned databases as named sources (D42: `postgres-demo`/`mysql-demo`/`sqlserver-demo`/`mongodb-demo`, connection string as a literal property — no `${VAR}` indirection needed for a local demo) so the Builder wizard can build brand-new dynamic reports against any of them by name; `AddDynamicReports()` and `AddScheduling()` are both registered so Builder-created reports and recurring schedules work; one ready-to-run **typed** report per database (`wide-transactions-{postgres,mysql,sqlserver,mongodb}`) ships pre-registered so there's something to click "Run" on immediately, without first having to build one. **Scale:** seeds all four databases in parallel at 15,000 rows each (`WideTransactionGenerator`, same 51-column shape as 10-13) rather than the single-sample default of 500,000 — large enough to be a real wide report, small enough that seeding four databases at once on a contributor's machine finishes in a reasonable time; `SeedRowCount` is a local const, trivially bumped by a reader who wants the full H2 scale. Connection-string resource names are disambiguated per database (`postgres-db`/`mysql-db`/`sqlserver-db`/`mongodb-db`) since one `Web` project now references all four `AddDatabase(...)` calls in one `AppHost`, which would otherwise collide on the shared default name `10-13` each used alone. **Follow-up (2026-07-15):** the same "Demo mode" gap existed one layer down — `GET /api/capabilities`'s `Formats`/`Destinations` come from `IWriterFactory`/`IDestinationFactory` DI registrations, entirely independent of the typed `.To(...)`/`.UploadTo(...)` calls on the four pre-registered reports, so the Builder wizard's Format/Destination steps showed "No output formats registered" even though CSV/XLSX/Local worked fine for those four reports. Fixed by registering `CsvWriterFactory`/`XlsxWriterFactory`/`LocalDestinationFactory` directly (the same pattern `09-web-ui-live` already established), plus `AddPartialArtifacts()` (D40) for full production parity. S3 was deliberately **not** registered — the maintainer chose Local-only over either a fake capability that fails on run or a new MinIO dependency, keeping the demo genuinely 100% functional rather than partially fake. Also found and fixed along the way: `WithDataVolume()` volumes are keyed by a stable per-project hash and persist independently of container lifecycle, so removing containers without removing their volumes after a verification run leaves stale, differently-passworded data behind for the next launch — every database hit this at least once during verification (`password authentication failed`/`Login failed for user 'sa'`/SCRAM `storedKey mismatch`, all from the exact same root cause), fixed each time by `docker volume rm`-ing the affected `neoreports.samples.aspireallsourcesdemo.apphost-*-data` volume(s) before the next run. Not a code defect — a `WithDataVolume()` operational gotcha inherent to iterating on a sample locally, recorded here since it recurred (MongoDB twice) before being fully understood. |
| D23 | Multi-source | Planned for v2 (Epic B2). Any report assembled from several sources (join/enrich). Likely Pro. **Two explicit, user-chosen strategies** (not auto-detected): keyset **merge-join** of two ordered sources (constant memory) and per-row **enrichment/lookup** (batched per page). Reuses the workbook writer for multi-source-per-sheet. Design recorded before coding |
| D49 | Interactive source explorer + visual query builder — Pro, **designed** (see full section below), Epic K | Requested directly by the maintainer (2026-07-15): in the Builder wizard, after picking a source, let the user browse every table/column of the underlying database interactively (not just the report's own declared columns), preview up to the first 50 rows of any table, and compose the report's actual query visually — including **inner join** across tables — with a live preview of the resulting report output. This is exactly the feature **D36 already flagged** when removing the mocked `SourceExplorer.razor`: "schema/data introspection from a UI is a security-sensitive surface (arbitrary read access to whatever the connection string can see) ... needs an ADR before any code" (see `docs/ui-removed-mock-content.md`). **Not designed yet — recorded here only as intent**, per that same instruction: no code until a follow-up ADR settles (a) how far introspection reaches per provider (system catalogs / `information_schema`, not the report author's own already-declared SQL), (b) the row-preview cap and any column masking, (c) how a visually-composed join query gets validated and parameterized before becoming a report's persisted SQL (never raw string concatenation — same non-negotiable as every existing keyset/filter query), and (d) how this relates to D45's already-shipped, narrower preview mechanism (one *already-registered* report's own configured source, no ad-hoc browsing) and D42's named-source model. **Likely Pro-tier**, joining `NeoReports.Sources.Join.Pro` (D29) under the same PolyForm Small Business model. Blueprint: Epic K in `PLAN.md` (not started). |
| D50 | Pro packages in samples/demos — open question, not yet answered | Maintainer question (2026-07-15): can the existing Pro packages (`NeoReports.Sources.Join.Pro`, `NeoReports.Xlsx.Pro`) be wired into a sample (e.g. the D48 all-sources demo) so a demo can showcase Pro capabilities too? **Not a coding blocker today**: per D29/D30, Pro packages have **no runtime license enforcement** — they're gated only by distribution (`IsPackable=false`, never published to a feed, packed only as a CI build artifact via `pack-pro.yml`) and by the PolyForm Small Business *license terms themselves*, not by any code check ( **note: this "no runtime enforcement" premise is reversed by `## D70`** once Pro is published publicly, 2026-07-22 — any sample built after that lands will need a valid license key like any other Pro consumer). A sample could reference either Pro project directly (`ProjectReference`, same as any other in-repo project) and it would compile and run today with no license-key plumbing to build. What's actually unresolved is **whether that's a decision the maintainer wants to make**, not a technical one — bundling Pro code into a sample that ships in the OSS repo changes what a reader can see/copy from Pro-licensed source, which is a distribution/business call analogous to D29/D30, not an engineering one. Recorded as an open question rather than a decision; needs the maintainer's call before any sample references a `.Pro` project. |
| D51 | Builder wizard: token/value helpers — planned, not designed (Epic M) | Requested directly by the maintainer (2026-07-15): "adicionar helpers no builder ... pra colocar data e hora no arquivo" — add UI helpers to the Builder wizard, e.g. to assist generating the output filename with date/time. A full screen-by-screen audit (all 5 Builder steps, every free-text field) found the destination path template is the clear, currently-unassisted target: `BuilderDestination.razor`'s `Wizard.DestinationPath` is a bare `<input>` (placeholder `"./out/{name}-{date:yyyy-MM-dd}.{ext}"`) with **no token picker, insert-menu, or live preview anywhere in the UI**, even though the token system it consumes already fully exists and is richer than the placeholder suggests: `PathTemplate.Expand` (`NeoReports.Destinations.Local`, shared by both `LocalDestination` and `S3Destination`) supports `{name}` (report name), `{ext}` (the writer's real extension), `{date}`/`{date:FORMAT}` (any .NET custom date format string, default `yyyy-MM-dd` — there is no separate `{time}` token; a time component is just `{date:HH-mm-ss}` or combined `{date:yyyy-MM-dd_HH-mm-ss}`), and **any key from `RunReportRequest.Parameters`** (`{paramName}` or `{paramName:FORMAT}` via `IFormattable`) — an unrecognized token is left verbatim as `{token}` by design, "so misconfiguration is visible" (D36 pattern). **Other candidates found in the same audit, explicitly weaker/different in kind** — not filename/date helpers, so scoped separately if pursued: `Wizard.ReportName` (steps 2 and 5) could get a live name-availability check; `Wizard.ColumnNames` could become a pick-list once `Validate` has returned real columns instead of free-typed comma text; `Wizard.ScheduleCron` already has partial helpers (Hourly/Daily/Weekly preset buttons write literal cron strings) but the raw cron input itself has no builder/picker beyond those three presets. **Explicitly not helper candidates**: `Wizard.SqlQuery` (raw SQL, unrelated to filename tokens), `Wizard.KeyColumn`, `Wizard.ConnectionStringVariable` — none of Builder step 1 (source picker, no free text) or step 3 (format picker, no free text) has any relevant field either. **Not designed yet — recorded here only as intent**: needs a design pass on what the helper UI actually looks like (an insert-token dropdown/menu next to the path field, common date-format presets, and a live-resolved-filename preview reusing `PathTemplate.Expand`'s exact logic so the preview never drifts from the real substitution) before any code. Blueprint: Epic M in `PLAN.md` (not started). |
| D52 | Report detail page: missing fields audit — planned, not designed (Epic N) | Requested directly by the maintainer (2026-07-15), from real use of the D48 demo: "ao ver um report criado ... mostrar todos os detalhes do report, por exemplo faltou mostrar a source" — `ReportDetail.razor` should show all of a report's real detail, and is missing at least the source. A full audit against what `GET /reports/{name}` (`ApiReportDetail`) already returns found the gap is **wider than just source**, and splits into two very different sizes of fix. **Pure UI gap, data already flows (small fix):** `ApiReportDetail` already carries `PageSize`, `FailureStrategy`, `RetryMaxAttempts`/`RetryBackoff`/`RetryBaseDelaySeconds`/`RetryUseJitter`, and `AbortAfterConsecutiveFailures`/`AbortAfterTotalFailures`/`AbortAtFailureRate` — none of these render anywhere on `ReportDetail.razor` today, even though a `ResilienceFormatter` helper that formats retry policy already exists and is used on `JobCompleted.razor`'s own "Configuration" card (reusable as-is). **Needs a small API addition first (small-to-medium fix):** `CompiledReport.SourceRef` (the named source's name, for a `Ref`-based dynamic report) is already captured by the compiler but never leaves `GetReportDetailAsync` — `ReportDetailView`/`ApiReportDetail` has no field for it at all. Adding it is safe under D42's existing rule (GET-never-returns-properties applies to the *registry's* source properties, not to which named source a *report* references — the reference name itself is not a secret, same reasoning that already lets `GET /sources` list source names freely). **Needs new engine plumbing, out of scope for a quick fix (bigger question):** there is no source-*type* label (e.g. `"postgres"`) tracked on `CompiledReport` at all for either code-first (typed) reports or dynamic reports using an inline `type` (not `ref`) — sources are fully type-erased into a `ReaderFactory` closure at compile time with nothing else retained. Showing "this report reads from Postgres" for those two cases would need `CompiledReport` to start carrying a declared source-type string through compilation, a real (if small) engine change, not just wiring already-captured data through — deferred pending a design pass on whether it's worth the surface for every future source type. Blueprint: Epic N in `PLAN.md` (not started). **N1 correction (2026-07-15):** re-checking before implementing found this ADR's "none of these render anywhere" claim was already stale at the time it was written — `FailureStrategy` and the retry/abort-threshold fields were added to `ReportDetail.razor` back in D37 (commit `8c5451e`) via `ResilienceFormatter`, before this audit. Only `PageSize` was genuinely missing; N1 added just that one field. |
| D53 | UI test strategy: bUnit component tests (Epic O) | Requested directly by the maintainer (2026-07-15), after being shown a Preview-screen screenshot on a Postgres-sourced report and asking "me diga que tipo de testes voce pode criar para deixar 100% tudo testado na ui" — bUnit (`bunit`, added to `build/Directory.Packages.props`) is the chosen tool: it renders real Razor components against a real `IServiceProvider`/`NavigationManager` (bUnit's own `FakeNavigationManager`, auto-registered) without a browser, so the same `INeoReportsApiClient` seam the pages already use for testability just needs one hand-written test double (`tests/NeoReports.UI.UnitTests/Fakes/FakeNeoReportsApiClient.cs` — a plain field-backed fake per the existing `tests/NeoReports.Core.UnitTests/Fakes` house style, no mocking framework) rather than a real HTTP server. Every one of the 15 pages and the interactive shared components (`DataGrid`, `FilterBar`, `Switch`, `ProgressBar`, `WizardStepper`, `JobStatusBadge`) now has bUnit coverage — 138 new `[Fact]`s across 23 new test files (198 tests total in `NeoReports.UI.UnitTests`, including the 45 pre-existing pure-logic tests for `BuilderConfigMapper`/`BuilderState`/`JobEventFormatter`/`JobRowFormatter`/`ResilienceFormatter`, which this PR doesn't touch) — exercising every documented UI state: engine-unreachable vs. live-empty vs. populated, two-click delete confirmation, the Builder wizard's create-vs-edit `PersistAsync` branches, the Jobs list's stale-response race guard (`_loadSequence`), and — the concrete trigger for this work — `ReportPreview.razor`'s filter wiring. That investigation confirmed the "This source type doesn't support server-side filters" banner is driven **purely** by the engine's own `ApiPreviewData.FiltersApplied` flag on the preview response (`ReportPreviewTests.cs`), never by any source-type check inside the Blazor page — so if that banner appears for a report whose source has a registered `IFilterTranslator` (e.g. Postgres, D45), the defect is in the engine's preview endpoint / filter-translator resolution for that source (worth a separate investigation), not in this UI. `JobRunning.razor`/`SystemMemory.razor` poll via a raw, non-DI-injectable `System.Threading.Timer` (1.5s/3s); most of their tests trigger the identical `PollAsync` codepath through the "Refresh" button (`JobRunning`) or just assert the initial poll (`SystemMemory`, which has no refresh control) to stay fast, but `JobRunningTests.The_real_poll_timer_is_actually_started_and_ticks_on_its_own` deliberately waits on a real tick (`WaitForAssertion`, ~1.5-4s) so a regression that dropped the `Timer` registration entirely wouldn't slip past every other test in the file — bUnit disposes all rendered components (stopping the timers) at test teardown either way. Two bUnit 2.6.2 API notes worth keeping in mind for future tests: `TestContext`/`RenderComponent` are obsolete in this version in favor of `BunitContext`/`Render` (this repo's `TreatWarningsAsErrors=true` makes the obsolete warning a build error); and a child-component click (`SelectableCard`, `Switch`, or `Button`) that changes which `@if` block the parent page renders needs `cut.WaitForState(...)` after `.Click()` rather than an immediate markup assertion, since the resulting re-render isn't always flushed synchronously by the time `.Click()` returns — applied consistently across every such test in this PR. |
| D54 | Preview filter-translator investigation for `Ref`-based sources — no engine defect found | Follow-up to D53's "worth a separate investigation" note. Traced `ReportPreviewRunner.PreviewFilteredAsync`/`ResolveRefPropertiesAsync` (`src/NeoReports.Core/Preview/ReportPreviewRunner.cs:95-174`) end to end: for a `Ref`-based `SourceConfig` (the shape `BuilderConfigMapper` always produces when a registered source is selected — `SourceConfig.Type` is `null`, never both `Type` and `Ref`), the runner already resolves `effectiveType = sourceConfig.Type ?? definition.Type` from the named source's own registered `SourceDefinition.Type` before doing the `IFilterTranslator` lookup — the same fallback `ReportConfigCompiler.ResolveRefSource` uses at compile time. Verified empirically, not just by reading: added `PreviewEndpointTests.Filters_against_a_Ref_based_source_resolve_the_translator_from_the_registered_type` (`tests/NeoReports.AspNetCore.IntegrationTests/PreviewEndpointTests.cs`) — registers a named source via `ISourceRegistry`, creates a dynamic report with `"source": { "ref": "..." }` (no inline `type`), and asserts `filtersApplied: true` against a registered `IFilterTranslator`. **The test passes against the code as it stands** — this exact scenario, previously entirely uncovered (every existing preview test used an inline `"type"`, never `"ref"`), was already correct. Also confirmed `samples/14-aspire-all-sources-demo/Web/Program.cs`'s `postgres-demo` named-source registration (`RegisterNamedSourcesAsync`) uses the literal type string `"postgres"`, matching `AddPostgresConfigSource`'s `AdoFilterTranslator("postgres", ...)` registration exactly — no casing/string mismatch there either. **Conclusion:** no engine-side defect was found for this code path; the mechanism is now covered by a permanent regression test closing the gap. The originally reported banner remains unexplained by anything found through code review — reproducing it again would need either the report's actual persisted config document (`GET /api/reports/{name}` doesn't expose the raw source type for `Ref`-based reports today, see D52/N3) or the named source's registered type (`GET /api/sources/{name}`) from the live session where it was observed, to rule out a config-time mistake (e.g. a report accidentally built against a non-Postgres registered source) rather than an engine bug. |
| D55 | Broad source-type expansion — every feasible bounded-extraction source, message queues/streams out (Epic P) | Requested directly by the maintainer (2026-07-16): "possibilitar todas as fontes possíveis, menos Kafka." The engine's source scope opens up to cover, over time, **every feasible bounded-extraction data source**, each shipped as a new source package on the **existing** extensibility contracts (`IBatchSource<T>`/`IStreamingSource<T>` typed, `IConfigSourceProvider` dynamic) — the same seam Epic G used for Postgres/MySQL/Oracle/Mongo — so the frozen `Abstractions` (rule 7) is never touched. **Explicitly out: message queues / unbounded streams (Kafka, etc.)** — a report is a bounded extraction, not a stream consumer; that's a different product. **Full design section below.** Each source type still needs its own design pass before code (like K1/D43 did), especially the non-relational ones. |
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

## D49 — Interactive source explorer + visual query builder (Epic K, Pro) — design

**Status.** Designed with the maintainer (2026-07-16), superseding D49's earlier "planned, not designed" table row. This is the K1 deliverable the D49 row and D36 both required before any code; K2 (implementation) is unblocked by this section. Pro-tier, joining `NeoReports.Sources.Join.Pro` (D29) under the same PolyForm Small Business, `IsPackable=false`, no-runtime-enforcement model.

### The core decision: the query is a structured model; the SQL is generated

The visual builder holds a **structured query model** (source table + joins + selected columns + WHERE + GROUP BY/aggregations + ORDER BY key) and **generates** the report's SQL from it — the SQL is a derived artifact, never hand-concatenated from user text. An **"edit raw SQL" escape hatch** drops to a free-text editor; once used, the query is marked *custom SQL* and the visual builder is disabled for it (the two states are never kept in sync — the standard dbForge/Metabase resolution to state divergence). Rationale: NeoReports reports **must** be valid keyset queries (`WHERE (@cursor IS NULL OR key > @cursor) ORDER BY key`, D13) — writing that by hand is a footgun (the maintainer's own `vip.json` had a hand-typed keyset). The structured model **auto-generates** a correct keyset wrapper from the chosen key column, so a visually-built query is keyset-valid by construction.

### Introspection scope (maintainer decision, 2026-07-16)

Both sides are visible at once: **(a)** the provider's full catalog via `information_schema` (Postgres/MySQL/SQL Server) / `ALL_TABLES`+`ALL_TAB_COLUMNS`+`ALL_CONSTRAINTS` (Oracle) — schemas → tables → columns with type/nullable/PK/FK — **and (b)** the report's own already-declared SQL, shown in a read-only tab alongside. The catalog tree marks which tables the current SQL already references. **MongoDB is out of K1** (no SQL, no `information_schema`; a "collections + fields inferred by sampling docs" explorer is a much larger, separate lift). Row preview: **top 50 rows, shown raw** (no column masking in K1 — maintainer decision; masking can be a later refinement). No new data exposure: the named source's connection string is the same one the report runs under, so the explorer shows nothing the report could not already read — this is the security premise, stated so it's on record.

### UI (Blazor Server, pure design-system CSS per D31 — no MudBlazor)

**Layout: the notebook** (maintainer decision). Chosen over a 2D drag-canvas (SVG join lines + drag math is too *chatty* over the Blazor Server circuit — every drag is a round-trip) and over a two-panel variant. The screen is:
- **Left — schema explorer**: searchable `schema → table → columns` tree; 🔑 PK / 🔗 FK column icons; a `ti-circle-check` icon (not a text badge — with a hover tooltip "already used in this query") on tables the current SQL references; a per-table "preview 50 rows" action; drag a table from here to add it to the query (hint copy: "drag a table to include it in the query", never the word "notebook" — users don't know the term).
- **Center — the notebook**: linear stack of step cards — `FROM` (with the keyset key column locked and shown), `JOIN` (**FK auto-detected → `ON` pre-filled**; user picks inner/left), `WHERE` (reuses D45's structured filter rows: column/operator/value), `GROUP BY` + aggregations (`sum`/`count`/etc.), `ORDER BY` (auto from the key). "Add step" at the bottom.
- **Bottom — generated SQL** (read-only, live) + a **preview grid** driven by the real generated SQL through D45's `ReportPreviewRunner` (bounded, one page, read-only — closes the build→see-output→adjust loop).
- **"Raw SQL" tab** = the escape hatch. When active, a warning banner explains the visual builder is off **and** that the "already used" table markers use lightweight FROM/JOIN name extraction that "won't be 100% — subqueries, CTEs, and aliases can fool it" (maintainer-requested honest caveat; matches D36's degrade-honestly culture).

**Model richness (maintainer decision):** joins + WHERE + ORDER BY **and aggregation** (GROUP BY / `count`/`sum`/etc.). CTEs, window functions, and anything past the model stay in the raw-SQL escape hatch.

**"Already in query" marker on hand-written SQL:** lightweight regex extraction of table names from FROM/JOIN clauses — imperfect (accepted, since it only drives a hint icon), always accompanied by the caveat banner above so the imprecision is disclosed, never silent.

### Safety by construction

The structured model makes the classic injection vectors impossible in visual mode, without a SQL parser:
1. **Identifier injection** (table/column names can't be parameterized, they're interpolated into SQL text): in visual mode the user only ever *picks* identifiers that came from the introspection result — an **allow-list by construction**, never free-typed — and they're emitted through the provider's existing `quoteIdentifier` (already on `AdoFilterTranslator`). Structurally impossible.
2. **Value injection** (filter values): parameterized (`@p0`, `@p1`), exactly as `IFilterTranslator` already does — reused.
3. **Keyset contract**: auto-generated from the chosen key, never hand-written.
4. **Preview**: goes through the already-bounded `ReportPreviewRunner` (D45).
5. **Authorization**: the explorer inherits the Builder's own access controls — anyone who can reach the Builder can already register reports that run arbitrary SQL, so this escalates nothing; it only makes schema discovery easier. Host-level restriction is the host's auth chain (out of v1 engine scope).
6. **Raw-SQL mode**: the user owns the SQL, identical to today's Builder where they already type raw SQL — no regression.

### New capability: `ISchemaExplorer`

A new per-provider capability, same shape and registration as `IFilterTranslator`/`ISourceHealthCheck` — a `Type` id (matched to `IConfigSourceProvider.Type`) plus `GetCatalogAsync(SourceDefinition, …)` and `PreviewTableAsync(SourceDefinition, table, top: 50, …)`, registered via `TryAddEnumerable` inside each `AddXConfigSource()`. One shared ADO implementation parametrized by dialect (like `AdoFilterTranslator` already is), plus the Oracle catalog-view specifics. The visual query model → SQL generator is a Pro-package concern (`NeoReports.Sources.Join.Pro` or a sibling `.Pro`); the `ISchemaExplorer` contract itself lives in Core (an engine capability, like the other per-provider interfaces), but nothing in the OSS core depends on it — hosts that don't register an explorer simply don't get the feature (honest capability-gating, D36).

**Bynote — free win:** the selected output columns derive the report's `columns`/`ReportSchema` automatically (mapping DB type → `ColumnType`: String/Integer/Decimal/Money/Boolean/Date/…), auto-filling the Builder's currently hand-typed "Output columns" step (a D51 audit candidate too) — and would have caught the maintainer's `OrderDat` typo.

**Implementation refinement (K5a) — the MIT/Pro seam.** The visual builder's SQL generator is a Pro concern, but the UI screen and the HTTP endpoint that drive it are MIT and **must not** reference the Pro package (the `ISchemaExplorer` subsection above already said the model→SQL generator is a Pro concern while the contract stays in Core; K5a is where that seam gets built). The seam is a new Core contract, `NeoReports.Core.QueryBuilder.IQuerySqlGenerator` — `GeneratedReportSql Generate(string modelJson, string sourceType)` returning `(Sql, Parameters, ReportSchema)`. Crucially the query model crosses the boundary as **opaque JSON** (the UI's serialized builder state), *not* a typed model, precisely so the `QueryModel` record can stay Pro-only — no MIT layer ever names it. The Pro `QuerySqlGenerator` deserializes that JSON (camelCase + string-enum `JsonStringEnumConverter`), resolves the `SqlDialect` from the source type, and delegates to `KeysetSqlGenerator`; a malformed/unsupported model throws `QuerySqlGenerationException` with a caller-safe message (it never echoes the raw model back, which could carry sensitive literals). Registered with `AddQueryBuilder()`. The endpoint `POST /sources/{name}/query-sql` resolves the named source through the registry (D42), reads its `Type`, resolves `IQuerySqlGenerator` from DI (422 when none is registered — an MIT-only host, exactly the `ISchemaExplorer` D36 gating), reads the raw JSON body and calls `Generate(json, type)`, returning `{sql, parameters, schema}` or a 400 carrying the exception's caller-safe message. This is the identical capability-gated, honest-degradation shape as `ISchemaExplorer` (K3) and `IFilterTranslator` (D45). Only K5b (the Blazor screen) remains of Epic K.

**Implementation refinement (K5b) — the Blazor screen.** `QueryBuilder.razor` (route `/query-builder` + `/query-builder/{source}`, a "Query builder" nav item) implements the D49 notebook layout: a source picker, a left schema-explorer tree over `GET /sources/{name}/catalog` (search, 🔑/🔗 icons, the `ti-circle-check` "already used" marker + tooltip, a per-table preview via `GET /sources/{name}/preview`), a center notebook of step cards (FROM with the keyset key defaulting to the table PK, JOIN with FK-auto-`ON`, Columns with per-column aggregate, WHERE as D45 structured rows), a live Generated-SQL panel via `POST /sources/{name}/query-sql`, and the Raw-SQL escape-hatch tab with the caveat banner. Only MIT UI code — it talks to the Pro generator solely through the K5a endpoint, so the 422 path honestly shows "not available on this host". **Two faithful simplifications from the D49 sketch, both consequences of the Blazor Server circuit that D49 itself cited when choosing the notebook over a 2D drag-canvas:** *(1)* a table is added to the query with a `+` button, not HTML5 drag-drop — D49's stated reason for the notebook was that "every drag is a round-trip", and a click is the robust end of that same trade-off (the "drag a table…" hint copy became "add a table to include it in the query"); *(2)* SQL is generated on an explicit "Generate SQL" click, not live on every keystroke, for the same round-trip reason. **One scope deferral (tracked as K6):** the D49 result-preview grid — "a preview grid driven by the real generated SQL through D45's `ReportPreviewRunner`" — turned out **not** to be wireable in K5b: `ReportPreviewRunner` (D45) previews a *registered report's* configured source, not arbitrary ad-hoc SQL, and no endpoint runs generated SQL for a bounded preview. Rather than fake it, K5b ships the per-table samples + the generated-SQL panel (both real) and defers the built-query result grid + a "create report from this query" handoff to K6, which needs its own design pass on the ad-hoc-preview endpoint's safety envelope. Consistent with D36's degrade-honestly rule — the screen never shows a result grid it can't actually populate.

**Implementation refinement (K6a) — the ad-hoc query-preview endpoint, and its safety envelope.** K5b deferred the built-query result grid to K6 pending "its own design pass on the ad-hoc-preview endpoint's safety envelope." Resolved: **the endpoint takes the query *model JSON*, not raw SQL, and generates the SQL server-side.** `POST /sources/{name}/query-preview` resolves the named source (D42), resolves `IQuerySqlGenerator` (422 when the Pro package isn't registered — the same D36 gate as `query-sql`), generates the keyset SQL from the model (injection-safe by construction, per the "Safety by construction" points above), then runs **one bounded page** through the source's own `IConfigSourceProvider` — the exact keyset engine a real run uses — via a new `NeoReports.Core.Preview.QueryPreviewRunner` (the query-side sibling of D45's `ReportPreviewRunner`). This deliberately sidesteps the raw-SQL risk the K5b note flagged: **no raw caller SQL is ever executed by this endpoint**, so it can run nothing the visual builder couldn't already compile, and it needs no SQL parser or DDL/DML guard. The runner overlays the generated SQL + a valid result-column key + the row cap (`pageSize`) onto the source's resolved properties, binds the generated parameters through the execution context, reads the first page (cursor bound null), clamps to `QueryPreviewRunner.MaxRows` (100), and reports `truncated` when the page fills. Driver exceptions are mapped to a generic secret-free 502 (the same guard the schema/table-preview endpoints use — a connection-string fragment must never reach the caller). **Previewing hand-written *raw* SQL** (the escape-hatch tab) is a separate future slice with its own decision — it would run arbitrary caller text (bounded, but the same trust boundary as registering a raw-SQL report), and is intentionally out of K6a. The "create report from this query" handoff (K6c) is likewise still open: it additionally needs the generator to expose the key column's output name, since a keyset report requires the key to be a selected result column.

**Implementation refinement (K6c) — "create report from this query", and the key-must-be-selected guarantee.** K6a's closing note flagged the open risk directly: `AdoKeysetSource`/`AdoNamedKeysetSource` read the keyset key **by name out of the query's own result set** (`BuildOrdinalMap` over the live `DbDataReader`, matched against the configured `key` property) — if the key column isn't actually in the generated `SELECT` list, the lookup silently misses (`keyOrdinal = -1`), `lastKey` never advances, and the source pages exactly once forever with no error. K6a's own "any real result column satisfies it" shortcut for the *preview* runner is safe only because a preview binds the cursor null and reads a single page, discarding the computed next-cursor entirely — that shortcut is explicitly wrong for a persisted, recurring report, where pagination correctness across many runs is the whole point.

Fix, additive and non-breaking: `KeysetSqlGenerator.Generate` now guarantees the chosen key is always a real column of the generated SQL's `SELECT` list, and returns its exact output name as a new field, `KeyColumnName`, threaded through `GeneratedQuery` → `IQuerySqlGenerator`'s `GeneratedReportSql` → `POST /sources/{name}/query-sql`'s `GeneratedQuerySqlResponse` (all three gain one additive property; no existing field changes shape). Two cases: **(a)** the key is already one of `model.Select`'s plain (non-aggregated) columns — the existing `OutputName` is reused as `KeyColumnName`, nothing extra is emitted; **(b)** it isn't — the generator appends one more `SELECT` expression for the key, aliased to a reserved synthetic name (collision-checked against the user's real `OutputName`s), *without* adding it to `DeriveSchema`'s columns. That keeps the report's visible output schema exactly what the user picked in the Columns step — a keyset report never gains a phantom extra column just because K6c needed the key readable. This works unchanged for the `GROUP BY` case too: the existing validation already requires `Key` to be one of the grouped columns, so a synthetic plain `SELECT` of that same expression is always legal SQL alongside a `GROUP BY` on it. `QueryPreviewRunner` is updated to consume the real `KeyColumnName` instead of the old "first schema column" shortcut — strictly more correct, and removes the stale comment explaining why the hack was safe only for one page.

With that guarantee in place, **no new "create report" endpoint is needed** — the existing `POST /reports` (`CreateReportAsync`, ADR D33) already accepts exactly the config shape a query-builder-derived report needs: a `Ref`-based `SourceConfig` (D42) with `sql`/`key`/`pageSize` as report-local `Properties`, plus `Columns` derived from the generated schema. The UI wiring reuses the existing Builder wizard (`BuilderState` + `BuilderConfigMapper`, the same machinery `POST /reports` already goes through for every dynamic report) rather than building a second creation path: `QueryBuilder.razor` gets a "Create report from this query" button (visual tab only — the raw-SQL escape hatch stays its own future slice per K6a) that populates `Wizard.SourceRef`/`SourceType`/`SqlQuery`/`KeyColumn`/`ColumnNames` from the generated query and hands off to `builder/configure`, where the maintainer sets the report name, resilience, output format(s), and destination exactly as they would for any other dynamic report, then reviews and saves through the wizard's existing `POST /reports` call. Known pre-existing gap, not introduced or fixed here: `BuilderConfigMapper` declares every wizard-entered column as `ColumnType.String` (it only ever had a comma-separated name list to work with) — the query builder's generator knows each column's real `ColumnType`, but plumbing that through `BuilderState`/`BuilderConfigMapper` is a wider change to the wizard's column model, deliberately left for a separate pass.

## D55 — Broad source-type expansion (Epic P) — design

**Status.** Recorded with the maintainer (2026-07-16): "possibilitar todas as fontes possíveis, menos Kafka." A directional design for opening the source scope; each concrete source type still gets its own small design pass + ADR before code (the D43/K1 pattern). No code in this decision — backlog only.

### Principle

Every new source is a **new package** implementing the existing contracts — `IBatchSource<T>`/`IStreamingSource<T>` (typed path) and `IConfigSourceProvider` (dynamic path, a new `type:` id) — plus optionally `ISourceHealthCheck` (D42) and, only where it makes sense, `ISchemaExplorer` (D49). The frozen `Abstractions` (rule 7) is never touched; this is exactly how Epic G added Postgres/MySQL/Oracle/MongoDB.

**Two non-negotiables carry to every source:**
1. **Constant memory (rule 8).** Each source maps its own native pagination onto the batch/opaque-cursor model (D3/D4): cursor/continuation-token, `Link: rel="next"`, or page/offset all encode into the `string?` cursor. A source that can only return everything at once must **stream-parse** its response body (e.g. `Utf8JsonReader` over the HTTP stream) or is documented as not-constant-memory for that source.
2. **Honest capability gaps (D36).** The query builder (D49) and server-side preview filters (D45) are SQL-catalog features. A non-relational source (an HTTP API, a file) simply doesn't register `ISchemaExplorer`/`IFilterTranslator` and the UI degrades honestly — no fabricated schema, no silently-dropped filters — exactly as MongoDB already does.

### Explicitly out

**Message queues / unbounded streams (Kafka, RabbitMQ streams, etc.).** A report is a *bounded* extraction with a well-defined end; a stream has none. Consuming a stream is a different product with different lifecycle/checkpointing needs — out of scope, by maintainer decision.

### The catalogue, by cost/value

- **ADO.NET-family (near-free — ride `AdoKeysetSource`, driver swap only):** SQLite (`Microsoft.Data.Sqlite`), Snowflake, Amazon Redshift. These also get `ISchemaExplorer`/keyset almost for free (they're SQL). (Aurora is already covered — it speaks Postgres/MySQL wire.) **Cheapest, highest leverage.**
- **File sources (CSV / Excel / Parquet, local or S3):** symmetric to the existing Local/S3 *destinations*; stream-parsed for constant memory. High demand in reporting. A file has a fixed column set (header/sheet/schema) so a *lightweight* catalog is possible, but no server-side filters.
- **Generic HTTP/REST** — the headline new source (see below).
- **HTTP with richer query semantics:** **OData** (can push filters server-side — would register an `IFilterTranslator`), **GraphQL** (Relay cursor pagination fits the cursor model cleanly).
- **Search engines:** Elasticsearch / OpenSearch — `search_after`/scroll is a natural cursor.
- **SaaS APIs** (special cases of the HTTP source, often with an SDK): Salesforce, HubSpot, Google Sheets, Airtable.

### HTTP/REST source — the design questions its own ADR must settle

- **Pagination strategy** (a required, per-source config): cursor/continuation-token · `Link` header · page-number · offset/limit · none (→ stream-parse). The source maps the chosen strategy to `BatchResult.NextCursor`/`HasMore`.
- **Response → rows:** a JSON path to the record array + a field map to the report schema (the same mapping idea the column step already has). XML/CSV response bodies are variants.
- **Auth:** API key / Bearer / OAuth client-credentials, supplied via `${VAR}` placeholders (D33/D42) in the source properties — same secret-handling boundary as connection strings (GET never echoes them).
- **Resilience:** Polly (already in the pipeline); `429 Too Many Requests` → retry-with-backoff honoring `Retry-After`.
- **Capability gaps:** no `ISchemaExplorer` (no `information_schema` for an arbitrary API) → no query builder; no server-side filters unless the API is OData/GraphQL. Honest degradation (D36).

### Packaging

Default **MIT** for these commodity connectors, consistent with the OSS-core philosophy and the existing DB sources (D42/D43) — the Pro line stays the *composition/build* features (the query builder D49, multi-source join D29, multi-sheet XLSX D27), not the connectors themselves. Revisit per-source only if a specific connector is high-value enough to gate (a maintainer call at that source's own ADR).

### Priority (suggested)

SQLite → ADO.NET warehouses (Snowflake/Redshift) → file sources (CSV/Excel/Parquet) → generic HTTP/REST → OData/GraphQL → Elasticsearch → SaaS. Blueprint: Epic P in `PLAN.md` (not started).

## D56 — SQLite source (Epic P, P1) — design

New MIT package `NeoReports.Sources.Sqlite` (`Microsoft.Data.Sqlite`), source type id `"sqlite"`, following the exact G-epic (D43) shape: `Source.Sqlite(connectionString, sql)` / `Source.SqliteNamed(name, sql)` typed entry points, `SqliteConfigSourceProvider` (dynamic path, delegates to `AdoConfigProperties.CreateAdoConfigSource`), `SqliteSourceHealthCheck` (open-and-`SELECT 1`, via `AdoSourceHealth`), `AddSqliteConfigSource()` DI registration. `AdoKeysetSource<T>`/`AdoNamedKeysetSource<T>`/`AdoConfigProperties`/`AdoSourceHealth` are 100% reused unchanged — SQLite needs only a driver swap, confirming D55's "near-free" bucket.

**Dialect knobs** (`@` prefix — `Microsoft.Data.Sqlite` supports `@`/`:`/`$`, keep `@` for consistency with every other provider): ANSI double-quote identifier quoting (`AdoSchemaExplorer.QuoteAnsi`/`SqlDialect.QuoteAnsi`) — SQLite accepts double-quote natively, no bespoke quoting needed. `AdoFilterTranslator("sqlite")` registers with **no cast** (`castParameter: null`): SQLite's [operand-affinity rule](https://sqlite.org/datatype3.html#comparison_expressions) applies NUMERIC affinity to a bound TEXT parameter compared against a NUMERIC/INTEGER/REAL-affinity column, so the text-bound filter values the preview UI always sends (D45) compare correctly without an explicit cast — verified empirically against a real SQLite file in `SqliteFilterTranslatorIntegrationTests`, the same "don't assume, test it" rule D43's Oracle work established. `CountInnerSuffix` stays empty — SQLite allows a bare `ORDER BY` inside a derived table (`SELECT COUNT(*) FROM (... ORDER BY ...) q`), same as Postgres/MySQL/Oracle. `KeysetSqlGenerator`'s Pro-side `SqlDialect.Sqlite` mirrors the same knobs (`@` prefix, `QuoteAnsi`, no cast) so the D49 query builder works against a registered SQLite source too.

**Schema explorer — bespoke, not `AdoSchemaExplorer`.** SQLite has no `information_schema`; catalog introspection is `sqlite_master` (table list) + `PRAGMA table_info("table")` (columns/PK) + `PRAGMA foreign_key_list("table")` (FKs) — and `PRAGMA` takes the table name as SQL text, not a bind parameter, run **once per table**. `AdoSchemaExplorer` assumes three whole-catalog queries run once each, so it doesn't fit; `SqliteSchemaExplorer` implements `ISchemaExplorer` directly instead, looping per table. Table names come only from `sqlite_master` itself (never caller input) before being interpolated, double-quoted via `AdoSchemaExplorer.QuoteAnsi` (embedded `"` doubled), into the per-table `PRAGMA` text, so this stays injection-safe by the same "identifiers only ever come from the catalog" rule every other explorer follows — and the same quoting is applied to `PreviewTableAsync`'s caller-supplied table name, where it's what actually keeps that path injection-safe. Table-name matching (a FK's `REFERENCES` target vs. `sqlite_master`'s own stored name) is case-insensitive (`StringComparer.OrdinalIgnoreCase`), matching SQLite's own identifier semantics; an omitted-column FK shorthand (`REFERENCES table` with no explicit column) resolves to the referenced table's declared primary key, or `"rowid"` when it declares none (SQLite's own implicit resolution for an ordinary rowid table). "Schema" is reported as the literal `"main"` (SQLite's default database name; the same slot Postgres uses for a real schema and MySQL uses for the database) — attached-database (`ATTACH DATABASE`) multi-schema support is out of scope, matching the single-connection-string model every other provider already assumes. Preview reuses `AdoSchemaExplorer.QuoteAnsi`/`PreviewWithLimit` directly (`SELECT * FROM "table" LIMIT n` — SQLite's `LIMIT` is identical to Postgres/MySQL) rather than duplicating them.

**Test fixture — no Testcontainers, no Docker, no skip logic.** SQLite is embedded/file-backed, not a server: `SqliteFileFixture` creates a temp `.db` file, seeds it via a plain `Microsoft.Data.Sqlite` connection, and deletes the file on dispose — always available, so every test is a plain `[Fact]`, never `[SkippableFact]`/CI-Docker-dependent. (A bare `:memory:` connection string was rejected: `AdoKeysetSource` opens a **new connection per page**, and `:memory:` databases are private per-connection unless using a shared-cache URI — a real temp file sidesteps the whole question and is closer to SQLite's actual embedded-file use case anyway.) This makes the SQLite suite strictly *less* infrastructure than every other relational provider's tests, not more — consistent with D55 calling it "also useful for tests" (a fast, dependency-free source for the engine's own test suite, a benefit beyond being a supported source in its own right).

Packaging: MIT (per D55's default), same tier as the other relational connectors.

## D57 — Redshift + Snowflake sources (Epic P, P2) — design

**Testing strategy (maintainer decision, 2026-07-17).** Unlike every prior relational provider — SQLite included, which traded Testcontainers for a real embedded file but still ran real queries — Snowflake and Amazon Redshift are paid cloud services with no local/Docker equivalent (no `Testcontainers.Snowflake`/`Testcontainers.Redshift` exists, and neither vendor ships an embeddable engine). Given that constraint, the maintainer chose **unit tests only, the coverage gap documented honestly** over the alternatives (deprioritizing P2 until a container exists, or provisioning real paid cloud test accounts). Concretely this means: SQL generation, config validation, DI wiring, `AdoFilterTranslator`/`SqlDialect` string-level behavior are covered exactly like every other provider; **actual keyset pagination against a live server, actual schema-catalog reads, and actual health-check network behavior are NOT covered by any test in this repo** for these two source types — a real, standing gap, not a temporary oversight, consistent with D36's rule that a capability the engine can't honestly verify should say so rather than pretend. A future maintainer with cloud credentials (or a Snowflake/Redshift emulator, should one appear) should add the missing integration suites rather than assume the unit tests are equivalent proof.

### Redshift — low risk, reuses the proven Postgres path

Amazon Redshift speaks the PostgreSQL wire protocol and is documented by AWS as derived from PostgreSQL 8.0.2, so `NeoReports.Sources.Redshift` reuses **the same `Npgsql` driver** the Postgres provider already uses successfully in this exact framework — this is not a new, unproven driver integration the way SQLite or Snowflake are. `type: "redshift"` gets its own thin package (mirroring D43's "SQL Server predates the shared extraction, left standalone" precedent, just for a new type id rather than legacy reasons) so hosts/UI can distinguish it from a real Postgres server, even though the wire-level mechanics are identical: `@` parameter prefix, ANSI double-quote identifiers, and the same `AdoFilterTranslator.PostgresCast`/dialect cast Postgres uses (**assumed from Redshift's documented Postgres lineage — Redshift, like Postgres, has no implicit text-to-typed conversion in comparisons — not empirically re-verified against a live cluster**, per the testing-gap note above; if a real account ever becomes available, re-verifying this cast empirically is the first thing to check, the same "don't assume, test it" instinct D43's Oracle work and D56's SQLite work both applied). Catalog introspection reuses the Postgres provider's own `information_schema`-based `SchemaCatalogQueries` unchanged — Redshift documents `information_schema.columns`/`table_constraints`/`key_column_usage` support closely matching Postgres, including for PK/FK metadata (which Redshift stores for the query planner's benefit even though it doesn't *enforce* the constraints at write time — irrelevant here, since this explorer only reads DDL metadata, never enforces anything).

### Snowflake — new driver, verified against documentation (not a live account)

`NeoReports.Sources.Snowflake` uses the official `Snowflake.Data` ADO.NET driver (`SnowflakeDbConnection`/`SnowflakeDbCommand`/`SnowflakeDbParameter`). Two load-bearing facts were checked against Snowflake's own documentation before writing any code, precisely because getting either wrong would silently ship a provider that compiles and passes unit tests but never actually works against a real server:

1. **Parameter binding uses a `:name` prefix, not `@name`.** Confirmed via Snowflake's own docs/connector source: `SnowflakeDbParameter.ParameterName` is set without the colon, and the SQL text carries the colon-prefixed marker (`:cursor`, `:pageSize`, …) — the same convention Oracle already uses in this codebase (`AdoProviderOptions.ParameterPrefix = ":"`, `SqlDialect.Oracle`'s `:` prefix), so `SqlDialect.Snowflake`/the config source's `parameterPrefix` both reuse `":"` rather than the `"@"` every other provider in this repo defaults to. Getting this wrong would have meant every generated query silently failed to bind its cursor/filter parameters against a real server — undetectable by any unit test that never talks to Snowflake.
2. **No cast needed for filter values.** Snowflake's documented implicit-conversion rules apply NUMBER coercion to a VARCHAR operand in a comparison context (the same category of behavior MySQL/SQL Server already get away with uncast) — so `AdoFilterTranslator("snowflake")` registers with no `castParameter`, matching that documented behavior. **This is evidence from Snowflake's own conversion-rules documentation, not an empirical test against a live warehouse** — flagged explicitly per the testing-gap note above as the next thing to verify if a real account becomes available.

Everything else follows the established shape: ANSI double-quote identifiers (Snowflake accepts double-quoted identifiers natively), `information_schema.columns`/`table_constraints`/`key_column_usage` for catalog introspection (Snowflake's `INFORMATION_SCHEMA` is documented as scoped to the connection's current database, the same "one connection, one catalog" assumption every other provider's `AdoSchemaExplorer` instance already makes), and no `ConfigureCommand` opt-in (unlike Oracle's `BindByName = true` — nothing in the driver's documented parameter API suggested Snowflake needs an equivalent positional-vs-named toggle, but this too is unverified against a live server).

Packaging: MIT for both (per D55's default). `Snowflake.Data` added to `build/Directory.Packages.props`.

## D58 — CSV file source (Epic P, P3a) — design

**Scope narrowed from PLAN.md's original P3 (maintainer decision, 2026-07-17).** P3 originally bundled CSV, Excel (XLSX), and Parquet as one item. Researching XLSX first surfaced a real blocker: `ClosedXML` (already used for writing, D-era `NeoReports.Formats.Xlsx`) does not stream on read — it materializes the whole `XLWorkbook` object graph in memory (reports of multi-GB usage for a ~65k-row file), which would silently violate rule 8 (constant memory) for any real-sized spreadsheet. A genuinely streaming XLSX read needs the low-level `DocumentFormat.OpenXml` SAX-style `OpenXmlReader`, a materially larger and riskier piece of work than CSV, and Parquet needs its own library evaluation (`Parquet.Net` or similar) — neither guess should ship un-verified, the same "don't assume, test it" rule D43/D56/D57 already established. **Split: P3a (this decision) ships CSV only; XLSX and Parquet become their own P3b/P3c items, each with its own design pass**, tracked in `PLAN.md`.

### The right contract: `IStreamingSource<T>`, not `IBatchSource<T>`

A CSV file has no native keyset/cursor concept the way a database does — "resume from where the last page left off" is trivially just "keep reading the same open stream." `NeoReports.Core.Pipeline.TypedBatchReader<T>` already special-cases exactly this shape: for an `IStreamingSource<T>`, it opens **one** `IAsyncEnumerator<T>` for the whole run and pulls `pageSize` items off it per page — no cursor encoding, no reopen-and-reparse-from-scratch logic needed, and it's automatically constant-memory as long as the underlying enumerable itself only buffers one row at a time. So the CSV source's typed path is authored directly as an `IStreamingSource<T>` (`rule 2`'s "ergonomic authoring contract for naturally streaming sources" — precisely this case), not force-fit into the ADO-shaped `IBatchSource<T>`/cursor model P1/P2 used.

The dynamic (config) path is constrained the other way: `IConfigSourceProvider.Create` must return `IBatchSource<ReportRecord>` (the interface's fixed return type, `Abstractions` is frozen). `NeoReports.Sources.Join.Pro` already solved this exact bridging problem internally (`StreamingToBatchSource<T>`: holds one enumerator across a run, keyed on `context.Cursor is null` as the "start of run" signal, mirroring `AdoNamedKeysetSource`'s own "resolve fresh on first page" idiom) — but that class is `internal` to a **Pro** package, and file connectors are MIT (D55's packaging default), so it can't be reused directly. Rather than hand-copy it into the new CSV package (which would trip the exact new-code duplication gate P2 just hit, see [[sonar-sln-and-duplication-gotchas]]), the adapter is **promoted to a new public class, `NeoReports.Core.Sources.StreamingToBatchSource<T>`** — implementation identical to Join.Pro's proven one, just made public and MIT-reachable. Join.Pro's own internal copy is left untouched (out of this PR's scope to retrofit an unrelated, already-shipped package); any *future* source needing this exact bridge (XLSX/Parquet's dynamic path will) reuses the new Core one instead of writing a third copy.

### Packaging: one package, not split by storage backend

Unlike destinations (`NeoReports.Destinations.Local` vs `.S3`, kept separate specifically so a local-only consumer never pulls in `AWSSDK.S3`), the CSV **source** ships as a single `NeoReports.Sources.Csv` package supporting both `Source.CsvFile(path, …)` (local) and `Source.CsvS3(bucket, key, …)` (S3) — matching how D55/PLAN.md itself frames file sources as "(local or S3)", a single feature with a storage-location toggle, not two products. `AWSSDK.S3` is already a pinned CPM dependency (used by the S3 *destination*); referencing it from one more package isn't a new dependency in the ecosystem sense. Both entry points funnel into the same streaming RFC 4180 parser and materialization logic — only the `Stream`-opening call differs (`FileStream` vs. `AmazonS3Client.GetObjectAsync(...).ResponseStream`).

### CSV parsing: hand-rolled, not a new dependency

The existing `NeoReports.Formats.Csv` **writer** is hand-rolled RFC 4180 (comma delimiter, doubled-quote escaping, quotes only when a field contains the delimiter/quote/CR/LF) with no external CSV library — consistent with this repo's minimal-dependency philosophy. The **reader** mirrors it exactly (the inverse state machine: unquoted field ends at delimiter/line-end; a quoted field runs until an unescaped closing `"`, `""` inside means a literal quote, and may itself contain delimiter/CR/LF), built as a true streaming parser over a `StreamReader` (its internal buffer stays small regardless of file size; only one row's fields are held in memory at a time) — this was judged lower-risk than reaching for a general-purpose library (e.g. CsvHelper) given the parser only needs to be the exact mirror of a writer this codebase already owns and tests byte-for-byte (golden-file convention).

### Capability gaps — honest, not fabricated (D36)

No `ISchemaExplorer`, no `IFilterTranslator`, no `IQuerySqlGenerator` dialect entry: a flat file has no catalog/query protocol to introspect or push filters into (D55's own framing: "a lightweight catalog is possible [header row], but no server-side filters"). Even the lightweight header-only catalog is deferred past this pass — not required for a working source, and D36's rule is "don't fabricate," not "must ship every optional nicety immediately." `ISourceHealthCheck` **is** included (local: can the path be opened for read; S3: `HeadObjectAsync`) — a meaningful, honest signal a file source can cheaply provide, unlike catalog/filter capabilities it structurally can't.

### Typed materialization requires a header row

`Source.CsvFile(path).As<T>()`/`Source.CsvS3(...).As<T>()` map CSV columns to `T`'s constructor parameters **by header name** (mirroring `RecordMaterializer<T>`'s reflection strategy, but converting from the row's `string` fields via `Convert.ChangeType` instead of a `DbDataReader`'s typed `GetValue`) — so the typed path requires `HasHeader: true` (the default) and throws a clear, caller-safe error if disabled. The dynamic path (`type: "csv"`) similarly matches CSV header names against the report's declared `ReportSchema` column names (case-insensitive), the same "materialize a positional `ReportRecord` by matching declared schema names against what's actually in the result" pattern `AdoConfigProperties.MaterializeReportRecord` already established for the ADO family — letting a report declare a reordered subset of the file's real columns.

Packaging: MIT (per D55's default).

## D59 — XLSX file source (Epic P, P3b) — design

**Follows D58's deferral.** D58 split P3 into P3a (CSV, shipped) / P3b (this decision) / P3c (Parquet, still deferred) after finding `ClosedXML` doesn't stream on read. This decision covers the XLSX read path.

### `DocumentFormat.OpenXml`'s `OpenXmlReader`, not ClosedXML — verified, not assumed

Confirming the same "don't guess, verify" discipline D43/D56/D57 established: Microsoft's own docs call the SAX-style `OpenXmlReader` "the recommended approach for reading very large files," walking one XML element at a time rather than materializing the DOM `ClosedXML` uses. `DocumentFormat.OpenXml` 3.1.1 is already a transitive dependency of `ClosedXML` 0.105.0 (used by the existing writer), so this adds no new dependency family to the ecosystem, only a direct `PackageVersion` pin matching the version already resolved.

Three bounded (not row-count-scaling) side-tables are required alongside the row-by-row scan, each built once per read and kept in memory for its duration — consistent with rule 8, which is about *row* count, not *distinct-value* count:
- **Shared strings** (`SharedStringCache`): XLSX de-duplicates repeated text into one table, referenced by cells via an integer index. Naively calling `.ElementAt(index)` on the raw `IEnumerable<SharedStringItem>` per cell is a documented O(n²) trap (one real-world report: 200k rows → 2.5+ hours); this reads the table into an indexed `string[]` exactly once instead.
- **Number formats** (`NumberFormatCache`): Excel has no distinct "date" cell type — a date is a plain numeric serial (days since 1899-12-30) whose *style* carries a date `NumberFormatId`. Built-in date ids are checked first — not just the common 14–22/45–47 range, but also 27–36/50–58 (CJK locale date/time formats a modern Excel still writes bare, with no matching `<numFmts>` entry to fall back on, since built-in ids are spec-defined rather than per-file — verified against multiple reference implementations, not assumed). This project's own `NeoReports.Formats.Xlsx` writer (`XlsxCells.ApplyDateFormat`) writes dates with a **custom** `"yyyy-mm-dd"` format code, so a `FormatCode`-string heuristic (strip quoted literals and non-elapsed-time bracketed sections, then look for `y`/`m`/`d`/`h`/`s` tokens) is required too — caught by the golden round-trip test against the real writer (mirroring D58's own "reader must be the exact inverse of a writer this codebase owns" convention). The bracket-stripping matters beyond this project's own writer: a code review caught that a naive "strip quotes only" heuristic misclassifies a real-world locale-currency format like `[$USD-409]#,##0.00` as a date, because the trailing `D` in `USD` survives into the token scan — `[h]`/`[m]`/`[s]` elapsed-time brackets are kept (they are genuine date/time tokens), every other bracketed section (currency, color, condition) is discarded whole.
- **Column alignment** (`CellReference` parsing, not cell order): Excel omits empty cells from the XML entirely, so a row's cells are placed by parsing the base-26 column letters out of each cell's own `CellReference` (e.g. `"AA10"` → column 26), never assumed positional — a sparse row (a cleared middle cell) is the regression test for this.

`XlsxRowReader.ReadRows` is a deliberate, documented exception to this repo's "async for everything that does I/O" convention: it returns a synchronous `IEnumerable<object?[]>`, not `IAsyncEnumerable`, because `OpenXmlReader`/the underlying `XmlReader` over a `ZipArchiveEntry` stream expose no genuine async read path — an `async` signature would only wrap synchronous zip-inflate work and misrepresent the I/O model. Recorded here so the departure reads as an intentional, researched call, not an oversight.

### Seekability — verified empirically, a genuine surprise

XLSX is a ZIP container; ZIP format requires random access to read its central directory, which would seem to rule out feeding a non-seekable stream (an S3 `GetObject` response, unlike a local `FileStream`) directly to `SpreadsheetDocument.Open`. **Verified empirically** (not assumed) with a throwaway probe: a stream that throws on `Position`/`Length`/`Seek` — genuinely forward-only, closer to a real network response than a `MemoryStream` with `CanSeek` merely reporting `false` — still opens and reads correctly. `DocumentFormat.OpenXml` transparently buffers a non-seekable stream internally before treating it as a ZIP package. Practical consequence: for an S3-sourced XLSX file, the whole *compressed* package is buffered once (bounded by file size, not row count) before row-by-row streaming begins — a materially different, much smaller memory profile than ClosedXML's ruled-out "materialize the deserialized DOM," but worth stating plainly rather than silently implying "S3 XLSX reads are exactly as constant-memory as CSV's are." No extra code was needed for this — `Source.XlsxS3`/`XlsxConfigSourceProvider` plug straight into the same `S3Stream.OpenAsync` the CSV source uses.

### Packaging: `S3Stream` promoted to a new shared `NeoReports.Sources.Files.Common` package

D58's own `S3Stream` (open an S3 object as a `Stream`, owning a self-built client's lifetime) is needed verbatim by XLSX too. Leaving a second copy inside `NeoReports.Sources.Csv` would trip the same `new_duplicated_lines_density` gate P2 already hit once (see [[sonar-sln-and-duplication-gotchas]]) — so it is extracted to a new, minimal shared package, `NeoReports.Sources.Files.Common` (parallel to the ADO family's own `NeoReports.Sources.Common`, but scoped to file/S3 plumbing rather than ADO's keyset engine — the two shouldn't share a package, since `Sources.Common` has no reason to carry an `AWSSDK.S3` dependency for the ADO providers that never need it). `NeoReports.Sources.Csv` now references this new package instead of owning `S3Stream` itself; both source packages ship it as a public class, consistent with how `ReflectedRowShape<T>`/`StreamingToBatchSource<T>` were already promoted to `NeoReports.Core.Sources` in D58 for the same reason.

A code review caught the same near-duplication forming a second time in this same PR: `XlsxSourceHealthCheck` was initially a near-verbatim copy of `CsvSourceHealthCheck`'s whole body. Rather than let it ship and trip the gate again, the health-check logic itself is now `FileSourceHealth.CheckAsync` in `NeoReports.Sources.Files.Common`, with `CsvSourceHealthCheck`/`XlsxSourceHealthCheck` reduced to one-line `Type`-specific wrappers. This also fixed a latent, pre-existing gap the extraction surfaced: the S3 branch of both health checks built its own `AmazonS3Client()` unconditionally, unlike `S3Stream.OpenAsync`/`ResolveStreamFactory`'s established "resolve a DI-registered `IAmazonS3` first" precedent — `FileSourceHealth` now follows that same precedence, so a source configured against a non-default client (e.g. a LocalStack test double) reports health correctly instead of probing the wrong endpoint.

### Values arrive natively typed, not as text

Unlike CSV (everything is text, requiring `Convert.ChangeType`/`*.Parse` from strings), an XLSX cell already yields a native CLR value from the reader (`double`, `string`, `bool`, `DateTime`, or `null`) — so `XlsxRecordMaterializer<T>`/`XlsxReportRecordMaterializer` convert from that native type rather than parsing text, using `Convert.ChangeType` only to bridge a genuine type mismatch (e.g. a numeric cell into a `long` property). One caveat worth documenting rather than "fixing": Excel has no decimal type, only IEEE `double` — a `Decimal`-typed value like `10.50` written by this project's own XLSX writer legitimately reads back as `10.5` (the trailing zero was never a real digit of precision on the Excel side), unlike CSV's text-based round trip which preserves it. This is not a defect; a report author who needs exact decimal-scale round-tripping through a file format should use CSV, not XLSX.

### Capability gaps and typed-path requirements — same shape as D58

No `ISchemaExplorer`/`IFilterTranslator` (D36 honest gap, a file has no catalog/query protocol); `ISourceHealthCheck` included (local: openable; S3: `HeadObjectAsync`). The typed `.As<T>()` path requires a header row (same reasoning as D58); the dynamic path (`type: "xlsx"`) accepts an optional `sheetName` property (default: the workbook's first sheet) alongside `path`/`bucket`+`key`/`hasHeader`.

Packaging: MIT (per D55's default).

## D60 — Parquet file source (Epic P, P3c) — design

**Closes out the CSV/XLSX/Parquet trilogy D58 originally split.** D58 narrowed the bundled P3 to CSV only and deferred XLSX (D59) and Parquet (this decision), each pending its own library evaluation. `NeoReports.Sources.Parquet` (`type: "parquet"`, local or S3 in one package, MIT) completes the file-source family — same shape as its two siblings (`Source.ParquetFile(...)`/`Source.ParquetS3(...)` typed builders with an `As<T>()` completion, a `ParquetConfigSourceProvider` for the dynamic path, a one-line `ParquetSourceHealthCheck` over `FileSourceHealth`, and an `AddParquetConfigSource()` registering only the provider + health check). `Parquet.Net` 6.0.3 is the library (the standard pure-.NET Parquet implementation; no simpler viable alternative), a new CPM pin in `Directory.Packages.props`.

### Row-group granularity satisfies rule 8

A Parquet file is a sequence of self-contained **row groups**, each a columnar chunk. Row-group-at-a-time is the finest granularity the format's reader exposes — not a library limitation but inherent to the columnar layout (a past `Parquet.Net` version deliberately removed per-row `IAsyncEnumerable` streaming because "Parquet is not row-oriented... [it] doesn't add anything in terms of performance"). This is the honest interpretation of rule 8's constant memory: peak memory is bounded by one row group's rows, never the whole file. `ParquetStreamingSource<T>` opens a `ParquetReader` once to learn `RowGroupCount`, then yields the rows of one row group at a time; the pipeline's own `StreamingToBatchSource<T>`/`TypedBatchReader` paging (reused unchanged, as D58 established) turns those into report-batch pages. Verified empirically with a `RowGroupSize = 2` fixture that a 5-row file spans 3 row groups and every row is yielded in order.

### Known, accepted tradeoff: per-row-group footer re-parsing

A code review caught, and independently re-verified empirically (a throwaway probe against the real installed `Parquet.Net` 6.0.3 assembly, same "don't guess, verify" discipline as the rest of this decision), that `ParquetSerializer.DeserializeAsync<T>`/`DeserializeUntypedAsync` only accept a `Stream` or file path — never an already-open `ParquetReader` — so `ParquetStreamingSource<T>`'s per-row-group call re-parses the file's footer/Thrift metadata every time, even though `ParquetReader.OpenRowGroupReader(int)` exists specifically to avoid exactly this when driving reads from one already-open reader. This is metadata-only work (column-chunk offsets/statistics, not row data), so it never threatens the constant-memory guarantee, but it is real, avoidable I/O that scales with row-group count. The only way to eliminate it is dropping down to `Parquet.Net`'s lower-level, buffer-oriented per-column `ParquetRowGroupReader.ReadAsync<T>(DataField, Memory<T>, ...)` API and hand-rolling the type-mapping/nullable/decimal-precision logic `ParquetSerializer` already gets right for both the typed and dynamic paths — exactly the kind of hand-rolled complexity this decision's "the typed path needs no reflection materializer" section chose to avoid. Judged not worth that risk for this pass: accepted and documented here (in `ParquetStreamingSource<T>`'s own XML doc too) rather than silently left for a future maintainer to rediscover.

### Seekability — the opposite surprise from XLSX, verified empirically

D59's genuine surprise was that `DocumentFormat.OpenXml` *transparently buffers* a non-seekable stream, so XLSX needed no extra code for S3. Parquet is the mirror image: `ParquetReader.CreateAsync` (and `ParquetSerializer.*Async`) throw immediately — `ArgumentException: "stream must be readable and seekable"` — when handed a forward-only stream, because the Parquet footer lives at the *end* of the file and is read by seeking. An S3 `GetObject` response body (`S3Stream.OpenAsync`, reused as-is) is not seekable, so this is the one genuinely new problem versus XLSX. It is solved by a new shared helper, `SeekableStream.EnsureSeekableAsync`, added to `NeoReports.Sources.Files.Common` alongside `S3Stream`/`FileSourceHealth`/`FileSourceProperties`: it returns an already-seekable stream (a local `FileStream`) untouched at zero cost, or copies a non-seekable body once into a temp file opened with `FileOptions.DeleteOnClose` (so the OS removes it automatically when the returned stream is disposed — no manual temp-file bookkeeping) and returns that, rewound. The original forward-only stream is disposed once fully drained. This is reusable file-source infrastructure — any future binary format with a trailing directory would need the same guarantee — so it belongs in `Files.Common`, not the Parquet package. The throw, the temp-file round trip, and the `DeleteOnClose` cleanup are all covered by tests (a forward-only `Stream` subclass that throws on `Position`/`Length`/`Seek` and reports `CanSeek == false`, the same throwaway-probe technique D59 used for XLSX), including end to end through the S3 read path. Practical consequence, stated plainly (as D59 did): an S3-sourced Parquet file has its whole *compressed* body buffered to disk once before row-group streaming begins — bounded by file size, not row count, and materially smaller than materializing decoded rows, but not as purely constant-memory as a local file.

A security review caught a real issue in the first version of this helper: it used `Path.GetTempFileName()` (which atomically creates the file) and then *reopened* that same path with `FileMode.Create` — discarding the atomicity `GetTempFileName()` provides and reintroducing a TOCTOU window (a local attacker who can race that gap could redirect the reopen via a symlink). This is the first place in the repo that both creates and writes into a temp file (every other temp-path usage builds a directory path and creates its own file directly, a single operation) — not an already-accepted pattern, genuinely new risk. Fixed to `Path.GetRandomFileName()` + a single `FileMode.CreateNew` `FileStream` constructor call: creation and opening happen atomically, and `CreateNew` fails outright rather than silently succeeding if anything already exists at that path.

### The typed path needs no reflection materializer — `Parquet.Net` owns that

Unlike CSV/XLSX (whose typed paths hand-roll reflection via the shared `ReflectedRowShape<T>` to map columns onto `T`'s constructor), `ParquetSerializer.DeserializeAsync<T>` does the object mapping itself, so there is **no** `ParquetRecordMaterializer<T>` and `ReflectedRowShape<T>` is unused here. The typed builder simply hands `ParquetStreamingSource<T>` a delegate that calls `DeserializeAsync<T>(stream, options, rowGroupIndex, ct)` per row group and yields `result.Data`. Column-to-property matching is made case-insensitive (`ParquetOptions.PropertyNameCaseInsensitive = true`), keeping the whole file family's "columns match by name, not case" behavior. Verified empirically: a lowercase-property POCO matches PascalCase file columns only with the flag on; a POCO declaring a subset of the file's columns reads fine (extras ignored).

### The dynamic path reads untyped, one row group at a time

There is no compile-time POCO on the dynamic path, so `ParquetSerializer.DeserializeAsync<T>` does not apply. `Parquet.Net` 6.0.3 provides `ParquetSerializer.DeserializeUntypedAsync`, which returns one row group at a time as a list of column-name-keyed dictionaries (`DeserializationResult<Dictionary<string, object>>`) with values already natively typed. `ParquetReportRecordMaterializer` matches each declared `ReportSchema` column name against the file's actual field names case-insensitively (resolved once per row group from the file's own `ParquetSchema`, obtained from `result.Schema` — the stable field list, not a sampled row) and zips values into positional `ReportRecord`s — the same "match by declared schema column name" pattern `AdoConfigProperties.MaterializeReportRecord`/`XlsxReportRecordMaterializer` established, letting a report declare a reordered subset of the file's columns. **Null handling was verified empirically, not assumed:** `DeserializeUntypedAsync` represents a null cell by *omitting the key entirely* from that row's dictionary, not by a null value — so the materializer treats an absent key (and a declared column absent from the file altogether) as null via `TryGetValue`. Because Parquet's logical types are explicit in the file's own metadata (a column *is* typed as a date/timestamp/decimal, unlike Excel's number-format-implies-date guessing), the values arrive already correctly typed; conversion mostly passes them through and only coerces on a declared-vs-native type mismatch, falling back to the native value rather than throwing mid-batch. This typed-vs-untyped asymmetry (the one place the two paths genuinely diverge) is isolated in `ParquetRowGroups`, which produces both delegate kinds behind the one shared `ParquetStreamingSource<T>` — no artificial unification that fights the real API split, and no second streaming class.

### Capability notes — the typed path's row-type requirement

`ParquetSerializer.DeserializeAsync<T>` constrains `T` to `class, new()` — a public parameterless constructor with settable/`init` properties. This is a real, honestly-documented asymmetry versus CSV/XLSX, whose typed paths match a positional record's *longest constructor* by parameter name: a plain `public record Sale(long Id, decimal Amount)` works for CSV/XLSX but **not** for Parquet (no parameterless constructor). A Parquet report author must use a class with `{ get; set; }` properties or an `init`-only record (both verified working); the `As<T>()` builder therefore carries a `where T : class, new()` constraint. Notably, the decimal-annotation concern raised during research turned out **not** to apply to a read source: a plain `decimal` property round-trips without any `[ParquetDecimal(precision, scale)]` attribute (verified) — that attribute only pins a scale when *writing*, which a source never does; Parquet's self-describing schema carries the column's precision/scale and `Parquet.Net` materializes a plain `decimal`. As with D58/D59, no `ISchemaExplorer`/`IFilterTranslator` (D36 honest gap — a flat file has no query protocol to push filters into); `ISourceHealthCheck` is included (local: openable; S3: `HeadObject`). One forward-looking note: Parquet's embedded schema could in principle back a *real* `ISchemaExplorer` more honestly than CSV/XLSX's inferred-from-header approach — but that stays out of this pass, same "ship the source, capabilities can come later" precedent D58 set.

Packaging: MIT (per D55's default).

## D61 — Generic HTTP/REST source (Epic P, P4) — design

New MIT package `NeoReports.Sources.Http`, source type id `"http"`. Zero changes to the frozen `Abstractions` (rule 7): `ISourceHealthCheck`, `ISchemaExplorer`, `IFilterTranslator` all live in Core (D20 pattern), not the ABI. **Split, mirroring D58's CSV/XLSX/Parquet precedent**: this decision covers **P4a** (static-auth HTTP source); **P4b (OAuth2 client-credentials) is deferred** to its own item/ADR — a stateful token-cache/refresh concern is a materially different, bigger piece of work than the rest of P4, the same complexity signal that made D58 split XLSX/Parquet out of the original P3.

### Contract choice: `IBatchSource<T>` (the ADO shape), not the file family's `IStreamingSource<T>`

This is the opposite of D58/D59/D60. A REST API has genuine server-side pagination state, the same shape SQL's keyset cursor addresses — so `HttpBatchSource<T>` fetches **one HTTP page per `ReadBatchAsync`** and encodes the next-page locator into `BatchResult.NextCursor`, structurally mirroring `AdoKeysetSource`. The deciding reason is **retry idempotency**: the engine's resilience (D6/D11) wraps a single `ReadBatchAsync`. A transient `429`/`503` mid-pagination is the *expected* case for HTTP (unlike a rare local-file hiccup), and with a cursor-keyed batch, a failed page-N fetch is retried in isolation — re-invoking `ReadBatchAsync` with the same cursor N refetches page N, which is idempotent. The file family's `IStreamingSource` + `StreamingToBatchSource` shape would instead throw out of a partially-advanced `IAsyncEnumerator` on a mid-page failure, and a retry would re-enter that same half-consumed enumerator — corrupt state, non-idempotent, wrong for a paginated network resource even though it's right for a local stream.

**One exception — the `none` strategy** (the whole result set is a single JSON array, no pagination signal at all). There is no per-page token to retry a sub-range of regardless, so this one case is authored as `HttpStreamingSource<T> : IStreamingSource<T>`, stream-parsing the body with `Utf8JsonReader` element-by-element (constant memory even for a large single response — one row at a time, response held open across pages), wrapped in the already-public `NeoReports.Core.Sources.StreamingToBatchSource<T>` (promoted in D58 for exactly this kind of reuse) for the dynamic path. Each element is parsed from an isolated byte slice located via `Utf8JsonReader.TokenStartIndex`/`TrySkip`, not read directly off the continuing reader — verified empirically (a throwaway probe, same discipline as D58–D60) that `JsonDocument.ParseValue`/`JsonSerializer.Deserialize<T>(ref reader)` both enforce "exactly one value, nothing after it" on the reader's remaining span regardless of the incoming `JsonReaderState`'s array nesting, throwing on the `,`/`]` that legitimately follows every element past the first; an isolated slice has nothing after it, so the same validation is satisfied trivially instead of fought. Honest limitation, stated plainly: for `none`, only the initial fetch (cursor still `null`) is retryable — a transient drop after streaming has begun cannot be resumed, inherent to an endpoint with no pagination to resume from. The typed builder returns `IBatchSource<T>` uniformly regardless of strategy.

A security review surfaced one more real gap in this strategy specifically: the `link-header` next-page URL comes verbatim from the *response's* `Link` header, and the original code re-attached the configured API key/bearer token/static headers to whatever host that URL pointed at, with no check against the configured base URL — a compromised, malicious, or response-tampered endpoint could redirect the very next paginated request, credentials attached, to an attacker-controlled host. `ReadBatchAsync` now compares the built request URI's scheme/host/port against the base URL before attaching auth and throws `HttpSourceException` on a mismatch instead of silently sending credentials cross-origin — the same "don't forward `Authorization` across a different authority" rule `HttpClient` itself already applies to redirects. This check only ever fires for `link-header` (every other strategy builds its URI from the configured base URL by construction, so origin can't drift).

Two further failure modes a code review surfaced, both fixed rather than left silent: first, matching `recordsPath`'s segments while descending the token stream is a flat name scan with no depth tracking — a genuine, accepted limitation stated in `JsonRecords.StreamArrayAsync`'s own remarks (a response with an unrelated property sharing a path segment's name at a *shallower* depth than the real target still mis-resolves) — but the resolved target's *kind* is checked: the token immediately following the fully-matched path must be a JSON array, or the source throws `HttpSourceException` immediately instead of continuing to scan forward and silently latching onto an unrelated array elsewhere in the response (mirroring `GetArray`'s `ValueKind` check for the paginated strategies). Second, reaching the end of the response body while the configured path was never found, or while an element was cut off mid-parse (a dropped connection or truncated body), now throws `HttpSourceException` instead of a bare `yield break` — a truncated `none` read is a failure, not a short-but-successful one.

### Pagination strategies — opaque cursor per strategy

The cursor is always a single opaque `string?` (D3 rule): Base64(UTF-8(JSON)) of a small strategy-tagged record, so the pipeline never sees structure.

| Strategy | Cursor content | Next-page fetch | End-of-pages signal |
|---|---|---|---|
| `cursor` | continuation token read from a configured response field (dotted path, e.g. `meta.next_cursor`) | sent back as a configured query param / body field | field absent, `null`, or empty |
| `link-header` | the absolute URL from `Link: <url>; rel="next"` (RFC 5988/8288) | `GET` that URL verbatim | no `rel="next"` relation present (GitHub's own convention) |
| `page` | `{page: N+1}` | `?page=N+1&<pageSizeParam>=M` | returned array shorter than the page size, or empty — works without the API exposing a total; an optional configured `totalPages`/`total` field is honored when present |
| `offset` | `{offset: prevOffset + returnedCount}` | `?offset=K&limit=M` | returned array shorter than `limit`, or empty |
| `none` | n/a | one request, whole body | end of the JSON array (stream-parsed) |

### Response → rows mapping: hand-rolled dotted path, no new dependency

Two config knobs: a **records path** (where the array lives in the body — `"data.items"`, `"results"`, or `""` for a root array) and an optional **field map** (report-column → dotted field path within each record, e.g. `"author.name"`). Both are a minimal dotted-path + array-index traversal over `System.Text.Json`'s `JsonElement`/`Utf8JsonReader` — hand-rolled, consistent with D58's CSV parser choice over pulling in a library, and `System.Text.Json` is already used throughout the dynamic path (`JsonReportConfigParser`). **No JSONPath library.** Full JSONPath filter expressions are explicitly out of scope — that is query/filter *pushdown*, which belongs to P5 (OData registers a real `IFilterTranslator`; GraphQL has Relay cursors), not a source with no query protocol of its own.

### Typed materialization: nearly free via `System.Text.Json`

Unlike CSV/XLSX (hand-rolled reflection materializer, `ReflectedRowShape<T>`, because the source data isn't objects) and like Parquet (the library owns mapping), the typed path is `JsonSerializer.Deserialize<T>(element, options)` per array element (`PropertyNameCaseInsensitive = true`) — no `HttpRecordMaterializer<T>`. For `none`, `JsonSerializer.Deserialize<T>(ref Utf8JsonReader)` keeps it streaming. Caveat, documented: a configured dotted field-map (JSON field names differ from `T`'s property names) falls outside plain `Deserialize<T>`, so the field-map is a **dynamic-path-only** feature — the typed path assumes JSON fields already match property names, the common case. The dynamic path (`type: "http"`) matches each declared `ReportSchema` column name against a record's JSON fields (dotted path per column, `HttpReportRecordMaterializer`) into a positional `ReportRecord` — the same "match by declared schema name" pattern `AdoConfigProperties.MaterializeReportRecord` / `CsvReportRecordMaterializer` / `ParquetReportRecordMaterializer` already established.

### Authentication — static only in this pass; OAuth2 deferred (P4b)

`ReportConfigEnvironment.Substitute` (whole-value `${VAR}` rewriting on a source's `Properties` bag, already used for connection strings) is reusable as-is for `{"apiKey": "${MY_API_KEY}"}` / `{"bearerToken": "${TOKEN}"}` on the dynamic path — no HTTP-specific change needed; the same GET-never-echoes-secrets boundary applies. The typed builder takes literal values, exactly as the typed SQL builder takes a literal connection string.

**Ships in P4a: static API-key header** (configurable header name + value) and **static Bearer token** — both are just "set a header from a string," stateless. **OAuth2 client-credentials is deferred to P4b**: fetching a token from a token endpoint, caching it, tracking expiry, and refreshing on expiry/`401` is a stateful piece with its own design and test surface, not something to cram into this pass. Its natural home when it lands is a `DelegatingHandler` on the source's `HttpClient` — token acquisition/refresh is a handler-layer concern, distinct from *retry*, which stays at the batch level below.

### Resilience / 429 handling — reuse the existing batch-level pipeline, one small additive Core hook

The per-page fetch reuses `ResiliencePipelineFactory` exactly as SQL does — retrying at an `HttpClient`/`DelegatingHandler` layer instead would be a second resilience mechanism, which rule 5 / D6 forbid, and would bypass a report's declared `RetryOptions` and the D38 `OnRetry` job-event hook the factory already wires. One page = one `ReadBatchAsync` = one unit of retry/threshold/event.

The source does **not** call `EnsureSuccessStatusCode()` — verified that it discards the response's `Retry-After` header (the exception it throws does not carry it; `Retry-After` lives on `HttpResponseMessage.Headers.RetryAfter`, gone once the response is disposed). Instead it inspects `response.StatusCode` directly and, on a non-success, reads `Headers.RetryAfter` (`RetryConditionHeaderValue`: `Delta` or `Date`) before disposing the response, then throws `HttpSourceException(statusCode, retryAfter)`.

**`Retry-After` honoring** is wired through a small, generic, additive Core hook rather than a second mechanism: a marker interface `IRetryDelayHint { TimeSpan? RetryAfter { get; } }` in `NeoReports.Core`; `ResiliencePipelineFactory.Build` unconditionally sets `RetryStrategyOptions.DelayGenerator` to `args => new ValueTask<TimeSpan?>(args.Outcome.Exception is IRetryDelayHint h ? h.RetryAfter : null)` (verified: Polly v8's `DelayGenerator` is `Func<RetryDelayGeneratorArguments<TResult>, ValueTask<TimeSpan?>>`, and returning `null` falls back to the strategy's configured backoff — so this is byte-identical behavior for every source that isn't `HttpSourceException`, a regression-tested no-op elsewhere). `HttpSourceException` implements the hint. The generated delay is clamped to a fixed 5-minute ceiling (`ResiliencePipelineFactory.MaxHintedDelay`) regardless of what the hint requests — a single-worker job (rule 6) blocked on an unbounded `Retry-After` from a misconfigured or hostile upstream would stall every other queued report behind it for however long that header says, so the shared pipeline enforces a sane worst case itself rather than trusting every current and future `IRetryDelayHint` implementer to.

`IRetryDelayHint` is deliberately narrower than D37's rejected `IExceptionClassifier`: it supplies *only* a delay suggestion into Polly's own `DelayGenerator` extension point — `ShouldHandle` (whether to retry at all), `MaxRetryAttempts`, and `BackoffType` all stay exactly as D37 left them, uniform across every exception type. D37's bar was "no per-exception-type retry *filtering*" (deciding retry-or-not by exception identity); this hook never decides retry-or-not, only how long to wait once the uniform decision has already said yes — a distinction worth stating explicitly here since the interface's shape (detected via `is`-pattern on the thrown exception) otherwise looks close enough to D37's rejected shape to warrant the comparison.

**4xx vs 5xx is deliberately *not* special-cased** — retrying is uniform across any transient failure, same as every other source. D37 already rejected per-exception-type retry filtering; distinguishing "don't retry 404/401" from "do retry 429/503" would reopen exactly that decision. A permanent 404/401 is retried up to the configured attempt count and then aborts — wasteful but consistent and predictable. A report author facing this sets a low attempt count for that endpoint. Documented gap, not a defect.

### HttpClient lifecycle — no new dependency; DI-resolved plain `HttpClient`, not `IHttpClientFactory`

Rather than take a new `Microsoft.Extensions.Http` CPM dependency for `AddHttpClient`/`IHttpClientFactory` (a new-dependency addition needs the maintainer's confirmation per the standing workflow rules, and the repo's established preference is to avoid a dependency when a small hand-rolled alternative suffices — same call as D58's hand-rolled CSV parser and D61's own dotted-path mapping above), the source instead follows the **already-established "resolve a DI-registered client first, else self-manage" precedent** (`FileSourceProperties.ResolveStreamFactory` / `FileSourceHealth` for `IAmazonS3`, D59) using a plain `HttpClient` — which needs no extra package, since `HttpClient` itself is BCL (`System.Net.Http`) and a host can register `services.AddSingleton<HttpClient>()` (or a keyed one) with zero extra dependency on either side. If no DI-registered `HttpClient` is found, `HttpClients.Default` lazily creates and caches **one process-wide shared instance** (simpler than per-config caching: the source never presets `BaseAddress`/`DefaultRequestHeaders` on the client itself — it builds absolute URIs and applies headers per-request — so nothing about the client needs to vary per source configuration in the first place). That shared instance disables its `CookieContainer` (`HttpClientHandler.UseCookies = false`): without this, a `Set-Cookie` from one report's endpoint would silently be replayed on a later, unrelated report's requests to the same host, since nothing else resets a client shared across otherwise-independent sources. The typed builder accepts an optional `HttpClient` (caller-supplied, caller owns lifetime) and otherwise resolves the same shared default. No `IHttpClientFactory`-specific behavior (named-client DNS rotation) is lost that matters here — a report source talks to one fixed base URL for the life of a run, not a fleet of rotating endpoints.

### Health check

`HttpSourceHealthCheck : ISourceHealthCheck`, `Type => "http"`: issues a lightweight request (configurable `healthCheckPath`, default `HEAD` the base URL, falling back to `GET` if `HEAD` isn't supported by the target API) with the same configured auth, reports `Healthy` on 2xx / `Unhealthy` with the status code otherwise, plus latency. It does not validate the records-path or pagination shape — "can we reach and authenticate," nothing fabricated, matching `FileSourceHealth`'s "can this be read right now" honesty.

### Package / class layout — self-contained, no new shared package

`NeoReports.Sources.Http`: `Source.cs` (typed fluent entry, `Source.Http(baseUrl)....As<T>()`), `HttpSourceOptions.cs` (strategy enum, auth, records-path, field-map, headers), `HttpPagination.cs` (per-strategy cursor encode/decode + end-detection), `JsonRecords.cs` (dotted-path traversal + `Utf8JsonReader` streaming), `HttpBatchSource.cs`, `HttpStreamingSource.cs` (the `none` case), `HttpReportRecordMaterializer.cs`, `HttpSourceException.cs`, `HttpConfigSourceProvider.cs` (`type: "http"`), `HttpSourceHealthCheck.cs`, `ServiceCollectionExtensions.cs` (`AddHttpConfigSource()`). `IRetryDelayHint` is the one Core addition (`NeoReports.Core.Pipeline`). No new shared package: HTTP reuses none of `NeoReports.Sources.Files.Common` (different I/O entirely); the only cross-cutting reuse is the already-public `StreamingToBatchSource<T>`.

### Honest capability gaps (D36) — three, not two

1. **No `ISchemaExplorer`** — a generic REST API has no catalog protocol to introspect (no D49 query builder). Same as the file family.
2. **No `IFilterTranslator` / server-side filter pushdown** — generic REST has no standard filter protocol; that's P5's OData/GraphQL territory.
3. **No `ISourceRowCounter` (D47) → progress goes indeterminate.** Not implemented in this pass: most REST APIs don't return a total count, so a pre-run `CountAsync` can't be honestly implemented in general; fabricating one from an unreliable field would violate D36. Left as a documented gap rather than a future TODO with no config path — if a specific API's response carries a real total, a later pass can honor an optional `totalCountPath`.

Constant-memory honesty note (as D58/D59/D60 each carry): paginated and `none`-streamed reads are constant-memory (one page / one row at a time held); a misconfigured `none` strategy against a genuinely unbounded response is bounded only by how long the stream runs, not by anything the source enforces — stated plainly, not fabricated as a hard guarantee.

Packaging: MIT (per D55's default). No new CPM dependency (see HttpClient lifecycle above).

## D62 — OData source (Epic P, P5a) — design

New MIT package `NeoReports.Sources.OData`, source type id `"odata"`. **Split, mirroring D58's CSV/XLSX/Parquet precedent and D61's P4a/P4b split**: P5 ("HTTP with richer query semantics: OData, GraphQL") splits into **P5a (OData, this decision)** and **P5b (GraphQL, its own follow-up ADR)**. The two protocols differ on two axes at once, which is the same signal that triggered every prior Epic-P split: OData has a real, standardized query protocol (`$filter`, `$top`/`$skip`, `@odata.nextLink`, `$count`), letting it genuinely implement server-side filter pushdown (`IFilterTranslator`) and row counting (`ISourceRowCounter`) — the first non-SQL sources in this epic to do either honestly; GraphQL has no universal filter language at all (a pure D36 honest-gap source, closer kin to generic REST) and inverts the transport (`POST` a query document, `200 OK` can still carry an `errors` array). One touches a shared Core contract; the other doesn't. Bundling them would repeat exactly the mistake D58 avoided by splitting XLSX out of P3 the moment its complexity diverged from CSV's.

### Shared HTTP-family plumbing: extracted `NeoReports.Sources.Http.Common`

P4a's reusable plumbing — DI-first `HttpClient` resolution, auth header application, dotted-path JSON traversal (`JsonRecords`), the streaming `Utf8JsonReader` element reader, `HttpSourceException` (+ `IRetryDelayHint`), the cross-origin credential guard, the opaque-cursor Base64-JSON codec, and the property-bag reading helpers — was `internal` to `NeoReports.Sources.Http`. OData needs all of it; GraphQL (P5b) will need most of it too. This is the same situation D59 hit when XLSX needed CSV's `S3Stream`: promote the shared plumbing into a new package (`NeoReports.Sources.Files.Common`, MIT) and retrofit the already-shipped sibling to consume it instead of keeping a private copy. **Followed the identical pattern here**: extracted `NeoReports.Sources.Http.Common` (MIT) — `HttpClients`, `HttpAuth` (the four auth fields generalized off `HttpSourceOptions`), `HttpRequests.ApplyAuth`/`BuildExceptionAsync`, `JsonRecords`, `JsonRecordMaterializer` (renamed from `HttpReportRecordMaterializer`), `HttpSourceException`, `HttpOrigin.IsSameOrigin` (extracted from `HttpBatchSource`, same security-review-driven check unchanged), `OpaqueCursor.Encode<T>`/`Decode<T>` (generalized from `HttpPagination`; a non-null malformed cursor still throws rather than silently restarting pagination — only a genuinely absent, `null` cursor decodes to "first page"), `PropertyBag` (generalized from `HttpConfigProperties`'s private readers, plus a `RequireString` helper both `RequireUrl` implementations share), `QueryStrings.AddQuery` (query-string builder shared by both packages' paginated strategies — only percent-encodes each pair's *value*, never its *key*, since a query-option name is either a fixed protocol token that must reach the wire literally, e.g. OData's `$filter` — escaping its `$` to `%24` would stop a server recognizing it as that system query option, a real bug code review caught in the first draft — or an author-configured plain identifier that never needs escaping in practice), and `HttpHealthProbe` (the shared `HEAD`-then-`GET`-fallback probing algorithm, factored out after code review flagged `HttpSourceHealthCheck`/`ODataSourceHealthCheck` as byte-for-byte duplicates) — and refactored the shipped `NeoReports.Sources.Http` to reference all of it (behavior-preserving; its own 29 tests pass unchanged). `NeoReports.Sources.OData` references `Http.Common`, not `NeoReports.Sources.Http` directly — a host that wants only OData shouldn't pull in the generic-HTTP `Source.Http`/`type:"http"` surface.

### Contract choice: `IBatchSource<T>` (the ADO/P4a shape)

Same reasoning as D61: an OData feed has genuine server-side pagination state, so `ODataBatchSource<T>` fetches **one page per `ReadBatchAsync`**, encoding the next-page locator into `BatchResult.NextCursor` — a transient `429`/`503` mid-feed refetches the same page from the same cursor, idempotently (D6/D11). Unlike P4a, there is no `none`-equivalent single-response streaming case: an OData collection response is always a bounded page (the service enforces its own max page size and emits `@odata.nextLink` when it truncates), so every response is safely materialized with `JsonDocument.Parse`, no `Utf8JsonReader` element-streaming needed.

### Pagination / cursor table

The cursor is `OpaqueCursor.Encode` of a small strategy-tagged `ODataCursorState` record — Base64(UTF-8(JSON)), opaque to the pipeline (D3). Rows live at the standard `"value"` root array (`RecordsPath` defaults to `"value"`, reusing `JsonRecords.GetArray`); `@odata.nextLink`/`@odata.count` are sibling root properties.

| Strategy | Cursor content | Next-page fetch | End-of-pages signal |
|---|---|---|---|
| `nextLink` (server-driven, **default**) | the absolute URL from `@odata.nextLink` (already encodes the service's own `$skiptoken`/`$top`) | `GET` that URL verbatim | `@odata.nextLink` absent |
| `skip` (client-driven) | `{skip: prevSkip + returnedCount}` | `?$skip=K&$top=M` on the configured resource URL | returned `value` shorter than `$top`, or empty |

`nextLink` is the default — keyset-stable, immune to mid-run inserts, and the OData-idiomatic form. Same security posture as P4a's `link-header`: `@odata.nextLink` comes verbatim from the response body, so `ODataBatchSource` checks `HttpOrigin.IsSameOrigin` against the configured base URL before attaching auth, throwing `HttpSourceException` on a mismatch rather than replaying credentials cross-origin. `skip` shares P4a's `page`/`offset` caveat: a feed mutating mid-run can skip/duplicate rows under client-driven offsets — documented, not hidden — which is why it isn't the default.

### Response → rows mapping and typed materialization

Reused wholesale from the HTTP family via `Http.Common`: `JsonRecords.GetArray`/`TryGetField` for traversal, `JsonSerializer.Deserialize<T>(element, PropertyNameCaseInsensitive)` per element on the typed path (no bespoke materializer), `JsonRecordMaterializer.Materialize` matching declared `ReportSchema` column names (optional dotted `fieldMap`) on the dynamic path (`type:"odata"`).

### Server-side query: static passthrough, plus real `$filter` pushdown via a generalized `IFilterTranslator`

Two separate mechanisms:

1. **Static, author-supplied query options** — literal `$filter`/`$select`/`$orderby`/`$top` on the builder/config, appended to the resource URL as-is. No translation, no engine involvement — the 90% path.
2. **Structured preview-filter pushdown** (`ODataFilterTranslator : IFilterTranslator`, ADR D45's seam) — the reason OData is more than "REST with `value` at the root." The closed `PreviewFilterOperator` set maps directly onto OData v4 `$filter`: `Equals`/`NotEquals` → `eq`/`ne`, `GreaterThan(OrEqual)`/`LessThan(OrEqual)` → `gt`/`ge`/`lt`/`le`, `Contains`/`StartsWith` → `contains(...)`/`startswith(...)`; multiple filters join with `and`. Scoped out, with no `PreviewFilter` shape to translate from: `$expand`/navigation filters, lambda operators (`any`/`all`), arithmetic, `$search`, `$apply`. Literal formatting — OData's analog of `AdoFilterTranslator`'s cast, since `$filter` inlines into the URL with no bind-parameter mechanism (`parameters` is always empty) — is driven by the column's declared `ColumnType`: `String` single-quoted (`'`→`''` doubling); `Uuid` **unquoted** per OData v4's `Edm.Guid` literal grammar (a v4 departure from v2/v3's quoted `guid'...'` form — code review caught the shipped translator initially quoting it like a string, which a spec-compliant v4 service would reject as a type mismatch; fixed to `Guid.TryParse`-validate then emit the bare canonical form, declining on an unparseable value); numeric/`Boolean` parse-validated then emitted **from the parsed value**, not the original text (also code-review-caught: `NumberStyles.Number` accepts a thousands separator like `"1,234.56"`, which is not a valid OData numeric literal — emitting the original text would leak the comma into the URL, so the reformatted, canonical parsed value is emitted instead) — declining (`TryTranslate` → `false`) rather than emitting a malformed expression on a non-parseable value against a typed column, the same "decline, don't emit garbage" stance `AdoFilterTranslator` takes for `Contains`/`StartsWith` on a non-`String` column. Filter columns are validated against the report's declared schema before the translator runs (`ReportPreviewRunner.ValidateFilterColumns`, unchanged), closing identifier injection the same way it already does for the SQL family.

**Core change (bounded, made in this PR): `IFilterTranslator` generalized off `"sql"`.** `TryTranslate` previously took `string sql` and returned `out string translatedSql` — irreducibly SQL-string-specific; OData has no `sql` property. Generalized to:

```csharp
bool TryTranslate(
    IReadOnlyDictionary<string, object?> properties,
    IReadOnlyList<PreviewFilter> filters,
    ReportSchema schema,
    out IReadOnlyDictionary<string, object?> propertyOverrides,
    out IReadOnlyDictionary<string, object?> parameters);
```

`ReportPreviewRunner.PreviewFilteredAsync` passes the source's effective `properties` bag straight through and merges the returned `propertyOverrides` into a copy of it (instead of assigning `["sql"] = translatedSql` specifically) before compiling the filtered source; `pageSize` is still overridden by the runner itself, generically, same as before. The "does this source type even have a `sql` property to filter" check moves from the runner into `AdoFilterTranslator.TryTranslate` itself (it now reads `properties["sql"]`, throwing `ConfigurationException` when absent/wrong-typed — the runner no longer knows about `"sql"` at all). Since that relocated check loses direct access to `report.Name` (only the translator's own generic message survives), `PreviewFilteredAsync` wraps the `TryTranslate` call and re-throws any `ConfigurationException` from it with the report name added back — code review caught a first draft that let this context silently disappear from what is a caller-facing `400` body (`NeoReportsEndpointRouteBuilderExtensions` returns `ConfigurationException.Message` verbatim). `AdoFilterTranslator` returns `propertyOverrides = {["sql"] = translatedSql}` (behavior-identical to today for every SQL-family provider — Sql/Postgres/MySql/Oracle/Sqlite/Redshift/Snowflake — regression-tested); `ODataFilterTranslator` returns `propertyOverrides = {["filter"] = <combined $filter, ANDed onto any static $filter already configured>}`. This is the one shared-contract change in P5a — small, touching one interface, its one existing implementation, and its one consumer, and additive in spirit (no new interface, `Abstractions` untouched) — but recorded explicitly here since it's a seam six SQL providers depend on, exactly the kind of change this repo's workflow calls for a decision on rather than a silent one-off.

### Row count — `ISourceRowCounter` via `$count` (first non-SQL source in Epic P to implement it)

`ODataRowCounter : ISourceRowCounter` issues `GET <resource>/$count` (honoring any static or pushed `$filter`, same auth) and parses the bare integer response, giving a report that opts into progress (D47) a real completion percentage. Best-effort: any non-2xx, unsupported `$count`, or parse failure returns `null` (indeterminate) — never fabricated (D36), never a throw.

### Authentication, resilience, health check — unchanged from P4a, reused via `Http.Common`

Static API-key header / Bearer token via `${VAR}` (`HttpAuth`), OAuth2 still deferred to P4b. Per-page fetch runs through the existing `ResiliencePipelineFactory`; non-2xx becomes `HttpSourceException(statusCode, retryAfter)` via the shared `BuildExceptionAsync`, so `IRetryDelayHint` honors `Retry-After` with no new mechanism (5-minute clamp, D61). 4xx vs 5xx stays uniform (D37) — not reopened here. `ODataSourceHealthCheck : ISourceHealthCheck`, `Type => "odata"`, probes the resource URL (`HEAD` then `GET` fallback) with the configured auth — reachability/auth only, no `$metadata` validation.

### Package / class layout

`NeoReports.Sources.OData`: `Source.cs` (typed entry, `Source.OData(resourceUrl)...As<T>()`), `ODataSourceOptions.cs` (strategy enum, static query options, records path, field map, `HttpAuth`), `ODataCursorState.cs`, `ODataBatchSource.cs`, `ODataFilterTranslator.cs`, `ODataRowCounter.cs`, `ODataConfigProperties.cs`, `ODataConfigSourceProvider.cs` (`type:"odata"`), `ODataSourceHealthCheck.cs`, `ServiceCollectionExtensions.cs` (`AddODataConfigSource()` registering provider + health check + filter translator — `ISourceRowCounter` is never DI-registered anywhere in this codebase; every implementer, including `ODataBatchSource<T>`, implements it directly on the source class the pipeline already holds, detected by pattern-matching the resolved instance, the same shape `AdoKeysetSource<T>` uses). Core edits: `IFilterTranslator.cs` (new signature), `AdoFilterTranslator.cs` (reads `"sql"` itself, returns `propertyOverrides`), `ReportPreviewRunner.cs` (generic merge).

### Honest capability gaps (D36)

1. **`$filter` pushdown covers only the `PreviewFilterOperator` set** (`eq/ne/gt/ge/lt/le`/`contains`/`startswith`, `and`-joined) — no `or`/`not`/navigation/`$expand`/lambdas/arithmetic/`$search`, none has a `PreviewFilter` shape to translate from. A non-parseable value against a typed column makes the translator decline rather than emit a broken expression.
2. **No `ISchemaExplorer` in this pass.** OData's `$metadata` CSDL/EDMX document is a real machine-readable catalog — unlike generic REST, this source *could* back a genuine explorer honestly — but parsing EDMX into `SchemaCatalog` is its own sizable, separately-verifiable piece (the same complexity-deferral judgment D58 made for XLSX); left as a forward-looking gap, not implemented now. No D49 query builder for OData yet.
3. **`skip`-strategy offset instability** under concurrent writes to the feed (documented above); `nextLink` (the default) is immune.

Constant-memory note (as every Epic-P ADR carries): each page is bounded by the service's page size / configured `$top`, materialized one page at a time — constant across pages; `$count` reads a scalar, never the collection.

### New dependency — none

Hand-rolled over `HttpClient` + `System.Text.Json`, via `Http.Common`. No OData client library (`Microsoft.OData.Client`, `Simple.OData.Client`) — those pull in EDMX-driven codegen and a materialization stack this streaming, schema-declared, config-driven model doesn't use; a new dependency would need the maintainer's explicit confirmation per the standing workflow rules, and the repo's established preference (D58's hand-rolled CSV, D61's dotted-path JSON) is to hand-roll the small thing instead. The `$filter` generator is a closed-operator-set string builder bounded by `ColumnType`; paging/mapping reuse P4a's proven JSON traversal via `Http.Common`.

Packaging: MIT (D55 default). No new CPM dependency.

## D63 — GraphQL source (Epic P, P5b) — design

New MIT package `NeoReports.Sources.GraphQl`, source type id `"graphql"`. The mirror image of D62: GraphQL has no standard query/filter protocol — every API defines its own schema — so this is a pure D36 honest-gap source in the same class as generic REST (P4a), with **no `IFilterTranslator`, no `ISchemaExplorer`, no `ISourceRowCounter`**. Its one genuinely standard piece is the **Relay Cursor Connections spec** (`edges { node cursor } pageInfo { hasNextPage endCursor }`), which maps cleanly onto the opaque-cursor model. Zero `Abstractions` changes and — unlike D62 — zero Core changes (no seam to generalize; `IFilterTranslator` stays exactly as D62 left it). References the shipped `NeoReports.Sources.Http.Common` (D62's extraction: `HttpClients`, `HttpAuth`, `HttpRequests`, `JsonRecords`, `JsonRecordMaterializer`, `HttpSourceException`, `HttpOrigin`, `OpaqueCursor`, `PropertyBag`, `QueryStrings`, `HttpHealthProbe`) — the third HTTP-family consumer this package was designed for from the start.

### Contract choice: `IBatchSource<T>` with Relay cursor paging

Same retry-idempotency reasoning as P4a/D62 (D6/D11): `GraphQlBatchSource<T>` issues **one GraphQL request per `ReadBatchAsync`**, injecting the page size as the `first` variable and the prior `endCursor` as the `after` variable, and encodes the next `after` into the opaque cursor — a transient failure refetches the same page from the same `after`, idempotently. **Scope: Relay connection queries only.** A non-connection GraphQL query that returns a whole list in one response has no standard pagination to stream or resume, so it is out of scope (an honest gap, not a silent half-feature) — the author of such a query should page it or use a different source.

### Transport — the genuinely-different piece: `POST` a query document, `200 OK` can still be a failure

Unlike every prior HTTP-family source (all `GET`), GraphQL is a single-endpoint `POST` of `{"query": <document>, "variables": {...}}` with `Content-Type: application/json`; the response is `{"data": {...}, "errors": [...]}`. The load-bearing difference from REST: **a GraphQL error arrives as HTTP `200` with a populated `errors` array**, so the source inspects `errors` *before* reading `data` and throws `HttpSourceException` (with the concatenated error messages) even on a 2xx — a non-2xx is still a failure via the shared `HttpRequests.BuildExceptionAsync`, but a 2xx-with-`errors` is *also* a failure, which no other source in the family has to handle. Retry stays uniform (D37): a GraphQL error may be permanent (malformed query) or transient (rate limit) and the two aren't distinguished — retried up to the configured attempts, then aborts, the same documented 4xx/5xx-style gap P4a/D62 carry. `HttpRequests.BuildExceptionAsync` is reused for the non-2xx case; the `errors`-on-200 case is handled locally in `GraphQlBatchSource` (reads the already-parsed `errors` array, builds an `HttpSourceException` with the concatenated `message` fields, `statusCode: null` since the transport succeeded).

### Pagination / cursor table

| Strategy | Cursor content | Next-page fetch | End-of-pages signal |
|---|---|---|---|
| `relay` (the only strategy) | `{after: <pageInfo.endCursor>}` | re-`POST` the same query with variables `{ …static, first: <pageSize>, after: <endCursor> }` | `data.<connectionPath>.pageInfo.hasNextPage` is `false` |

First page: `after` omitted (or `null`). Cursor is `OpaqueCursor.Encode` of a `GraphQlCursorState(string? After)` — Base64(UTF-8(JSON)), opaque to the pipeline (D3), the exact same codec P4a/D62 use.

### Response → rows mapping — reuses the family's dotted-path traversal directly

A GraphQL response is just JSON, so `JsonRecords` (`Http.Common`) is reused as-is. Config supplies a `connectionPath` (dotted, from the response root — the source prepends `data.`, e.g. `viewer.repositories`); the records array is `JsonRecords.GetArray(root, "data." + connectionPath + ".edges")`, and each record is the edge's `node` (`nodePath`, default `"node"`) via `JsonRecords.TryGetField`. `pageInfo.hasNextPage`/`pageInfo.endCursor` are read from `data.<connectionPath>.pageInfo` the same way. Typed path: `JsonSerializer.Deserialize<T>(nodeElement, PropertyNameCaseInsensitive)` per edge, no bespoke materializer (same as P4a/D62). Dynamic path (`type:"graphql"`): `JsonRecordMaterializer.Materialize` over each `node` against the declared schema (optional `fieldMap`) — the shared family pattern, reused verbatim from `Http.Common`.

### Configuration — author supplies the query document (typed builder *and* dynamic config)

Because there is no universal GraphQL query language to hand-build (the antithesis of OData's constructible query string), the query document is authored by the report author in **both** paths — there is no way around it and no attempt to synthesize one:
- **Typed:** `Source.GraphQl(endpointUrl).Query(document).Variables(obj).Connection("viewer.repositories").Node("node").As<T>()` (`first`/`after` variable names default to `"first"`/`"after"`, overridable via `.PageVariables(first:, after:)` for a schema that names them differently).
- **Dynamic (`type:"graphql"`):** properties `url`, `query`, `variables` (nested object), `connectionPath`, `nodePath` (default `"node"`), `firstVariable` (default `first`), `afterVariable` (default `after`), plus the shared `apiKeyHeader`/`apiKeyValue`/`bearerToken`/`headers` (all read via `PropertyBag`/`ODataConfigProperties`-style helpers, mirroring `HttpConfigProperties`). **No `healthCheckPath`** — unlike HTTP/OData, GraphQL is a single-endpoint transport with no relative-path concept to probe instead of the main endpoint (see Health check below); a `healthCheckPath` property on a `type:"graphql"` source is simply unread, not an error. The author's `query` document is expected to declare the `first`/`after` variables and select `pageInfo { hasNextPage endCursor }` — validated defensively at read time (a missing `pageInfo` in the response throws `HttpSourceException`, not a silent single-page run).

### Authentication / resilience — identical to P4a/D62, reused unchanged

Static API-key/Bearer via `${VAR}` (`HttpAuth`); `ResiliencePipelineFactory` + `IRetryDelayHint` honoring `Retry-After` (GitHub's GraphQL API and others return it on rate-limit) through the existing batch-level pipeline, 5-minute clamp (`ResiliencePipelineFactory.MaxHintedDelay`). No new mechanism.

### Health check

`GraphQlSourceHealthCheck : ISourceHealthCheck`, `Type => "graphql"`: `POST`s a trivial `{ __typename }` probe query (the one universally-valid GraphQL query, present on every schema's root) with the configured auth via the shared `HttpHealthProbe.SendAsync` (not the `HEAD`-then-`GET` `ProbeAsync` — GraphQL is single-endpoint `POST`-only, so the HEAD/GET fallback dance doesn't apply here); `Healthy` on 2xx-with-no-`errors`, else `Unhealthy`. This is a slightly richer-but-still-honest probe than a bare reachability check — it confirms the endpoint actually speaks GraphQL and authenticates — without validating the author's specific query/connection (the family's honesty boundary, D36). **Code review caught two real bugs here before merge**: first, `SendAsync`'s original signature had no body parameter, so the first draft built its POST request directly instead of genuinely reusing the shared helper (contradicting this very paragraph) — fixed by adding an optional `HttpContent? content` parameter to `HttpHealthProbe.SendAsync` itself, which both this health check and the read path's error-check logic (below) end up needing. Second, the health check originally called the same `ReadOptions` the read path uses, which unconditionally requires `query`/`connectionPath` — meaning "test connection" on an author's not-yet-fully-configured source (url/auth set, query not written yet) failed with a misleading error instead of the honestly-scoped "can we reach and authenticate" this section already claimed — fixed by adding a `requireQueryAndConnection: bool` parameter to `ReadOptions`, `false` for the health check.

Two more real bugs code review caught in the read path (`GraphQlBatchSource`), both fixed: `pageInfo.hasNextPage: true` with a missing/null `endCursor` encoded a cursor that decoded back to the same "no cursor" state as the first page, causing the source to silently re-request the same page forever instead of failing — now throws `HttpSourceException` when that combination is seen. And the `errors`-on-200 check never read the response's `Retry-After` header (unlike the non-2xx path), undermining this ADR's own stated reason for reusing `IRetryDelayHint` ("GitHub's GraphQL API and others return it on rate-limit," the canonical case being exactly a 200-with-`errors` rate-limit response) — fixed by extracting the header-reading logic from `HttpRequests.BuildExceptionAsync` into a reusable `HttpRequests.ReadRetryAfter`, called from both paths. A present-but-`null` Relay `node` (a valid tombstone for a deleted entity, not a malformed response) is now skipped rather than materialized as a garbage null/all-null row, and a static `variables` entry colliding with the configured paging-variable name (`first`/`after`) now throws at construction instead of being silently overwritten every page.

### Honest capability gaps (D36)

1. **No `IFilterTranslator` / server-side filter pushdown.** GraphQL has no universal filter protocol — filtering is per-schema, expressed as arguments baked into the author's own query document/variables. There is nothing to translate a structured `PreviewFilter` *into* generically, so previews for a GraphQL report run unfiltered, reported honestly as ignored (exactly MongoDB's/generic-REST's degradation, D44/D61), never silently dropped.
2. **No `ISchemaExplorer`.** GraphQL *does* have introspection (`__schema`), but it is a type-system graph, not a relational table/column/FK catalog, and mapping it into `SchemaCatalog` would be both a large piece and a semantic stretch — declined, not faked (D36). No D49 query builder for GraphQL.
3. **No `ISourceRowCounter`.** Relay connections *may* expose a `totalCount`, but it is optional and non-standard, and unlike D62's OData (a genuine, spec-guaranteed `$count`), there is no universal mechanism to fall back to. Not fabricated: progress is indeterminate for this source. (A later pass could honor an optional author-supplied `totalCountPath` if a specific schema exposes one — not implemented now, no config path exists for it yet, same "documented gap, not a silent TODO" stance D61 took for REST's total count.)
4. **Relay connections only** — non-connection queries are out of scope (no standard pagination to resume).

Constant-memory note: one connection page (`first` nodes) is materialized at a time via `JsonDocument`, bounded by page size — constant across pages; the whole result is never materialized.

### New dependency — none

Hand-rolled `POST` + `System.Text.Json`, reusing `Http.Common`. **No GraphQL client library** (`GraphQL.Client`, `StrawberryShake`, etc.): those center on schema introspection, strongly-typed codegen, and subscription transports — none of which a config-driven, author-supplies-the-document, connection-paging source uses. Building the request is "serialize `{query, variables}` to JSON"; parsing is "read `data`/`errors` with the dotted-path traversal `Http.Common` already owns." Same hand-roll-the-small-thing call as D58/D61/D62. Tradeoff: the source can't validate the query document against the server's schema ahead of time (a client library could) — but that would require an introspection round trip the health check deliberately avoids, and the defensive response-shape checks (`errors`, missing `pageInfo`) catch the real failure modes at read time.

Packaging: MIT (D55 default). No new CPM dependency.

## D64 — Elasticsearch / OpenSearch source (Epic P, P6) — design

New MIT package `NeoReports.Sources.Elasticsearch`, source type id `"elasticsearch"` — one type for both engines. Elasticsearch and OpenSearch (a fork of ES 7.10's core) are wire-compatible for every endpoint this source touches (`_search`, `_count`, `search_after`); no behavioral divergence exists in that subset, so no separate `"opensearch"` type is registered (would be pure duplication, not a real capability difference). References the shipped `NeoReports.Sources.Http.Common` (`HttpClients`, `HttpRequests`, `JsonRecords`, `JsonRecordMaterializer`, `HttpSourceException`, `OpaqueCursor`, `PropertyBag`, `MutableHttpAuth`, `HttpHealthProbe`) — the fourth HTTP-family consumer.

### Contract choice: `IBatchSource<T>` with `search_after` keyset paging — no Point-in-Time (PIT)

Same retry-idempotency reasoning as P4a/D61–D63 (D6/D11): `ElasticsearchBatchSource<T>` issues one `POST {url}/{index}/_search` per `ReadBatchAsync`, with the prior page's last hit's `sort` values as the `search_after` array, and encodes the next `search_after` into the opaque cursor. **Deliberately does not use Elasticsearch's Point-in-Time (PIT) API.** PIT would give consistent deep pagination immune to concurrent index mutations, but its endpoint shape genuinely diverges between engines — Elasticsearch opens a PIT via `POST /{index}/_pit?keep_alive=…` returning `id`; OpenSearch's equivalent is `POST /{index}/_search/point_in_time?keep_alive=…` returning `pit_id` — which would force either two code paths or a second registered type, undermining the one-type-for-both premise this ADR opens with. Plain `search_after` (no PIT) is identical on both engines and has been stable API since Elasticsearch 5.1. The accepted tradeoff, an honest D36 gap: without a PIT, a page fetched after a concurrent delete/insert that shifts sort-key ordering can rarely skip or duplicate a row — the same class of gap D62 already documented for OData's `Skip` strategy, and in practice *less* severe here, since `search_after` is forward-only keyset paging (like `AdoKeysetSource`), not offset-based — it only misbehaves if the specific sort-key **values** change concurrently, not on every unrelated write elsewhere in the index. PIT support is a plausible future enhancement (its own design pass, mirroring the P4a/P4b split) — not started now, no half-wired config property for it exists.

### Query DSL is JSON, not a string — no encoding bugs to root out

D62's `ODataFilterTranslator` and `Http`'s configurable query-param names needed real code-review/security-review fixes for **URL string encoding** (percent-encoding a literal `$`, escaping injected `&`/`=` in a key). Elasticsearch's Query DSL is itself JSON, sent as a POST body — so `ElasticsearchFilterTranslator` builds the merged query as a `System.Text.Json.Nodes.JsonObject`/`JsonArray` tree and lets `JsonSerializer` emit it, rather than string-concatenating fragments. This isn't a style preference: it structurally eliminates the entire bug class D62 had to fix after the fact (a value can never "break out" of its JSON string/number slot the way it could break out of a URL-encoded literal).

### Pagination / cursor table

| Aspect | Behavior |
|---|---|
| Cursor content | The last hit's `sort` array from the previous page, verbatim (`ElasticsearchCursorState(JsonElement[]? SearchAfter)`) |
| Next-page fetch | `POST {url}/{index}/_search` with `{"size": pageSize, "query": …, "sort": …, "search_after": [...]}` (`search_after` omitted on the first page) |
| End-of-pages signal | `hits.hits.length < size` — the same "short page means last page" convention D62's `Skip` strategy uses; `search_after` has no server-provided "more pages" flag, so a full-size page always fetches one more page to confirm |

`sort` is **required, author-supplied, and never defaulted** — Elasticsearch's implicit ordering (`_score`, which is typically a uniform 1.0 and thus not a stable tiebreak for a `match_all`-style query) cannot support resumable paging. `ElasticsearchSourceOptions.Sort(...)`/the `sort` config property throws `ConfigurationException`/`ArgumentException` when absent, and the ADR requires the configured sort to end in a tiebreaker producing a total order (conventionally `{"_id":"asc"}`) — an uncommunicated caller responsibility exactly like `AdoKeysetSource`'s own requirement that its keyset column be unique and monotonic; not automatically enforced (Elasticsearch has no server-side way to validate "is this sort total"), documented instead.

### Response → rows mapping

`hits.hits[]` is the records array (`JsonRecords.GetArray(root, "hits.hits")`); each hit's `_source` object is the record (`JsonRecords.TryGetField(hit, "_source", ...)`). Typed path: `JsonSerializer.Deserialize<T>(sourceElement, PropertyNameCaseInsensitive)` per hit, no bespoke materializer (P4a/D61's precedent). Dynamic path (`type:"elasticsearch"`): `JsonRecordMaterializer.Materialize` over each `_source` against the declared schema (optional `fieldMap`), the shared family pattern reused verbatim from `Http.Common`. One page's response (bounded by `size`) is parsed whole via `JsonDocument.ParseAsync` — constant memory across pages, the same reasoning D62 gives (Elasticsearch enforces its own `index.max_result_window` regardless).

### Server-side query and filter pushdown — `ElasticsearchFilterTranslator : IFilterTranslator`

A real, structured Query DSL exists, so — like D62's OData translator and unlike D63's GraphQL — this source implements genuine server-side filter pushdown. `properties["query"]` (a JSON object, default `{"match_all":{}}` when absent) is the author's static base query; `TryTranslate` builds one `JsonObject` clause per `PreviewFilter` and ANDs the whole set onto the base query via `bool.filter`, returning `propertyOverrides = {["query"] = mergedQueryElement}`. No bind-parameter mechanism (`parameters` is always empty) — same as OData, but for a different reason: ES values are embedded as native JSON types directly in the tree, not interpolated into any string at all, so there is nothing to "bind" *or* to escape.

Operator mapping: `Equals`/`NotEquals` → `term`/`bool.must_not[term]`; `GreaterThan(OrEqual)`/`LessThan(OrEqual)` → `range` with `gt`/`gte`/`lt`/`lte`; `StartsWith` → the native `prefix` query; `Contains` → `wildcard` with the value wrapped `*value*` **and ES wildcard metacharacters (`*`, `?`) escaped in the value first** — an unescaped user-supplied `*`/`?` inside a "contains" value would silently change the query's meaning (not a security hole — this is a request body the report author already controls, not attacker input — but a correctness bug worth closing at the source, same instinct as D62's literal-formatting care). Value formatting per `ColumnType` mirrors D62's `TryFormatLiteral` switch (String/Uuid as JSON strings, Integer/Decimal/Money as parsed JSON numbers, Boolean as JSON true/false, Date/Time/DateTime/Timestamp as parsed-then-ISO-8601 strings) — reusing the same decline-rather-than-emit-garbage stance for a value that fails to parse against its declared column type.

### Row count — `ElasticsearchRowCounter : ISourceRowCounter`

`POST {url}/{index}/_count` with `{"query": <the same effective query>}`, reading the response's `count` field — genuinely analogous to D62's `$count` (a real, spec-guaranteed mechanism, not a fabricated estimate). Best-effort by contract (D36): any non-2xx, missing-field, or parse failure returns `null` rather than throwing or fabricating a count, matching `ODataRowCounter`. `ISourceRowCounter` is (as always in this codebase) never DI-registered — `ElasticsearchBatchSource<T>` implements it directly and callers pattern-match the resolved instance.

### Health check

`ElasticsearchSourceHealthCheck : ISourceHealthCheck`, `Type => "elasticsearch"`: the shared `HttpHealthProbe.ProbeAsync` (`HEAD` then `GET` fallback on 405/501) against `{url}/{index}` (or the configured `healthCheckPath`, relative to `{url}/{index}`) with the configured auth — reachability/auth only, the same honesty boundary D61–D63 keep (does not validate the configured `query`/`sort` shape, does not require `sort` to be set to pass).

**Code review caught and fixed three real bugs before merge, none security-relevant (all fixed within the batch-source/health-check logic itself):** first, the batch source tracked its cursor's `search_after` values from *any* hit in the page that happened to carry a `sort` field, rather than specifically the page's last hit — a full page whose final hit was missing `sort` (while an earlier hit had it) would silently build the next request from a stale, earlier position instead of tripping the "can't compute next page" guard, which could duplicate rows on the next fetch; fixed by capturing the truly-last hit first and reading its `sort` only after the loop (which also removed a per-hit clone-and-discard the old approach performed on every hit, not just the last one — an efficiency finding from the same review pass). Second, the health check's `healthCheckPath` resolved against the bare base `url` instead of `{url}/{index}` as documented, silently probing the wrong endpoint whenever a custom path was configured; fixed to combine through `ElasticsearchUrls.Combine` (plain path concatenation) rather than `HttpHealthProbe.CombineUrl`'s relative-`Uri` resolution, which independently would have dropped the `index` segment whenever `{url}/{index}` had no trailing slash (`Uri`'s relative-combination rules replace the last path segment rather than appending after it). Third, `ElasticsearchFilterTranslator`'s date/time literal parsing (copied from `ODataFilterTranslator`'s `DateTimeStyles.None`) interpreted an offset-less filter value in the host process's local timezone rather than UTC — non-deterministic across deployments and wrong for Elasticsearch's conventionally-UTC date fields; fixed to `DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal` (this source's own new code, not a change to the already-shipped OData translator). A fourth, smaller finding — `ElasticsearchBatchSource`'s and `ElasticsearchRowCounter`'s identical "write query, defaulting to `match_all`" `Utf8JsonWriter` logic — was deduplicated into a shared `ElasticsearchQueries.WriteQuery` helper.

**Security review: no findings.** Checked specifically for D61/D62's cross-origin credential-replay pattern (`HttpOrigin.IsSameOrigin`) — it does not apply here: `search_after` values are JSON literals round-tripped through the request *body* back to the one fixed, author-configured `_searchUrl`, never a server-supplied URL/host the way OData's `@odata.nextLink` or the HTTP family's `Link` header are, so there is no analogous surface to leak credentials across. `ElasticsearchFilterTranslator` builds every clause as a `JsonNode` tree (`JsonValue.Create` for literals) rather than string-interpolating into the query — structurally immune to the query-injection class regardless of the `EscapeWildcard` helper's completeness (that helper is a semantic correctness fix — stopping a literal `*`/`?` in a value from acting as an ES wildcard metacharacter — not a security boundary; JSON-syntax escaping is handled by `System.Text.Json` itself). Auth reuses the already-reviewed `MutableHttpAuth`/`HttpRequests.ApplyAuth` pattern unchanged.

### Authentication / resilience — identical to P4a/D62/D63, reused unchanged

Static headers/API-key/Bearer via `MutableHttpAuth` (`Header`/`ApiKey`/`Bearer` on `ElasticsearchSourceOptions`) — covers Elasticsearch's common deployments: Basic auth (`Header("Authorization","Basic "+base64)`), API key (`Header("Authorization","ApiKey "+encoded)` or the dedicated `ApiKey(...)` helper for a custom header), and Bearer service-account tokens (OpenSearch, Elastic Cloud). No OAuth2/token-refresh flow (P4b's still-deferred scope, unchanged). `ResiliencePipelineFactory` + `IRetryDelayHint` honoring a `Retry-After` response header through the existing batch-level pipeline, reusing `HttpRequests.BuildExceptionAsync`/`ReadRetryAfter` — no new mechanism.

### Honest capability gaps (D36)

1. **No PIT-backed consistency.** See the contract-choice section above — `search_after` alone, not PIT; a rare consistency gap under concurrent sort-key mutation, documented rather than silently risked or half-implemented across two divergent engine APIs.
2. **No `ISchemaExplorer`.** Elasticsearch's field mappings are a type catalog, not a relational table/column/FK graph, and index mappings are frequently dynamic/schema-less by design — mapping that into `SchemaCatalog` would be a stretch fit, declined rather than faked (the same call D63 made for GraphQL's `__schema`). No D49 query builder for Elasticsearch.
3. **`sort` has no enforced totality.** Documented caller responsibility (see pagination section) — the source cannot verify server-side that a configured sort produces a total order.

Constant-memory note: one page (`size` hits) is materialized at a time via `JsonDocument`, bounded by page size — constant across pages; the whole result set is never materialized.

### New dependency — none

Hand-rolled `POST` + `System.Text.Json`/`System.Text.Json.Nodes`, reusing `Http.Common`. **No Elasticsearch client library** (`Elastic.Clients.Elasticsearch`, `NEST`, `OpenSearch.Client`): those are engine-specific (a NEST-based source could not also serve OpenSearch, undermining the one-type-for-both premise), version-coupled to a specific server major version, and centered on strongly-typed request builders this config-driven, JSON-DSL-passthrough source doesn't need. Same hand-roll-the-small-thing call as D58/D61–D63.

Packaging: MIT (D55 default). No new CPM dependency.

## D65 — SaaS APIs (Epic P, P7) — split, and P7a design (HubSpot, Airtable)

P7 names four providers ("Salesforce, HubSpot, Google Sheets, Airtable"). Researched each provider's real REST surface before splitting (not guessed):

- **HubSpot** (CRM v3, `GET /crm/v3/objects/{objectType}`): static Bearer token (a private-app access token, generated once in HubSpot's UI — no OAuth2 dance for this integration style), cursor pagination (`paging.next.after` in the response body, sent back as `?after=`), record values nested under a `properties` envelope.
- **Airtable** (`GET /v0/{baseId}/{tableIdOrName}`): static Bearer token (a personal access token), cursor pagination (`offset` in the response body, sent back as `?offset=`), record values nested under a `fields` envelope.
- **Google Sheets** (`GET /v4/spreadsheets/{id}/values/{range}`): the API has **no server-side row pagination at all** — one call returns the whole requested range as a 2D array — and real usage is almost always a private, OAuth2/service-account-gated sheet (an API key only works for a sheet explicitly published as fully public, a narrow case). Materially different shape (single bounded response, not cursor-paginated) and materially different auth (real OAuth2/JWT, not a static token) — **its own design pass, not part of P7a.**
- **Salesforce** (`GET /services/data/vXX/query?q=<SOQL>`): pagination via `nextRecordsUrl`, a **relative path** returned in the response body that must be joined with the org's own `instance_url` (obtained at login, not a fixed public host) — a genuinely new pagination shape, no prior Epic P source has this. Auth is normally real OAuth2 (JWT bearer flow for server-to-server) — ties directly into P4b's still-deferred OAuth2 scope. **Its own design pass, not part of P7a.**

**Split decision**: P7a ships HubSpot + Airtable now (bundled in one PR — same "structurally near-identical, ship together" call D57 made for Redshift/Snowflake): both are static-Bearer-only, single-fixed-pagination-shape, envelope-nested-record REST APIs — no new auth mechanism, no new pagination mechanism beyond what `HttpBatchSource`'s existing `Cursor` strategy already proves works (D61), just two different field/param names and two different envelope keys. P7b (Google Sheets) and P7c (Salesforce) are deferred to their own design passes — Google Sheets for its single-response/no-pagination shape, Salesforce for its relative-URL pagination and its dependency on P4b's real OAuth2 work.

### Why P7a is NOT built on top of `NeoReports.Sources.Http`

The obvious-looking shortcut — `Source.HubSpot(...)` as a thin wrapper constructing a preconfigured `Source.Http(...).Paginate(HttpPaginationStrategy.Cursor).CursorField(...)` — does not work: `HttpBatchSource<T>` is `internal sealed` to `NeoReports.Sources.Http`, and even if it weren't, the typed `.As<T>()` builder always materializes via `JsonSerializer.Deserialize<T>` directly on the response element, with no hook for HubSpot's/Airtable's envelope nesting (`properties`/`fields`) or for the dynamic path's schema/`fieldMap`-driven `ReportRecord` materialization. Every Epic P HTTP-family source so far (`Http`, `OData`, `GraphQl`, `Elasticsearch`) owns its own dedicated internal `IBatchSource<T>` built on the shared `Http.Common` primitives rather than delegating to a sibling source package — P7a follows the same shape, not a new one: two small dedicated packages, `NeoReports.Sources.HubSpot` and `NeoReports.Sources.Airtable`, each referencing only `Http.Common` + `Core` + `Abstractions` (the same dependency shape as every prior HTTP-family package), each hand-rolling its own minimal batch source. Deliberately **not DRY'd into one shared "enveloped-cursor" base class** despite the two providers' near-identical shape — three/four similar files is better than a premature abstraction invented for exactly two consumers shipped in the same PR (CLAUDE.md); if a third enveloped-cursor SaaS API shows up later, that is the point to extract, mirroring how `Http.Common` itself only grew `QueryStrings`/`HttpHealthProbe`/`MutableHttpAuth` on a real 2nd/3rd shipped duplicate, never speculatively.

### Contract choice, pagination, response mapping (both providers)

`IBatchSource<T>`, one page per `ReadBatchAsync`, cursor-per-page (D6/D11), reusing `OpaqueCursor` for the opaque `string?` cursor (D3) — identical retry-idempotency reasoning to every other HTTP-family source. Fixed, non-configurable pagination shape per provider (unlike the generic HTTP source's four-strategy choice, or OData's two — there is nothing to choose, each provider has exactly one pagination mechanism):

| Provider | Records path | Envelope key (per record) | Cursor response path | Cursor request param | Page-size param |
|---|---|---|---|---|---|
| HubSpot | `results` | `properties` | `paging.next.after` | `after` | `limit` (max 100) |
| Airtable | `records` | `fields` | `offset` | `offset` | `pageSize` (max 100) |

Typed path: `JsonSerializer.Deserialize<T>` on the **envelope element** (`hit.properties`/`hit.fields`), not the whole record — so `T`'s properties match the provider's actual field names directly, the same "materialize from the nested payload" precedent D64's `_source` and D63's `node` established. Dynamic path (`type:"hubspot"`/`type:"airtable"`): `JsonRecordMaterializer.Materialize` over the envelope element against the declared schema + optional `fieldMap`, reused verbatim from `Http.Common`. **Honest gap, documented, not silently worked around**: envelope-sibling fields (HubSpot's top-level `id`/`createdAt`/`updatedAt`/`archived`; Airtable's top-level `id`/`createdTime`) are not reachable through this materialization — a report needing them isn't supported in this pass (a future `recordIdField`-style config property could expose the sibling `id` specifically; not implemented now, no half-wired property for it exists).

**HubSpot-specific config knob**: `.Properties(params string[] propertyNames)` (typed) / `properties` (dynamic, a JSON array), sent as `?properties=a,b,c`. Not optional in practice, not just a nicety — HubSpot's default response for most object types includes only a handful of standard fields (e.g. `createdate`, `hs_object_id`, `lastmodifieddate` for contacts); without explicitly requesting the fields a report's schema actually maps, every custom/CRM property would silently materialize as `null`, a confusing footgun `JsonRecordMaterializer`'s "missing field → null" contract would otherwise hide. Airtable has no equivalent — its response always includes every field the record has a value for.

### URL construction — provider fills in the fixed API host

Unlike every prior HTTP-family source (an author-supplied `url`), HubSpot's and Airtable's API hosts are fixed (`https://api.hubapi.com`, `https://api.airtable.com/v0`) — only the *resource* varies. `Source.HubSpot(objectType, token)` builds `{fixedHost}/crm/v3/objects/{objectType}`; `Source.Airtable(baseId, tableIdOrName, token)` builds `{fixedHost}/{baseId}/{tableIdOrName}` (`Uri.EscapeDataString` on the author-configured path segments, same discipline D64's `ElasticsearchUrls.Combine` uses for `index`). An optional `.BaseUrl(...)` override exists on both for the rare self-hosted-proxy/API-mocking case, mirroring no existing precedent but a small, clearly-scoped escape hatch — not exposed on the dynamic config path's required properties (an author who needs it can still reach it; documented as advanced/optional).

### Authentication, resilience, health check — reused unchanged

Static Bearer token only via `MutableHttpAuth`/`HttpRequests.ApplyAuth` (`.Bearer(token)`) — both providers' real, standard integration method for a private/personal-access-token integration (not a workaround; HubSpot explicitly recommends private-app tokens over OAuth2 for a single-account server-to-server integration, and Airtable's personal access tokens are its only non-OAuth2 auth mechanism). `ResiliencePipelineFactory` + `IRetryDelayHint` honoring `Retry-After` (both APIs rate-limit and return it) through the existing batch-level pipeline — no new mechanism. `ISourceHealthCheck` via the shared `HttpHealthProbe.ProbeAsync` (`HEAD`/`GET` fallback) against the resolved resource URL.

### Honest capability gaps (D36)

1. **No `IFilterTranslator` / server-side filter pushdown.** Neither API exposes a universal filter query language through the plain REST read endpoints used here (HubSpot's CRM search API and Airtable's formula-based filtering are different, more complex endpoints, out of scope for this pass) — previews run unfiltered, reported honestly as ignored, same posture as the generic HTTP/GraphQL sources.
2. **No `ISchemaExplorer`.** Neither has a relational table/column/FK catalog to map.
3. **No `ISourceRowCounter`.** Neither response includes a total count by default.
4. **Envelope-sibling fields not mappable** (see response-mapping section above) — `id`/timestamps are outside the materialized record.

Constant-memory note: one page (`limit`/`pageSize` records) is materialized at a time via `JsonDocument`, bounded by page size — constant across pages.

### New dependency — none

Hand-rolled `GET` + `System.Text.Json`, reusing `Http.Common`. No HubSpot/Airtable SDK (`HubSpot.Api.Client`, `AirtableApiClient`, etc.): both are thin, stable REST APIs this size of surface doesn't need an SDK for, and an SDK would bring its own auth/retry/pagination opinions that would have to be worked around rather than reused, the same hand-roll-the-small-thing call as every prior Epic P HTTP-family source.

Packaging: MIT (D55 default), two packages (`NeoReports.Sources.HubSpot`, `NeoReports.Sources.Airtable`). No new CPM dependency.

**Code review caught and fixed the same bug in both new health checks**: `HubSpotSourceHealthCheck`/`AirtableSourceHealthCheck` originally combined a configured `healthCheckPath` via the shared `HttpHealthProbe.CombineUrl` — `Uri`'s relative-combination resolution, which *replaces* the base URL's last path segment rather than appending after it whenever the base has no trailing slash (neither the object-collection URL nor the table URL ever does). This is the identical bug class D64 found and fixed for Elasticsearch's health check, reintroduced here because `HttpHealthProbe.CombineUrl` is the shared HTTP-family default and neither package initially routed the trailing segment through its own plain-concatenation URL builder the way `ElasticsearchUrls.Combine` does. Fixed by adding an optional trailing-segment parameter to `HubSpotUrls.ObjectCollection`/`AirtableUrls.Table` (plain string concatenation, no relative-`Uri` resolution) and routing `healthCheckPath` through it instead — both packages now had zero test coverage of the `healthCheckPath` branch, which is why the regression shipped unnoticed in the first draft; regression tests were added for both. A second, minor finding: `HubSpotBatchSource` rebuilt its comma-joined `properties` query value with `string.Join` on every page even though it never changes after construction — precomputed once in the constructor instead, alongside the already-precomputed `_collectionUrl`.

**Security review: no findings.** Neither source's pagination ever follows a server-supplied URL — the cursor is only ever a scalar token (HubSpot's `paging.next.after`, Airtable's `offset`) re-embedded as a query-string *value* on every request to the same fixed, author-configured collection/table URL, so there is no analogous surface to D61's `Link`-header credential-replay fix. `HubSpotUrls.ObjectCollection`/`AirtableUrls.Table` percent-encode every author-configured path segment (`objectType`; `baseId`/`tableIdOrName`) via `Uri.EscapeDataString` before assembly; cursor tokens and the `properties` list go through the shared `QueryStrings.AddQuery`, never raw-concatenated. Auth reuses the already-reviewed `MutableHttpAuth`/`HttpRequests.ApplyAuth` pattern unchanged.

**SonarCloud's new-code duplication gate (>3% new-code duplication fails the PR) caught real duplication between the two new packages — and, for two of the three flagged blocks, between the new packages and the already-shipped `Http`/`OData`/`GraphQl`/`Elasticsearch` health checks and batch sources.** Since the gate only counts *new* lines (this PR's own diff), the fix was to make the new code stop containing the duplicated lines itself, by promoting three genuinely shared tails into `Http.Common` — without touching any already-shipped package (out of scope, avoids regression risk, and unnecessary since old code isn't "new" and doesn't count against the gate):
1. `HttpRequests.GetJsonAsync(client, requestUri, auth, cancellationToken)` — the "build a GET request, apply auth, send, throw on non-2xx, parse the body as `JsonDocument`" tail every GET-only HTTP-family batch source (`HubSpotBatchSource`, `AirtableBatchSource`, and structurally `HttpBatchSource`'s GET strategies) repeats verbatim.
2. `HttpHealthProbe.CheckAsync(Func<Task<HttpResponseMessage>> probe, cancellationToken)` — the "time the probe, convert success/non-2xx/exception into a `SourceHealthResult`" tail every `ISourceHealthCheck` implementation repeats verbatim; the provider-specific config-property reads and `ProbeAsync`/`SendAsync` call happen inside the passed `probe` delegate, so they're still caught by the shared try/catch.
3. `PropertyBag.ApplyCommonFieldsAndAuth<TOptions>(properties, options)` plus a new `ICommonHttpOptions<TSelf>` marker interface (a self-referencing/CRTP generic constraint) — the "read `fieldMap`/`headers`/`bearerToken`/`healthCheckPath`, call the matching fluent setter" tail every dynamic-path `ConfigProperties.ReadOptions` repeats verbatim. `HubSpotSourceOptions`/`AirtableSourceOptions` now implement the marker interface — costless, since their existing `FieldsFrom`/`Header`/`Bearer`/`HealthCheckAt` methods already had exactly the required fluent shape. (A first attempt passed the four setters as `Action<...>` delegates instead; C# doesn't allow a method-group conversion from a non-`void`-returning method directly to an `Action` delegate type (`CS0407`), and wrapping each in a lambda at the call site would have just re-created the same duplicated text the fix was meant to remove — the generic-interface design avoids both problems.)

All three are purely additive to `Http.Common` (new methods/interface, no existing signature changed) — every already-shipped package's test suite (`Http`, `OData`, `GraphQl`, `Elasticsearch`) was re-run unchanged and stayed green.

## D66 — Google Sheets source (Epic P, P7b) — design

New MIT package `NeoReports.Sources.GoogleSheets`, source type id `"googlesheets"`. Confirmed by research (not assumed) that this genuinely needed its own design pass, as D65 anticipated: the Sheets API v4 has **no cursor/next-page mechanism at all** (unlike every prior Epic P source) and its data shape is **positional cells with a header row**, not name-keyed JSON — closer to the file family (CSV/XLSX) than the HTTP-JSON family (OData/GraphQL/Elasticsearch/HubSpot/Airtable) it sits alongside in Epic P's own numbering.

**Maintainer-confirmed risk acceptance (2026-07-20):** several behaviors below are researched from Google's documented API surface but **not verified against a live call** — no internet access in this environment, no Testcontainers equivalent for a hosted Google API, no test spreadsheet/API key available. Flagged to the maintainer before implementing; the maintainer chose to proceed with the gap explicitly documented (mirroring D57's Redshift/Snowflake "unit tests only, no live-server verification" precedent) rather than skip P7b or block on manual verification. The specific unverified assumptions are called out inline below and in the honest-gaps section.

### Contract choice: `IBatchSource<T>`, transport-driven — but `ReflectedRowShape<T>` for materialization, data-shape-driven

Two axes, decided independently, worth stating explicitly since they point different directions:
- **Contract**: `IBatchSource<T>`, not the file family's `IStreamingSource<T>`. Each page is one bounded HTTP request (a fixed row window), so a transient failure retries in isolation from its own cursor (D6/D11) — the same reasoning every HTTP-family source in Epic P already uses, and a materially better fit than `IStreamingSource<T>`'s "resume a partially-consumed enumerator" shape, which doesn't apply to a request/response transport.
- **Materialization**: reuses `NeoReports.Core.Sources.ReflectedRowShape<T>` — the CSV/XLSX/ADO families' shared "match by header/column name, build via longest constructor or settable properties" helper — rather than `Http.Common`'s `JsonRecordMaterializer` (JSON-key-based). A Sheets row is an array of cell values with **no field names of its own**; the only names available are whatever the sheet's own header row contains, exactly the same shape CSV already solves. `GoogleSheetsRecordMaterializer<T>` mirrors `CsvRecordMaterializer<T>` almost exactly (own package, not shared — same "don't DRY two-consumer duplication preemptively" call as P7a, since CSV lives in an unrelated file-source package with no natural shared home for this one).

### Pagination — no server cursor, so a fixed-size row window advanced until an empty response

There is no `nextPageToken`/`Link` header/cursor of any kind in `spreadsheets.values.get`/`batchGet`. Pagination is synthesized: each page requests a fixed-size row window in A1 notation (`{sheet}!A{offset+1}:{lastColumn}{offset+pageSize}`), advances `offset` by `pageSize` every page regardless of how many rows the response actually contained, and stops when a window's response has no `values` array at all (an empty range). **The header row is fetched once per source-instance run and cached** (via `values:batchGet`'s multiple-`ranges` parameter — the first page's single round trip returns both the header range and the data window; every later page requests only the data-window range). Code review initially flagged the opposite choice (re-fetching the header on every page "to keep the source fully stateless like every sibling Epic P source") as conflating retry-statelessness with instance-level caching: a retry of a later page's failed data-window fetch never needs to redo an earlier page's already-successful header fetch, so caching costs nothing in retry-safety while saving one Sheets API range-fetch per page for the life of the run — the mutable `_cachedHeaderIndex` field this requires is genuinely new for an Epic P HTTP-family source (every sibling batch source is fully stateless per instance), but is scoped to one run and reset on the next `Create` call, matching how the ADO family's connection-holding sources already carry per-run instance state.

**Honest, documented gap**: advancing by a fixed `pageSize` regardless of how many rows a window actually returned means a window that is *entirely* blank (a large gap in a sparse sheet) is indistinguishable from "no more data" and ends pagination early. This is the same class of gap D62's OData `Skip` strategy and D61's offset-based HTTP strategy already carry (an offset/window-based scheme can't distinguish "temporarily sparse" from "exhausted" without a total-count oracle) — not a new kind of risk, just a new source hitting the existing, accepted tradeoff. Documented, not silently risked.

### The `valueRenderOption=UNFORMATTED_VALUE` requirement — an easy, high-stakes default to get wrong

Every request sets `valueRenderOption=UNFORMATTED_VALUE` explicitly. Sheets' *default* render option (`FORMATTED_VALUE`) returns every cell as its display string (e.g. a currency cell as `"$1,234.56"`, a date cell as `"7/20/2026"`) — useless for typed conversion and actively wrong for a numeric/date column's declared `ColumnType`. `UNFORMATTED_VALUE` returns numbers as JSON numbers and **dates/times as serial-number doubles** (days since the 1899-12-30 epoch, the same convention spreadsheet formats have used for decades — verified against documented, stable, widely-referenced behavior, not a live call) rather than ISO-8601 strings; `GoogleSheetsRecordMaterializer<T>`'s date/time conversion path converts that serial number back to a `DateTime`/`TimeSpan` explicitly rather than assuming a parseable string, the one non-obvious conversion step this source owns that CSV's equivalent doesn't need.

### Authentication — API key as a query parameter, not `MutableHttpAuth`

Static API key only (no OAuth2/service-account/JWT-bearer flow — that would materially overlap with P4b's still-deferred scope, and a private, non-publicly-shared spreadsheet requires exactly that flow to read at all; out of scope here, an honest gap below). Sent as the well-documented, universally-supported `?key=<API_KEY>` query parameter on every request (via the existing `QueryStrings.AddQuery`, not a new mechanism) — **deliberately not** routed through `MutableHttpAuth`/`ICommonHttpOptions` (both are header-based; a Google API key's secondary header form exists but is less certain from documentation alone, so the query-parameter form — Google's primary, unambiguous mechanism — was chosen instead of gambling on the alternative). `GoogleSheetsSourceOptions` does not implement `ICommonHttpOptions<TSelf>` for this reason; its auth surface is a single `ApiKey(string)` setter, not `Header`/`Bearer`.

### Configuration

Typed: `Source.GoogleSheets(spreadsheetId, sheetName, apiKey).Columns("A", "F").As<T>()` (`Columns` sets the A1 column-letter bounds of the read; the header row defaults to row 1, overridable via `.HeaderRow(n)`). Dynamic (`type:"googlesheets"`): required `spreadsheetId`, `sheet`, `apiKey`, `firstColumn`/`lastColumn` (A1 column letters); optional `headerRow` (default `1`).

### Health check

`GoogleSheetsSourceHealthCheck : ISourceHealthCheck`: a plain `GET` (via `HttpHealthProbe.SendAsync`, not `ProbeAsync`'s `HEAD`-then-`GET` fallback — see the code-review findings below) against the spreadsheet metadata endpoint (`GET .../v4/spreadsheets/{spreadsheetId}?key=...&fields=spreadsheetId`, the smallest possible metadata request) — reachability/auth/spreadsheet-exists only, the same honesty boundary every prior health check in Epic P keeps.

### Honest capability gaps (D36)

1. **No OAuth2/service-account auth, so no private-sheet support.** Only a spreadsheet shared as "anyone with the link can view" (public) is readable — a real, if narrow, use case (published reference/open datasets), not a workaround. Private-sheet support needs a JWT-bearer/service-account flow, which is P4b-shaped scope, deferred with it.
2. **No `IFilterTranslator`/`ISchemaExplorer`/`ISourceRowCounter`.** `values.get` has no query language, no relational catalog, and no accurate row-count mechanism (a sheet's allocated `gridProperties.rowCount` is grid capacity, not a count of populated data rows — declined as a row-count proxy rather than fabricated, D36).
3. **Fixed-window pagination can end early on a large blank gap** (see pagination section) — documented, same class of gap as D61/D62's offset-based strategies.
4. **Date-serial conversion and the multi-range `batchGet` response shape are researched, not live-verified** (see the risk-acceptance note above) — flagged explicitly to the maintainer before implementation; unit tests cover the conversion logic against constructed fixtures matching documented API shape, but nothing in this repo exercises a real Google Sheets API call.

Constant-memory note: one page (`pageSize` rows, plus the one-row header range) is materialized at a time via `JsonDocument`, bounded by page size — constant across pages.

### New dependency — none

Hand-rolled `GET` + `System.Text.Json`, reusing `Http.Common` (`HttpClients`, `HttpRequests.GetJsonAsync`, `JsonRecords`, `OpaqueCursor`, `QueryStrings`, `HttpHealthProbe`) and `NeoReports.Core.Sources.ReflectedRowShape<T>` (already public, the file family's shared materializer). No Google API client library (`Google.Apis.Sheets.v4`): it brings its own auth/HTTP-client/discovery-document machinery this config-driven, API-key-only, single-endpoint source doesn't need — same hand-roll-the-small-thing call as every prior Epic P source.

Packaging: MIT (D55 default). No new CPM dependency.

**Code review caught four real findings, all fixed:** (1) a documentation/code mismatch — this ADR's own configuration section originally listed `apiKey` as optional for the dynamic path, contradicting `GoogleSheetsConfigProperties.ReadOptions`'s unconditional `PropertyBag.RequireString(..., "apiKey", ...)` and its own test coverage; fixed by correcting the ADR text. (2) The health check originally used `HttpHealthProbe.ProbeAsync`'s `HEAD`-then-`GET` fallback (the default for every other Epic P health check), but that fallback only retries on `405`/`501` — Google's REST-transcoded API frontend is not a general-purpose HTTP server and isn't confirmed to reject an unsupported `HEAD` with one of those two specific codes (plausibly a `400`, which the fallback wouldn't catch, silently reporting a healthy spreadsheet as unreachable); fixed by going straight to `GET` via `HttpHealthProbe.SendAsync`, avoiding the unverified assumption entirely rather than risk it — consistent with this whole ADR's "don't gamble on unverified live-API behavior" stance. (3) The date-serial-epoch/cell-decode conversion logic (`SheetsEpoch`, the JSON-value-to-text switch) was duplicated three times across the typed and dynamic materializers — extracted into a shared internal `GoogleSheetsCellText` helper. (4) The header-range re-fetch-on-every-page choice (see the pagination section above) was reversed in favor of caching, once code review pointed out the original "keeps the source stateless" justification didn't actually hold up under a retry-safety analysis.

**Security review: no findings.** The API key travels only as a query-string value (`?key=`, never a header, never logged as a header), never derived from or combined with user-controlled input beyond the author-configured spreadsheet id/sheet name/column letters, which are percent-encoded (`Uri.EscapeDataString`) or A1-quote-escaped (`GoogleSheetsRanges.QuoteSheet`) before assembly — no path/query injection surface. No server-supplied URL is ever followed (unlike the `Link`-header/`@odata.nextLink` pattern D61/D62 had to guard), since every request target is built entirely from the fixed, author-configured spreadsheet/sheet/columns plus a locally-computed row offset.

## D67 — Salesforce source (Epic P, P7c) — design

New MIT package `NeoReports.Sources.Salesforce`, source type id `"salesforce"` — the last of P7's three "SaaS API" sub-items (P7a HubSpot/Airtable, P7b Google Sheets, this one). Confirmed by research the two things the original P7 split (D65) flagged as materially different about Salesforce, and one thing that turned out simpler than expected:

- **Genuinely new pagination shape, confirmed**: the REST Query resource (`GET /services/data/{apiVersion}/query?q=<SOQL>`) returns `{"totalSize": N, "done": bool, "nextRecordsUrl": "/services/data/{apiVersion}/query/<locator>", "records": [...]}`. `nextRecordsUrl`, when present, is a **relative path** (by Salesforce's documented contract, always rooted at the org's own `instanceUrl`, never a different host) — no prior Epic P source has a next-page pointer shaped like this (OData's `@odata.nextLink`/HTTP's `Link` header are absolute URLs; HubSpot/Airtable/Elasticsearch have no next-page URL concept at all, only a token value).
- **Materialization turned out to be the simplest in the whole HTTP-JSON family, not harder**: each record is a plain JSON object with the queried fields at the **top level**, alongside a sibling `attributes` metadata key (`{"type":"Account","url":"..."}`) that isn't in any real schema and is simply never matched by name — no envelope to descend into (unlike HubSpot's `properties`/Airtable's `fields`). This is the same flat shape OData's `value` array already has; `JsonRecordMaterializer`/the typed `JsonSerializer.Deserialize<T>` builder are reused directly on each record element, unmodified.
- **OAuth2 is still real, still deferred to P4b** — Salesforce's standard machine-to-machine auth is the JWT bearer flow (or the deprecated username-password flow); this source instead accepts a pre-obtained `accessToken` + the org's `instanceUrl` as static configuration, the same "static Bearer token, caller obtains/refreshes it externally" posture every prior Epic P source has taken pending P4b's real OAuth2/token-refresh work. `instanceUrl` itself (not a fixed public host like `api.hubapi.com`) is also new — it varies per org/sandbox, obtained once by the caller alongside the token.

### Contract, cursor, request shape

`IBatchSource<T>`, cursor-per-page (D6/D11) — `nextRecordsUrl` is stored verbatim in the opaque cursor (`SalesforceCursorState`, via `OpaqueCursor`) rather than re-derived, since it's an opaque locator Salesforce itself hands back, not something this source could reconstruct from an offset. First page: `GET {instanceUrl}/services/data/{apiVersion}/query?q={soql}`. Subsequent pages: `GET` against `nextRecordsUrl` resolved relative to `instanceUrl` via real `Uri` relative-resolution (`new Uri(baseUri, nextRecordsUrl)`, not plain string concatenation) — safe here specifically because `nextRecordsUrl` always starts with `/` (an absolute-path reference replaces the base's entire path, sidestepping the D64/D65 "drops the last path segment" pitfall, which only bites a reference *without* a leading `/`). Using real resolution rather than concatenation also makes the same-origin check that follows meaningful: `HttpOrigin.IsSameOrigin` verifies the resolved URL stays on `instanceUrl`'s origin before use (D61's cross-origin credential-replay guard) — a malformed/unexpected absolute URL in `nextRecordsUrl` would genuinely resolve to a different origin here (string concatenation would instead have silently mangled it into a same-origin path segment, making the check moot). `done:true` (no more pages) or a response with no `nextRecordsUrl` ends pagination.

### Row counting — a real capability, not a proxy this time

`SalesforceRowCounter : ISourceRowCounter`, via a derived `SELECT COUNT() FROM ...` query: the author's configured SOQL has its `SELECT <fields>` clause replaced with `SELECT COUNT()` (`SalesforceCountQuery.TryBuildCountQuery` — a hand-rolled, paren-depth-aware keyword scan, not a regex, so a subquery's own nested `FROM` isn't mistaken for the outer query's; word-boundary matching treats `_` as a word character, since Salesforce field API names routinely contain underscores, e.g. `Migrated_From_System__c` — a bare `char.IsLetterOrDigit` boundary check would misdetect `FROM` embedded in such a name, a code-review-caught bug), keeping everything from `FROM` onward — `WHERE`/`ORDER BY`/etc. — completely unchanged, then issued the same way a normal query is. Unlike Elasticsearch's `rowCount` (declared grid capacity, not real data) or a query-builder count, `totalSize` in a `COUNT()` query's response **is** an accurate count of exactly the rows the real query would return with the same filter — the same honesty tier as OData's `$count`, the first non-SQL source in Epic P since OData to have a genuinely accurate mechanism rather than a documented proxy or gap. Best-effort by `ISourceRowCounter`'s own contract (D36): a SOQL that doesn't match the expected `SELECT ... FROM ...` shape (e.g. no `FROM` found) returns `null` rather than guessing at a rewrite.

### Authentication, resilience, health check — reused unchanged

Static Bearer token via `MutableHttpAuth`/`ICommonHttpOptions<TSelf>` (`.Bearer(accessToken)`) — `SalesforceSourceOptions` implements `ICommonHttpOptions<SalesforceSourceOptions>` (P7a's marker interface), so the dynamic-path's `fieldMap`/`headers`/`bearerToken`/`healthCheckPath` properties are read via the existing shared `PropertyBag.ApplyCommonFieldsAndAuth` with zero new duplicated code. `ResiliencePipelineFactory` + `IRetryDelayHint` honoring `Retry-After` through the existing batch-level pipeline — no new mechanism. `ISourceHealthCheck` probes `GET {instanceUrl}/services/data/{apiVersion}/` (the REST API's own "list available resources" endpoint — lightweight, stable, requires only valid auth, and deliberately does not depend on the configured `soql`, the same "test connection doesn't validate the read path's shape" boundary D63's GraphQL fix established) via `HttpHealthProbe.ProbeAsync`. A configured `healthCheckPath` is appended via `SalesforceUrls.Resources`'s own trailing-segment parameter (plain concatenation), not `HttpHealthProbe.CombineUrl`'s `Uri` relative-resolution — a path-with-leading-`/` would otherwise be treated as an absolute-path reference and replace the whole URL rather than append to it (code-review finding, the same D64/D65 bug class HubSpot/Airtable/Elasticsearch already had to avoid).

### Configuration

Typed: `Source.Salesforce(instanceUrl, soql, accessToken).As<T>()` (optional `.ApiVersion(v)`, default a recent stable version). Dynamic (`type:"salesforce"`): required `instanceUrl`, `soql`, `bearerToken`; optional `apiVersion`, `fieldMap`, `headers`, `healthCheckPath`.

### Honest capability gaps (D36)

1. **No OAuth2/JWT-bearer flow, so no built-in token refresh.** Static access token only, ties into P4b (see above).
2. **No `IFilterTranslator`.** Unlike the ADO family's SQL translation or OData's `$filter`, translating `PreviewFilter` into SOQL `WHERE` syntax (its own literal-quoting/date-literal rules, distinct from both SQL and OData) is new, non-trivial work — declined for this pass rather than rushed, the same call already made for HubSpot/Airtable/Google Sheets. The author's configured `soql` is used verbatim; previews run unfiltered, reported honestly as ignored.
3. **No `ISchemaExplorer`.** Salesforce's object/field metadata (the Describe API) is a different, heavier endpoint this pass doesn't reach for.

Constant-memory note: one page (Salesforce's own default/configured batch size, up to 2,000 records per Salesforce's documented API limit) is materialized at a time via `JsonDocument` — constant across pages.

### New dependency — none

Hand-rolled `GET` + `System.Text.Json`, reusing `Http.Common` in full (`HttpClients`, `HttpRequests.GetJsonAsync`, `JsonRecords`, `OpaqueCursor`, `QueryStrings`, `HttpHealthProbe`, `MutableHttpAuth`, `ICommonHttpOptions`, `HttpOrigin`) — no new Http.Common surface needed this time, everything P7a/P7b already built is directly reusable as-is. No Salesforce SDK (`Salesforce.Force`, `DeveloperForce/Force.com-Toolkit-for-NET`, etc.): those bring their own OAuth/connection-management opinions this config-driven, pre-authenticated-token source doesn't need — same hand-roll-the-small-thing call as every prior Epic P source.

Packaging: MIT (D55 default). No new CPM dependency.

**Code review caught four real findings, all fixed:** (1) most significantly, `SalesforceBatchSource<T>` originally implemented only `IBatchSource<T>`, not `ISourceRowCounter` — unlike `ODataBatchSource<T>`/`ElasticsearchBatchSource<T>`, which both implement `ISourceRowCounter` by delegating to their own row counter internally. Since `ReportBuilder` detects counting support via `source as ISourceRowCounter` pattern-matching on the instance a source factory actually returns, and both `SalesforceConfigSourceProvider.Create`/`SalesforceSourceBuilder.As<T>()` returned a bare `SalesforceBatchSource<T>`, the fully-built, fully-tested `SalesforceRowCounter` — despite this ADR's own "a real capability, not a proxy" framing above — was unreachable dead code in production, only ever constructed directly from unit tests; fixed by having `SalesforceBatchSource<T>` implement `ISourceRowCounter` and compose a `SalesforceRowCounter` instance, matching the established sibling pattern exactly. (2) `SalesforceCountQuery`'s word-boundary check used a bare `char.IsLetterOrDigit`, so `FROM`/`SELECT` embedded inside an underscore-delimited Salesforce field API name (e.g. `Migrated_From_System__c`, `_` bounded on both sides by non-alphanumeric characters) was misdetected as the keyword, corrupting the count-query rewrite for an otherwise perfectly valid, extremely common Salesforce naming pattern; fixed by treating `_` as a word character too. (3) The health check's `healthCheckPath` went through `HttpHealthProbe.CombineUrl`'s `Uri` relative-resolution on the (correct) assumption that the base URL's trailing slash was sufficient protection — it isn't: a configured path *itself* starting with `/` is an absolute-path reference that replaces the whole path regardless of the base's trailing slash; fixed the same way HubSpot/Airtable/Elasticsearch already had to (a dedicated trailing-segment parameter on `SalesforceUrls.Resources`, plain concatenation, no `Uri`-resolution ambiguity to reason about). (4) A minor efficiency finding: `instanceUrl` was re-parsed into a `Uri` twice per page (once for `NextPage`, once for the same-origin check) — precomputed once in the constructor instead.

**Security review: no findings.** `nextRecordsUrl` combination and the same-origin check are reasoned about explicitly above (real `Uri` resolution chosen specifically so the check is meaningful, not moot). The access token travels only via the standard `Authorization: Bearer` header (`MutableHttpAuth`/`HttpRequests.ApplyAuth`, already reviewed unchanged). `SalesforceCountQuery`'s rewrite operates on the author's own already-trusted `soql` (not attacker input) and degrades safely to `null` on any malformed result (a corrupted rewrite from an unanticipated query shape — e.g. a string literal containing unbalanced parens or the word `FROM` — produces an invalid SOQL query Salesforce itself rejects with a non-2xx response, which `SalesforceRowCounter.CountAsync`'s catch-all already treats as "can't count," never a wrong count or a crash).

## D68 — OAuth2 client-credentials auth (Epic P, P4b) — design

Adds OAuth2 client-credentials grant support (RFC 6749 §4.4) to the generic HTTP source (`NeoReports.Sources.Http`, P4a) — the last remaining item in Epic P's original numbering (P1–P7 are all done; P4b was deferred from P4a's own PR since D61, and every HTTP-family source shipped since (OData/GraphQL/Elasticsearch/HubSpot/Airtable/Google Sheets/Salesforce) carried the same honest "static token only" gap pending this pass).

### Scope: only the generic HTTP source, not every sibling

Deliberately scoped to `NeoReports.Sources.Http` alone, not retrofitted onto every other HTTP-family source. Each of those sources' own ADR (D62/D63/D64/D65/D66/D67) already made a considered, provider-specific auth call (Elasticsearch's Basic/API-key/Bearer mix for its own deployment models; HubSpot's/Airtable's private-app tokens as their *standard*, non-workaround integration method; Google Sheets' query-parameter API key; Salesforce's pre-obtained access token) — extending each to OAuth2 is a separate, provider-specific decision (does Elasticsearch even have a standard OAuth2 flow? does Salesforce's JWT-bearer flow fit this same client-credentials shape, or does it need its own?), not something this pass should quietly bundle in. The new `OAuth2ClientCredentialsProvider` (below) is built as a source-agnostic primitive precisely so a later, deliberate decision to adopt it elsewhere is cheap — an honest, undone extension (D36), not a design dead end.

### Grant type: client-credentials only

Client-credentials (RFC 6749 §4.4) is the only OAuth2 grant with no interactive/human step — the only one that fits NeoReports' headless, scheduled-job execution model (D6: a job restarts from zero, runs unattended). Authorization-code (needs a user's browser redirect), resource-owner-password (deprecated in OAuth 2.1, requires storing a real user's password), and JWT-bearer (needs a pre-provisioned signing key/assertion per provider, a materially different and heavier mechanism) are all out of scope — an honest gap, not silently worked around; a source needing one of those still configures a static, externally-obtained/refreshed bearer token via the existing `Bearer(...)` option, the same posture every other Epic P source already has.

### Client authentication: `client_secret_basic`, not `client_secret_post`

The token request authenticates via HTTP Basic (`Authorization: Basic base64(client_id:client_secret)`) — RFC 6749 §2.3.1's own recommended default and the most broadly supported method across real identity providers (Auth0, Okta, Azure AD, Google, etc. all accept it; many treat it as primary). `client_secret_post` (credentials as form fields alongside `grant_type`) is a real, valid alternative some legacy providers require instead — declined for this pass as a documented, narrow gap (D36) rather than adding a second, less-tested code path for a single, well-supported default.

### Where the async token-fetch lives — `HttpAuth` stays a plain, synchronous snapshot

The hardest design constraint: `HttpAuth` (`Http.Common`) and `HttpRequests.ApplyAuth` are synchronous, and every existing HTTP-family source (7 shipped packages) calls `options.ToAuth()` as a cheap, synchronous snapshot. Fetching/refreshing an OAuth2 token is unavoidably async (an HTTP round trip). Two designs were possible: (a) make `HttpAuth`/`ApplyAuth` async everywhere, a breaking change rippling through every already-shipped HTTP-family source for a capability only one of them uses; or (b) resolve the token *before* building the per-request `HttpAuth` snapshot, keeping `HttpAuth`/`ApplyAuth` unchanged. Chose (b): a new `HttpOAuth2` helper (kept in `NeoReports.Sources.Http`, not `Http.Common`, since it operates on `HttpSourceOptions` directly) exposes `ResolveAuthAsync(options, provider, cancellationToken)` — returns `options.ToAuth()` unchanged when no OAuth2 provider is configured (zero behavior change, verified: all 33 pre-existing `NeoReports.Sources.Http.UnitTests` pass unmodified), or that same snapshot with `BearerTokenValue` overridden by a freshly-resolved token otherwise (a one-line `with` expression, since `HttpAuth` is a record). `HttpBatchSource<T>`/`HttpStreamingSource<T>`/`HttpSourceHealthCheck` each call this instead of `options.ToAuth()` directly — the only behavior-visible change, and only when OAuth2 is actually configured.

### Token caching and refresh

`OAuth2ClientCredentialsProvider` (new, `Http.Common`, source-agnostic) caches the fetched `access_token` and its expiry, refreshing proactively 30 seconds before expiry (a clock-skew buffer, so a request starting an instant before expiry doesn't race a token going stale mid-flight) rather than on every call. Concurrent callers serialize on a single `SemaphoreSlim`-guarded refresh rather than each independently hitting the token endpoint. When a token response omits `expires_in` (RFC 6749 doesn't require it), assumes a conservative 3600-second default rather than treating an unspecified expiry as "never" — an unbounded-lifetime assumption risks holding a stale/revoked token indefinitely with no forced re-validation. One provider instance per source instance (constructed once in each `HttpBatchSource`/`HttpStreamingSource` constructor, reused across every page/request for that source's lifetime) — not shared globally across sources or report runs, matching D6's "no cross-run state" posture.

### Configuration

Typed: `Source.Http(url, client).OAuth2ClientCredentials(tokenEndpoint, clientId, clientSecret, scope?)` — mutually exclusive with `.Bearer(...)` (configuring both throws immediately at options-build time, a clear config-mistake signal rather than silently picking one). Dynamic (`type:"http"`): optional `oauth2TokenEndpoint`/`oauth2ClientId`/`oauth2ClientSecret` (all three required together) and `oauth2Scope`.

### Health check

`HttpSourceHealthCheck` resolves a token the same way (`HttpOAuth2.ResolveAuthAsync`) before probing — a fresh, unshared provider per health-check invocation (a health check is a one-shot, infrequent call; no cross-invocation caching needed the way a batch source's per-page reuse benefits from it).

### Honest capability gaps (D36)

1. **Only the generic HTTP source adopts this** — every sibling HTTP-family source keeps its existing static-token-only auth (see "Scope" above); adopting OAuth2 elsewhere is a separate, future, provider-specific decision.
2. **Only the client-credentials grant.** No authorization-code, resource-owner-password, or JWT-bearer flow.
3. **Only `client_secret_basic` client authentication.** No `client_secret_post` fallback.
4. **No token persistence across job restarts.** A crashed/restarted job (D6: restarts from zero) fetches a fresh token — no different from any other in-memory state this codebase already treats as disposable.

### New dependency — none

Hand-rolled `POST` + `System.Text.Json`, reusing `Http.Common` (`HttpRequests.ReadRetryAfter`, `JsonRecords.TryGetField`, `HttpSourceException`). No OAuth2/OIDC client library (`IdentityModel`, `Duende.AccessTokenManagement`, etc.): those bring session/cookie/interactive-flow machinery a single non-interactive grant type doesn't need — same hand-roll-the-small-thing call as every prior Epic P source.

Packaging: MIT (D55 default). No new CPM dependency.

### Code review

Four parallel finder angles (line-by-line diff scan, removed-behavior audit, reuse/simplification/efficiency, altitude/conventions) surfaced 2 real, confirmed findings, both fixed before merge:

1. **Dynamic-config partial OAuth2 properties silently produced no auth.** `HttpConfigProperties.ReadOptions` only called `OAuth2ClientCredentials(...)` when all three of `oauth2TokenEndpoint`/`oauth2ClientId`/`oauth2ClientSecret` were present, but a config with only 1 or 2 of them (a typo'd or omitted property name) fell through with no error and no auth at all — sending unauthenticated requests to what was meant to be a protected endpoint. Fixed: now throws `ConfigurationException` when any of the three is present without the other two.
2. **`OAuth2ClientCredentialsProvider.RefreshAsync`'s non-2xx branch hand-rolled its exception** instead of reusing `HttpRequests.BuildExceptionAsync`, losing the response-body-snippet diagnostics every other HTTP-family failure path captures. Fixed: now builds via `BuildExceptionAsync` and wraps its message/status/retry-after with a "token endpoint" prefix so the failure is still distinguishable from an API-call failure.

A third finding — the fast-path token read in `GetAccessTokenAsync` reading `_cachedToken`/`_expiresAt` as two separate, non-atomic fields outside the refresh lock, racing `RefreshAsync`'s writes under the lock from another thread — was accepted as plausible under concurrent callers (e.g. two pages fetched in parallel) even though the failure direction (an old token treated as expired) was benign. Fixed by bundling both into one immutable `CachedToken` record swapped via `Volatile.Read`/`Volatile.Write`, so a reader can never observe a torn combination of stale token with fresh expiry or vice versa.

Judgment calls made, no fix applied: `HttpSourceHealthCheck` constructing a fresh provider per call (no cross-invocation caching) — reasonable for an on-demand, infrequent check, matches the stated design; token-failure-vs-API-failure exception shape both being `HttpSourceException` with only the message text distinguishing them — sufficient, no separate exception type warranted for one caller; `HttpOAuth2` living in `NeoReports.Sources.Http` rather than promoted to `Http.Common` — avoids a premature shared abstraction for a single consumer.

### Security review

No findings. Client secret and fetched access token are held only in memory (`OAuth2ClientCredentialsProvider`'s private fields), never logged, never written to disk; the token request body (`FormUrlEncodedContent`) is not logged; HTTP Basic credentials are transmitted only over whatever transport the caller's `HttpClient` is configured for (same trust boundary as every other Epic P source's static credentials).

## D69 — Builder wizard: default-reset over opt-in-reset for `/builder` (UI, 2026-07-21)

Fixes a real bug found while generalizing the Builder wizard's Configure step (PR #203): `Builder.razor`'s `OnInitializedAsync` called `Wizard.Reset()` unconditionally whenever the URL had no `?edit=` query param — including on "Back" navigation from any later wizard step, since Blazor's router constructs a fresh `Builder` instance on every route change. This silently wiped in-progress query/properties/columns/formats/destination/resilience settings the user had already entered, even though they never left the wizard.

**First attempt (reverted before merge): opt-in reset.** Added `?new=true`, only resetting when present; updated every known "New report"/"start fresh" entry point (`Dashboard.razor`, `Reports.razor` ×2, `JobFailed.razor`'s "Edit configuration and retry") to pass it, leaving the wizard's own internal Back/Change/edit links untouched. Self-review (4 parallel finder angles) caught a real miss with this design before it shipped: `Layout/Topbar.razor`'s persistent "Builder" nav link (present on every page) was never updated, reproducing the exact original bug through a 5th entry point the initial pass didn't grep for. The review's own altitude angle diagnosed why: the opt-in-reset polarity puts the burden on the *larger, open-ended* set (every current and future "start a report" entry point anywhere in the app), while the *smaller, fixed* set (3 internal wizard links) got safety for free. Forgetting the opt-in silently reintroduces stale-data reuse — the original, more dangerous bug; forgetting the opposite convention would just wipe an in-progress draft — annoying but immediately visible, never wrong data reaching Save.

**Shipped instead: default-reset, opt-in-resume.** `Builder.razor` now resets `Wizard` by default whenever `EditName` is blank; only `?resume=true` (set on the wizard's own 3 internal "Back"/"Change"/"edit" links — `BuilderConfigure.razor`'s "Change →" and "Back", `BuilderReview.razor`'s Source "edit") skips it. Every external entry point (`Dashboard`/`Reports`/`JobFailed`'s buttons, and `Topbar.razor`'s persistent link, which needed no change at all) now gets correct behavior automatically, with nothing to remember. `QueryBuilder.razor`'s "Create report from this query" handoff (K6c) is unaffected either way — it calls `Wizard.Reset()` itself directly before navigating straight to `builder/configure`, bypassing `Builder.razor`'s `OnInitializedAsync` entirely.

Docs: `docs/ui-handoff.md`'s Builder · step 1 row updated to describe the new `?resume=true`-gated behavior instead of the stale "without `?edit`, resets" description.

## D70 — Pro package licensing: offline signed-key runtime validation (design, 2026-07-22)

**Reverses D30's "no runtime enforcement" posture.** D29/D30 shipped the Pro packages (`NeoReports.Xlsx.Pro`, `NeoReports.Sources.Join.Pro`, `NeoReports.QueryBuilder.Pro`) gated only by distribution (never published to a feed) and by the PolyForm Small Business license *terms*, with no code-level check. The maintainer now wants Pro **publicly published** (so anyone can pull it from NuGet), which removes the distribution gate — a runtime license check becomes the only remaining enforcement mechanism. Requested shape: the website issues a 30-day trial license on request; the key is validated **offline** (no network call at runtime) — chosen specifically because a runtime dependency on a license server conflicts with this codebase's own resilience/unattended-execution philosophy (D6: a job restarts from zero and runs unattended; Polly resilience exists for external *data* sources, not for gating whether the product runs at all). Design discussed and agreed with the maintainer in chat before any code.

### Token format and crypto

A compact two-part token: `base64url(payload JSON)` + `.` + `base64url(signature)` — no JWT library dependency. Payload: licensee (name/email/company — free text, not a technical enforcement key), issued-at, expires-at. **No product/edition field** — confirmed with the maintainer during Q1's implementation (2026-07-22): NeoReports Pro is **one bundle**, a single license unlocks all three Pro packages (`NeoReports.Xlsx.Pro`, `NeoReports.Sources.Join.Pro`, `NeoReports.QueryBuilder.Pro`) together, not sold/licensed per-package. No revocation list (a denylist needs an online check, which would break the offline requirement — an accepted, honest gap for v1; a compromised/leaked key is a legal matter under the license terms, same posture as D29's non-runtime-enforced predecessor). Signed with **ECDsa (P-256)** via `System.Security.Cryptography` — built into the BCL on every target framework this repo multi-targets (net8/net9), so this ships with **no new CPM dependency**, matching the "hand-roll the small thing" precedent set by every Epic P source's own auth/crypto code. The private signing key lives only on the maintainer's license-issuing side (a website/service outside this repo); the public verification key is embedded as a constant in the validation package below.

### Where the check lives — new shared `NeoReports.Licensing` package, MIT

A new package (not `Abstractions`, not `Core` — those are consumed by every OSS-only user who never touches Pro) holds the token parser + ECDsa verification + expiry check. All three Pro packages take a project/package reference to it, avoiding tripling the validation logic — the same "promote shared plumbing once 2+ consumers need it" pattern this repo already applies (`Http.Common`, `Files.Common`), applied proactively here since all three known Pro packages need identical logic from day one. Licensed **MIT**, not Pro: the verification code being publicly auditable (no hidden phone-home, no obfuscated check) is a trust signal worth more than the marginal secrecy it would otherwise buy — enforcement comes from the Pro packages refusing to operate without a valid key, not from hiding how the key is checked.

### Supplying the key at runtime

Follows the existing `${VAR}`-style convention this codebase already uses for every other secret (connection strings, API keys, OAuth2 client secrets): an environment variable (e.g. `NEOREPORTS_LICENSE_KEY`) or an explicit value passed to a DI registration call (`AddNeoReportsProLicense(key)`), checked once at startup/registration time — not lazily on first Pro-feature use, so a misconfigured license fails immediately and loudly rather than surfacing mid-run.

### Failure behavior: hard-fail, no degraded mode

Missing, malformed, unverifiable, or expired license → throws at startup with a clear, actionable message (what's wrong + where to renew). No silent degrade to a reduced-functionality mode — consistent with D36's "never ship mock/fake/degraded behavior presented as if it were real." A grace period past expiry was considered and explicitly rejected for v1: it adds leniency logic disproportionate to a 30-day trial's stakes; revisit only if real customer feedback asks for it.

### Accepted honest gaps (D36 posture)

1. **Clock trust.** Offline validation has no way to confirm real wall-clock time — a customer can roll back their system clock to extend a trial. Not mitigated: for a 30-day trial the abuse ceiling is low, and any mitigation (persisted last-seen-timestamp, monotonic clock cross-checks) adds real complexity for marginal deterrence. Documented here rather than silently ignored.
2. **No machine/hardware binding.** The license is not tied to a machine id or hardware fingerprint — deliberately, since that breaks in containers/autoscaling/cloud redeploys where "the machine" is ephemeral, and this product's audience (small business / self-hosted) doesn't warrant enterprise-grade DRM friction. Enforcement of "one license per organization" stays a licensing-terms matter, same posture as D29.
3. **No revocation.** See "Token format" above.

### Out of scope for this decision

The website/service that *issues* trial licenses (signs tokens with the private key) is not part of this repo and is not designed here — only the verification side (`NeoReports.Licensing`) and its consumption by the three Pro packages are in scope. Implementation (the `NeoReports.Licensing` package, wiring into each Pro package, tests) follows in a subsequent PR.

### Q1 implementation — code review findings, all fixed

Four parallel finder angles caught 3 real issues plus one already-checked-and-cleared item, on top of the product/edition-field question resolved with the maintainer above:

1. **Duplicated embedded-public-key import logic** in two call sites — extracted into one internal `ProLicense.ImportEmbeddedPublicKey()` helper, also wrapped in a try/catch converting a corrupted constant into the documented `NeoReportsLicenseException` instead of a raw BCL crypto/format exception (a real risk during a future key-rotation release, per the constant's own "replace this constant in a new release" instruction).
2. **Culture-dependent date formatting.** The expired-license message used an implicit-culture date format — under e.g. Thai (`th-TH`) or Persian (`fa-IR`) culture this renders in a non-Gregorian calendar year (`2569` for the Buddhist calendar instead of `2026`). Fixed to `CultureInfo.InvariantCulture` everywhere a date is formatted into a message.
3. **A license key with surrounding whitespace failed as "malformed."** A key sourced from a mounted file (e.g. a Kubernetes Secret) commonly carries a trailing newline; `Convert.FromBase64String` isn't whitespace-tolerant. Fixed: `LicenseValidator.Validate` trims the key before parsing.
4. **`Licensee` could silently deserialize as `null`** despite the record declaring it non-nullable, if a validly-signed payload ever omitted the field (a bug in the maintainer's own future issuing tooling, not reachable by a tampered key since the signature check runs first). Fixed: `Validate` now rejects a missing/blank `Licensee` as a malformed payload instead of returning a token that violates its own type's nullability contract.
5. **Checked, not a bug**: ECDsa's `SignData`/`VerifyData` both default to the same signature format (IEEE P1363) on sign and verify; `VerifyData` returns `false` (never throws) for malformed-length signatures on both target frameworks; `DateTimeOffset` round-trips exactly (including non-UTC offsets) through `System.Text.Json`'s Web defaults; ECDsa instances are constructed and disposed within the same synchronous call, safe under concurrent DI container builds.

Also fixed, from the reuse/simplification angle: `AddNeoReportsProLicense`'s doc comment claimed a second call "no-ops" once a token is registered, but only `TryAddSingleton` skipped re-registering the *result* — the full signature-verification path still re-ran. Added a real short-circuit (checks whether a `LicenseToken` descriptor already exists before validating at all) so the doc comment is now accurate and redundant Pro-package registrations (e.g. a host referencing more than one of the three Pro packages, each calling this once) don't pay for repeated verification. `NeoReportsLicenseException` gained a `LicenseFailureReason` enum property (`Missing`/`Malformed`/`SignatureInvalid`/`OutOfValidityWindow`) so a caller can branch (e.g. a "start a trial" vs. a "renew" call to action) without matching on `Message` text. The exception message's placeholder renewal contact was generalized to "contact your NeoReports Pro provider" rather than referencing an unverified, not-yet-existing domain/email — inventing one would have been presumptuous. `D29`/`D30`/`D50` below are annotated to point forward to this decision as the one that reverses their "no runtime enforcement" premise. Security review: no findings — the embedded public key is safe to publish by design; the corresponding private key exists only in this implementation session's own scratch output, never committed, and the constant is explicitly marked in `ProLicense.cs` as a placeholder needing rotation through a proper secrets-vault process before any real license is issued.

### Q2 implementation — where enforcement actually sits

Designed as a bounded delegation to a stronger model (per the repo's model policy), then implemented as returned.

**The problem Q2 had to solve.** Each Pro package has two independent public entry paths: DI registration (`AddXlsxWorkbook`, `AddMergeJoinConfigSource`, `AddQueryBuilder`), used by config-driven reports and the web UI host; and a **static fluent API** (`Format.XlsxWorkbook`, `Join.MergeJoin`, `Enrichment.Enrich`, `KeysetSqlGenerator.Generate`) used by **typed code-first reports** — this library's primary documented usage pattern (rule 1, "Typed-only"), which never touches a DI container at all. Gating only the DI path would have left the main usage pattern completely unlicensed, making the whole mechanism close to decorative.

**Shipped: one process-wide gate, both paths gated.** `ProLicenseGate` (new, `NeoReports.Licensing`) holds the validated `LicenseToken` in a single field swapped via `Volatile.Read`/`Write` — the same immutable-snapshot pattern D68's OAuth2 token cache uses, and for the same reason (a benign race just re-validates; a reader can never see a half-written token). `EnsureValidated()` is a single volatile read once the gate is open, so calling it from a method that could run in a loop costs nothing. On a closed gate it falls back to the `NEOREPORTS_LICENSE_KEY` environment variable, and it deliberately **does not cache failure** — a host that fixes the variable recovers without a process restart. `Register(key)` is the code-first counterpart of `AddNeoReportsProLicense(key)` for applications that never build a container.

Gate call sites, chosen to cover every route with the fewest checks: `XlsxWorkbookWriterFactory`'s **constructor** (covers `Format.XlsxWorkbook`, `AddXlsxWorkbook`, and direct construction at once); `Join.MergeJoin` and `Enrichment.Enrich`, **eagerly** — outside the iterator body, so a missing license fails while the report is being *defined* (startup for a code-first app) rather than on the first row read, honoring D70's "not lazily on first Pro-feature use"; `QuerySqlGenerator`'s constructor **and** `KeysetSqlGenerator.Generate` separately, since the latter is a public static type reachable without ever constructing the former. Each DI extension calls `services.AddNeoReportsProLicense()` as its first statement.

**One license state, not two.** Implementing this surfaced a real defect in Q1's DI extension: it validated independently of the gate, so a host that supplied its key through DI still had a *closed* static gate (and vice versa) — exactly the "several separate license states in one process" the shared-gate design existed to prevent. Caught by an existing Pro test failing. Fixed: `AddNeoReportsProLicense` now opens the gate on success, and short-circuits when the gate is already open by publishing the established token into the container.

**Testing.** Each Pro suite gets a `[ModuleInitializer]` seeding the gate through the internal `Accept` seam, so all ~52 pre-existing Pro tests keep passing **unmodified** — they cannot sign a token the embedded key accepts, since the matching private key deliberately never lives in this repo. The enforcement itself (that each gated entry point really does throw) is tested in `NeoReports.Licensing.UnitTests`, the one assembly with no such initializer, in an `xUnit` collection with `DisableParallelization` — it mutates two pieces of process-global state (the environment variable and the gate), and xUnit parallelizes test *classes* by default, so without that the license tests would flake against each other.

**Security posture of the `Accept`/`Reset` seams: accepted, unchanged risk.** They are `internal`, but that is not the real defense — reflection can already write the field, and the entire validator is MIT source anyone may fork or IL-patch. They grant an attacker nothing they did not already have. Enforcement here is a compliance speed bump plus a clear legal line, not DRM — the same posture D70 already takes on clock rollback and revocation.

### Q2's honest capability gaps (D36 posture)

1. **Ungated public plumbing.** `DelegatingStreamingSource`, `EnrichingBatchSource`, and the `QueryModel`/`SqlDialect` records stay constructible without a license: they are genuinely thin — the merge algorithm lives inside `MergeJoin`'s private iterator and the batched-lookup logic inside `Enrichment.Enrich`'s closure, both behind the gate — and smearing checks across every public constructor buys nothing against someone who can recompile MIT-licensed code anyway. **`XlsxWorkbookWriter` was initially on this list and should not have been**: the security review demonstrated a working bypass in ~8 lines of ordinary consumer C# (the writer is public, sealed, self-sufficient from a publicly-constructible `XlsxWorkbookOptions`, and plugs into `SectionedOutputSpec` through a hand-rolled `ISectionedWriterFactory`), so the "needs the gated factory in practice" rationale was simply false for it. Its constructor is now gated too — the lesson being that "low-value plumbing" has to be verified per type, not assumed from the layer it sits in.
2. **Process-wide, not per-container, license state.** One validated license per process; the static path has no container to scope to. Irrelevant for this product's self-hosted audience.
3. **Trivially removable enforcement** (fork, reflection, IL edit) — inherited from D70 and restated here.
4. **Samples 06 and 07 now need a license to *run*.** Both reference Pro projects and predate this decision. They still build without one, so their code stays readable; each README now says so explicitly rather than letting a reader hit an unexplained startup exception. (This is a concrete instance of D50's still-open "may a sample reference a `.Pro` project" question, now with a runtime cost attached.)
5. **The expensive validation happens once; the validity window is re-checked every call.** Review flagged that caching the token outright would let a long-running host outlive its own trial indefinitely, making a 30-day license unbounded in practice — so `EnsureValidated` compares the cached token's window against the clock on every call (`LicenseToken.IsValidAt`, one `DateTimeOffset` comparison) while still verifying a *signature* only once. That keeps D70's "no per-call crypto, no mid-run surprise for a valid license" intent without the hole.

### Q2 security review — two findings, both fixed

1. **A real bypass of the XLSX Pro feature, no reflection required (medium).** `XlsxWorkbookWriter` is public, sealed, and needs nothing from the gated `XlsxWorkbookWriterFactory` beyond a publicly-constructible `XlsxWorkbookOptions`; `SectionedOutputSpec` accepts any `ISectionedWriterFactory`. A customer could therefore reach the entire multi-sheet implementation through a hand-rolled factory in ~8 lines of ordinary consumer C#, with the gate never consulted. Notably, this type had been placed on the accepted-gaps list above on a rationale the review proved false for it specifically. Fixed by gating the writer's own constructor; the gap entry is corrected rather than quietly dropped.
2. **An explicitly-supplied license key could be silently ignored (low).** `AddNeoReportsProLicense`'s short-circuits ran before the `licenseKey` argument was examined, so once any license was established (typically by a Pro package's own no-key call falling through to the environment variable), a host passing its own pinned key got that key silently discarded and never validated — letting whoever controls the process environment substitute the license the application intended to run under. Fixed: only the no-key form short-circuits; an explicit key is always validated and always wins. Regression-tested.

Three further defects came from the other review angles and are also fixed: the "a `LicenseToken` is already registered" short-circuit returned **without** opening the static gate, so a host that validated its own key and registered the token itself would still be refused by every Pro static entry point — with a message telling it to configure the license it had just configured; `JoinConfigSourceProvider` is public and was reachable by direct DI registration, deferring its only check to `Create` (mid-job) instead of startup — its constructor is now gated; and `Enrichment.Enrich` was gated in source but covered by no test at all, so deleting the check left the whole repo green (verified by mutation: the new test fails when the gate line is removed). Documentation gaps closed at the same time: the three Pro package READMEs (packed into the `.nupkg`, so they become the nuget.org landing page at Q3) said nothing about needing a key, `ProLicenseGate.Register` appeared in no README despite being what the exception message tells code-first users to call, and the query-builder "capability unavailable" copy in both the API and the UI blamed a missing package for what can now be a licensing failure.

Checked and cleared: no unvalidated token can reach the gate through any public API (`Accept` has exactly three callers, all downstream of a real signature verification against the *embedded* key); no license key or licensee name can reach an HTTP response body (a license failure is thrown outside `ReportRunner`'s try block, so it never populates `ReportRunResult.Error`, and no message anywhere echoes the key itself); no DoS amplification (an open gate costs one volatile read, and the only repeated-crypto path fails the caller's operation anyway). The `InternalsVisibleTo` seams were assessed concretely: the assemblies are not strong-named, so a friend reference is satisfiable by assembly name alone — but plain reflection against the private field achieves the same with strictly fewer constraints, so strong-naming would raise the bar on one path while leaving an easier one open. Left as is, consistent with D70's "compliance speed bump, not DRM" posture.

### Q3a — the license-issuing tool, and why it was needed before publishing

Q1 and Q2 shipped license *validation* with no way to *issue* a license: `LicenseSigner` existed as a public API that nothing in the repo called, so the maintainer could not actually produce a key without writing code on the spot. `tools/NeoReports.LicenseTool` closes that (`IsPackable=false` — the issuing side belongs with the maintainer and must never travel with the product, but it stays in the solution so CI keeps it compiling):

- `keygen --out <file.pem>` generates the ECDsa P-256 pair, prints the **public** key for embedding in `ProLicense.PublicKeyBase64`, and writes the **private** key to a PEM file. It **refuses to overwrite an existing file** (silently replacing a signing key would orphan every license already issued under it) and **refuses to write anywhere inside a git working tree** — the security review's highest-severity finding was that the README's own example wrote the key into the repo root, one `git add -A` away from publishing it, with no `*.pem` rule in `.gitignore`. Both `.gitignore` and the docs were fixed alongside the guard. On Unix the file is created `0600` by the `open` call itself (`FileStreamOptions.UnixCreateMode`), closing a window the first cut left open: creating then chmod-ing leaves the file momentarily group-readable, and a Unix permission check happens at `open`, not per read, so a local attacker could hold an fd across the change. **On Windows there is no equivalent** — the file inherits its directory's ACL, and the review measured that a file under the repo path is readable by every local user — so the tool prints that caveat and the docs steer to a user-private directory.
- `sign --key <file.pem> --licensee <name> [--days 30] [--from <date>]` issues a key, printing it to stdout and the who/when summary to stderr so piping yields just the key.

It reuses `LicenseToken`/`LicenseSigner` rather than restating the token shape, so an issued license cannot drift from what the shipped validator accepts. Tested through the same PEM export/import round trip the tool performs (`LicenseToolRoundTripTests`), including that rotating the pair really does invalidate previously-issued licenses — the property the tool's own rotation warning promises.

**This tool is a prerequisite for publishing, not a nicety.** Two things must happen before the Pro packages go public, in this order: (1) run `keygen` locally and move the private key into a vault, replacing Q1's placeholder — that placeholder's private half was generated during an implementation chat session, i.e. stored in plaintext with no rotation or audit trail, so shipping it would mean every license ever issued is signed by an already-compromised key; (2) decide the publishing mechanism, since all three Pro projects are `IsPackable=false` with `PackageLicenseExpression` cleared and are therefore skipped by `release.yml`'s solution-wide pack — either extend that workflow or make `pack-pro.yml` push rather than only upload artifacts.

## D71 — Pro packages in samples: yes, in a dedicated demo (resolves D50, Epic L1, 2026-07-29)

D50 recorded an open question — may a sample reference a `.Pro` project? — as a distribution/business call rather than a technical one, and left it for the maintainer. **Answered (2026-07-29): yes**, as a new sample that reuses the most complete existing Aspire demo and adds the Pro packages on top, rather than scattering Pro references through the existing samples.

Shipped as `samples/15-aspire-pro-demo`, sample 14's twin plus `NeoReports.Xlsx.Pro`, `NeoReports.Sources.Join.Pro` and `NeoReports.QueryBuilder.Pro`. To make "reuses everything sample 14 has" literally true instead of a copy, everything the two hosts share — connection-string resolution, the ~600 lines of database seeding, the 51 column definitions, the four typed report registrations, the named-source registrations and every non-Pro DI registration — was **extracted into `samples/NeoReports.Samples.AllSourcesShared`**, which both demos reference. Sample 14's `Program.cs` went from 811 lines to 43; sample 15's is 62. `diff` between the two files returns only the Pro `using`s, the Pro registration block, and the header comment plus the AppHost path each one names — i.e. the extraction was carried far enough that the diff *is* the Pro surface, which is the point of having the sample at all. The shared project is deliberately separate from `NeoReports.Samples.Shared`: it pulls in four database drivers plus the UI, jobs and ASP.NET integrations, and every lightweight sample references the latter.

**Consequence, stated rather than discovered:** because Q2 now enforces the license at run time, this sample **builds without a license but refuses to start without one** — the same honest hard-fail every Pro consumer gets (D70/D36). Its README says so up front and points at `tools/NeoReports.LicenseTool`, and points readers who just want the all-sources tour at sample 14, which needs no license. Samples 06 and 07 (which predate all of this and already referenced Pro projects) carry the same note.

The two commercial packages' PolyForm Small Business terms are unchanged by this: a sample referencing the project is the maintainer's own use of their own code, and the packages stay `IsPackable=false`, out of the OSS NuGet release.

## D72 — Page-loop termination: the runner refuses a cursor that does not advance (2026-08-05)

`ReportRunner`'s page loop is `while(true)` driven purely by `BatchResult.HasMore`. It has no page
cap and no notion of progress, so a source that reports more data while handing back the cursor it
was given makes the runner re-issue the identical read **forever**: a job that never completes and
never fails, holding its worker until an operator notices. This is not hypothetical — Facebook
Graph's `paging.cursors.after` echoes the requested cursor on the last page, and the same shape
showed up across the audit in `docs/STATUS-AND-BACKLOG.md` §6.

**Decision (maintainer, 2026-08-05): a non-advancing-cursor guard, and no page cap.** The guard
catches the real defect — a source making no progress — without imposing an artificial ceiling that
a legitimately enormous report could hit. Cost is one string comparison per page.

The run fails with `Failed` and an error naming the batch. The check runs **after** the batch is
written, not before: the rows just read were delivered correctly and it is only the *next* read that
is impossible, so aborting earlier would throw away a good page for no reason.

**Where it belongs.** In the runner, not in each source. A source only ever sees one page at a time
and cannot tell that it is repeating itself, and third-party `IBatchSource<T>` implementations get
the protection for free. GraphQL (D63), Elasticsearch and — since the §6 pass — the generic HTTP
source each guard their own token shape as well; those are cheaper, more specific errors, and this
is the backstop underneath them.

### What this cost, and why it is the right price

The guard turns "a source with more data returns a cursor different from the one it received" from
an unstated assumption into an enforced contract — and **one shipped source violated it**.
`StreamingToBatchSource` keeps its real position in a retained enumerator and only ever reads the
incoming cursor as null-or-not; it emitted the constant `"+"` for every page. Under a naive guard,
**every file-backed source** (CSV, Parquet, XLSX, and the HTTP source's `None` strategy) would have
failed at page 2.

Two ways out. Exempting the sentinel in the runner was rejected: it would couple the runner to
another type's private constant and leave the contract quietly false, so the next adapter written
the same way breaks again. Instead the adapter now emits its page count. The value is opaque and
never read back, so this changes nothing about how streaming works — it just makes the invariant
true everywhere, which is what allows the runner's check to stay a two-line comparison with no
special cases.

`StuckCursorTests` covers both halves, and covers them against each other: reverting the guard fails
the stuck-source tests, and reverting *only* the adapter (keeping the guard) fails the streaming
test. The echoing fake throws past 50 reads on purpose — without the guard it loops forever, and a
hanging test is worse than a failing one, both for CI and for anyone bisecting later.

### D72, continued — the source-level half: never truncate quietly

The runner's guard bounds a source that makes no progress. It cannot help with the opposite failure
direction, which the §6 audit found in four places: a source that **stops early** and reports the
run as `Completed` with rows missing. Nothing downstream can tell that apart from a genuinely
complete run — no exception, no partial-artifact capture, no warning — so it is the worst outcome
the pipeline can produce. All four are resolved the same way: fail, or keep going, but never
silently deliver less than was asked for.

**Elasticsearch partial searches now fail.** A partial search is HTTP **200** with fewer hits than
the shards hold: the cluster sets `timed_out`, or reports failed shards under `_shards`, and returns
what the responsive shards had. Neither field was inspected, so the short page ended pagination.
This matches what GraphQL (D63) already does with a 200 carrying `errors`, and this source's own
"full page with no sort values" guard. It does turn a previously-silent success into a hard failure
— that is the point; a report missing an unknown number of rows is not a success.

**`records.Count == pageSize` is no longer how "is there more?" is decided.** OData's `Skip` and the
HTTP source's `Page`/`Offset` strategies have no server token to follow, so it can only be inferred
— but inferring it from a *full* page is wrong whenever the service caps the page below what was
requested. Dynamics, SAP Gateway and Business Central all clamp `$top`/`limit`, and many REST APIs
silently reduce an over-max value; against any of them the **first** page comes back short and the
run stopped there. These now page until a response comes back **empty**. The cost is one extra
request at the end of a run; the benefit is that this class of truncation is structurally impossible
rather than merely unlikely. (`NextLink` and the cursor strategies are unaffected — they follow a
real token.)

**HubSpot and Airtable clamp the page size instead of failing.** Both cap at 100 while the engine
defaults to 1000, so a source built with defaults failed its very first request until the author
happened to call `.PageSize(100)` — a default configuration that could not work.
**Decision (maintainer, 2026-08-05): clamp.** A report author should not have to know each
provider's ceiling, and a page size is a throughput hint, not a promise about how many rows arrive
at once. Both derive `hasMore` from the server's own continuation token, so clamping only means more
requests — it cannot truncate. (Had they inferred it from a full page, clamping alone would have
been unsafe; that is precisely the bug fixed in the paragraph above.)

### D72, closing item — structured run parameters are refused at the boundary

Array/object parameter values were documented out of scope for v1, but nothing rejected them, and
what happened next depended on which backend ran the job: the sync and in-memory paths handed the
source a `JsonElement` — the very type an ADO provider cannot bind — while Hangfire round-tripped
the bag and handed over raw JSON text. Either way the caller learned about the limit as a driver
error partway through a run, attributed to the source rather than to the request that caused it.

`POST /reports/{name}/run` now answers **400** naming the offending parameter. That makes the
documented limit real and identical on every backend, and moves the failure to the moment the
caller can act on it. Scalars are unaffected — `null` explicitly still binds, since an optional
parameter is a normal thing to send.

The guard is deliberately **not** applied to source property bags, which travel the same
`object?`-valued shape: those are a provider's own configuration surface rather than a value bound
into a query, and nothing in the audit showed them failing this way.

## D73 — S3 key templates: a substituted value may not introduce hierarchy (breaking, 2026-08-05)

`LocalDestination` passes `LocalPathSegment.EnsureSafe` to `PathTemplate.Expand` (the WP2 guard);
`S3Destination` passed **none** — deliberately, since `/` is a legitimate S3 key separator. But that
reasoning covers the author's *template*, not the `{param}` *values*, which arrive in the body of a
report-run request. With a key template like `reports/{tenant}/{name}.{ext}`, a caller posting a
`tenant` containing `/` steers the object into a prefix the template never described: a
**cross-tenant write** anywhere a shared bucket relies on prefix isolation. Harmless in a
single-tenant bucket, which is why it went unnoticed.

**Decision (maintainer, 2026-08-05): reject `/` in substituted values, keep it in template
literals.** The author's hierarchy is untouched; only the values filling tokens are constrained.

**This is breaking** for anyone deliberately passing a hierarchy fragment as a run parameter — that
now fails the upload instead of silently relocating the object. The remedy is to move the hierarchy
into the template, which is where it was always meant to live. Recorded in `CHANGELOG.md`.

The guard is deliberately **narrower than the Local one**. S3 keys are literal: `..` is not
collapsed, and there is no drive-letter or alternate-data-stream syntax, so none of that is a
traversal risk and none of it is rejected — `acme..corp` stays a perfectly good tenant name. The only
thing a value must not do is add separators.

A rejected substitution returns a failed `UploadResult` rather than throwing, so the run fails
through the same path as any other delivery problem — matching `LocalDestination`, and keeping the
`IReportDestination` contract intact. The guard's message names the token and the offending value;
since D72 the runner keeps that detail in the log rather than in the API response.

## D74 — Hangfire jobs run once: `AutomaticRetry(Attempts = 0)` (2026-08-05)

The invoker carried no retry attribute and nothing configured `GlobalJobFilters`, so Hangfire applied
its **default of 10 attempts**. A deterministically failing job — bad credentials, an unreachable
source, a report whose SQL no longer matches the schema — was therefore re-run up to ten times,
re-reading the entire dataset each time and flapping the stored status through
Failed → Running → Failed, so an operator watching `GET /jobs` saw ten failures for one problem.

**Decision (maintainer, 2026-08-05): pin `Attempts = 0`.**

This makes rule 6 true in practice rather than only on paper ("a job is an atomic unit; if it
crashes it restarts from zero"). It also removes a duplicated responsibility: retrying a *transient*
fault is already the pipeline's own job, and it does it far better — Polly retries a single batch in
isolation from its cursor (D6), which is a cheaper and more precise unit than re-running the whole
report. Job-level retry was never the layer that could tell a transient failure from a permanent
one, and it paid full dataset cost either way.

Output integrity was never at risk (temp-dir staging is idempotent), so this changes cost and
observability, not correctness.

A host that genuinely wants job-level retries can still add them through its own `GlobalJobFilters`
or by re-enqueuing; nothing here prevents that. The attribute is on the invoker type, which is where
Hangfire reads it when the job is created, so it covers both the one-shot and the recurring entry
points.

## D75 — `ReportJobStatus.Partial`: a run that skipped batches no longer reports green (2026-08-05)

A run that skipped batches (`SkipBatchAndLog`) produced `ReportRunStatus.CompletedPartial` and the
job layer mapped everything that was not `Failed` onto `ReportJobStatus.Completed`. The output was
therefore missing rows the source held, and the job said it succeeded.

The backlog recorded this as "the skip is visible only in `Stats.SkippedBatches`". **That was wrong,
and the truth is worse:** `SkippedBatches` lives on the runner's `ReportRunResult` and is *not* one
of `JobStats`'s counters, so it never reaches the job record at all. Before this change a caller of
the job API had **no way whatsoever** to distinguish a partial run from a whole one — the status was
the only channel, and it was green.

**Decision (maintainer, 2026-08-05): add `Partial`.** It is the only fix consistent with the rest of
D72's work, which was all about never truncating quietly.

### ABI notes (rule 7)

`ReportJobStatus` lives in the frozen `NeoReports.Abstractions`, so two things were deliberate:

- **Appended at the end**, after `Retrying`. The members carry implicit values, so inserting `Partial`
  next to `Completed` — where it reads best — would have renumbered every member after it and
  silently reinterpreted any status already persisted as an integer.
- **Additive, not breaking at the ABI level**, but a consumer with an exhaustive `switch` over the
  enum now has an unhandled case, and one compiling with warnings-as-errors will see CS8509. Recorded
  in `CHANGELOG.md` for that reason.

`Completed`'s own doc comment used to read "(possibly partial when batches were skipped)" — that
caveat is now a status of its own, so the comment says what it means: every batch was written.

The UI treats `Partial` as terminal (it is) and as *not* a clean success: it counts toward the
denominator of the dashboard's success rate but not the numerator, which is the honest arithmetic. It
renders `warn` + `file-alert` — deliberately not `alert-triangle`, which is `Failed`'s icon and would
read as an outright failure. The dashboard's recent-files list is left to `Completed` only: that row
has no status indicator, and inventing one would mean designing outside the handoff (CLAUDE.md), so a
partial run's artifact is surfaced on the Jobs and report-detail pages instead, where its status is
visible next to it.

## D76 — `TimeProvider` in the in-memory scheduler, so the recurring loop can be tested (2026-08-05)

`InMemoryJobScheduler`'s recurring loop was untestable in practice. Cronos granularity is one minute,
so every assertion about firing behaviour would have cost a wall-clock minute of CI — which is why
`RecurringSchedulingTests` carried a *"verified manually via the live sample"* caveat from D41 onward,
and why the loop's catch-all shipped uncovered in #268 and had to be reverted when SonarCloud's
new-code coverage gate refused it at 57.1%. The gate was right: a permanently-untested error path is
not fixable with a footnote.

**Decision (maintainer, 2026-08-05): inject `TimeProvider` and add
`Microsoft.Extensions.TimeProvider.Testing` to CPM as a test-only dependency.**

`TimeProvider` itself is **BCL** (.NET 8), so the shipped package gains nothing: `DateTime.UtcNow`
becomes `_timeProvider.GetUtcNow()`, `new PeriodicTimer(chunk)` becomes
`new PeriodicTimer(chunk, _timeProvider)`, and `Task.Delay(x, token)` becomes
`Task.Delay(x, _timeProvider, token)`. The constructor parameter is optional and defaults to
`TimeProvider.System`, so `new InMemoryJobScheduler(store, worker)` still compiles.

Hand-rolling a fake was considered and rejected: implementing timer scheduling semantics correctly is
fiddly, and it would mean maintaining test infrastructure Microsoft already ships. The package is in
the `Microsoft.Extensions.*` family already present in CPM.

### What it bought

The catch-all reverted in #268 is back **with a test that fails without it**, and the loop now has
real coverage: it fires when the clock reaches the occurrence, it survives a firing that throws and
fires again, and removal stops it. The D41 caveat is retired.

One test-authoring note worth keeping: a fake clock makes the loop's *waiting* deterministic, but the
firing it releases still runs on the thread pool. Advancing the clock in one jump can land before the
loop has reached its next wait, and the time is then consumed by nothing — the first draft of the
failure test hung on exactly that. The tests advance in steps until the condition holds, with a
ceiling, rather than jumping and hoping.

## D77 — XLSX value fidelity: the UTC instant, and numbers that would round (2026-08-05)

Two items §5 recorded as "representation tradeoffs, deferred". Re-reading the code showed the first
is not a tradeoff at all, and the second only is for the values it actually affects. Both live in
`XlsxCells`, shared by the MIT writer and the Pro workbook writer, so one fix covers both.

### `DateTimeOffset` was written as the wrong instant

`DateCell(dto.DateTime, …)` takes the wall-clock part and **discards the offset**, so
`2026-03-14T08:30:00-03:00` was written as `08:30`. Read back as a plain timestamp that is not "the
offset was lost" — it is an instant three hours wrong. Worse, the **CSV writer keeps the offset**
(it formats through `Convert.ToString`), so the same report exported both ways disagreed by up to
14 hours.

Now `dto.UtcDateTime`. The cell model still has no time zone, but the value it holds is at least
correct, and the two writers now agree on the instant.

### Numbers are checked per value, not assumed

Everything numeric went through `Convert.ToDouble`, so a `long` past 2^53 (a bigint key) or a
`decimal` past double's ~15–17 significant digits was **silently rounded** — an id that came out of
Excel as a different id.

The framing in §5 was "exact value requires text, which loses Excel's numeric sorting/formatting —
a product decision". True, but only for the values that would actually round. Losslessness is
decidable at write time, so the fallback is now per value: a real number cell whenever the double
holds the value exactly (the overwhelmingly common case, Excel semantics intact), text only when it
would otherwise be wrong.

Two bounds worth stating because they are easy to get wrong: the `long` check compares against
±2^53 rather than using `Math.Abs`, which **overflows on `long.MinValue`** — precisely a value this
check exists to catch — and the `decimal` round-trip is wrapped, because near `decimal.MaxValue` the
intermediate double can round above the decimal range and the comparison itself throws.

### Considered and dropped

A one-time warning when a value falls back to text. `XlsxCells` is a static helper called per cell;
threading a logger through it for a diagnostic costs more than it returns, and the written value is
correct either way. Recorded here rather than silently skipped.

### Still deferred

Pre-1900 dates remain unrepresentable: `DateTime.ToOADate` cannot express them, and that is inherent
to the OADate serial Excel uses, not something this layer can decide around.

## D78 — `FailureRate` needs a sample, and a cancelled upload is not a destination error (2026-08-05)

Two §5 items that §5 called "semantics choices". Reading the code closely made the first one look
less like a choice and more like a threshold that never worked.

### `FailureRate` behaved as "abort on the first failure"

`ReportRunner` increments `batches` **and** `totalFailures` before computing
`totalFailures / (double)batches`, so a failure in the very first batch always yields exactly **1.0**
— which trips every `FailureRate` below 1, whatever it was configured to. Later failures are just as
unstable: the second batch failing gives 0.5. Three batches is not a rate.

`FailureRate` now evaluates only once `FailureRateMinimumBatches` (default **10**) batches have been
seen. Aborting early is what `ConsecutiveFailures` and `TotalFailures` are for, and both are
unaffected — a run that should stop immediately still stops immediately, through the threshold that
actually means that.

**ABI note (rule 7):** `FailureRateMinimumBatches` is an **init-only property**, not a fourth
positional parameter on `AbortThresholdConfig`. Adding a parameter would change the record's primary
constructor signature, which is a binary break; a new property is additive. `ThresholdContext` gained
the batch count the same way — an optional constructor parameter, sourced from `BatchFailureContext.PageNumber`,
which the runner increments in lockstep with `batches`.

One existing test (`FailureRate_threshold_aborts_when_the_ratio_is_reached`) encoded the old
behaviour on a three-batch fixture. It now sets the minimum to 3 explicitly, so it keeps measuring
the ratio arithmetic it was written to measure rather than the guard in front of it — that guard has
its own tests. This is the third test in this sweep found asserting a defect as if it were the spec.

### A cancelled upload was reported as a destination failure

Both destinations' `catch (Exception)` swallowed `OperationCanceledException` into
`UploadResult.Fail`, so a deadline firing mid-upload was attributed to S3 or the filesystem, with the
real reason replaced by a provider-shaped message. The run ended `Failed` either way, so this is
attribution accuracy — but attribution is the first thing an operator reads.

Cancellation now rethrows, filtered on **the caller's own token**, exactly as `ReportJobWorker` has
done since #240. An `OperationCanceledException` carrying someone else's token — an SDK's internal
timeout, most commonly — remains a genuine transport failure and is still reported as one. Filtering
on our token rather than rethrowing every OCE is also what leaves the runner's multi-destination loop
free to carry on collecting per-destination results.

## D79 — `SkipBatchAndLog` is refused with more than one output (2026-08-05)

§5 recorded that multi-output batch writes are not atomic and that this contradicts D11. The runner
writes each batch to every output in a sequential loop with no per-batch buffer, so when output *k*
throws, output *k-1* has already appended: a batch the strategy calls "skipped" is **physically
present in one delivered file and missing from another**, and the run's own stats match neither.

§5 proposed "buffer each batch per output, commit all-or-nothing". That cannot be built generally.
Rolling back an appended batch means truncating the output stream to a recorded offset — fine for
CSV, impossible for the XLSX/OpenXML writer, which is a zip package being assembled. A fix that works
for one writer family and silently does not for another is worse than no fix.

**Decision: refuse the combination.** `SkipBatchAndLog` with more than one output throws
`ConfigurationException` at `Build()`. Skipping stays available with a single output, where there is
nothing to disagree with, and multi-output stays available with `AbortReport`. D11's batch-atomicity
promise is kept by never entering the state that would break it.

### A guard I wrote and then deleted

The first cut had two layers: this one, plus a runtime check in `ReportRunner` for a custom
`IFailureStrategy` or the dynamic config path. Auditing reachability before shipping showed the
runtime layer is **dead code**: `CompiledReport`'s constructor is `internal`, so the only way to get
one is `ReportBuilder.Build()`, and the config compiler goes through that same builder. There is no
injection point for a custom strategy today.

Deleted rather than kept "for the future" — an unreachable guard is indistinguishable from a working
one until the day it is needed, and P7c already cost this repo a fully-built, fully-tested
`ISourceRowCounter` that nothing could reach.

## D80 — Row-count reconciliation: advisory, and free (2026-08-05)

§5 recorded that the QueryBuilder allows a **non-unique keyset key**: single-column keyset with
strict `>` drops the tail of a duplicate group that straddles a page boundary. Silently. It is not
statically detectable — the model carries no PK/unique metadata — and no source can observe it,
because a source only ever sees the page it returned.

Reconciliation is the one signal that surfaces it. Progress tracking (D47) already counts the
source's rows before the loop starts, so comparing that against `recordsRead` at the end costs one
subtraction and **needs no new configuration at all**.

That last part changed the shape of this from what was originally proposed. The plan was an opt-in
flag; there is nothing to opt into, because the count is already there whenever progress tracking is
on. No new public API was added.

**Advisory, never fatal.** The count predates the run, so a concurrent insert or delete explains a
difference exactly as well as a defect does; failing a run on it would make a busy table look broken.
It surfaces as a warning log plus a `row-count-mismatch` job event, so it is visible in
`GET /jobs/{id}/events` rather than only in a log nobody greps.

A **strict mode** that fails the run was considered and left out. A count taken before the run cannot
be the authority for failing one, and adding public surface for a check that is approximate by
construction is how a knob becomes a support burden.

Only a `Completed` run is reconciled. `CompletedPartial` skipped batches on purpose — reading fewer
rows is the expected outcome and is already reported as `Partial` (D75), so flagging it again would
dress a known cause as an unknown one. `Failed` and `Cancelled` runs legitimately read less.

The non-unique key itself remains undetectable up front; this makes its *consequence* observable,
which is the most that can be done without the model change §5 describes.

## D81 — Zoned temporal keys get a zoned cast (2026-08-08)

§5 recorded that a PostgreSQL/Redshift `timestamptz` keyset key can shift its page boundary under a
non-UTC session, and deferred it: the fix supposedly needed `ColumnType` to carry the
with/without-time-zone distinction, a change to the frozen `Abstractions` ABI and therefore a
next-major item.

**That premise was wrong, and re-reading the model is what unblocked this.** `ColumnType` already
carries the distinction. `ReportColumns.InferColumnType` maps `DateTime` → `ColumnType.DateTime` and
`DateTimeOffset` → `ColumnType.Timestamp`; the two members are the naive and the offset-aware
temporal type, and have been since they were introduced. Two things ignored it:

1. `SqlDialect.PostgresCast` and `AdoFilterTranslator.PostgresCast` both matched
   `DateTime or Timestamp` and emitted `::timestamp` for either.
2. `SqlTypeMap.ToColumnType` never produced `Timestamp` at all — every name containing `timestamp`
   or `datetime` mapped to `DateTime`, so the catalog path could not express a zoned column even
   though the enum could.

So the change is a classification fix plus two cast arms. **No ABI change, no new enum member.** The
doc comments on both members now state the distinction, because leaving it implicit is what let two
independent call sites collapse it.

### What the bug actually did, measured

Against real containers, not reasoned about:

- **PostgreSQL** (session `America/Sao_Paulo`): the cursor for a `timestamptz` key is the codec's
  round-trip of a `Utc`-kind `DateTime`, so it ends in `Z`. `::timestamp` discards that, and Postgres
  re-reads the now-naive value in the session zone — moving the boundary by the session offset. Of
  three rows past the cursor, **one silently disappeared**. The run reports `Completed`.
- **Oracle**: not silent at all. The driver returns `TIMESTAMP WITH TIME ZONE` as a `DateTimeOffset`,
  so the cursor ends in `+00:00`, and the naive `TO_TIMESTAMP(…FF7)` model has no element to consume
  it — **ORA-01830 on page 2**. The same failure shape as the ORA-01858 crash fixed for plain
  `TIMESTAMP` keys, reached through a different type. `TO_TIMESTAMP_TZ(…FF7TZH:TZM)` parses it.

Two databases, two very different symptoms, one cause. The Postgres half was the one on record; the
Oracle half was found only because the fix was verified against a container instead of reasoned about.

### The substring trap

`IsZoned` cannot simply test for `"with time zone"`: PostgreSQL's `information_schema` reports the
naive type as **`timestamp without time zone`**, which contains that string. A predicate that missed
this would have inverted the fix — sending every naive column down the zoned path — while looking
correct. `"without time zone"` is therefore excluded first, and a test pins both spellings.

Two neighbours are deliberately left where they were, each verified:

- **`time with time zone`** (`timetz`) matches the same substring but is `ColumnType.Time`; casting it
  to `timestamptz` would make the comparison fail outright. The classifier only asks `IsZoned` about
  names that are already timestamp-ish. `timetz` has the *same* zone-dropping bug against `::time`
  (reproduced: 1 row found vs 0), but fixing it needs a `Time`/`TimeTz` split — a new public enum
  member for a type PostgreSQL itself discourages, with no analogue in the other four dialects, and
  whose cursor form is a `DateTimeOffset` that does not round-trip into a `timetz` literal anyway.
  Recorded in the backlog rather than guessed at.
- **Oracle `TIMESTAMP WITH LOCAL TIME ZONE`** is normalized to the session zone by the driver and
  comes back as a plain naive `DateTime`, so the naive model is the correct one for it. It falls out
  of the predicate for the incidental reason that `"with local time zone"` does not contain
  `"with time zone"` — incidental enough to be worth a test, which exists.

### Filters get the same cast

`AdoFilterTranslator` translates hand-typed preview filter values, where most inputs carry no offset
at all. Those are unaffected: `::timestamptz` reads a zone-less literal in the session zone, which is
exactly what `::timestamp` plus the implicit coercion already did (verified both ways). The behaviour
differs only when the typed value *states* an offset — and then honouring it is the answer the user
asked for.

### Verification

`KeysetSqlGeneratorTests` pins which cast is emitted for each type-name spelling; the Postgres and
Oracle integration suites pin that those casts are correct against the real engines under a non-UTC
session. The Postgres suite keeps the old zone-less cast as a **live control** that asserts rows *are*
lost — without it, a suite that never sets a session zone would pass with the bug still in place,
since under UTC both casts agree. Same split as the Oracle `TO_TIMESTAMP` fix: unit tests for what we
emit, containers for whether the database agrees.

## D82 — XLSX dates below 1900-03-01 are written as text (2026-08-08)

§5 recorded "XLSX pre-1900 dates" as inherent to the OADate serial and not decidable in that layer.
Measuring it moved the boundary and turned one entry into four distinct defects.

### The boundary is 1900-03-01, not 1900

Excel's 1900 date system contains a phantom **1900-02-29** — a deliberate Lotus 1-2-3 compatibility
bug — which .NET's calendar does not have. The OLE epoch (`1899-12-30`, two days before Excel's
serial 1) is chosen so the two off-by-ones cancel, but they only cancel *after* the phantom day.
Verified against the framework rather than assumed:

| date | `ToOADate()` | Excel serial | agree? |
|---|---|---|---|
| `2020-01-01` | 43831 | 43831 | yes |
| `1900-03-01` | 61 | 61 | yes — the first day they do |
| `1900-02-28` | 60 | 59 | **no**, lands on the phantom day |
| `1900-01-01` | 2 | 1 | **no**, one day late |

So the sixty days from `1900-01-01` to `1900-02-28` were not "unrepresentable"; they were written as
a **different, plausible date**. That is the quietest failure of the four and was not on record at all.

### Four failures, one guard

1. `1900-01-01 .. 1900-02-28` — off by one day (above).
2. Anything before `1899-12-30` — **negative** serial, which Excel cannot render as a date.
3. `DateTime.MinValue` — the framework special-cases it to return `0.0` instead of throwing, so an
   unset or default date was written as `1899-12-30`: a real-looking date the report never held. A
   guard written only against the exception would have missed exactly this one.
4. A year below 100 — `ToOADate` throws `OverflowException`, which aborted the **entire workbook**
   over a single cell. The same shape as the illegal-control-character bug fixed earlier, and the
   reason a range check is better than a `try`/`catch` here: the check also covers 1-3, which do not
   throw at all.

A single `value < 1900-03-01` test covers all four, which is why it is a comparison and not exception
handling.

### Text, not a corrected serial

Writing `OADate - 1` for the Jan/Feb 1900 window would make Excel display the right date, and was
rejected: it stores a serial that means a different date to anything reading the file with OLE
semantics, and Excel's own date arithmetic across the phantom day is inconsistent regardless. An
invariant `"O"` round-trip string is unambiguous in every reader, at the cost of Excel's date
formatting for that cell — the same per-value trade D77 makes for numbers that cannot be represented
exactly, taken for the same reason: only pay it where the alternative is wrong.

The guard lives in `XlsxCells.DateCell`, which the MIT and Pro writers share, and covers `DateTime`,
`DateOnly` and `DateTimeOffset` (whose UTC conversion can itself cross the boundary) through the one
call site each already routes to.

**Verified failing without the fix**: all six new cases fail when the threshold is neutralized, while
the three above-boundary cases keep passing — so they are not vacuous. Neutralizing it with
`if (false)` does not compile (CS0162 under warnings-as-errors), so the revert was done by moving the
threshold, which keeps the branch reachable.

## D83 — Pro packages published to nuget.org, signing key rotated (2026-08-08)

Completes Epic Q3. Three things had to land together, and could not land apart.

### The key

`ProLicense.PublicKeyBase64` now holds a production key the maintainer generated locally on
2026-08-08, with the private half going straight into a vault. It replaces the placeholder Q1 shipped,
whose private half had been generated inside an implementation chat session — plaintext, no rotation,
no audit trail. That placeholder never signed a customer license, which is the only reason this was
cheap: **rotating after the first license is issued invalidates every license already out there**,
because validation is offline and has no revocation list (D70's accepted gap). A rotation is a
breaking release, not maintenance.

The supplied key was validated before it went in: distinct from the placeholder, imports as ECDsa
P-256, and carries no private half (91-byte SubjectPublicKeyInfo).

### Publication reverses D30, which D70 had already superseded on paper

D29/D30 gated Pro by **distribution** — `IsPackable=false`, never pushed to a feed, packed only as a
CI build artifact by `pack-pro.yml`. D70 replaced that with a runtime license check precisely so the
packages could go public. The code half of that landed in Q1/Q2; the packaging half never did, so the
repo sat in a contradictory state: enforcement built for a public package, on a package nothing could
publish.

The three Pro projects are now `IsPackable=true` and are picked up by `release.yml`'s solution-wide
`dotnet pack` / `nuget push`. Verified by actually packing the solution rather than assuming: all
three produce packages, and each carries its own PolyForm `LICENSE.txt`
(`<license type="file">`) instead of the repo-wide MIT expression — the failure that would otherwise
be both silent and legally wrong.

`pack-pro.yml` is **deleted**. Its entire purpose was D30's artifacts-only stance; keeping it would
have meant a redundant job on every tag whose header comment ("nothing is published to nuget.org")
had become actively false.

### Why this had to be one PR

Splitting it looked reasonable and was the dangerous option. `release.yml` packs the whole solution,
so **flipping `IsPackable` alone arms any `v*` tag to publish the compromised placeholder** — and
NuGet versions are immutable, so that could not be taken back. The obvious safety net (a test
asserting the constant is not the placeholder) cannot merge on its own either: it is red until the key
is swapped. So: key, flags, guard, together or not at all.

### The guard

`ProLicenseTests.The_embedded_public_key_is_not_the_burned_placeholder` fails if the placeholder ever
returns via a revert, a bad merge resolution, or a copy-paste from an old branch. The burned key is
written out in the test in the clear — it is worthless as a secret, and the only thing it can still do
is come back by accident.

This is a gate rather than a comment because **there is no longer a human between a `v*` tag and a
public push**. Verified by putting the placeholder back: exactly that test fails, the other 48 pass.

### Not a CI secret

The private key stays out of GitHub entirely — not a secret, not a variable. The release pipeline only
*publishes* packages; signing a license is a manual act by the maintainer against the vault copy. A
signing key in CI would let anyone who can run a workflow mint permanent licenses, and offline
validation means those could never be revoked. The only secret the release needs remains
`NUGET_API_KEY`.

## D84 — `verify`: proving the key pair before a release (2026-08-08)

D83 added a guard that the embedded key is not the burned placeholder. That catches a *revert*. It
does not catch the other way of getting the key wrong, which is at least as likely on a rotation:
committing a public key that belongs to a **different pair** than the private key in the vault — a
second `keygen` run, a copy from the wrong terminal scrollback.

Nothing detected that. It compiles, packs, passes CI, and publishes. The failure surfaces later, all
at once, as every license the maintainer issues being rejected by every customer — and since D83 a
`v*` tag publishes with no human step and NuGet versions are immutable.

The tool had `keygen` and `sign` but no way to exercise both halves together. `verify --license <key>`
runs `ProLicense.Validate` against the key **embedded in that build** — deliberately the same code
path a customer's process takes, so what it proves is what they will experience. The documented
pre-release ritual is: sign a throwaway one-day license with the vaulted key, verify it, expect
`VALID`.

It takes the license key, never a `.pem`: verification needs only the public half, so there is no
reason for the command to be able to read a private key at all.

**The test pins that it can fail.** A `verify` that reports success regardless would be worse than not
having one — it would launder the exact mistake it exists to catch into a green tick. A freshly
generated pair stands in for a mismatched one, since it is by construction not the embedded pair.
Verified by making `verify` always return 0 — that test, and only that test, fails.

**And the success path is executed too**, which the first cut of this got wrong. It shipped with the
`VALID` branch untested and the gap rationalized as inherent ("the positive path needs the vaulted
key, so CI cannot run it"). The coverage gate rejected that, correctly: the branch the maintainer
depends on before every release would have gone out having never run once, so a null licensee or a bad
format string in it would surface on the single occasion it matters. The body moved behind an internal
seam taking an explicit verifying key — `null` for every call the CLI makes, meaning the embedded key
— so a test can drive the same code with a generated pair.

A `--public-key` **flag** was considered for this and rejected: it would let the pre-release check be
run against the key it was just handed, which checks nothing, and the footgun would sit on the command
whose entire purpose is catching that class of mistake. An internal seam has the same testing benefit
with none of that surface.

## D85 — Consumer smoke test, and why the samples were not converted (2026-08-08)

With the Pro packages published (D83), the maintainer asked for a sample that *installs* them rather
than referencing the projects. The obvious move — point `samples/15-aspire-pro-demo` at the published
packages — was investigated and rejected.

**The samples are the repo's compile-time canary.** Break a public API in `Core` and samples 14 and 15
stop building immediately, before anything is tagged. On a pinned `PackageReference` they would keep
building happily against the last release and the break would ship. That canary is worth more than a
demonstration of how a customer installs, because the second goal has a cheaper answer that proves
strictly more: exercise the artifact **on nuget.org**, not a local build of it.

It also could not have been done by halves. `samples/15` reaches the NeoReports projects through
`AllSourcesShared`, which sample 14 uses too, so converting only the Pro references would have put a
*package* `NeoReports.Core` and a *project* `NeoReports.Core` in one build (NU1605). Converting the
shared project would have dragged sample 14 along — a sample nobody asked to change.

### What was built instead

`tools/consumer-smoke`: outside `NeoReports.sln`, with a `Directory.Build.props` that does not import
the repo's and an empty `Directory.Packages.props` beside it to stop the walk-up to Central Package
Management. Everything resolves from nuget.org at the versions a customer would type.

Three levels: identity (versions, `2.0.0+<commit>` informational version, the embedded key is the
production one), **enforcement** (all three Pro packages refuse to work unlicensed, via both the static
API and DI), and — only when `NEOREPORTS_LICENSE_KEY` is set — a real sectioned-workbook report whose
output `.xlsx` is opened and inspected for two worksheets.

### What it caught immediately

Writing it surfaced three things about consuming 2.0.0 that no in-repo build could have:

1. `NeoReports.Core 2.0.0` requires `Microsoft.Extensions.*` **10.0.10**; a consumer still on 9.x gets
   a hard `NU1605`.
2. `Core` resolves `ILoggerFactory` from DI but only depends on `Logging.Abstractions`, so the consumer
   must bring a logging implementation or the provider throws at `GetRequiredService`.
3. PowerShell's `>` redirect writes UTF-16, so a license key captured that way arrives with embedded
   NULs and is reported as *malformed* — a wrong-encoding file masquerading as a bad signature.

### An assertion that could not exist

The harness first tried to prove "these came from packages, not projects" by inspecting
`Assembly.Location`. That check can never pass: the SDK copies package assemblies into `bin/`, so the
path is identical either way. **The package-not-project guarantee is structural, not testable** — a
single added `ProjectReference` would defeat the harness with every check still green. Recorded in the
`.csproj` and README as the actual guard, rather than papered over with an assertion that looks like
protection and is not.

### A broken test, not a broken product

The enforcement checks initially failed whenever a license *was* available, because `ProLicenseGate`
falls back to `NEOREPORTS_LICENSE_KEY` by design: with a key exported, the Pro calls correctly
succeed. "No license registered" is not the state "no license available". The variable is now cleared
around those checks and restored after, which keeps them meaningful in both modes instead of skipping
them in the mode where a regression would be most expensive.

---

## D86 — Editing a report: the secrets round-trip D33 deferred (2026-08-09)

**Reported by the maintainer:** opening a report's *Edit* in the Builder produced a form that was
blank for almost everything the report actually reads from — source type, query, key column,
connection, source properties, destination path — so "edit" meant "retype the report from memory".

The cause was not a UI bug. `GET /reports/{name}` deliberately exposes none of that: **D33(c)** ruled
that GET responses never echo property bags, because a bag may hold a secret, and **D33(f)** deferred
report editing outright for exactly that reason — *"needs a secrets round-trip story, future ADR"*.
The Edit button shipped later against the only endpoint available, which could offer nothing better
than a blank form and two banners apologising for it. This is that future ADR.

### The round-trip

`GET /reports/{name}/config` returns the **stored** document — the one `IReportConfigStore` already
persists with `${VAR}` placeholders unresolved — with credential-bearing values replaced by the
reserved sentinel `${neoreports:redacted}`. `PUT /reports/{name}` swaps the sentinel back for the
stored value. So an editor can round-trip a property it was never allowed to see, and changing a page
size no longer costs the user a connection string.

What is *not* redacted: a `${VAR}` placeholder (the secret is in the environment, not the document —
that is the entire point of D33(d)), and any non-string value. What is: any string under a key whose
name contains one of a list of credential fragments (`password`, `secret`, `token`, `apikey`,
`connectionstring`, `auth`, …), plus — value-based, independent of the key name — any URL carrying
userinfo, because `https://user:pass@host` is a credential under a key as innocent as `url`.

The fragment list **over-matches on purpose**: `oauth2TokenEndpoint` contains "token" and gets hidden
too. A denylist that fails open ships a literal secret the first time a key name is not on it; this
one fails closed, and because `Restore` puts the value back untouched, over-matching costs only
visibility, never correctness.

**The walk is recursive, and that was not the first attempt.** A property-bag value is not always a
scalar: an HTTP source declares `headers` as an object (`Authorization` lives in there) and a
merge-join source nests whole child sources, each with its own `properties` and connection string.
The first implementation stopped at the top level and handed both back in plaintext — caught by
`/code-review`, not by any test, because every test used a flat bag. A key whose *name* matches a
fragment now hides its entire subtree rather than being descended into: `"credentials": {…}` is a
credential whatever its inner keys are called, and guessing at them is the fail-open behaviour the
list exists to avoid.

**The placeholder carries the address it came from** — `${neoreports:redacted:destinations[1]}` —
so restoring never has to work out which stored section an incoming one corresponds to. Two earlier
designs did work it out, and both were wrong. Pairing by section id alone put one S3 bucket's access
key into another. Pairing by id-then-occurrence fixed that case and broke a different one: changing
an earlier section's type shifts the count, so the *next* same-typed section inherits the previous
one's secret — same silent wire-crossing, one trigger further along. There is no reliable identity to
pair on, because a report may legitimately declare two destinations of the same type to different
buckets, so the address is carried rather than inferred. The source is a singleton and needs none.

That also removes a second defect from the occurrence design: it filtered the incoming sections to
those carrying a property bag before counting, but not the stored ones, so a stored section without a
bag made an otherwise untouched edit fail with a confusing 400. The address is the raw array index,
which nothing can shift.

The sentinel sits deliberately **outside** `ReportConfigEnvironment`'s `${NAME}` grammar (a colon is
not legal in an environment variable name), so it can never be resolved as a variable lookup. It is
rejected in three places: `POST /reports` (nothing to restore from), `Restore` itself (a sentinel with
no stored counterpart), and `ReportConfigEnvironment.Substitute`. The last one is the one that
matters: **a test proved the sentinel would otherwise sail through substitution as an ordinary string**
and a report would go live with the literal `${neoreports:redacted}` as its connection string. The
endpoint guards are the first line; the substitution guard is the one that does not depend on any
particular caller remembering.

### PUT, not delete-then-create

Editing was previously validate → `DELETE` → `POST`, driven from the browser. That fails in the worst
possible direction: the replacement is rejected *after* the original is already gone, and the user is
left with no report at all — a case the old code could only apologise for in an error message.
`PUT /reports/{name}` compiles the replacement before touching anything, so a rejected edit changes
nothing. `IMutableReportRegistry` gains `Replace` (a default interface method for compatibility;
`ReportRegistry` overrides it with a single `ConcurrentDictionary` assignment) so the report never
briefly resolves to nothing. A schedule **override** (`PUT .../schedule`) survives an edit and stays
effective — editing a definition is not the same act as changing when it runs. Renaming stays out:
the document's name must match the route.

`IReportConfigStore` gains `TryGetAsync` — reading one report's document previously meant reading
every stored document.

### The Builder patches the document; it does not regenerate it

`BuilderConfigMapper` now writes the wizard's fields **into** the stored document. The wizard has no
editor for a JsonLogic `filter`, per-output properties or sections, a column's type / display name /
format / culture, or a second destination. Regenerating the document from the form — which is what
"save" used to do — deleted every one of them without a word. In particular **every column would have
been rewritten as an untyped `String`**, a silent downgrade of the report's output from a form that
never showed the type in the first place.

Two consequences of the same rule, both found by running the flow rather than by reading it:

- A generic property row the user did not touch keeps its **original JSON type**. Every row in that
  editor is text, so writing them all back as strings turned `"commandTimeoutSeconds": 90` into
  `"90"` on every edit. Caught by opening the saved file after a browser run, not by a test.
- Stored properties and the kept connection are carried over **only while the source is unchanged**.
  Restoring an old connection into a source nobody pointed it at is the one outcome that would be
  both invisible and wrong.

The wizard edits the **first** destination and passes the rest through, saying so on the Destination
step rather than presenting the report as having exactly one. Matching by index rather than by type
is what makes "change local to s3" mean changing *this* destination.

### What the review pass caught

Four defects survived implementation, self-review and a full green suite, and were found by
`/code-review` afterwards. All four share a shape: the code does something plausible and nothing
throws.

- **Nested bag values escaped redaction** (above) — the only one that was a live secret leak.
- **`Restore` paired sections by first match on a non-unique id** (above).
- **Duplicate output formats were dropped on save.** The Format step is a set of checkboxes, so the
  Builder collapses `outputs` into a `HashSet<string>`; emitting one output per distinct format
  deleted the second of two `csv` outputs, each of which can carry its own writer options. Every
  stored output of a kept format is now kept, and the step says so — the same treatment destinations
  already had.
- **A JSON `null` became `""` on a generic-property round-trip.** A JSON null *is* a null node, so
  "present but null" has to be told apart from "absent" with `HasMember`; comparing against a null
  stored value called the row changed and rewrote it as an empty string on every edit.
- **The `PUT` rollback only caught `IOException`/`UnauthorizedAccessException`.** An
  `IReportConfigStore` is an interface and a custom one can fail with anything; any other exception
  left the registry holding the new definition while the store held the old, so the edit applied
  until the next restart and then silently reverted.

### The second review pass

A `/code-review` run after the security work found five more, and the first two were the same
wire-crossing class arriving through new triggers — which is what moved the design from *pairing* to
*addressing* (above) rather than patching a third heuristic. The other three:

- **An object-valued property could not survive being edited.** An HTTP source's `headers` is an
  object; the generic editor is a one-line text box, so it arrives as JSON text, and editing it wrote
  the whole subtree back as a JSON **string** — breaking the source, and hiding any placeholder inside
  it from every guard, since none of them look inside a larger string. Structured rows are now flagged
  on the way in and parsed back on the way out, and `HoldsRedactedValue` — the last-resort guard, not
  the restore path — became a substring test so an embedded placeholder is still rejected.
- **`accountKey` and friends were in plaintext.** Excluding the bare substring `key` to protect the
  ADO keyset column excluded every key-shaped name with it: `accountKey` (an Azure Storage account),
  `sharedKey`, `licenseKey`. The fragment list now carries `key` and carves out the single exact word.
- **"Destinations: none" was a lie** on a report with more than one: picking None drops the slot the
  wizard edits, and the rest ride along as designed — the Review summary now says so.

### What the security review changed, and what it deliberately did not

A `/security-review` pass over the finished branch produced **no finding above the reporting bar**.
Two candidates were raised and both were filtered out below the confidence threshold; one of them is
worth recording, along with why the other was dropped.

**Closed anyway (rated Low, not a finding): a URL can be the credential.** The value-based rule only
tested `Uri.UserInfo`, so `https://user:pass@host` was hidden but an Azure SAS (`…?sv=…&sig=…`), an
S3/GCS pre-signed URL, or `…?key=<google api key>` was not — and those live under `url`, `baseUrl`
and `instanceUrl`, the required properties of every shipped HTTP-family source and the most
innocuous key names in the document. The reviewer rated this Low because no privilege boundary is
crossed (one `RequireAuthorization` policy covers the whole route group, so anyone who can GET the
config can already PUT it), which is correct. It was fixed regardless: this file's stated contract is
*fail closed, over-match on purpose*, and a credential-by-construction URL escaping it under the
product's only bag-echoing endpoint contradicts that contract. The rule now also redacts an absolute
URI whose query carries a credential-shaped parameter, and `cookie`, `session` and `api-key` joined
the fragment list — `Authorization` always matched via `auth`, but `Cookie` and `X-Api-Key` matched
nothing. `key` is treated as credential-shaped **only as a query parameter**: as a property-bag key
it is the ADO keyset column, and hiding it would blind an editor to its own report's pagination.

A follow-up review pass over that fix raised one non-security consequence worth recording rather
than fixing: `key` and `code` are generic query-parameter names, so `?code=US` or `?key=name` on an
ordinary paging URL now shows as the placeholder too — in the field an editor most wants to see.
Narrowing them (say, only redacting when the value is long enough to be a credential) was considered
and **rejected**: a length guess is exactly the fail-open heuristic this module refuses everywhere
else, and short API keys exist. The cost stays visibility only — `Restore` returns the value
untouched, and the Configure step already explains the placeholder — so the fail-closed side of the
trade wins. `sig` and `sv` are unambiguous and need no such qualification.

**Dropped: "PUT lets a caller reuse a credential they cannot read."** The claim is true — `Restore`
re-attaches a stored secret to whatever document the client sends, so an editor can point a report's
SQL or URL somewhere new while keeping a connection string they never saw. It is not a regression,
because the pre-existing `POST /reports` path already grants exactly this, twice over: a `${VAR}`
placeholder is returned unredacted by design, so anyone can `POST` a new report reusing it with
arbitrary SQL; and a D42 `source.ref` resolves its connection from the registry with overlay-wins
report-local properties, which `GET /sources` already lets a caller enumerate by name. The residual
delta — retargeting a *literal* inline secret — is a strict subset of what the same authenticated
caller could always do. This is the management API's established trust model (D26), recorded here
rather than fixed: **`POST`/`PUT`/`DELETE /reports` are credential-use-equivalent and should be
gated with the authorization one would give the secret itself.**

### Verified end to end

Driven in a browser against `samples/09-web-ui-live`: a report carrying a literal password, a
JsonLogic filter, a typed column with a display name, a numeric source property and two output
formats was opened in the Builder — every step prefilled — its page size changed, and saved. The
stored document came back with the password intact, the filter intact, the column type and display
name intact, `90` still a number, and the new page size applied. Saving with no changes at all
reproduces the original document.
