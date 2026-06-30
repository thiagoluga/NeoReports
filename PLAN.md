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
- [x] `MapNeoReports("/api")` (Minimal API): `run` (async 202+jobId / `?mode=sync` stream), `GET /reports`, `GET /jobs/{id}`, `POST /jobs/{id}/cancel`, `GET /jobs/{id}/download` (multi-output → zip).
- [x] Validation: sync rejects multi-output (`400`); auth inherited from the host (optional `RequireAuthorization`). Artifact store in Core; status as string — see D20.
- [x] Sample `03-async-job-hangfire` (Hangfire single-server, in-memory storage).
- **Acceptance:** AC-9, AC-10. **Demonstrable MVP.** ✅ 8 integration tests via TestServer.
- **Depends on:** PR 6, PR 4.

## PR 8 — OSS release polish
- [x] README, LICENSE (MIT), CHANGELOG, NuGet packaging (symbols/snupkg), per-package README.
- [x] Shared package metadata in `build/Directory.Build.props`; tests/samples/benchmarks marked non-packable; `release.yml` workflow publishes to NuGet on a `v*.*.*` tag.
- **Acceptance:** `dotnet pack` produces all packages; samples documented. ✅ 10 nupkg + 10 snupkg, each with a per-package README; no test/sample/benchmark packages.
- **Depends on:** PR 7.

---

# NeoReports — Implementation Plan (v2 / post-MVP)

v1 shipped (PR 0–8, published to NuGet as 1.0.0). v2 reopens scope deliberately:
every epic below was kept *possible* by the frozen `Abstractions` (D1/D9 — "close no
door"), so each addition is an **additive, SemVer-minor** change, never a rework.

**Order (locked with the maintainer):** Epic A (dynamic path) → Epic B (multi-source /
multi-sheet) → validation gate → **Epic C (Blazor UI) last**. Same rules as v1: one
small PR per item, green tests, and a recorded ADR entry before any out-of-v1-scope
code. See **D21–D25** in `DECISIONS.md`.

## Epic A — Dynamic path (config-driven reports)

Run reports from JSON config with no compile-time POCO, reusing the *entire* existing
pipeline. The row type is a positional `ReportRecord` (`object?[]` aligned to a declared
`ReportSchema`); the writer edge already speaks `object?[]` + schema (D1), so writers,
destinations and jobs are untouched. See **D21**.

- [x] **A1 — `ReportRecord` + dynamic pipeline.** Positional row type `ReportRecord`
  (`object?[]` + schema) added to Abstractions (additive); `ReportColumns.Positional(...)`
  declares dynamic columns over the existing `ReportBuilder<ReportRecord>`, so the whole
  v1 pipeline runs with `T = ReportRecord` unchanged. **Acceptance:** dynamic rows reach
  CSV byte-identically to the typed path for the same data. ✅ 26 green Core tests (+4).
  **Depends on:** v1.
- [x] **A2 — Config model + parser.** Serializer-agnostic `ReportConfig` DTOs (source ·
  columns · outputs · destinations) + `IReportConfigParser` (JSON) in Abstractions;
  `ReportConfigCompiler` (Core) turns a parsed config into a runnable `CompiledReport`,
  resolving source/format/destination from DI by stable id (`IConfigSourceProvider`,
  `IWriterFactory`, `IDestinationFactory`). Filter is parsed but deferred to A4 (compiler
  rejects it explicitly). **Acceptance:** golden config → compiled, runnable report. ✅ 33
  green Core tests (+7). **Depends on:** A1.
- [x] **A3 — SQL source from config.** `SqlConfigSourceProvider` (`type: "sql"`) reads
  connectionString/sql/key/pageSize from the source properties and materializes
  `ReportRecord`s by schema-column name, reusing the v1 keyset engine (an additive internal
  materializer overload on `SqlKeysetSource<T>`); `AddSqlConfigSource()` DI helper.
  **Acceptance:** Testcontainers E2E config→SQL→CSV. ✅ 6 green SQL integration tests
  (4 typed + 2 dynamic). **Depends on:** A2, reuses v1 keyset.
- [x] **A4 — JsonLogic filter.** `JsonLogicFilter` compiles a JsonLogic expression to
  `Func<ReportRecord,bool>` (operators `var`, `==`/`===`/`!=`/`!==`, `>`/`>=`/`<`/`<=`,
  `and`/`or`/`!`/`!!`, `in`); unsupported ops raise a clear error. No new dependency —
  a lean evaluator in Core. The parser accepts `"filter"` as a JsonLogic **object** (raw
  JSON captured into the config), and the compiler applies it. **Acceptance:** operator
  coverage + a filtered E2E. ✅ 48 green Core tests (+15); sample 04 now filters
  (`Amount > 250` → 3 of 5 rows). **Depends on:** A1.
- [x] **A5 — DI + dynamic trigger.** `AddReportFromConfig` / `AddReportFromConfigFile` /
  `AddReportsFromConfigDirectory` register config reports; they compile lazily on first
  registry resolution (so providers/factories need only be present by then) and are
  runnable **by name** through the standard runner and the existing AspNetCore endpoints —
  no separate endpoint, and no running arbitrary config from a request body (a deliberate
  safety choice). **Acceptance:** register from JSON/file/dir and run by name. ✅ 51 green
  Core tests (+3); samples 04 and 05 now use the DI sugar. **Depends on:** A2, A4, PR 7.

## Epic B — Multi-source & multi-sheet XLSX (possibly paid)

Tracked in `memory/open-questions.md`; monetization (free vs paid) is **TBD with the
maintainer** before building. See **D22** (multi-sheet) and **D23** (multi-source).

- [ ] **B1 — Multi-sheet XLSX.** One workbook, several sheets, each fed by a different
  filter (and later a different source). Needs a writer that targets a named sheet within
  a shared workbook without breaking the single-pass read. **Acceptance:** TBD with the
  recorded decision.
- [ ] **B2 — Multi-source reports.** Any report assembled from several sources
  (join/enrich) into one output. Likely the headline paid feature. **Acceptance:** TBD.

## Validation gate (do not skip before Epic C)

Validate Epic A/B with real users before investing in UI. This is a maintainer activity,
not a coding task — it gates Epic C.

## Epic C — Blazor UI (LAST)

Blazor Server + MudBlazor, built **only** from the Claude Design handoff
(`tokens.css`, `components.html`, per-screen `.html`, `handoff.md` — see the ADR). Never
invent design. Deliberately last per the maintainer. See **D24**.
