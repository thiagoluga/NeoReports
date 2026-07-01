# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

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

[Unreleased]: https://github.com/thiagoluga/NeoReports/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/thiagoluga/NeoReports/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/thiagoluga/NeoReports/releases/tag/v1.0.0
