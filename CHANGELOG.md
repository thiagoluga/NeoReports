# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

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
- Multi-output in a single source pass (e.g. CSV + XLSX read once).
- Samples `01-sql-to-csv-local` and `02-sql-to-xlsx-s3`.

[Unreleased]: https://github.com/thiagoluga/NeoReports/commits/master
