# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

### Added
- `NeoReports.Sources.MongoDb` — a MongoDB source (MongoDB.Driver, D44). Standalone design, unlike
  the relational providers: Mongo has no `DbConnection`/`DbDataReader` to share `AdoKeysetSource`
  with, so `MongoDbKeysetSource<T>` implements keyset pagination directly —
  `Find(key > cursor).Sort(key ascending).Limit(pageSize)` — with the cursor round-tripped through
  MongoDB Extended JSON (`BsonValue.ToJson()`) so its exact BSON type, not just its textual form,
  survives the trip. `Source.MongoDb(...)` (typed; no by-registry `MongoDbNamed` entry point in this
  pass), `type: "mongodb"` (dynamic path), `MongoDbSourceHealthCheck` (`{ ping: 1 }`),
  `AddMongoDbConfigSource()`. Reuses `NeoReports.Sources.Common`'s `MemberSelector` and
  `AdoConfigProperties.RequireString`/`OptionalInt` for the parts that are genuinely
  provider-agnostic (expression-tree key-name extraction, property-bag parsing), despite not
  sharing the ADO engine itself. Two MongoDB-specific pitfalls found and fixed: MongoDB.Driver's own
  `BsonClassMap` deserialization silently rebinds a POCO property literally named `Id` to the
  document's `_id` field, breaking typed reads when the report's key field is legitimately named
  `Id` but stored under its own literal name — worked around with a small reflection-based
  `BsonDocumentMaterializer<T>` (mirroring `RecordMaterializer<T>`'s approach) instead of the
  driver's own deserializer; and `BsonDateTime` always stores UTC, so an `Unspecified`/`Local`-kind
  `DateTime` gets silently shifted by the local machine's timezone offset on serialize. Also:
  `MongoClient` (unlike `DbConnection`) is meant to be created once and reused, not per operation —
  it owns its own pooled connections — so `MongoDbKeysetSource<T>` builds one in its constructor and
  shares it across every page, not one per `ReadBatchAsync` call. 8 new integration tests
  (Testcontainers.MongoDb: keyset paging, typed materialization, health check x3, dynamic-config
  E2E + validation + DI registration).
- `NeoReports.Sources.Oracle` — an Oracle source (Oracle.ManagedDataAccess.Core), same shape as
  `NeoReports.Sources.Postgres`/`NeoReports.Sources.MySql` on the shared `NeoReports.Sources.Common`
  ADO.NET engine (D43): `Source.Oracle(...)`/`Source.OracleNamed(...)` (typed), `type: "oracle"`
  (dynamic path), `OracleSourceHealthCheck` (pings with `SELECT 1 FROM DUAL` — Oracle has no
  FROM-less `SELECT`), `AddOracleConfigSource()`. Oracle's ODP.NET needed two new extension points
  on `AdoKeysetSource`/`AdoNamedKeysetSource`/`AdoConfigProperties.CreateAdoConfigSource` (both
  optional, default-backward-compatible for every existing provider): a configurable
  `parameterPrefix` (Oracle bind variables use `:name`, not `@name`) and an optional
  `configureCommand` hook (ODP.NET binds parameters positionally by default — every Oracle command
  sets `OracleCommand.BindByName = true`). Note for report authors: Oracle rejects a handful of
  type-name keywords (notably `DATE`) as a bare column identifier in DDL/DML — alias such columns
  in the SELECT list, e.g. `SELECT ..., SaleDate AS "Date" FROM ...`.
- `NeoReports.Sources.MySql` — a MySQL/MariaDB source (MySqlConnector), same shape as
  `NeoReports.Sources.Postgres` on the shared `NeoReports.Sources.Common` ADO.NET engine (D43):
  `Source.MySql(...)`/`Source.MySqlNamed(...)` (typed), `type: "mysql"` (dynamic path),
  `MySqlSourceHealthCheck`, `AddMySqlConfigSource()`. Unlike Postgres, MySQL needed no
  provider-specific fixes to the shared engine — `AdoKeysetSource`'s existing `DbType.String`
  null-parameter handling and implicit string-to-numeric comparison both worked unmodified.
- `NeoReports.Sources.Postgres` — a PostgreSQL source (Npgsql), matching `NeoReports.Sources.Sql`'s
  shape exactly: `Source.Postgres(...)`/`Source.PostgresNamed(...)` (typed), `type: "postgres"`
  (dynamic path), `PostgresSourceHealthCheck`, `AddPostgresConfigSource()` (D43). Built on a new
  shared package, `NeoReports.Sources.Common`, which extracts `SqlKeysetSource<T>`'s engine into a
  provider-agnostic `AdoKeysetSource<T>` (parametrized by `Func<DbConnection>` instead of a
  hardcoded `SqlConnection`) plus `AdoNamedKeysetSource<T>`, `AdoSourceHealth.PingAsync`, and the
  property-bag/member-selector helpers every relational provider needs — reused by every future
  provider package (MySQL, Oracle) without duplicating the ADO.NET plumbing three more times.
  `NeoReports.Sources.Sql`'s public `SqlKeysetSource<T>` itself stays untouched (already-published
  since v1.2.0 — no need to risk a break for zero behavioral gain), but its internal glue
  (`SqlConfigSourceProvider`'s property parsing, `SqlSourceHealthCheck`'s ping body, the
  member-selector helper) now calls the same shared helpers instead of duplicating them, closing
  the duplication Sonar's quality gate flagged. Along the way, `AddParameter` now sets an explicit `DbType.String`
  on null-valued parameters — Postgres (unlike SQL Server) can't infer a parameter's type from a
  null CLR value alone and rejects the query outright; harmless for every other provider. Note for
  report authors: Postgres doesn't implicitly convert the cursor parameter to the key column's
  type the way SQL Server does — the keyset query needs an explicit `@cursor::type` cast.
- `NeoReports.Abstractions` — `SourceConfig` gains a trailing optional `Ref` (ADR D42): a report's
  source can now reference a registered source definition by name instead of inlining a
  connection. `Type` becomes nullable — required for an inline source, optional (taken from the
  definition) when `Ref` is set. `NeoReports.Core` — the dynamic-path compiler resolves a `Ref`
  at compile time only far enough to fail fast (existence + type-match check); the actual
  properties are **never** baked into the compiled report. At run time, a dedicated source wrapper
  re-resolves the definition through `ISourceRegistry` on every run (definition base, report-local
  properties overlay, `${VAR}` substituted last), so rotating a connection string or deleting the
  source takes effect on the very next run without recompiling anything. `CompiledReport.SourceRef`
  is now populated for ref-based reports. Inline sources (`Ref` omitted) are entirely unaffected.
- `NeoReports.Sources.Sql` — `Source.SqlNamed("sales-db", sql).Keyset(key, pageSize)` (ADR D42
  locked decision 4): a typed-path SQL source that resolves its connection by name through the
  source registry instead of an inline connection string. Typed sources are constructed by static
  entry points inside registration lambdas with no `IServiceProvider`, so `NeoReports.Core` gains
  `INamedSourceResolver` — the Core builder calls `AttachServices` once per run, right before the
  source's first read (the only point in the typed pipeline where a service provider is
  available); `CompiledReport.ReaderFactory`'s (internal) signature grows an `IServiceProvider`
  parameter to carry it through. Registering a `SqlNamed`-based report on a host with no source
  registry configured throws `ConfigurationException` immediately at `AddReport(...)`, checked
  against the service collection before the registry ever needs to be built.
  `CompiledReport.SourceRef` is populated for typed by-name reports exactly like the dynamic
  path's `SourceConfig.Ref`, so they count in `ReferencedByCount` and block source deletion (F3's
  409) the same way. E2E Testcontainers proof: the same registered source name, pointed at two
  different databases in the same container across two runs, redirects rows on the very next run.
- `NeoReports.UI` — sources screens wired to the registry (ADR D42): the Sources page gains a
  "Registered sources" section (grid, real health strip aggregating only actual results, add/edit
  forms with write-only properties and a `${VAR}` hint, "Check now", two-click delete blocked
  while any report references the source). Builder step 1 gains a "Use a registered source"
  picker (`SelectableCard` grid from `GET /sources`) that sets `source.ref` and hides the inline
  connection-string field through steps 2 and 5; `BuilderState` gains `SourceRef`,
  `BuilderConfigMapper`'s `SourceDocument.Type` becomes nullable with a new `Ref`. Dashboard gains
  a "Most referenced sources" card ranked by the real `ReferencedByCount`, hidden entirely on an
  empty registry rather than shown empty (consistent with D9). No fabricated content anywhere —
  the pre-D42 decorative source catalog and its "Most used" card (removed under D36) are now
  legitimately real.
- `NeoReports.AspNetCore` — source registry CRUD and on-demand health endpoints (ADR D42):
  `GET/POST/PUT/DELETE /sources[/{name}]` and `POST /sources/{name}/health`. `SourceView` (the
  read model) never carries `properties` — the D33 property-bag rule at its most literal, since
  that's precisely where secrets live — and its `ReferencedByCount` is always the live count of
  registered reports whose `SourceRef` matches, never a separately tracked number. All endpoints
  degrade gracefully (empty list / 404 / 409 "not supported") when no `ISourceRegistry` is
  configured on the host, matching the rest of the optional-service surface. `DELETE` is blocked
  (409) while any report still references the source. `NeoReports.Core` — `ISourceHealthCheck`
  (provider-type-extensible, resolved by `Type` exactly like `IConfigSourceProvider`) and
  `ISourceHealthCache` (in-memory only, deliberately not persisted, so "never checked" is the
  honest state after a restart); checks run **on-demand only**, never on a background poller (D36).
  `ISourceRegistry` gains `GetAsync` — a raw, non-throwing single-name lookup for metadata display,
  distinct from `ResolveAsync` which substitutes `${VAR}` placeholders and can throw.
  `NeoReports.Sources.Sql` — `SqlSourceHealthCheck` (`type: "sql"`): opens a connection and runs
  `SELECT 1`, bounded by a 10s timeout so the endpoint can never hang on an unreachable server.
- `NeoReports.Core` — the source registry's Core layer (ADR D42): `SourceDefinition` (name/type/
  property bag with `${VAR}` placeholders/description), `ISourceRegistryStore` (file- or
  in-memory-backed, `AddSourceRegistry()`/`AddInMemorySourceRegistry()`) with atomic writes and
  corrupt-file skip-at-load, and `ISourceRegistry` — a thin, cached resolution layer that
  substitutes placeholders **at resolve time** (never baked into a compiled report or cached
  itself), so rotating a connection string takes effect on the next run of every referencing
  report without recompiling anything. `CompiledReport` gains `SourceRef` (populated starting
  with the dynamic path's `SourceConfig.Ref` and the typed path's by-name authoring, both
  upcoming); "used in N reports" for a source will always be this derivable count, never a
  separately tracked number. No HTTP surface yet (Epic F continues with the CRUD/health endpoints
  and UI screens).
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
- `NeoReports.Abstractions` — `ScheduleConfig` (`Cron`, UTC-only) and a trailing optional
  `ReportConfig.Schedule` (ADR D41). `NeoReports.Core` — `ReportBuilder<T>.Schedule(cron)` and
  `CompiledReport.Schedule`, cron validated via Cronos; `IRecurringReportScheduler` (register/
  remove/next-occurrence/list, implemented by `InMemoryJobScheduler` and `HangfireJobScheduler`
  — recurring-job id `neoreports:{name}`, each firing creates its own job record); a uniform,
  file- or in-memory-backed `IScheduleOverrideStore` for runtime overrides on either origin
  (code-first or config-first), with an explicit "unscheduled" tombstone — effective schedule =
  override if present else the declaration, never patching the declaration or config document.
  A startup `ScheduleReconciliationHostedService` (`AddScheduling`/`AddInMemoryScheduling`)
  reconciles every report's effective schedule and removes orphaned registrations for reports no
  longer registered. Overlapping firings run concurrently — no skip-if-running. `NeoReports.AspNetCore`
  — `PUT`/`DELETE {prefix}/reports/{name}/schedule`, `GET /reports/{name}` gains `scheduleCron`/
  `nextRunAt`/`scheduleOverridden`, `GET /capabilities` gains `scheduling`; `POST /reports` with a
  `schedule` field is effective immediately, and rejected (400) without a recurring scheduler
  registered; `DELETE /reports/{name}` removes the recurring registration and any override first.
  `NeoReports.UI` — the Schedule card returns on Report detail and the Builder's Review step: cron
  input with preset chips, "Next run" in the viewer's local time (UTC subline), an "overridden at
  runtime" chip, and Set/Clear actions; honest states when scheduling isn't supported or nothing
  is scheduled.

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
