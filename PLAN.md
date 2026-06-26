# NeoReports — Implementation Plan (v1)

Small, independent PRs, in order. Each one closes with green tests and closes one acceptance criterion (AC-n) from `docs/MVP-Spec.md`. Check the box when done.

## PR 0 — Repository bootstrap
- [x] `global.json`, `build/Directory.Build.props`, `build/Directory.Packages.props`, `.editorconfig`, `.gitignore`.
- [x] `NeoReports.sln` with solution folders mirroring `src/ tests/ benchmarks/ samples/`.
- [x] Minimal CI (`dotnet build` + `dotnet test` + `dotnet format --verify-no-changes`).
- **Acceptance:** `dotnet build` and `dotnet test` pass on an empty repo.

## PR 1 — NeoReports.Abstractions
- [x] Typed-only types and interfaces per D9 (already skeletoned in `src/NeoReports.Abstractions/`).
- [x] English XML docs on everything public.
- **Acceptance:** compiles multi-target (net8/net9), no dependencies beyond `Logging.Abstractions`.
- **Depends on:** PR 0.

## PR 2 — NeoReports.Core: builder + batch pipeline
- [x] Generic fluent builder `ReportBuilder<TRow>` (`From`/`Filter`/`Columns`/`Column`/`To`/`UploadTo`/`Retry`/`OnFailure`; mapping via `From(source, map)` — see D12).
- [x] `IReportRegistry` + `AddReport<TRow>(...)` (DI).
- [x] `ReportRunner`/pipeline: batch loop, `TypedBatchReader` (adapts streaming → batches), `T → object?[]` projection at the writer edge.
- [x] Polly v8 integration (`ResiliencePipeline`) on the batch read.
- [x] `IFailureStrategy`: `AbortReport`, `SkipBatchAndLog`; threshold (consecutive/total/ratio) via `AbortIf` (see D11).
- **Acceptance:** AC-1, AC-11, AC-12, AC-13, AC-14. Pipeline tested with an in-memory fake source. ✅ 13 green tests.
- **Depends on:** PR 1.

## PR 3 — Sources.Sql + Formats.Csv + Destinations.Local (first end-to-end)
- [x] `Source.Sql(...).Keyset(key, pageSize)` — `IBatchSource<T>`, connection per page, `string?` cursor, parameterized parameters (auto-binds only what the query references).
- [x] `Format.Csv(...)` — non-generic writer (delimiter, encoding, header via `DisplayName`, culture/format formatting, RFC 4180 escaping, CRLF, UTF-8 without BOM).
- [x] `Destination.Local(pathTemplate)` — `{name}/{date[:fmt]}/{ext}` tokens + parameters; atomic publish (temp + move).
- [x] Sample `01-sql-to-csv-local`.
- **Acceptance:** AC-2, AC-4, AC-7. Reference report runs end-to-end to CSV+Local. SQL tested with Testcontainers. ✅ 26 green tests (13 Core + 4 CSV + 6 Local + 3 SQL/E2E).
- **Depends on:** PR 2.

## PR 4 — Formats.Xlsx + Destinations.S3
- [x] `Format.Xlsx(...)` with ClosedXML (sheet, auto-filter, native types; per-column format/date). Memory grows with rows — see D14.
- [x] Multi-output in a single pass (CSV + XLSX reading the source once) — proven in the E2E `Csv_and_xlsx_are_generated_reading_the_source_once`.
- [x] `Destination.S3(bucket, keyTemplate)` — all-or-nothing upload via `PutObject` (no partial object on failure) — see D15.
- [x] Sample `02-sql-to-xlsx-s3`.
- **Acceptance:** AC-5, AC-6, AC-8. ✅ Cumulative green tests: 34 (13 Core + 4 CSV + 6 Local + 4 Xlsx + 3 S3 + 4 SQL/E2E).
- **Depends on:** PR 3.

## PR 5 — Constant memory (validation)
- [x] `NeoReports.Benchmarks` with `MemoryDiagnoser`: synthetic source (lazy, page by page) of 100k and 1M rows → CSV/XLSX.
- [x] No buffering tweak needed: per-row allocation already constant (~446 B/row @100k vs ~461 B/row @1M — linear, not super-linear).
- **Acceptance:** AC-3 (~constant allocation). ✅ proven. CSV is streaming; XLSX grows with volume by ClosedXML design (D14).
- **Depends on:** PR 4.

## PR 6 — Jobs: single worker
- [x] `IJobStore` (InMemory) + `ICheckpointStore` (no-op) + `InMemoryJobScheduler` in the base package `NeoReports.Jobs` (see D18).
- [x] `Jobs.Hangfire` single-server: `HangfireJobScheduler` + invoker reusing `ReportJobWorker`; params via JSON; DI. SQL storage configured by the host (see D19).
- [x] Cooperative cancellation (per-job CTS / Hangfire `CancellationToken`); idempotent restart (per-job temp + upload only at the end, inherited from the pipeline).
- **Acceptance:** AC-15, AC-16; status `queued→running→completed`. ✅ 16 green tests.
- **Depends on:** PR 2.

## PR 7 — Integrations.AspNetCore: trigger endpoints
- [ ] `MapNeoReports("/api")`: `run` (async/sync), `GET /reports`, `GET /jobs/{id}`, `cancel`, `download`.
- [ ] Validation: sync rejects multi-output (`400`); auth inherited from the host.
- [ ] Sample `03-async-job-hangfire`.
- **Acceptance:** AC-9, AC-10. **Demonstrable MVP.**
- **Depends on:** PR 6, PR 4.

## PR 8 — OSS release polish
- [ ] README, LICENSE (MIT), CHANGELOG, NuGet packaging (symbols/snupkg), per-package README.
- **Acceptance:** `dotnet pack` produces all packages; samples documented.
- **Depends on:** PR 7.

---

## Post-MVP (do not start before validating with users)

Likely order once there's traction: dynamic path (config + JsonLogic) → **Blazor UI from the Claude Design handoff** → variants/pipeline → multi-worker + mid-job resume → remaining sources/formats/destinations. Reminder: any UI work always starts from the Claude Design deliverables, never from invented design.
