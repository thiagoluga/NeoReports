# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

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

[Unreleased]: https://github.com/thiagoluga/NeoReports/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/thiagoluga/NeoReports/releases/tag/v1.0.0
