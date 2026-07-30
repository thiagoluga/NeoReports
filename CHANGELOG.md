# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The `NeoReports.Abstractions` contract follows SemVer strictly.

## [Unreleased]

### Removed (breaking, Abstractions ABI — next major)
- **Removed the never-thrown exception types `BatchFailedException`, `SourceFailedException` and
  `ThresholdExceededException` from `NeoReports.Abstractions`.** They described a batch/source/
  threshold failure but were never thrown anywhere: the pipeline reports those failures through
  `ReportRunResult.Status` + its error string and the `IFailureStrategy` decision, not by throwing.
  As dead surface in a frozen ABI (rule 7) they were a liability. `NeoReportsException` (the base)
  and `ConfigurationException` are unchanged and still used. This is source-breaking for any consumer
  that referenced those three types (nothing ever threw them, so no `catch` for them could have
  fired) and is therefore slated for the next **major** release.

### Changed (breaking, public API — next major)
- **`CancellationToken` moved to the last parameter** in the three public health-check helpers that
  had it mid-list (CA1068): `AdoSourceHealth.PingAsync(connectionFactory, timeout, pingSql,
  cancellationToken)`, `AdoSourceHealth.CheckConnectionStringAsync(definition, connectionFactory,
  timeout, pingSql, cancellationToken)` and `HttpHealthProbe.SendAsync(client, method, targetUrl,
  auth, content, cancellationToken)`. The token now has a `default`, so most callers are unaffected,
  but positional callers that passed the token before the trailing `pingSql`/`content` argument are
  source-breaking; bundled with the removal above for the next **major** release.

### Added
- **Streaming XLSX output at constant memory (resolves D14).** Both the MIT single-sheet XLSX writer
  and the Pro multi-sheet workbook writer are rebuilt on `DocumentFormat.OpenXml`'s SAX writer and a
  hand-assembled `ZipArchive`, streaming each worksheet to a temp file and deflating straight to the
  output — bypassing `System.IO.Packaging`'s in-memory buffer. Measured live memory is flat writing
  100k→2.4M rows. ClosedXML is removed from both writer packages. The only behavioural change is the
  dropped column auto-fit. (`AdjustToContents` can't stream.)
- **Opt-in `AddNeoReportsStartupValidation()`** compiles config-driven reports at host startup so a
  malformed document fails fast at boot rather than on the first request.
- **`ReportDeadlineExceededException`** (in `NeoReports.Core.Pipeline`), thrown when a run exceeds its
  configured `Deadline`. It derives from `OperationCanceledException`, so every existing catch site is
  unaffected and a deadline still surfaces as a cancelled run — the distinct type exists because the
  caller's own token is *not* cancelled on a deadline, which left it indistinguishable from an
  unrelated `OperationCanceledException` (an `HttpClient.Timeout`, say — a genuine failure).
- **Parameterless `ReportBuilder<T>.Retry()`** enabling a sensible production default (3 attempts,
  exponential backoff from 1s, jitter). Retries remain **off by default** — this lowers the barrier
  to turning them on and the docs now flag the recommendation for production network sources.

### Changed
- **Startup warning when the API is mapped without authentication.** `MapNeoReports()` logs a warning
  when neither host authentication nor `RequireAuthorization` is configured (auth still inherits from
  the host — D20 — this is a nudge, not a behaviour change).

### Fixed
- A job whose source times out is now recorded as **Failed**, not "Cancelled". The worker caught
  every `OperationCanceledException`, including the `TaskCanceledException` an `HttpClient.Timeout`
  raises (its token is not the run's), so a real failure was labelled as an operator-initiated
  cancellation with its error detail discarded — and, because that path does not rethrow, Hangfire
  recorded the run as succeeded. Only a cancellation from the run's own token takes that path now.
- Concurrent saves of the same report's schedule override — or of the same report config — no longer
  collide: both file stores staged every write through a temp path derived only from the report name,
  so two overlapping saves shared it (sharing violation, or a `FileNotFound` when the second move
  found the first had taken it). Both now share `AtomicFileWrite`, whose temp name is unique per
  write and cleaned up if the save fails.
- XLSX output no longer corrupts on edge-case cell values: an XML-illegal control character in a
  string is stripped (previously it threw and aborted the whole file), `NaN`/`Infinity` are written
  as text (previously an un-openable number cell), `byte[]` is Base64 (previously `"System.Byte[]"`),
  and `TimeOnly` uses an invariant format (previously the server's culture). `byte[]` is Base64 in the
  CSV writer too.
- Pro `QueryBuilder` Oracle keyset on a `DATE`/`TIMESTAMP` key no longer crashes on the second page:
  the generated SQL now casts the ISO-8601 cursor with `TO_TIMESTAMP(:cursor,
  'YYYY-MM-DD"T"HH24:MI:SS.FF7')` instead of leaving it bare (Oracle's `NLS_DATE_FORMAT` implicit
  conversion threw `ORA-01858`).
- Keyset cursor now encodes `DateTime`/`byte[]` keys type-faithfully (was corrupting/duplicating rows).
- Local destination blocks path traversal via run-time parameters.
- A failed upload now fails the run instead of reporting success.
- Run-time parameters override same-named static parameters; `@name` matched on an identifier boundary.
- Health/sync-run error responses are scrubbed of connection details (logged server-side instead).
- Multi-artifact zip downloads stream at constant memory (were buffered in a `MemoryStream`).
- Run failures are surfaced through `ILogger` (scoped with job id + report name), not only the event store.

### Changed (breaking, commercial packages only)
- **The Pro packages now require a license key at run time (D70/Epic Q).** `NeoReports.Xlsx.Pro`,
  `NeoReports.Sources.Join.Pro` and `NeoReports.QueryBuilder.Pro` previously had **no runtime
  enforcement** — they were gated only by not being published and by the PolyForm terms themselves
  (D29/D30). With the Pro packages moving to public distribution, that gate disappears, so a valid
  license is now checked in code. Supply one by setting the `NEOREPORTS_LICENSE_KEY` environment
  variable, or explicitly at startup with `services.AddNeoReportsProLicense(key)` (dependency
  injection) or `NeoReports.Licensing.ProLicenseGate.Register(key)` (code-first, no container).
  Without a valid key the Pro packages throw `NeoReportsLicenseException` — deliberately loud and
  immediate rather than a silent degrade (D36 posture).
  - Enforced on **both** entry paths: the DI registrations (`AddXlsxWorkbook()`,
    `AddMergeJoinConfigSource()`, `AddQueryBuilder()`) **and** the static fluent APIs typed
    code-first reports use (`Format.XlsxWorkbook(...)`, `Join.MergeJoin(...)`, `.Enrich(...)`,
    `KeysetSqlGenerator.Generate(...)`), which never touch a DI container.
  - The signature is verified once per process; the license's **validity window is re-checked on
    every call**, so a long-running host cannot outlive its own trial without restarting.
  - Samples `06-multi-sheet-xlsx` and `07-multi-source` still **build** without a license but now
    need one to **run**; both READMEs say so.
  - The **OSS packages are unaffected** — nothing outside the three Pro packages requires a license.

### Added
- **`NeoReports.Licensing` — offline Pro license validation (D70/Epic Q).** New **MIT** package (so
  the verification logic is publicly auditable — there is no hidden phone-home) that validates an
  ECDsa P-256-signed license key **fully offline**: no network call at run time, which keeps Pro
  usable in the unattended, restart-from-zero execution model the engine is built around (D6). One
  "bundle" license unlocks all three Pro packages together. Ships with no new dependency beyond
  `Microsoft.Extensions.DependencyInjection.Abstractions` — the crypto comes from the BCL. Public
  surface: `LicenseToken`, `LicenseValidator`, `ProLicense`, `ProLicenseGate`, `LicenseSigner`
  (license-issuing tooling only), `NeoReportsLicenseException` (+ a `LicenseFailureReason` enum, so a
  caller can tell "no license" from "expired" without matching on message text).
  Honest gaps, documented rather than hidden: offline validation cannot detect a rolled-back system
  clock, there is no revocation list, and the license is not bound to a machine.
- **Query builder: run the built query and see its rows (D49/Epic K, K6b).** The visual query-builder
  screen now has a **Run preview** button beside **Generate SQL**; it calls the new
  `POST /sources/{name}/query-preview` and renders a **Query result** grid of the built query's own
  rows (with a "more rows exist" note when the sample fills the cap), closing the
  build→see-output→adjust loop. Honest states are preserved: "not available on this host" (422 — no Pro
  package), the engine's caller-safe validation message (400), and a generic "couldn't run against the
  source" for an engine/database error. New API-client method `TryPreviewQueryAsync` (+ fake). The
  raw-SQL escape-hatch tab is intentionally not previewed (it would run hand-written SQL).
- **Query builder: bounded result preview endpoint (D49/Epic K, K6a).** New
  `POST /sources/{name}/query-preview` runs a read-only sample of a visually-composed query and
  returns its columns, a capped page of rows, and a `truncated` flag. It takes the query **model JSON**
  (the same body as `query-sql`) and generates the keyset SQL **server-side** — no raw caller SQL is
  ever executed — then reads one bounded page through the source's own keyset provider (new Core
  `QueryPreviewRunner`, the query-side sibling of the report preview runner). Honest states: 422 when
  the Pro query-builder package isn't registered, 400 on an invalid model, and a secret-free 502 if the
  source's database can't be read. The UI result grid and a "create report from this query" handoff
  ride on this in follow-ups (K6b/K6c).
- **Builder: insert-token helper for the destination path (D51/Epic M).** The Builder's
  "Choose a destination" step now offers clickable **Insert token** buttons below the path/key
  template field — `{name}`, `{ext}`, `{date}`, `{date:yyyy-MM-dd}`, `{date:yyyyMMdd-HHmmss}`
  (a date+time stamp), `{date:yyyy/MM/dd}` — each appending its token to the template, plus a hint
  that tokens resolve at run time (and any run-time parameter is available as `{paramName}`). The
  tokens mirror exactly what the Local/S3 destinations' `PathTemplate.Expand` recognizes. A live
  resolved-filename preview is deferred (it needs a small engine endpoint to reuse `Expand` without
  the decoupled UI reimplementing it and risking drift).
- **Visual query builder UI (D49/Epic K, K5b, Pro).** A new **Query builder** screen
  (`/query-builder`) composes a query visually and generates keyset-safe SQL. A schema explorer over
  the source's catalog (searchable tree, PK/FK icons, an "already used" marker, per-table 50-row
  previews) feeds a notebook of step cards — FROM (keyset key defaults to the table's PK), JOIN (FK
  auto-detected, `ON` pre-filled), Columns (with aggregates), WHERE (structured, parameter-bound) —
  and a live generated-SQL panel through `POST /sources/{name}/query-sql`; a Raw-SQL escape-hatch tab
  carries the honest caveat banner. On a host without the Pro query-builder package the panel says the
  builder isn't available (422) instead of faking it. New UI API-client methods
  (`TryGetSourceCatalogAsync`/`TryPreviewSourceTableAsync`/`TryGenerateQuerySqlAsync`). Adding a table
  uses a `+` button (not drag-drop) and SQL generates on an explicit click — both to keep the Blazor
  Server circuit quiet. The D49 result-preview grid of the built query's own rows is deferred (it needs
  an ad-hoc-SQL preview endpoint that doesn't exist yet); per-table samples and the generated SQL are
  available now.
- **Visual query builder: generate-SQL seam + endpoint (D49/Epic K, K5a, Pro).** The MIT UI and
  endpoints stay decoupled from the commercial generator via a new Core contract,
  `NeoReports.Core.QueryBuilder.IQuerySqlGenerator` — the visual query model crosses the seam as
  opaque JSON, so no MIT layer references a Pro type (the same capability-gating pattern as
  `ISchemaExplorer`/`IFilterTranslator`). `NeoReports.QueryBuilder.Pro` implements it (register with
  `AddQueryBuilder()`), and a new `POST /sources/{name}/query-sql` endpoint compiles a visual query
  into keyset-safe report SQL (`{sql, parameters, schema}`); honest states for no registry (409),
  unknown source (404), no generator registered — i.e. an MIT-only host (422), and an empty or invalid
  model (400, with a caller-safe message). No UI yet (K5b).
- **`NeoReports.QueryBuilder.Pro` — visual query builder engine (D49/Epic K, K4, Pro).** A new
  commercial (PolyForm Small Business) package with a structured `QueryModel` (source + inner/left
  joins + columns + WHERE + GROUP BY/aggregation + keyset key) and a `KeysetSqlGenerator` that turns
  it into keyset-safe report SQL. Injection-safe by construction — identifiers are quoted per dialect,
  WHERE values are bind-parameter placeholders (never inlined), and the keyset wrapper is always
  appended, so a generated query is always valid. Feeds the interactive query builder (D49); no UI
  yet. Not in the OSS NuGet release (`IsPackable=false`, D30).
- **Schema-explorer HTTP endpoints (D49/Epic K, K3).** `GET /sources/{name}/catalog` (a source's
  tables/columns/PK/FK) and `GET /sources/{name}/preview?schema=&table=&top=` (a table's first N
  rows, capped server-side at 50). Both resolve the named source through the registry (D42) and
  delegate to the source type's `ISchemaExplorer`; honest states for no registry (409), unknown
  source (404), a source type with no explorer (422, e.g. MongoDB), missing table (400), or a
  database error (502). Backs the interactive query builder (D49); no UI yet.
- **Schema introspection capability (D49/Epic K, K2).** New `NeoReports.Core.Schema.ISchemaExplorer`
  — a per-source-type engine capability (like `IFilterTranslator`/`ISourceHealthCheck`) that reads a
  registered source's catalog (tables, columns, nullable/PK, foreign keys) and previews a table's
  first N rows. One shared `AdoSchemaExplorer` covers the SQL family, parametrized by dialect;
  registered automatically by `AddPostgresConfigSource`/`AddMySqlConfigSource`/`AddSqlConfigSource`/
  `AddOracleConfigSource`. MongoDB is not covered (no SQL/`information_schema`). This is the engine
  foundation for the interactive query builder (D49); no UI/endpoints yet.
- **Report detail: show the referenced named source (D52/N2).** `GET /reports/{name}` now returns
  `sourceRef` — the named source's name (ADR D42) whenever a report references one (a `Ref`-based
  dynamic source, or a code-first report built with `Source.SqlNamed`/its equivalents), `null` for
  an inline connection — additive on `ReportDetailView`. `ReportDetail.razor` shows it as a
  `source: {ref}` chip next to the existing `origin` chip. `CompiledReport.SourceRef` was already
  captured by the compiler; this just exposes it. Not a secret (D42's write-only rule applies to
  the registry's property bag, not to which named source a report references).
- **Report detail: show the real buffer/page size (D52/N1).** `ReportDetail.razor`'s Configuration
  card now shows `PageSize` ("N rows/page", matching `JobCompleted.razor`'s own wording). Retry
  policy and abort-threshold fields were already shown via `ResilienceFormatter` — D52's original
  audit predated that (D37) and was stale on that point.
- **Regression test for `Ref`-based preview filtering (D54).** Added
  `PreviewEndpointTests.Filters_against_a_Ref_based_source_resolve_the_translator_from_the_registered_type`,
  closing a real coverage gap: every existing filter-translator test used an inline source `type`,
  never `ref`. Investigated a maintainer-reported "doesn't support server-side filters" banner on a
  Postgres-sourced report — the engine's `Ref` → registered-type → translator resolution is proven
  correct by this test; no engine defect found.
- **bUnit component test suite for the Blazor UI (D53).** All 15 pages and the interactive shared
  components in `NeoReports.UI` now have bUnit coverage (138 new tests; 198 in the whole project
  including pre-existing pure-logic tests) — engine-unreachable vs.
  live-empty vs. populated states, two-click delete confirmation, the Builder wizard's
  create-vs-edit persist logic, the Jobs list's stale-response guard, and the Preview screen's
  filter wiring. Test-only change (`tests/NeoReports.UI.UnitTests`); no production code changed.
  `bunit` added to `build/Directory.Packages.props`.
- **`samples/14-aspire-all-sources-demo` — combined all-sources Aspire demo (D48).** A new,
  additive sample orchestrating all four database types (PostgreSQL, MySQL, SQL Server, MongoDB)
  from one Aspire `AppHost` and mounting one `NeoReports.UI` in front of all of them —
  `dotnet run --project samples/14-aspire-all-sources-demo/AppHost`. Registers a config-source
  provider for every type (`AddSqlConfigSource`/`AddPostgresConfigSource`/`AddMySqlConfigSource`/
  `AddMongoDbConfigSource`), so `GET /api/capabilities` is never empty and the UI's "Demo mode"
  banner never appears; pre-registers all four databases as named sources in the Source Registry
  (D42) so the Builder wizard can build new reports against any of them by name; registers dynamic
  reports (`AddDynamicReports`) and scheduling (`AddScheduling`) so both work end to end; ships one
  ready-to-run typed report per database (`wide-transactions-{postgres,mysql,sqlserver,mongodb}`).
  Also registers `IWriterFactory`/`IDestinationFactory` (CSV, XLSX, Local) and `AddPartialArtifacts`
  (D40) — the same "empty capabilities" gap that hid sources also hid output formats and
  destinations from the Builder wizard, independent of the typed reports' own `.To(...)` calls.
  Seeds all four databases in parallel at 15,000 rows each. The four existing single-provider
  samples (`10`-`13`) are unchanged.
- **Real progress percentage (D47).** Reports can now report a real completion percentage instead
  of the previous decorative animation: `ReportConfig.TrackProgress` / typed
  `ReportBuilder<T>.TrackProgress(bool)` — **enabled by default** — makes the engine count the
  source's total rows once before each run (SQL-family and MongoDB sources support it out of the
  box); `JobStats.TotalRecords` and a `totalRecords` event datum carry the total through to
  `GET /jobs/{id}` and `/jobs/{id}/events`. Disabled, unsupported, or failed counts degrade to
  `null`/indeterminate — never fails the run. **Behavior change on upgrade:** because tracking
  defaults to enabled, every existing report — typed and dynamic, no code change required — starts
  issuing one extra `COUNT` query per run. Set `.TrackProgress(false)` (typed) or
  `"trackProgress": false` (dynamic config) to restore the previous behavior. `NeoReports.UI`'s
  Builder gains a default-on "Track progress" switch (with an honest off-state warning) and the
  running-job page now shows a real, clamped percentage — falling back to an indeterminate sliding
  bar when no total is known.
- `NeoReports.UI` — a report preview screen (`/reports/{name}/preview`, D45), linked from
  `ReportDetail.razor`'s new "Preview" button. Shows a read-only sample via
  `POST /reports/{name}/preview` in a `DataGrid`, a page-size selector (10-200 rows), and — for
  dynamic (config-registered) reports — a structured filter editor (column/operator/value rows,
  closed operator list, "Add filter"/remove/"Apply"). A code-first report hides the filter editor
  entirely behind an honest banner explaining it has no structured source to filter (D36 pattern);
  a dynamic report whose source type has no registered translator shows an honest inline note
  after the first filtered attempt rather than upfront (the API has no "does this source support
  filters" capability query yet, only `filtersApplied` on the response, so the note can only appear
  after a real attempt) and still runs the unfiltered sample. "Run now"/"Run with these filters" is
  disabled with an explanatory note whenever filters are actually applied — `POST /run` doesn't
  accept `Filters` yet (deferred in G5), so offering a control that would 400 would be dishonest.
  No "Load more" pagination in this pass — `PreviewResponse` carries `hasMore` but no cursor for a
  second page (G5 always reads page 1 only), so the sample subtitle says "more rows exist" rather
  than offering a button that can't actually fetch them; deferred alongside filtered-run support.
- `POST /reports/{name}/preview` — a bounded, read-only sample of one page of a report: no output
  writing, no upload, no job record (D45). Reuses the exact reader machinery a real run uses for an
  unfiltered sample, so the preview matches what the report would actually write. Optional structured
  filters (`PreviewFilter`/`PreviewFilterOperator` in `NeoReports.Core`, a closed enum — never a
  free-form expression) apply only to dynamic (config-registered) SQL-family reports whose source type
  has a registered `IFilterTranslator` (implemented once in `NeoReports.Sources.Common` as
  `AdoFilterTranslator`, registered by `Sql`/`Postgres`/`MySql`/`Oracle`); MongoDB and any source
  without a translator run the sample unfiltered and the response says so honestly
  (`filtersApplied: false`) rather than silently dropping the filters. A typed (code-first) report has
  no structured source to filter — a non-empty `filters` array against one returns 400. Filters are
  ephemeral: never persisted, applied for that one call only. `RunReportRequest` gains an additive
  `Filters` field for parity, but a full filtered *run* (as opposed to a preview sample) is deferred —
  it needs a temporary re-compiled report threaded through the job/scheduler pipeline, a separate
  piece of work; `POST /run` returns 400 on a non-empty `Filters` until then. `AdoFilterTranslator`
  wraps the original keyset query as a derived table (`SELECT * FROM (<sql>) t WHERE ...`, no `AS`
  keyword before the alias — Oracle rejects it for derived tables, every other dialect accepts an
  alias with or without it) and binds filter values through the existing
  `ReportExecutionContext.Parameters` mechanism `AdoKeysetSource` already merges into its query — no
  new ADO plumbing needed. `PreviewFilter.Value`/`PreviewFilterRequest.Value` are `string?`, not
  `object?` — a filter value is always its literal text form, matching exactly what the preview UI's
  plain text input sends regardless of the filtered column's real type, checked by the compiler
  rather than merely documented. A new `FilterValueConverter` decodes `PreviewFilterRequest.Value` as
  that literal text however it arrived in JSON (string verbatim, a number's exact written digits, a
  boolean as `"true"`/`"false"`) with no date-sniffing — unlike the existing `PrimitiveObjectConverter`
  (used for config/parameter property bags), which would otherwise silently reinterpret an ordinary
  decimal like `"12.25"` as a `DateTime` (December 25) before it ever reached a translator. Because
  every filter value is now guaranteed text, `IFilterTranslator.TryTranslate` also receives the
  report's `ReportSchema` and `AdoFilterTranslator` takes optional per-provider `castParameter`/
  `innerQuerySuffix` hooks: Postgres casts the bind parameter to the column's real type
  (`{token}::{type}` — no implicit `text` conversion), SQL Server appends `OFFSET 0 ROWS` to the
  inner query (a derived table can't contain a bare `ORDER BY`, which every keyset query ends with,
  without `TOP`/`OFFSET`/`FOR XML`), and Oracle casts numeric columns via `TO_NUMBER` with an
  explicit format model (its implicit `VARCHAR2`→`NUMBER` conversion is session-NLS-dependent, so
  `"2000.00"` can fail with `ORA-01722` against a session that doesn't treat `.` as the decimal
  separator; verified empirically that a negative value like `"-1"` already parses correctly with
  this plain format, with no sign element needed); MySQL needs neither. A `Contains`/`StartsWith` filter against a non-`String` column now
  makes the translator decline (an honest 400) instead of emitting an uncastable `LIKE` comparison
  that crashed with a raw provider error. New Core unit tests for `AdoFilterTranslator` (SQL
  wrapping, all eight operators, `LIKE` wildcards bound as parameter values not string-concatenated,
  Oracle's `:` prefix, per-provider casting/suffix behavior, `LIKE`-on-non-string-column rejection)
  and AspNetCore integration tests for the endpoint (happy path, page-size capping, typed-report 400,
  filters applied/ignored-honestly per source type, unknown report 404, filter values decoded as
  literal text — including a date-shaped decimal that must survive unreinterpreted). New
  Testcontainers-backed integration tests per relational provider exercise a filtered preview against
  a real database end to end (Postgres/SQL Server/Oracle/MySQL) — the gap that let the
  `JsonElement`/cast/`ORDER BY`/NLS issues above ship unnoticed in the first place, since the existing
  `AdoFilterTranslator` tests only asserted translated SQL text and the endpoint tests only used
  fakes. A follow-up (G8) closed the one gap those tests left open: filtering an Oracle column whose
  name collides with a reserved word (e.g. `Date`) failed with `ORA-01747`, since the filtered
  column was interpolated bare into the outer `WHERE`. `AdoFilterTranslator` gained an optional
  per-provider `quoteIdentifier` hook; Oracle registers `OracleQuoteIdentifier`, which quotes only
  columns matching a curated reserved-word list, leaving every other column bare.
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
- Samples — `NeoReports.Samples.Shared` gains `WideTransaction` (51 columns spanning
  `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and `WideTransactionGenerator` (Epic H): a
  deterministic, lazily-streamed generator defaulting to 500,000 rows, so seeding a demo database
  never materializes more than one row at a time. Four new self-contained samples pair it with real
  Docker databases orchestrated by [.NET Aspire](https://learn.microsoft.com/dotnet/aspire/) — no
  manual setup beyond `docker` itself: `10-aspire-postgres-wide`, `11-aspire-mysql-wide`,
  `12-aspire-sqlserver-wide`, `13-aspire-mongodb-wide`. Each provisions its own container, seeds it
  idempotently on first run, and mounts the full NeoReports UI (same pattern as `09-web-ui-live`)
  with a typed `wide-transactions` report already registered against the matching G1-G4 source and
  the usual constant-memory keyset pagination — Aspire's job is standing up the database and
  starting the UI, running the report/watching progress/downloading CSV and XLSX all happen by
  clicking through it. Verified end to end against real containers for all four providers.
- Samples — standardized 01-09 (Epic H): a new non-packable `NeoReports.Samples.Shared` project
  holds a canonical `Sale` record (was copy-pasted across 01/02/03/06) and promotes 09's generic,
  schema-driven `InMemorySalesSourceProvider` as the one in-memory dynamic-config source, replacing
  04's own fixed copy. csproj naming for 01-06 unified onto the assembly-name style already used by
  07-09. Minimal READMEs added to 01/02/03 (the only samples with none).

### Fixed
- `NeoReports.AspNetCore` — a dynamic (config-registered) report that references a **named source**
  (`"source": {"ref": "..."}`, ADR D42) always failed its first async job run with
  `Cannot access a disposed object. Object name: 'IServiceProvider'.` (never on a `?mode=sync` run,
  since that completes inside the same request). `POST /reports` compiled the report using
  `http.RequestServices` — a per-HTTP-request scoped provider — and the compiled report's
  `RefBatchSource` captured that same provider for later, lazy per-run source resolution; but the
  compiled report is registered into the singleton report registry and an async job runs on its own
  background task, well after the triggering request (and its DI scope) has ended. Fixed by
  resolving reports through the app's root `IServiceProvider`
  (`IEndpointRouteBuilder.ServiceProvider`, captured once in `MapNeoReports`) instead of the
  request-scoped one, in both `POST /reports` and `POST /reports/validate`. New regression test
  (`DynamicReportEndpointsTests.Ref_based_report_runs_to_completion_on_an_async_job_after_the_creating_request_ends`)
  creates a named source, registers a report referencing it, and runs it asynchronously to
  completion — reproduced the exact reported error before the fix.
- `NeoReports.Core` — `PrimitiveObjectConverter` (the JSON reader for `object?` property-bag
  values used by dynamic report/source-registry config) silently re-boxed every whole JSON number
  as `double`, never `long`, even though `Utf8JsonReader.TryGetInt64` correctly succeeded — the
  switch expression's success arm returned `long`, its fallback arm returned `double`, and C#
  unifies a switch expression's arms to one common type, silently widening `long` to `double` on
  every successful integer parse. Found while migrating sample 04 to a shared, schema-driven
  in-memory source provider (Epic H): its `raw is long n` row-count check had *always* silently
  failed, for every whole-number `properties` value, in every dynamic-config report ever run
  against this converter — masked because the sample's own hardcoded fallback (5) happened to
  equal its `report.json`'s configured `"rows": 5`. No shipped source/writer/destination reads a
  numeric config property this way today, so the practical blast radius was limited to this
  sample, but the bug affected the converter itself, not sample code. Fixed with an explicit
  `(object)` cast on the `long` arm; new `PrimitiveObjectConverterTests` assert the exact boxed
  type (`ShouldBeOfType<long>`), not just numeric equality — `DynamicConfigTests`' existing
  `props["limit"].ShouldBe(10L)` assertion had silently accepted a boxed `double` all along,
  since Shouldly's `ShouldBe` coerces across numeric types for comparison.

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
