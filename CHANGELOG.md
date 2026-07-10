# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

### Added
- `NeoReports.Abstractions` — `AbortThresholdConfig` (`ConsecutiveFailures`/`TotalFailures`/`FailureRate`)
  and a trailing optional `ResilienceConfig.AbortWhen`, letting the dynamic path express
  threshold-based abort escalation as data (ADR D37). `FailureStrategyBuilder` gains a data-based
  `AbortIf(AbortThresholdConfig)` overload (introspectable via the new public `AbortThresholds`,
  alongside the existing predicate overload which stays non-introspectable); `CompiledReport`
  exposes it, `GET /reports/{name}` returns it, and the Builder's "Abort when" switches and the
  Report detail resilience summary are wired to it. Legal only alongside `onFailure: skip-and-log`.
- `NeoReports.Core` — a job event log (ADR D38): `IJobEventStore` (`InMemoryJobEventStore` /
  `FileJobEventStore`, one JSONL file per job) records a closed vocabulary of structured, per-job
  lifecycle events (started/restarted, page progress, retries, skipped batches, finalized outputs,
  uploads, terminal status) with a configurable per-job cap and optional retention. Opt-in via
  `AddJobEvents()`/`AddInMemoryJobEvents()` — a host that never calls either sees zero behavioral
  change. `ResiliencePipelineFactory` gains an optional retry hook used to emit `retry` events.
- `NeoReports.AspNetCore` — `GET /jobs/{id}/events` (ADR D38): lists a job's recorded lifecycle
  events (`type`/`limit`/`offset` filters), `[]` when no event store is registered or the job has
  none yet, 404 for an unknown job. `NeoReports.UI` — the Timeline, Retries, and processing-rate
  sparkline cards return on the job pages, driven entirely by this endpoint (no fabricated
  fallback content, per D36): Timeline on `JobRunning`/`JobCompleted`/`JobFailed`, Retries on
  `JobRunning`/`JobFailed`, the sparkline on `JobRunning`/`JobCompleted`.
- `NeoReports.AspNetCore` — `GET /system/memory` (ADR D39): process-level working set / GC heap /
  GC committed bytes, plus a count of currently running jobs. Deliberately process-wide, never
  per-job — a single worker process runs multiple jobs, so "memory used by this job" can't be
  measured honestly; run a job alone and watch this screen to estimate its footprint. One reading
  per request, no background sampling or time series. `NeoReports.UI` — a new Memory page
  (`/system/memory`, linked from the top nav) with auto-refresh and a running-jobs table composed
  client-side from `GET /jobs?status=Running`.
- `NeoReports.Core` — `IPartialArtifactStore` (ADR D40): when a job fails or is cancelled
  mid-run, the runner best-effort captures whatever was already written into a dedicated store —
  never at the report's real configured destinations, protecting the all-or-nothing publish
  guarantee (D2/D15). Files are renamed `{name}.partial.{ext}`. Opt-in via
  `AddPartialArtifacts()`. `NeoReports.AspNetCore` — `GET /jobs/{id}/partial-artifacts` and its
  own `/download`, completely separate from the completed-artifacts surface. `NeoReports.UI` —
  the JobFailed page's "Partial output" card returns, with a warning banner and per-file/zip
  download.

## [1.2.0] - 2026-07-03

Two additive feature sets, both SemVer-minor — v1/v1.1 code is unchanged:

- **Web UI** (`NeoReports.UI`): a Blazor Server admin UI — dashboard, reports, a 5-step report
  builder, job monitoring, sources — mountable in any ASP.NET Core host.
- **Dynamic report registration** (Epic D, ADR D33): register, validate, and delete reports at
  runtime over HTTP, backing the UI's Builder; plus the read endpoints (job list, report detail,
  job artifacts) the UI needs.

### Added
- `NeoReports.UI` — new Razor Class Library (not yet packed to NuGet, `IsPackable=false`).
  `AddNeoReportsUI()` + `UseNeoReportsUI("<base path>")` mount it under a configurable base path
  (default `/neoreports`) without touching the host's own routes (D32). Pure design-system CSS
  (Geist/Geist Mono, Tabler icons, no MudBlazor), self-hosted fonts/icons. `INeoReportsApiClient`
  talks to the engine over HTTP only — the UI has no compile-time dependency on the engine
  assemblies.
- `NeoReports.Core` — `IMutableReportRegistry` (`Register`/`Unregister`), `IReportConfigStore` +
  `FileReportConfigStore` (one JSON per dynamic report, rehydrated at startup),
  `ReportConfigEnvironment` (`${VAR}` connection-string placeholders resolved at compile time),
  `AddDynamicReports()`.
- `NeoReports.AspNetCore` — `POST /reports` (register a `ReportConfig` document), `POST
  /reports/validate` (dry-run compile), `DELETE /reports/{name}` (config-origin only), `GET
  /capabilities` (registered source/format/destination type ids), `GET /jobs` (filterable job
  list), `GET /reports/{name}` (full safe report definition — columns, formats, destinations,
  retry/failure policy, origin), `GET /jobs/{id}/artifacts`.
- `NeoReports.Abstractions` — `ReportConfig.Resilience` (`ResilienceConfig`: max attempts,
  backoff shape, base delay, jitter, on-failure strategy), additive, so the dynamic path can
  override the engine's default retry/failure policy per report (D34); omitting it keeps
  today's defaults.
- Samples `08-web-ui` (UI mounted alone, every screen shows its honest empty/"engine
  unreachable" state) and `09-web-ui-live` (UI + engine in one host, a self-contained in-memory
  source — click through Builder → validate → save/run → download a real file → delete, no
  external database or cloud account needed).

### Removed
- The UI no longer ships mock/hardcoded content presented as if it were real (D36). Removed
  outright rather than left as decoration: the Settings screens (Alerts/Authentication/Plugins/
  Retention/Audit — no accounts/RBAC/notification system exists), Pipeline+variants and Source
  explorer (both already flagged fully speculative), the decorative source/destination/format
  catalogs (replaced by pickers driven by `GET /capabilities` where a real one exists), and
  several cards embedded in otherwise-real screens (Permissions, Recent changes, Schedule,
  fabricated job telemetry). Every screen's demo-mode fallback (engine unreachable) now shows an
  honest empty/not-found state instead of a fabricated report or job. See
  `docs/ui-removed-mock-content.md` for the full list and what a real version of each would need.
- Scheduling (recurring runs) was found to be UI-only decoration with no backing anywhere in the
  engine; deferred rather than shipped half-built (D35) — the design sketch is recorded for
  later.

## [1.1.0] - 2026-07-01

Two additive feature sets, both SemVer-minor — v1 code is unchanged:

- The **dynamic (config-driven) path**: define and run reports from JSON with no
  compile-time POCO, reusing the exact v1 pipeline.
- **Multi-view and sectioned outputs**: a single source read can feed several outputs,
  each with its own filter and columns — one file per view, or one file with many
  sections (the hook the commercial multi-sheet XLSX workbook writer plugs into).

### Added
- `NeoReports.Abstractions` — positional `ReportRecord` (`object?[]` + `ReportSchema`) as the
  dynamic row type (not a dictionary); serializer-agnostic config model (`ReportConfig`,
  `SourceConfig`, `ColumnConfig`, `OutputConfig`, `DestinationConfig`); `IReportConfigParser`
  and `IConfigSourceProvider` contracts.
- `NeoReports.Core` — `JsonReportConfigParser` (System.Text.Json) and `ReportConfigCompiler`
  that compile a config into the same runnable report the fluent builder produces (source,
  format and destination resolved from DI by stable id); `ReportColumns.Positional(...)` for
  dynamic columns; `JsonLogicFilter` (a lean JsonLogic evaluator: `var`, `==`/`===`/`!=`/`!==`,
  `>`/`>=`/`<`/`<=`, `and`/`or`/`!`/`!!`, `in`); DI helpers `AddReportFromConfig`,
  `AddReportFromConfigFile` and `AddReportsFromConfigDirectory` (config reports compile lazily
  and run by name through the standard runner and endpoints).
- `NeoReports.Sources.Sql` — `SqlConfigSourceProvider` (`type: "sql"`) and `AddSqlConfigSource()`:
  config-driven SQL Server source materializing `ReportRecord`s by schema-column name, reusing
  the v1 keyset engine.
- Samples `04-dynamic-config-csv` (in-memory) and `05-dynamic-config-sql` (SQL Server).
- `NeoReports.Core` — per-output **views**: `To(spec, view => view.Where(...).Column(...))`
  gives each output its own filter and/or columns, projected per output in a single source
  pass (one file per view); the default single-output path is byte-identical to v1.
- `NeoReports.Core` — **sectioned outputs**: `ToSections(spec, s => s.Section("name", v => ...))`
  writes one file with several sections (each with its own filter/columns) in one pass, via
  the new Core contracts `IReportSectionedWriter` / `ISectionedWriterFactory`.
- `NeoReports.Abstractions` — `OutputConfig.Sections` (`SectionConfig`: name · JsonLogic
  filter · column subset) so the config-driven path can declare multi-section outputs
  (additive).
- `NeoReports.Formats.Xlsx` — public `XlsxCells` helper (typed cell writing shared with
  other XLSX writers).

### Commercial (source-available, not on NuGet)
- `NeoReports.Xlsx.Pro` — multi-sheet XLSX workbook writer (`XlsxWorkbook(...)`,
  `AddXlsxWorkbook()`): one worksheet per section from a single read.
- `NeoReports.Sources.Join.Pro` — multi-source composition: `.Enrich(...)` (batched
  per-page lookup, no N+1), `Join.MergeJoin(...)` (constant-memory keyset merge-join,
  inner + left-outer), and the config-driven `merge-join` source type.
- Both are licensed under **PolyForm Small Business 1.0.0** (free under USD 1M annual
  revenue), are excluded from the NuGet release, and are packed as CI build artifacts
  only (`pack-pro.yml`). Samples `06-multi-sheet-xlsx` and `07-multi-source` demo them.

## [1.0.0] - 2026-06-30

First public release.

### Added
- `NeoReports.Abstractions` — frozen, typed-only public contract (schema, data,
  sources, formats, destinations, resilience, jobs, extensibility, exceptions).
- `NeoReports.Core` — fluent `ReportBuilder<TRow>`, report registry and DI
  (`AddReport<TRow>`), batch pipeline with compiled `T → object?[]` projection at
  the writer edge, Polly v8 resilience, and `IFailureStrategy` (abort /
  skip-and-log) with escalation thresholds.
- `NeoReports.Sources.Sql` — SQL Server source with keyset pagination
  (`Source.Sql(...).Keyset(...)`), opaque string cursor, per-page connections.
- `NeoReports.Formats.Csv` — streaming CSV writer (RFC 4180, culture/format,
  configurable delimiter/encoding/header).
- `NeoReports.Formats.Xlsx` — XLSX writer (ClosedXML) with native types, named
  sheet, and auto-filter.
- `NeoReports.Destinations.Local` — local filesystem destination with path
  templating and atomic publish.
- `NeoReports.Destinations.S3` — Amazon S3 destination with all-or-nothing upload.
- `NeoReports.Jobs` — single-worker job execution: shared `ReportJobWorker`,
  in-memory store and scheduler, no-op checkpoint store; cooperative cancellation
  and idempotent restart.
- `NeoReports.Jobs.Hangfire` — Hangfire single-server job backend.
- `NeoReports.AspNetCore` — Minimal API endpoints to trigger (async/sync), list,
  query, cancel and download reports/jobs; artifact store for download/sync.
- Multi-output in a single source pass (e.g. CSV + XLSX read once); same-extension
  outputs are disambiguated and can be downloaded together as a zip.
- Constant-memory validation via `NeoReports.Benchmarks` (`MemoryDiagnoser`).
- Samples `01-sql-to-csv-local`, `02-sql-to-xlsx-s3`, and `03-async-job-hangfire`.

### Packaging
- All library projects ship as NuGet packages with symbols (`snupkg`),
  source-link, and a per-package README. Tests, samples and benchmarks are not
  packable.

[Unreleased]: https://github.com/thiagoluga/NeoReports/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/thiagoluga/NeoReports/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/thiagoluga/NeoReports/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/thiagoluga/NeoReports/releases/tag/v1.0.0
