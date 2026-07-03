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

**Out of scope for the epic** (recorded in D33/blueprint): `PUT` (edit), scheduling/recurring,
real progress percentage, source introspection (schema/preview), settings screens, variants.
