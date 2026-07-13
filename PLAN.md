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

## Epic B — Multi-sheet XLSX (first paid "Pro" feature), then multi-source

Maintainer decisions (locked): **start with multi-sheet XLSX (B1)**, shipped as a **paid, separate
"Pro" package** (commercial license), leaving the OSS core MIT. See the design blueprint in
[`docs/epic-b1-multisheet-pro.md`](docs/epic-b1-multisheet-pro.md), **D22** (multi-sheet) and **D27**
(Pro package model). Some sub-decisions (package name, license type/enforcement, exact OSS/Pro
boundary) are still open in that doc and must be settled before B1.2.

### B1 — Multi-sheet XLSX (Pro)

- [x] **B1.1 — OSS multi-view hook (MIT).** Each output may carry its own filters/columns (a "view")
  via `To(spec, view => view.Where(...).Column(...))`; the pipeline projects **per output** in a
  single pass and writes one file per view. Default single-output path stays byte-identical.
  ✅ 54 green Core tests (+3); Jobs (16) and AspNetCore (10) unaffected. The Pro workbook writer
  (views → sheets in one file) is B1.2.
- [x] **B1.2 — OSS sectioned-output hook (MIT).** A single output can carry several sections (one file,
  many sections — e.g. a workbook) via `ToSections(spec, s => s.Section("name", v => ...))`, each with
  its own filter/columns, all projected in one pass. New Core contracts `IReportSectionedWriter` /
  `ISectionedWriterFactory` (in Core, not the frozen Abstractions). ✅ 55 green Core tests (+1); Jobs
  (16) and AspNetCore (10) unaffected; default path still byte-identical.
- [x] **B1.3 — `NeoReports.Xlsx.Pro` package (commercial).** Fluent `XlsxWorkbook(...)` → a
  `SectionedOutputSpec` for `ToSections(...)`; ClosedXML `XlsxWorkbookWriter : IReportSectionedWriter`
  (one worksheet per section, reusing the MIT `XlsxCells` helper). `IsPackable=false` (excluded from
  the OSS NuGet release — verified `dotnet pack` produces no Pro nupkg); PolyForm Small Business
  `LICENSE.txt` (verbatim body TODO — see below). ✅ golden-file test (2 worksheets, own
  filters/columns from one read); Xlsx golden tests still pass after extracting `XlsxCells`.
- [x] **B1.4 — Packaging & CI + LICENSE.** Verbatim PolyForm Small Business 1.0.0 body (canonical
  SPDX text) pasted into both Pro `LICENSE.txt` files, header + `Required Notice:` line preserved;
  `LICENSE.txt` now packs into the Pro nupkgs (fixes NU5030). Distribution decided (**D30**): no
  feed/publishing yet — a `pack-pro.yml` workflow packs both Pro packages as CI **build artifacts**
  (per-project `IsPackable=true` override) on every version tag and on demand; the OSS release is
  untouched (verified: solution-wide `dotnet pack` still yields no Pro nupkg).
- [x] **B1.5 — Sample** `06-multi-sheet-xlsx` (typed: Approved/Rejected sheets). ✅ runs locally
  (in-memory source) → one `.xlsx` with an Approved sheet (report columns) and a Rejected sheet (own
  columns), each auto-filtered, from a single read.
- [x] **B1.6 — Dynamic config** support for sectioned/workbook outputs. `OutputConfig` gains
  `Sections` (`SectionConfig`: name · JsonLogic filter · report-column-name subset); the compiler
  resolves an `ISectionedWriterFactory` by format and builds `ToSections(...)`. `AddXlsxWorkbook()` DI
  helper registers the Pro writer (format `xlsx-workbook`). ✅ 56 green Core tests (+1) + Pro DI test.

### B2 — Multi-source reports (join / enrichment)

Blueprint: [`docs/epic-b2-multisource.md`](docs/epic-b2-multisource.md); **D28** (two strategies) and
**D29** (packaging — resolved to **Pro**). **Two explicit, user-chosen strategies**; both produce a
source the existing pipeline consumes unchanged.

- [x] **B2.1 — Enrichment** (`.Enrich(key, lookup, map)`): `EnrichingBatchSource<...>` in a new
  `NeoReports.Sources.Join.Pro` package — one batched lookup per page, O(pageSize), no N+1; a standard
  `IBatchSource<TResult>` the pipeline consumes unchanged. ✅ 2 green tests (batched-per-page with
  distinct keys; missing-key → default).
- [x] **B2.2 — Keyset merge-join** (`Join.MergeJoin(left, keyLeft, right, keyRight, map, kind)`): an
  `IStreamingSource<TResult>` merging two same-key-ordered sources; **inner + left-outer**; buffers
  one right key-group at a time (constant memory when per-key multiplicity is bounded); the pipeline
  slices the stream into batches. ✅ 4 green Join tests (inner drops unmatched, left-outer keeps them,
  multi-page merge, correct grouping).
- [x] **B2.3 — Pro package & docs + sample** `07-multi-source`: renamed the package to
  `NeoReports.Sources.Join.Pro` (PolyForm Small Business, `IsPackable=false`, commercial metadata +
  `LICENSE.txt`, like `NeoReports.Xlsx.Pro`); sample joins two in-memory sources via left-outer
  merge-join into a CSV, runs with no database. ✅ 4 green Join.Pro tests; sample runs green.
- [x] **B2.4 — Dynamic config** for multi-source: a composite `IConfigSourceProvider`
  (`type: "merge-join"`, `AddMergeJoinConfigSource()`) that recursively builds two nested sources
  against the shared report schema and merge-joins them on one column (`key` + `kind` inner/leftOuter),
  overlaying the right side's non-null columns onto the matching left row. Reuses the tested
  `Join.MergeJoin` via a `StreamingToBatchSource` adapter; no `Abstractions` change (composite spec
  lives in the source property bag). ✅ 6 green Join.Pro tests (+2: inner drops unmatched, left-outer
  keeps with null right columns). Enrichment-via-config is out of scope (needs a keyed-lookup source).

### Backlog (cross-cutting)

- [x] **Concurrency & memory under load.** `ConcurrencyTests` (Core.UnitTests, CI): 32 reports run
  at once without interfering (each its own file/counts), page-by-page reads (bounded memory), and
  cancelling some runs leaves the others unaffected + per-job temp dirs are isolated and cleaned up.
  `ConcurrencyMemoryBenchmark` (BenchmarkDotNet, MemoryDiagnoser, manual) measures allocation at
  concurrency 1/8/32 over 1M-row streaming CSV. ✅ 60 green Core tests (+4).

## Validation gate (do not skip before Epic C)

Validate Epic A/B with real users before investing in UI. This is a maintainer activity,
not a coding task — it gates Epic C.

## Epic C — Blazor UI (LAST)

Blazor Server with **pure design-system CSS** (no MudBlazor — see **D31**, superseding the
D24-era stack note), built **only** from the Claude Design handoff. The handoff arrived as a
runnable starter (all 17 screens, en-US, Geist + Tabler): app in `src/UI/NeoReports.UI`,
screen→route→endpoint map in `docs/ui-handoff.md`. Never invent design. See **D24/D31**.

- [x] **C1 — Starter in the repo.** The full Claude Design starter (17 screens + stubs, 26
  reusable components, tokens/styles, sample data) added as `src/UI/NeoReports.UI` and to
  the solution; fixed to compile (Razor named-fragment wrapping, `@page` on `_Host`, en-US
  leftovers) and smoke-tested: all 19 routes serve 200. Handoff table corrected to the real
  API surface (no invented endpoints). ✅ builds 0 warnings; all 126 tests still green.
- [x] **C2 — Wire the real endpoints.** New `INeoReportsApiClient`/`NeoReportsApiClient`
  (`src/UI/NeoReports.UI/Services`) calls `GET /api/reports`, `GET /api/jobs/{id}`,
  `POST /api/jobs/{id}/cancel`, `POST /api/reports/{name}/run`, and builds the download URL —
  registered by `AddNeoReportsUI()` via `AddHttpClient`, resolving the engine's scheme+host
  from `NavigationManager.BaseUri` (independent of the UI's own mount path, since
  `MapNeoReports` and `UseNeoReportsUI` are separate route branches). Every call is
  best-effort (`Try*`, catches network/JSON/timeout) so a page never breaks when no engine
  is mounted — it falls back to `SampleData`. Wired: **Reports list** (real reports overlay
  sample ones), **Report detail** (unknown slugs resolve by name against the live list before
  "not found"), **Run now/Run** buttons (real async trigger → lands on the new job), and all
  three **Job pages** (`Running`/`Completed`/`Failed`, each gaining a second `@page` route
  with `{Id}` and polling `GET /api/jobs/{id}` for real status/counters; `Running` redirects
  to `Completed`/`Failed` on terminal status; `Cancel`/`Download`/`Retry` call the real
  endpoints). Cells with no engine API today (schedule, metrics, permissions, pipeline
  variants, sources, settings) are untouched — still `SampleData`. ✅ solution builds 0
  warnings; all 126 tests green; sample host smoke-tested on all touched routes with no
  engine mounted (graceful fallback, 0 unhandled exceptions in the server log).
- [x] **C3 — Responsive breakpoints.** Added the `lg`/`md`/`sm` `@media` blocks from
  `docs/ui-handoff.md` §(b) to `neoreports.css` (desktop-first `max-width`, so narrower
  breakpoints inherit the wider ones without repeating rules): `grid-4` → 2 cols at `lg`;
  `grid-2`/`grid-1-5`/`grid-1-4` → 1 col at `md` (topbar nav unchanged, per spec); at `sm`,
  report/source card body grids → 1 col, tables scroll horizontally (`display:block;
  overflow-x:auto`, mono/numeric cells stay `nowrap`), buttons/icon-buttons hit the 44px
  touch-target minimum. Two of the four `sm` behaviors need real markup (CSS alone can't
  synthesize them), added as small, spec-literal component changes: `Topbar.razor` gains a
  hamburger toggle (`.nav-toggle`, closes itself on navigation) collapsing the nav into a
  dropdown; `WizardStepper.razor` gains a "Step N of M" compact label shown only at `sm`
  (the full step list hides there instead). ✅ solution builds 0 warnings; all 126 tests
  green; visually verified at `lg`/`md`/`sm`/mobile widths via the preview tool (grid
  collapses, table scroll, hamburger + dropdown all correct) — interactive click testing at
  a resized viewport hit a preview-tool limitation (confirmed by reproducing the same
  no-op on a pre-existing, already-shipped button), not a defect in the new code; the same
  click handler pattern works normally at the default desktop viewport.
- [x] **C4 — Self-host assets.** Fetched the real binaries (the Claude Design environment
  couldn't ship them) and dropped the CDN links entirely. Geist and Geist Mono are variable
  fonts — Google serves one physical file per family across the whole weight axis, so
  `tokens.css` now declares one `@font-face` per family with a weight **range**
  (`font-weight: 400 500;`) instead of duplicating a binary per static weight; only the
  `latin` subset was fetched (en-US-only UI). Tabler icons: `tabler-icons.min.css` +
  woff2/woff copied in (its `.ttf` fallback, ~2.5MB, was deliberately dropped — woff2/woff
  already cover every target browser); `_Host.cshtml` now links the local stylesheet.
  `wwwroot/fonts/README.md` rewritten to describe what's actually checked in and how to
  refresh it. ✅ solution builds 0 warnings; all 126 tests green; verified via the preview
  tool's network panel — zero requests to `fonts.googleapis.com`, `fonts.gstatic.com`, or
  any CDN; fonts/icons render correctly on screen.
- [x] **C5 — Hosting/packaging story.** Resolved (**D32**): the UI ships as a **Razor Class
  Library** — `NeoReports.UI` has no entry point; a host mounts it with
  `AddNeoReportsUI()` + `UseNeoReportsUI("<base path>")` (default `/neoreports`,
  host-configurable; routes, static assets and the Blazor hub all live under the branch).
  Sample `08-web-ui` is the runnable host (`--NeoReports:UIPath=/...` shows the custom URL).
  NuGet packaging of the UI (and MIT-vs-Pro) stays deferred (`IsPackable=false`).

## Epic D — Live API for the UI (dynamic registration + read endpoints)

Scope authorized in **D33** (2026-07). Full task specifications — files, types, endpoint
contracts, status codes, edge cases, test plans, acceptance criteria — live in the blueprint
**`docs/epic-d-dynamic-api.md`**; read it before starting any item. One PR per item, in order
(D1 → D2 are sequential; D3/D4/D5 are independent of each other; D6–D9 depend on their API
counterparts). Ground rules for every item: `Abstractions` stays frozen; GET responses never
echo property bags; dynamic report names are validated (`^[a-zA-Z][a-zA-Z0-9_-]{0,99}$`),
not sanitized.

- [x] **D1 — Core: mutable registry + persisted config store.** `IMutableReportRegistry`
  (`Register`/`Unregister`); `ReportRegistry` implements it (`Unregister` added, thread-safe
  `TryRemove`) and is exposed under both interfaces from the same singleton instance.
  `IReportConfigStore` + `FileReportConfigStore` (one `{name}.json` per report, atomic
  tmp-then-move writes, `*.json` listing naturally excludes `.tmp` leftovers,
  `DynamicReportName` regex shared for reuse by the AspNetCore endpoints in D2).
  `AddDynamicReports()` registers the store and an internal `IRegistryHydrator`
  (`FileStoreRegistryHydrator`) resolved via `GetServices<IRegistryHydrator>()` inside the
  existing lazy `IReportRegistry` factory — runs regardless of `AddDynamicReports` vs
  `AddNeoReports` call order, since the factory only executes on first resolution, after the
  whole service collection is built. Corrupt/incompilable stored documents and name
  collisions (code-first wins) are caught as `ConfigurationException`, logged (sanitized),
  and skipped — never crash the host. `ReportConfigEnvironment.Substitute` resolves
  whole-value `${VAR}` placeholders from environment variables at compile time so secrets
  never touch the persisted document. ✅ solution builds 0 warnings; 148/148 tests green
  (22 new: registry unregister, file-store roundtrip/invalid-name/tmp-exclusion, env
  substitution happy/missing/non-placeholder/non-string/lowercase/embedded, rehydration
  happy/corrupt-sibling/name-collision).
- [x] **D2 — AspNetCore: dynamic report endpoints.** `POST /reports` (parse → name regex →
  409-if-exists → env-substitute → compile → register → persist original document, with
  rollback on persist failure; 201 + Location), `POST /reports/validate` (dry-run, 200 with
  `Valid`/`Error`/`NameTaken`, no side effects; a truly empty body is the one 400), `DELETE
  /reports/{name}` (404 unknown / 409 code-first / 204, store-first ordering), `GET
  /capabilities` (source provider types + writer formats + destination types, read from
  `HttpContext.RequestServices.GetServices<T>()` — resolving `IEnumerable<T>` as a minimal-API
  parameter isn't reliable across hosts, so it's read explicitly instead). `IReportConfigStore`
  and `IMutableReportRegistry` are marked `[FromServices]` on every handler: without it, a host
  that doesn't call `AddDynamicReports()` makes ASP.NET's minimal-API metadata inference treat
  the un-resolvable `IReportConfigStore` parameter as a request body, which broke route building
  for the *entire* endpoint group (including the pre-existing job endpoints, not just the new
  ones) — caught by the existing `EndpointsTests` failing during this PR. ✅ solution builds 0
  warnings; 165/165 tests green (17 new integration tests: create happy-path + runs by name,
  invalid JSON, unknown source type, duplicate name, invalid name (3 cases, no file created),
  missing/set env var, validate valid/broken/name-taken/empty-body, delete dynamic/code-first/
  unknown, capabilities reflecting registered providers).
- [x] **D3 — AspNetCore: `GET /jobs`.** Exposes the existing `IJobStore.ListAsync(JobQuery)`:
  `status` (enum name, case-insensitive, invalid → 400), `report`, `since` (ISO-8601, invalid →
  400), `limit` (default 50, clamped to 1–200), `offset` (negative clamped to 0); `CreatedAt`
  desc re-enforced in the endpoint regardless of store ordering. Both DI paths already
  registered `IJobStore` (`AddNeoReportsInMemoryJobs` and `AddNeoReportsHangfireJobs` both do —
  no gap to fix). ✅ solution builds 0 warnings; 173/173 tests green (8 new: empty store,
  descending order, report-name filter, status filter + invalid status, since filter, limit=0
  clamped to 1, large limit/negative offset don't error).
- [x] **D4 — AspNetCore: report detail + enriched summary.** `CompiledReport` gains public
  computed `OutputFormats`/`DestinationTypes`, and `Retry`/`FailureStrategy` (already public
  types — `RetryOptions`, `IFailureStrategy`) went from `internal` to `public` rather than
  being duplicated under new names. `GET /reports/{name}` → `ReportDetailView` (columns with
  types, page size, formats, destinations, `FailureStrategy.Name`, retry attempts/backoff/base
  delay/jitter, `Origin` code|config via an **optional** `IReportConfigStore` resolved from
  `HttpContext.RequestServices` — hosts without `AddDynamicReports()` get every report as
  `"code"`, no throw); `ReportSummary` gains `Formats`/`Destinations`. No property bags in any
  response (regression-tested against the raw JSON). ✅ solution builds 0 warnings; 179/179
  tests green (6 new: code-first detail shape, dynamic-report detail is deletable, 404 unknown,
  no `"properties"` key, list summary carries formats/destinations, origin defaults to "code"
  with no dynamic-reports support configured).
- [x] **D5 — AspNetCore: `GET /jobs/{id}/artifacts`.** `ArtifactView` (file name / mime /
  `SizeBytes`, never `Path`) from the existing `IReportArtifactStore`; kept out of `JobView`
  so status polling does no IO; unknown job → 404; non-completed job → `[]` (not an error);
  `IReportArtifactStore` injected the same plain way `DownloadAsync` already does (no
  `[FromServices]`), mirroring its existing null/absence behavior exactly rather than
  introducing a new pattern. ✅ solution builds 0 warnings; 184/184 tests green (5 new: single
  artifact matches size, multi-output count matches the zip download, no `"path"` key in the
  response, running job → `[]`, unknown job → 404).
- [x] **D6 — UI: Builder wired end-to-end.** Discovered mid-implementation that `BuilderState`
  and the Configure/Format/Destination steps were almost entirely decorative (query editor was
  static HTML, pagination/resilience never touched any state, the destination catalog was
  fictional — SharePoint/email — with no engine equivalent); flagged the scope gap and the
  maintainer chose "add minimal real fields" over a stripped-down save or deferring the epic.
  `BuilderState` gained the real fields (`ReportName`, `SourceType`, `ConnectionStringVariable`,
  `SqlQuery`, `KeyColumn`, `PageSize`, `ColumnNames`, `DestinationType`/`DestinationPath`,
  `EngineAvailable`) alongside the existing cosmetic ones (schedule, template metadata — never
  serialized). `BuilderConfigMapper` (`Services/BuilderConfigMapper.cs`, dependency-free from
  the engine assemblies — the UI only ever talks HTTP) maps state to the `POST /api/reports`
  JSON shape. `NeoReportsApiClient` gained `TryGetCapabilitiesAsync`/`TryValidateReportAsync`/
  `TryCreateReportAsync`/`TryDeleteReportAsync`. Step 1 checks `GET /api/capabilities` and sets
  `EngineAvailable` (demo-mode banner + disabled Save/Run when absent); step 2 gained an
  "Engine configuration" card (report name, connection string `${VAR}`, SQL query, key column,
  page size, columns) with a working Validate button; step 4 gained a real "Engine destination"
  selector fed by capabilities, kept alongside the untouched decorative catalog; step 5's Save
  and Run now buttons call Create (→ navigate to `/reports/{name}`) and Create+Run (→ navigate
  to `/jobs/{jobId}`), with inline danger-banner errors on failure. Added a `.banner.danger`
  CSS variant (existing `--danger-fg`/`--danger-bg` tokens, same pattern as `.success`/`.info`).
  New `tests/NeoReports.UI.UnitTests` project (first for the UI) — `BuilderConfigMapperTests`:
  happy-path shape, column ordering, omitted-when-empty destination/connection-string/path,
  name passthrough. ✅ solution builds 0 warnings; 190/190 tests green (6 new). Browser-verified
  (preview tool): demo-mode banner and disabled Save/Run render correctly against the UI-only
  sample host (no `/api` mounted); two-way binding confirmed via real in-app navigation (a
  value typed on step 2 was still present on step 5 through the Scoped `BuilderState`) — note
  `preview_click`'s simulated click doesn't reliably reach Blazor Server's circuit even at the
  default viewport (a tooling limitation already logged in Epic C for resized viewports, now
  confirmed broader); native `element.click()` via `preview_eval` was used instead throughout.
- [x] **D7 — UI: dashboard + run histories.** `NeoReportsApiClient.TryListJobsAsync` (report/
  since/limit filters, reuses `ApiJobView`). Dashboard: recent-jobs strip (`limit: 8`) unified
  under a `DashboardJobRow` shape shared by the live (`ApiJobView`) and demo (`SampleData`)
  paths so the markup doesn't fork; metric cards (jobs today, success rate, records exported,
  avg duration) computed client-side from `TryListJobsAsync(since: today-utc, limit: 200)`.
  Report detail: history table (`report: name, limit: 10`) replaces the hardcoded 5-row demo
  array when live, with routes correctly split across `/jobs/{id}`,
  `/jobs/completed/{id}`, `/jobs/failed/{id}` by status (previously the demo rows pointed at
  the parameterless job routes). `EmptyState` on both screens when the live list is empty,
  distinct from the demo-data fallback when the engine is unreachable. ✅ solution builds 0
  warnings; 190/190 tests green (no new tests — UI pages aren't unit-tested in this repo,
  consistent with C2/D6; verified via the preview tool instead). Browser-verified: demo
  metric/table values render unchanged from before this PR when the engine is unreachable (UI-
  only sample host); report-detail history renders 5 demo rows and a clicked row correctly
  navigates to `/jobs/completed/{id}`; no console/server errors.
- [x] **D8 — UI: report detail, pipeline, delete, completed artifacts.** `GET /reports/{name}`
  (D4) replaces the old list-based lookup on `ReportDetail`: real columns, formats,
  destinations, retry/failure-strategy summary, and an `origin: code|config` chip next to the
  title tags. Delete button (danger variant, two-click "Delete report" → "Confirm delete"
  local-state confirm — the codebase had no existing confirm-dialog pattern to reuse, e.g. the
  Cancel button on the running-job screen acts immediately with no confirmation, so this is a
  new minimal safety step, not an invented design language) shown only when `Deletable`;
  `DELETE /reports/{name}` → navigate to `/reports` on success, inline danger banner on
  failure. Job completed: `GET /jobs/{id}/artifacts` (D5) replaces the hardcoded 2-file list
  with real name/size/mime, `EmptyState` when a completed job has none. **Pipeline stages
  deliberately NOT wired**: `PipelineView` is a single fixed "regional-sales" demo with no
  route parameter to select a report, and its variant rows are explicitly post-MVP (D23) — wiring
  only the shared source/columns section would be cosmetic without a way to pick a report, so
  it stays 100% `SampleData` (documented in `docs/ui-handoff.md`, not silently dropped).
  ✅ solution builds 0 warnings; 190/190 tests green (no new tests — UI pages aren't unit-tested
  in this repo). Browser-verified: both screens render their demo fallback unchanged (no
  origin chip/delete button on `ReportDetail`, original 2-file list on `JobCompleted`) when
  the engine is unreachable; no console/server errors.
- [x] **D9 — UI: sources page on capabilities.** New "Engine source types" section
  (`GET /capabilities`) additive above the existing decorative source catalog — one card per
  registered `IConfigSourceProvider` type id, nothing else: no per-source name/health/latency
  numbers, since capabilities only reports provider *types*, not a source registry (dropped
  rather than faked, per the blueprint's explicit bias). Section renders only when at least
  one capability is present; the page is byte-for-byte unchanged when the engine is
  unreachable. Source explorer stays 100% `SampleData` (schema/preview introspection is
  security-sensitive and needs its own ADR — unchanged from the blueprint). ✅ solution builds
  0 warnings; 190/190 tests green (no new tests — UI pages aren't unit-tested in this repo).
  Browser-verified: with no engine mounted, the page renders identically to before this PR (6
  decorative source cards, no new section, no console/server errors).

**Epic D is now complete (D1–D9 all merged).** Dynamic report registration
(`POST/DELETE /reports`, validate, capabilities), the read endpoints the UI needed
(job list, report detail, job artifacts), and the UI wiring for the Builder, dashboard, report
detail, and sources are all live. See `docs/epic-d-dynamic-api.md` for the full design and
`DECISIONS.md` (D33) for the scope decision.

**Follow-up: `samples/09-web-ui-live`** mounts `NeoReports.UI` and the engine (`AddDynamicReports`)
in one host with a self-contained `InMemorySalesSourceProvider`, so the full dynamic-registration
flow (Builder → validate → save/run → real CSV/XLSX on disk → report detail/dashboard/sources →
delete) can be clicked through end to end without a database or cloud account. Hands-on testing
of that sample found step 1 of the Builder set `Wizard.SourceType` from `GET /api/capabilities`
with no visible control to change it — the "Engine source type" selector (mirroring D6's "Engine
destination" pattern) was added to close the gap.

**Follow-up: hardcoded-UI audit.** A full pass over every screen found several spots still
showing fixed/stale data despite an already-wired real endpoint or already-bound `BuilderState`
field being available — fixed across 3 small PRs:
- Builder step 2's recap card and step 5's summary (Source/Columns/Destinations rows) read real
  `Wizard` fields now, instead of a fixed "SQL Server" string and a stale `Wizard.Destinations`
  set that step 4 never touches (was silently stuck on "sharepoint").
- `ReportDetail`'s metric strip (total runs/success rate/avg duration) and `Reports`' count strip
  (running now/failed) are computed from `GET /api/jobs` when live, same pattern as Dashboard's
  `ComputeMetrics` (D7); "Next run"/"active schedules"/"paused" are dropped or shown as "Not
  scheduled" rather than a fake future date, since v1 has no scheduler.
- `JobRunning`/`JobCompleted`'s Configuration and Destinations cards now read `GET
  /api/reports/{name}` instead of always showing SQL-Server-era demo data (CSV/XLSX, 1,000
  rows/page, SharePoint/S3); "Worker" and "Run by" rows are dropped — v1 is single-worker with no
  triggered-by identity on a job, so there's nothing real to show. Extracted the shared
  `ResilienceFormatter` helper to avoid duplicating the retry-summary logic a third time.

A larger list of screens/cards with **no** real backing at all (scheduling/cron, permissions,
dynamic parameters, SharePoint/email destinations, settings screens) was intentionally left
untouched — see `docs/ui-handoff.md` for what's still `mock/future` and why.

`docs/ui-handoff.md` and the sample's README are updated accordingly.

**Follow-up: Dashboard recent files + dynamic-path resilience (D34).** Two items the maintainer
asked to implement rather than remove:
- Dashboard's "Destinations" card becomes "Recent files" when live — real filenames/sizes from
  `GET /api/jobs/{id}/artifacts` for the last 3 completed jobs, instead of fixed SharePoint/S3/
  email rows. Extracted a shared `FileFormatting` helper (icon/byte-size) also used by
  `JobCompleted`.
- **D34**: the Builder's Resilience card was 100% cosmetic because `ReportConfig` never carried
  retry/failure-strategy fields for the dynamic path — an oversight, since `RetryOptions`/
  `FailureStrategyBuilder` are already in v1 scope for the typed path. Added an optional
  `ReportConfig.Resilience` (`ResilienceConfig`: max attempts, backoff shape, base delay, jitter,
  on-failure strategy) — additive on the frozen `Abstractions` record — applied by
  `ReportConfigCompiler` through the same `builder.Retry(...)`/`builder.OnFailure(...)` the fluent
  path uses; omitting it keeps today's defaults. Threshold-based abort escalation
  (`FailureStrategyBuilder.AbortIf`, a predicate) and per-exception-type retry filtering have no
  config-document equivalent (same reason dynamic filters are JsonLogic, not code) — the Builder's
  "Retry on errors" pills and "Abort when" switches stay cosmetic, now clearly labeled as
  illustrative-only. Full details in `DECISIONS.md` (D34). ✅ solution builds 0 warnings; new
  tests: `ResilienceConfigTests` (Core, parser + compiler), a `ReportDetailEndpointTests` case
  (AspNetCore, full round-trip through the API), `BuilderConfigMapperTests` (UI serialization).
  Browser-verified via `samples/09-web-ui-live`: configured 7 max attempts, exponential backoff,
  4.5s base delay, jitter on, on-failure skip-and-log through the Builder; saved report's
  `GET /api/reports/{name}` and the Report detail page's Resilience summary both reflected the
  exact values entered.

**Follow-up: real Rate/s on the job running page.** `JobRunning`'s "Rate/s" metric card was a
fixed "194 rows/s · peak 312" regardless of the actual job. Both inputs needed to compute it were
already available (`Stats.RecordsWritten`, `StartedAt`) — now recomputed on every poll tick, with
peak tracked as the highest rate seen across polls. "Memory" stays a fixed placeholder (no
per-job memory tracking in the engine, nothing real to show).

**Follow-up: scheduling deferred (D35).** The audit also flagged the Schedule cards
(`ReportDetail`, Builder step 5) as cosmetic. Unlike D34, this one is a genuinely new capability —
neither path has any recurring-execution concept today. Maintainer decision: **defer**, record the
sketch (cron field + `IRecurringJobManager.AddOrUpdate` on `HangfireJobScheduler`, which already
has the `IBackgroundJobClient` one-shot pattern to extend) in `DECISIONS.md` (D35), and leave the
UI illustrative rather than remove it — the code comments now point at D35 instead of describing
invented behavior as if real.

**Out of scope for the epic** (recorded in D33/blueprint, D35): `PUT` (edit), scheduling/recurring,
real progress percentage, source introspection (schema/preview), settings screens, variants.
(Scheduling and several of the audit-removed cards later entered scope as Epics E/F — D37–D42.)

## Epic E — Real backing for the removed UI content (post-D36)

Scope authorized in **D37–D41** (2026-07). Full task specifications — files, types, endpoint
contracts, edge cases, test plans, acceptance criteria — live in the blueprint
**`docs/epic-e-real-backing.md`**; read it before starting any item. One PR per item; E2 → E3 are
sequential, everything else is independent. Ground rules for every item: `Abstractions` changes
only as additive trailing optional record parameters (two in this epic); no `Abstractions`
interface gains a member — new contracts live in Core (D20 pattern); GET responses never echo
property bags; telemetry/capture is fire-and-forget and never changes a run's outcome; every
returning UI card has an honest empty/unavailable state (D36) — no fabricated fallback content.

- [x] **E1 — Abort thresholds as config (D37).** `AbortThresholdConfig` +
  `ResilienceConfig.AbortWhen` (Abstractions, additive; skip-and-log only, OR semantics);
  compiler → the same `AbortIf` the fluent path uses; data-based `AbortIf` overload on
  `FailureStrategyBuilder` + `CompiledReport.AbortThresholds` for introspection;
  `ReportDetailView` threshold fields; Builder "Abort when" switches return. The "Retry on
  errors" pills stay removed — per-exception retry filtering **rejected** (D37, reopens D6).
  ✅ solution builds 0 warnings; new Core/AspNetCore/UI tests (validation, compiler round-trip,
  API round-trip, UI serialization/rendering); browser-verified via `samples/09-web-ui-live`.
  PR [#114](https://github.com/thiagoluga/NeoReports/pull/114).
- [x] **E2 — Job event log: Core store + engine emission (D38).** `JobEvent` + `IJobEventStore`
  (+ InMemory/JSONL file stores; configurable per-job cap with `events-truncated` marker +
  optional TTL retention); **opt-in** `AddJobEvents()`; `ReportRunner` emits the closed lifecycle
  vocabulary; `ResiliencePipelineFactory` gains the optional `OnRetry` hook. No HTTP yet.
  Unregistered store ⇒ byte-identical behavior (regression-guarded).
  ✅ solution builds 0 warnings; 25 new Core tests (stores, DI, full-lifecycle emission ordering/
  counters, retry/skip/abort events, a throwing store never fails the run) + 3 new Jobs tests
  (cancelled/completed lifecycle, no-registration is a no-op); all pre-existing tests unaffected.
- [x] **E3 — `GET /jobs/{id}/events` + UI telemetry (D38).** Endpoint (type filter, paging,
  404/`[]` semantics, `JobView` untouched per D5); Timeline card (Running/Completed/Failed),
  Retries card (`?type=retry`), processing-rate sparkline derived from `page-completed` events —
  no second sampling mechanism. Honest states for store-absent/truncated/no-retries.
  **Depends on:** E2. ✅ solution builds 0 warnings; 8 new AspNetCore integration tests + 7 new
  UI unit tests (`JobEventFormatter`); browser-verified via `samples/09-web-ui-live`: real
  Timeline events (started → page-completed → outputs-finalized → uploaded → completed) render
  correctly on `JobCompleted`, the "not enough data" honest state shows with 1 page-completed
  event, and the sparkline renders real points with 2+.
- [x] **E4 — Memory screen (D39).** `GET /api/system/memory` (working set, GC heap/committed,
  measured-at, running-jobs count); UI Memory page with auto-refresh + running-jobs table composed
  client-side from `GET /jobs?status=Running`; process-wide copy per D39. No per-job memory,
  no time series. ✅ solution builds 0 warnings; 3 new AspNetCore integration tests
  (shape/sanity, running-jobs count reflects a started job, host without a job store still 200
  with 0 — `RemoveAll<IJobStore>()` after normal registration, since a fully bare host breaks
  minimal-API metadata inference for the whole `MapNeoReports` group per the D2 lesson);
  browser-verified via `samples/09-web-ui-live` (real working-set reading, honest "No jobs
  running" empty state, "Memory" nav item). Caught and fixed a Blazor "unclosed element" crash
  during verification: the honest-empty-state branch must be `@if/else`, not an early `return`
  inside an unclosed wrapping `<div>` (unlike the Job pages' pattern, which returns before any
  wrapping div opens).
- [x] **E5 — Partial artifacts for failed and cancelled jobs (D40).** `IPartialArtifactStore` +
  file store (own directory, TTL prune, opt-in DI); runner captures on Failed **and Cancelled**
  (best-effort finalize, files renamed `{name}.partial.{ext}`); `GET /jobs/{id}/partial-artifacts`
  + its own `/download` (completed artifacts surface never changed); JobFailed partial-output card
  with warning banner. One honest empty state, not two — the wire can't distinguish "no store
  registered" from "registered but nothing captured" (both `[]`), same resolution as E3's job
  events. ✅ solution builds 0 warnings; 16 new Core tests (5 covering capture behavior — aborted
  run captures exactly the fully-written batches renamed `.partial`, `CompletedPartial` runs never
  capture and still publish, cancelled run captures, no-store-registered and throwing-store are
  both no-ops for the run's own outcome — + 11 for `FileSystemPartialArtifactStore` itself) + 7
  new AspNetCore integration tests (404/empty-for-completed/captured-for-failed/download-streams/
  no-store-is-empty/completed-artifacts-never-includes-partials/multi-file-zip-download). Found
  and fixed a real bug during testing: `PartialArtifactOptions.Directory`'s blueprint-specified
  relative default (`./neoreports-partials`) breaks `Results.File(string, ...)`, which resolves a
  relative path against the ASP.NET **web root**, not the process working directory — changed the
  default to an absolute temp-folder path, matching `FileSystemArtifactStore`'s existing pattern.
  Extracted a shared `FileSystemArtifactLayout` helper to dedupe the on-disk mechanics between
  `FileSystemArtifactStore` and `FileSystemPartialArtifactStore` (SonarCloud duplication gate).
- [x] **E6 — Scheduling (D41, supersedes D35).** `ScheduleConfig` on `ReportConfig` (Abstractions,
  additive; **UTC-only cron**, UI renders next run in viewer-local time) + builder `.Schedule()`;
  `IRecurringReportScheduler` (Core) implemented by Hangfire (`neoreports:{name}` ids, per-firing
  job records, orphan detection reads Hangfire's own storage via `IStorageConnection.GetRecurringJobs()`)
  **and** InMemory (Cronos + `PeriodicTimer`; Cronos approved into CPM); overlapping firings run
  concurrently; runtime overrides for **both origins** via `IScheduleOverrideStore` (file/in-memory
  twin, tombstone semantics; config documents never patched — D33(f) stays punted);
  `PUT/DELETE /reports/{name}/schedule`; `ScheduleReconciliationHostedService`
  (`AddScheduling`/`AddInMemoryScheduling`) reconciles at startup and removes orphans;
  `Scheduling` in capabilities; `NextRunAt` (computed, never fabricated) in report detail;
  Schedule cards return on ReportDetail and Builder step 5 (cron input + presets, "overridden at
  runtime" chip, honest no-scheduling/not-scheduled states). ✅ solution builds 0 warnings; 42 new
  Core tests (cron validation, builder/compiler wiring, effective-schedule matrix, file/in-memory
  override store roundtrip + tombstone, reconciliation registers/overrides/tombstones/removes
  orphans/no-op without a scheduler, DI) + 8 new Jobs tests (InMemory registration/next-occurrence/
  removal/replace/invalid-cron/listing) + 5 new Hangfire seam tests (real `InMemoryStorage` +
  `RecurringJobManager`) + 13 new AspNetCore integration tests (capabilities flag, detail fields,
  404/400/409 error cases, activate/tombstone/clear for both origins, dynamic-report schedule
  effective immediately, delete removes the registration) + 3 new UI mapper tests. Browser-verified
  live end-to-end via `samples/09-web-ui-live` (`AddInMemoryScheduling()`): scheduled a report for
  "every minute" via the API, watched a real job appear and complete with **no manual trigger** at
  the exact minute boundary (confirmed via the job's `createdAt`), the ReportDetail Schedule card
  and "Next run" metric rendered the real cron/next-run/override state, clicked "Clear" in the
  actual UI, watched the card flip to "Not scheduled", and confirmed no further job fired after
  clearing.

## Epic F — Source registry (named source instances + on-demand health)

Scope authorized in **D42** (2026-07); **MIT** (maintainer call). Blueprint:
**`docs/epic-f-source-registry.md`** — read it first; its "locked design decisions" section
(run-time `Ref` resolution, `PUT` full-replace, typed by-name in scope) is not up for
re-litigation. Independent of Epic E; expected to start after it. GET responses never return
source `Properties` — this is where the actual secrets live.

- [x] **F1 — Core: `SourceDefinition` + `ISourceRegistryStore`/file store + `ISourceRegistry`.**
  One JSON per source (same atomic-write + name-regex discipline as `FileReportConfigStore`);
  resolve substitutes `${VAR}` **per call**; read-through cache invalidated on save/delete;
  `CompiledReport.SourceRef` for computed (never tracked) reference counts. Implementation note:
  the resolution service class is named `SourceRegistryService`, not `SourceRegistry` — a class
  named the same as its own containing namespace (`NeoReports.Core.SourceRegistry`) doesn't
  compile from sibling namespaces (C#'s enclosing-namespace lookup binds the bare name to the
  namespace first, `CS0118`). Extracted `PrimitiveObjectConverter` out of `JsonReportConfigParser`
  into a shared, now-bidirectional (read **and** write) converter, reused by the new file store —
  avoids duplicating the property-bag JSON shape and gives the store real serialization instead of
  the parser's read-only stub. ✅ solution builds 0 warnings; 26 new Core tests (file/in-memory
  store roundtrip + replace + delete + list + corrupt-file-skip + invalid-name, every primitive
  property kind roundtrips through JSON, registry resolve substitutes per call and reflects a
  changed env var on the very next call without re-saving, cache invalidated on save/delete, list
  stays unsubstituted, DI registration).
- [x] **F2 — Compiler + dynamic path: `SourceConfig.Ref`** (Abstractions, additive — plus `Type`
  made nullable, required only for an inline source). Compile-time existence/type checks;
  **run-time** definition resolution + merge (definition base, report overlay, then substitution)
  so source edits apply on the next run without recompiles; providers untouched. Inline sources
  unchanged. Implementation note: "sources hydrate before dynamic reports" turned out not to be a
  real ordering concern — `ISourceRegistry` resolves on demand straight from the store (unlike the
  report registry, it never pre-loads anything into memory at startup), so no hydrator sequencing
  was needed. Mechanism: a new internal `RefBatchSource` (re-)creates the real underlying source
  at the start of every run — detected the same way the rest of the pipeline already detects "a
  fresh run" (`BatchContext.Cursor == null` on the first page, a documented invariant) — rather
  than changing the pipeline's synchronous `CompiledReport.ReaderFactory` signature. Compile-time
  existence/type checks block synchronously (`GetAwaiter().GetResult()`) since `Compile()` itself
  is a synchronous, one-time call; no deadlock risk (no captured `SynchronizationContext` on this
  path, and the underlying store call is local file/in-memory I/O). ✅ solution builds 0 warnings;
  10 new Core tests (type-from-definition, type-match/mismatch, unknown ref, no-registry-configured,
  overlay-precedence-both-directions, env var change reflected on the very next run without
  recompiling, source deleted after compile fails the next run with a clear error, existing inline
  configs compile unchanged).
- [x] **F3 — AspNetCore: CRUD + health.** `GET/POST/PUT/DELETE /sources` (`PUT` full-replace;
  delete 409 while referenced; responses never carry properties — regression-guarded) +
  `POST /sources/{name}/health` (on-demand only, cached + timestamped result, 422 when the type
  has no check); `ISourceHealthCheck` contract in Core; open-and-ping implementation in
  `Sources.Sql`. Landed as a single PR (didn't grow enough to justify a split). `ISourceRegistry`
  gained `GetAsync` (raw, non-throwing single-name lookup) for the metadata-only GET handlers.
  17 new AspNetCore integration tests (CRUD happy paths + every error case, delete blocked-then-
  allowed, health 200/404/422, GET reflects cached health after a check, no `properties` key
  anywhere) + 3 new Core tests for `GetAsync` + 3 new `Sources.Sql` Testcontainers tests for
  `SqlSourceHealthCheck` (healthy/missing-property/unreachable).
- [x] **F4 — UI: sources screens on the registry.** Registered-sources grid returns (name/type/
  description/ref-count/last-health + "Check now"); health strip aggregates only real results
  ("N never checked" is a state, not a gap); add/edit forms with write-only properties + `${VAR}`
  hint; Builder "use a registered source" picker (`source.ref`); Dashboard "Most referenced
  sources" card. Source explorer stays out (own ADR still required). Verified live end-to-end in
  the browser (samples/09-web-ui-live): create → health-check honest-422 → two-click delete;
  Builder picker sets the ref and hides the inline connection field through review. 3 new
  `BuilderConfigMapperTests` for the ref-serialization shape.
- [x] **F5 — Typed path: `Source.SqlNamed("sales-db", sql)`.** Per-run registry resolution wired
  at compile time (sources have no DI on the read path); populates `SourceRef`; E2E proof:
  swapping the definition's connection string between runs redirects the next run.
  **Depends on:** F1, F2. New Core `INamedSourceResolver` — `CompiledReport.ReaderFactory` (internal)
  grows an `IServiceProvider` parameter, threaded from `ReportRunner.ExecuteAsync`; `AddReport`
  throws `ConfigurationException` at registration when a named source has no registry configured.
  5 new Core tests (registration guard, success path, `SourceRef` population, `AttachServices`
  called per run — including a second run) + 2 new `Sources.Sql` Testcontainers tests (connection
  swap between two databases in the same container redirects the next run; unregistered name
  throws).

## Epic G — More sources + report preview (D43/D44/D45)

Requested directly by the maintainer (2026-07). Blueprint: `docs/epic-g-more-sources.md`.

- [x] **G1 — `NeoReports.Sources.Common` + PostgreSQL.** Extract `SqlKeysetSource<T>`'s engine into
  a provider-agnostic `AdoKeysetSource<T>` (`Func<DbConnection>` instead of hardcoded
  `SqlConnection`) + shared `RecordMaterializer<T>`; `SqlKeysetSource<T>`'s own already-published
  public API stays untouched, but its surrounding glue (property parsing, health-check body,
  member-selector) was migrated onto the same shared helpers rather than left duplicated — Sonar's
  quality gate (correctly) rejected the first "leave Sources.Sql alone entirely" attempt on
  duplication grounds. `NeoReports.Sources.Postgres` (Npgsql):
  `Source.Postgres`/`.PostgresNamed`, `PostgresConfigSourceProvider`, `PostgresSourceHealthCheck`,
  `AddPostgresConfigSource()`. Testcontainers.PostgreSql integration tests. Found along the way:
  Postgres needs an explicit `DbType.String` on null parameters (Npgsql can't infer type from a
  null CLR value) and an explicit `@cursor::type` cast in the keyset query (no implicit
  parameter-to-column type coercion, unlike SQL Server) — both fixed/documented, `AdoKeysetSource`
  now sets the DbType explicitly for every provider. 6 new Postgres integration tests (keyset
  paging, typed materialization, health check x3, named-source connection swap).
- [x] **G2 — MySQL/MariaDB.** `NeoReports.Sources.MySql` (MySqlConnector), same shape as G1 on the
  shared engine. Testcontainers.MySql integration tests. **Depends on:** G1. No shared-engine
  changes needed — MySQL tolerated the existing null-parameter `DbType.String` handling and
  implicit string/numeric comparison in the keyset query without modification, unlike Postgres.
  One test-only wrinkle: the Testcontainers MySQL app user has no cross-database privileges, so
  the named-source connection-swap test's second database is created/seeded via an explicit root
  connection (root password set on the container, not exposed by the library). 9 new integration
  tests (keyset paging, typed materialization, health check x3, named-source connection swap,
  dynamic-config E2E + validation + DI registration).
- [x] **G3 — Oracle.** `NeoReports.Sources.Oracle` (Oracle.ManagedDataAccess.Core), same shape as
  G1/G2 on the shared engine. **Depends on:** G1. Needed two new optional extension points on
  `AdoKeysetSource`/`AdoNamedKeysetSource`/`AdoConfigProperties.CreateAdoConfigSource` (default
  values keep every existing provider unaffected): a configurable `parameterPrefix` (`:name`, not
  `@name`) and a `configureCommand` hook (ODP.NET requires `OracleCommand.BindByName = true`,
  positional binding otherwise). Found along the way: Oracle rejects `DATE` as a bare column
  identifier in DDL/DML (`ORA-00904`) — the test fixture's column is `SaleDate`, aliased back to
  `"Date"` in SELECT; sqlplus doesn't fail a seed script on a bad statement by default, so the
  Testcontainers seed script needs `WHENEVER SQLERROR EXIT SQL.SQLCODE` or failures pass silently.
  9 new integration tests (keyset paging, typed materialization, health check x3, named-source
  connection swap across two schemas — Oracle has no lightweight "create another database" —
  dynamic-config E2E + validation + DI registration), sharing one Testcontainers Oracle container
  across the whole assembly via a collection fixture (Oracle's container startup is much slower
  than SQL Server/Postgres/MySQL, unlike those providers' per-class fixtures).
- [x] **G4 — MongoDB.** `NeoReports.Sources.MongoDb` (MongoDB.Driver) — own pagination design (no
  shared engine with G1-G3): keyset via `Find(key > cursor).Sort(key).Limit(pageSize)`, cursor
  round-tripped through MongoDB Extended JSON to preserve its exact BSON type. No filter translation
  in this pass (D45) — preview runs unfiltered with an honest note. No by-registry `MongoDbNamed`
  entry point in this pass, unlike the SQL-family providers. Found along the way: MongoDB.Driver's
  own `BsonClassMap` deserialization silently rebinds a POCO property named `Id` to the document's
  `_id` field — worked around with a small reflection-based `BsonDocumentMaterializer<T>` instead of
  the driver's deserializer; `BsonDateTime` always stores UTC, so seed/assertion `DateTime`s need an
  explicit `DateTimeKind.Utc`; `MongoClient` (unlike `DbConnection`) is meant to be built once and
  reused, not per page — caught in code review before merge. 8 new Testcontainers.MongoDb
  integration tests (keyset paging, typed materialization, health check x3, dynamic-config E2E +
  validation + DI registration).
- [x] **G5 — Core + AspNetCore: report preview endpoint.** `POST /reports/{name}/preview` (bounded
  page, no output writing, no job record); `PreviewFilter`/`PreviewFilterOperator` (Core, not
  Abstractions) + `IFilterTranslator` seam implemented once in `Sources.Common` (`AdoFilterTranslator`)
  for the SQL family; `RunReportRequest` gains additive `Filters` (ephemeral, never persisted). Typed
  reports: preview-only, 400 on non-empty filters. **Depends on:** G1 (`IFilterTranslator` lives in
  `Sources.Common`). Implementation refinement from the original sketch (see D45): `IFilterTranslator`
  doesn't take a `DbCommand` — `CompiledReport` type-erases its source behind an internal
  `ReaderFactory`, unreachable at the DbCommand level from the preview endpoint — instead it returns
  translated SQL text plus a parameter dictionary that flows through the existing
  `ReportExecutionContext.Parameters` mechanism `AdoKeysetSource` already binds. A filtered dynamic
  preview re-reads the report's *stored* config document rather than its compiled source, resolving a
  `Ref`-based source's properties the same way `RefBatchSource` does (definition base, report-local
  overlay wins). A full filtered *run* (not just preview) is deferred — `RunReportRequest.Filters` is
  additive on the contract, but `POST /run` returns 400 on a non-empty value until a follow-up threads
  a temporary re-compiled report through the job/scheduler pipeline. New tests: Core unit tests for
  `AdoFilterTranslator` (SQL wrapping, all 8 operators, `LIKE` wildcards as bound values, Oracle's `:`
  prefix), AspNetCore integration tests for the endpoint (happy path, page-size capping, typed-report
  400, filters applied/ignored-honestly per source type, unknown report 404).
- [x] **G6 — UI: report preview screen.** `/reports/{name}/preview`, linked from `ReportDetail.razor`.
  Grid of the sample rows (`DataGrid`, page-size selector), filter editor (hidden with an honest
  banner for typed reports; a config-driven report with no registered translator gets an honest
  inline note after the first filtered attempt, since the API only reports `filtersApplied` on the
  response, not upfront). "Run with these filters" disabled + explained whenever filters are
  active, since `POST /run` doesn't accept `Filters` yet (G5's deferred scope). Two scope cuts from
  the original sketch, both because G5 didn't ship the capability yet: no "Load more" pagination
  (`PreviewResponse` has no next-page cursor — G5 always reads page 1), and no upfront
  hidden-for-unsupported-sources filter editor (no "does this source support filters" query
  exists). Verified live against `samples/09-web-ui-live` in a real browser: registered a dynamic
  report, previewed it, applied a filter against a source type with no translator (the honest note
  appeared, sample stayed unfiltered, "Run now" stayed enabled).
- [x] **G7 — Fix: filtered preview never actually worked against a real relational database.**
  G5/G6's tests only ever asserted `AdoFilterTranslator`'s translated SQL *text* (no database) or
  went through fakes (`PreviewEndpointTests`) — no test executed a filtered preview against a real
  Postgres/MySQL/Oracle/SQL Server. Adding real Testcontainers coverage surfaced five distinct bugs,
  all before this feature had shipped: (1) `PreviewFilterRequest.Value` deserialized to a raw
  `JsonElement`, which no ADO.NET provider can bind — broke every filtered preview, every provider,
  regardless of column type; (2) Postgres has no implicit `text`→typed conversion in a comparison
  (the D43 keyset-cursor gap, but with no report-author SQL text to hand-write a cast into this
  time); (3) SQL Server rejects a bare `ORDER BY` inside a derived table (every keyset query ends
  with one) without `TOP`/`OFFSET`/`FOR XML`; (4) Oracle's implicit `VARCHAR2`→`NUMBER` conversion is
  session-NLS-dependent, failing on ordinary invariant-culture decimals like `"2000.00"`; (5) a
  `Contains`/`StartsWith` filter against a non-`String` column crashed with a raw provider error
  instead of an honest 400.
  A first-pass fix for (1) reused an existing `PrimitiveObjectConverter` that also recovers
  date-shaped strings as `DateTime` — wrong for filter values (an ordinary decimal like `"12.25"`
  parses as December 25), caught by automated code review before merge with a concrete repro; the
  actual fix narrows `PreviewFilter.Value`/`PreviewFilterRequest.Value` from `object?` to `string?`
  (a filter value is always its literal text, checked by the compiler) via a new, non-date-sniffing
  `FilterValueConverter`. See D45's "Fix (G7)" note for the full fix (schema-aware `IFilterTranslator`,
  per-provider `castParameter`/`innerQuerySuffix` on `AdoFilterTranslator`).
  **Known gap, not fixed here:** filtering Oracle's `Date` column (or any reserved-word column name)
  still fails (`ORA-01747`, an identifier-quoting issue, not a value-type one) — fixed as a follow-up,
  see G8. `OracleCast` still only covers `Integer`/`Decimal`/`Money`, leaving
  `Boolean`/`Uuid`/`Date`/`DateTime`/`Timestamp` uncast for the same "no single safe cast to guess"
  reason — remains open.
- [x] **G8 — Fix: Oracle reserved-word column names rejected in filter WHERE clauses.** Follow-up to
  G7's known gap. `AdoFilterTranslator` interpolated a filtered column bare (`t.{Column}`) into the
  outer `WHERE`; Oracle rejects a bare reference to a column colliding with a reserved word/datatype
  (e.g. `Date`) with `ORA-01747`. Fixed with an optional per-provider `quoteIdentifier` delegate on
  `AdoFilterTranslator`; Oracle's `OracleQuoteIdentifier` quotes only columns matching a curated
  reserved-word list (`"Date"`), leaving every other column bare — unaffected, since it matches
  Oracle's default case-folding of the report author's own unquoted inner SQL. See D45's "Fix (G8)"
  note.

## Epic H — Samples standardization + Aspire multi-DB samples (D46)

Requested directly by the maintainer (2026-07). The 9 existing samples grew organically (01-03
predate READMEs entirely; 04 and 09 independently invented near-identical in-memory fake-data
providers; csproj naming splits between folder-name-style for 01-06 and assembly-name-style for
07-09) — standardize before adding more, so the new Aspire samples don't add a 4th variant of the
same in-memory-source pattern and don't have to guess which naming convention to follow.

- [x] **H1 — `NeoReports.Samples.Shared` + standardize 01-09.** New non-packable samples-only
  project: a canonical `Sale` record (currently copy-pasted with minor doc-comment drift across
  01/02/03/06) and a generic, schema-driven `InMemoryBatchSource<T>`/config-source-provider pattern
  promoted from 09's (strictly more capable than 04's fixed-to-`Sale`/5-row version — 09's generates
  values per declared column type, not hardcoded to one shape). Migrate 01/02/03/06 to the shared
  `Sale`; migrate 04 to the shared generic provider (keeping its existing 5-row/4-column behavior
  and `report.json`-driven config, just backed by shared code instead of a local copy). Unify csproj
  naming onto the assembly-name style already used by 07-09 (`NeoReports.Samples.<Name>.csproj`) —
  chosen over folder-name style because it scales better once Aspire adds several same-shaped
  numbered samples. Add a minimal README to 01/02/03 (the only samples with none). Remove the
  `Nullable`/`ImplicitUsings` re-declarations in 01-07's csproj that `samples/Directory.Build.props`
  → `build/Directory.Build.props` already set. Every touched sample must still build and run
  end-to-end after migration (01/02/05 need a real SQL Server reachable to fully exercise, same as
  before this change — not a new requirement).
  **Found along the way:** migrating 04 changed its observed row count from 5 to 25 (the shared
  provider's own default) even with `report.json`'s `"rows": 5` unchanged — `PrimitiveObjectConverter`
  (`NeoReports.Core`) was silently re-boxing every whole JSON number as `double`, never `long`,
  because of C#'s switch-expression common-type unification (see CHANGELOG's "Fixed" entry); 04's
  old hardcoded fallback of 5 happened to equal its own configured value, masking the bug for as
  long as the sample existed. Fixed at the converter, with a regression test that checks the exact
  boxed type rather than numeric equality (the existing `DynamicConfigTests` assertion had used
  `ShouldBe`, which coerces across numeric types and so never caught this).
- [x] **H2 — Aspire seed generator.** A shared "wide + large" fake-row generator in
  `NeoReports.Samples.Shared` (reused by all 4 new DB samples in H3, so their seed shape/scale is
  identical rather than each DB sample inventing its own): `WideTransaction` (51 columns spanning
  every scalar `ColumnType` with an obvious CLR mapping — string/long/decimal/bool/DateTime/Guid,
  denormalized sales-transaction shape) and `WideTransactionGenerator.Generate(rowCount, seed)`, a
  deterministic (seeded `Random`) lazy (`yield return`) bulk generator defaulting to 500,000 rows —
  large enough to make NeoReports' constant-memory streaming a visible, honest selling point in a
  sample run, not so large a sample takes unreasonably long to seed/run in CI or on a contributor's
  machine. Verified: 500,000 rows generate in ~0.6s with no more than one row materialized at a
  time (the seeding step honors the same constant-memory principle the engine itself is built on);
  the same seed reproduces byte-identical output, so re-running a sample's seeding step is
  idempotent without persisting what was generated.
- [x] **H3 — Aspire multi-DB samples (one per provider, self-contained).** Four new numbered
  samples — `10-aspire-postgres-wide`, `11-aspire-mysql-wide`, `12-aspire-sqlserver-wide`,
  `13-aspire-mongodb-wide` — each its own Aspire AppHost (`Aspire.Hosting.PostgreSQL`/`.MySql`/
  `.SqlServer`/`.MongoDB` 9.5.2, CPM-versioned) that provisions one Docker container, seeds it via
  H2's shared generator on first run, and a `ReportRunner` project that reads the wide/large table
  back out through the matching G1-G4 source (`NeoReports.Sources.Postgres`/`.MySql`/`.Sql`/
  `.MongoDb`) and writes CSV+XLSX. Self-contained per DB (not one shared AppHost orchestrating all
  four) so a reader interested in only one provider doesn't need Docker images for the other three.
  Aspire pinned to the 9.x line (not the default `dotnet new` template's 13.x, which forces
  `net10.0`) — 9.5.2's `Aspire.AppHost.Sdk` targets `net8.0`, matching every other runnable sample
  in the repo. Verified end to end against real, standalone containers for all four providers (not
  just via Aspire orchestration): 500,000/500,000 rows read and both CSV+XLSX written successfully
  in every case. Found along the way, one per provider:
  - **Postgres**: `TIMESTAMP` (no time zone) rejects a `DateTimeKind.Utc` value outright; the
    generator's UTC rows need `DateTime.SpecifyKind(..., Unspecified)` before a binary-`COPY` write
    (the value is unchanged, only the Kind tag Npgsql validates is dropped). The keyset cursor needs
    an explicit `@cursor::uuid` cast, the same class of gap D43 hit for other column types.
  - **MySQL**: no native UUID type — `TransactionId`/`SessionId` are `CHAR(36)`, and the connection
    string needs `GuidFormat=Char36` or `RecordMaterializer`'s `Convert.ChangeType(string,
    typeof(Guid))` throws (`Guid` doesn't implement `IConvertible`). No bulk-copy ADO.NET API
    comparable to Postgres/SQL Server exists, so seeding uses batched parameterized multi-row
    `INSERT`s (200 rows/batch) instead.
  - **SQL Server**: `UNIQUEIDENTIFIER` sorts by its own particular byte-group order, not the
    left-to-right binary comparison Postgres/MySQL/MongoDB use — `ORDER BY TransactionId` returns a
    genuinely different row order than the same report against the other three providers (every row
    is still read exactly once; keyset correctness never promised a cross-database order). Seeding
    uses `SqlBulkCopy` in batches of 5,000 rows (a plain batched `INSERT` would exceed SQL Server's
    2,100-parameter-per-query limit at even a 40-row batch for this 51-column table).
  - **MongoDB**: `MongoDB.Driver`'s `GuidSerializer` throws (`"cannot serialize a Guid when
    GuidRepresentation is Unspecified"`) unless a representation is registered process-wide
    (`BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))`) before
    any write — recent driver versions dropped the old implicit-default behavior. Only affects
    seeding (the driver's own typed serializer); reads go through the engine's own
    `BsonDocumentMaterializer` (D44), unaffected.
  - Also found (Sonar S2245, not provider-specific): `WideTransactionGenerator`'s `TransactionId`/
    `SessionId` were originally built from `Random.NextBytes` — flagged as "use a cryptographically
    strong RNG" despite being synthetic sample data with no security purpose. Fixed by packing the
    row index and a per-field salt directly into the Guid's 16 bytes instead of going through
    `Random` at all (H2, landed before H3).
  - **Follow-up (2026-07-13):** each sample's headless `ReportRunner` (seeded the DB, ran the
    report once via `IReportRunner.RunAsync`, and exited) was replaced by a `Web` ASP.NET Core host
    that mounts the full NeoReports UI (`AddNeoReportsUI`/`UseNeoReportsUI`/`MapNeoReports`, same
    pattern as `09-web-ui-live`) alongside the typed `AddReport<WideTransaction>` registration —
    Aspire's job is now only provisioning the database and starting that UI; running the report,
    watching live progress, and downloading the file all happen by clicking through it instead of
    happening automatically on startup. Found in the process: the shipped `AppHost.cs` files were
    never actually launched end to end before H3 merged (only `ReportRunner`'s logic was verified,
    against manually-started containers) — both were missing `Properties/launchSettings.json`
    (`DistributedApplication.Build().Run()` throws immediately without `ASPNETCORE_URLS`/OTLP
    dashboard endpoint env vars, which Visual Studio surfaces as a console window that opens and
    closes with no output) and `UserSecretsId` (without it Aspire can't persist the generated DB
    password across runs, so a Docker volume reused via `WithDataVolume()` fails auth forever on
    any run after the first).
