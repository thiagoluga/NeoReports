# Epic G — More sources + report preview

Blueprint for D43/D44/D45. Requested directly by the maintainer (2026-07), not derived from the
hardcoded-UI audit. Read `DECISIONS.md` D43-D45 first — this doc is the task breakdown, not a
restatement of the decisions.

## Ground rules

- `Abstractions` untouched by every task in this epic (frozen ABI, D1/D6). New wire shapes
  (`Filters` on `RunReportRequest`, the preview endpoint's request/response) live in
  `NeoReports.AspNetCore.Contracts`, not `Abstractions`.
- Each new source package (`Postgres`/`MySql`/`Oracle`/`MongoDb`) mirrors `NeoReports.Sources.Sql`'s
  existing public shape exactly: `Source.<Provider>(...)`/`Source.<Provider>Named(...)`,
  `<Provider>ConfigSourceProvider`, `<Provider>SourceHealthCheck`, `Add<Provider>ConfigSource()`.
  Don't invent a different shape per provider — copy-and-adapt from `Sources.Sql`.
- `NeoReports.Sources.Sql` itself is not touched by G1 (D43) — no behavior change, no refactor.
- New NuGet dependencies go in `build/Directory.Packages.props` (CPM), never inline.
- Package license: MIT, same tier as `Sources.Sql` (maintainer call, D42 precedent).

## G1 — `NeoReports.Sources.Common` + PostgreSQL

### `NeoReports.Sources.Common` (new package)

Extract `SqlKeysetSource<T>`'s engine into a provider-agnostic `AdoKeysetSource<T>` — same
constructor shape, but takes `Func<DbConnection> connectionFactory` instead of a connection string
+ hardcoded `new SqlConnection(...)`. Everything else (`DbCommand`/`DbDataReader`/`DbParameter`
usage, ordinal mapping, cursor encode/decode, parameter merging) copies over unchanged — it was
already provider-agnostic. Also move `RecordMaterializer<T>` here (reflection-based POCO
materializer from a `DbDataReader`, no provider-specific code).

A shared `AdoSourceHealthCheckBase` (or a small static helper) for "open a `DbConnection`, run
`SELECT 1`, measure latency, 10s timeout" — the exact same logic `SqlSourceHealthCheck` has today,
parametrized by connection creation.

### `NeoReports.Sources.Postgres` (new package, `Npgsql`)

- `Source.Postgres(connectionString, sql)` → `.Keyset<T,TKey>(keySelector, pageSize)` →
  `AdoKeysetSource<T>` with `() => new NpgsqlConnection(connectionString)`.
- `Source.PostgresNamed(sourceName, sql)` → same `INamedSourceResolver` pattern as
  `NamedSqlKeysetSource<T>` (F5) — copy its structure, swap the connection factory.
- `PostgresConfigSourceProvider : IConfigSourceProvider` (`Type => "postgres"`), reading
  `connectionString` from `SourceConfig.Properties` like `SqlConfigSourceProvider`.
- `PostgresSourceHealthCheck : ISourceHealthCheck` (`Type => "postgres"`).
- `AddPostgresConfigSource()` DI extension, registering both the config provider and the health
  check (`TryAddEnumerable`, mirrors `AddSqlConfigSource()`).

### G1 tests

- `NeoReports.Sources.Postgres.IntegrationTests` (Testcontainers.PostgreSql, `[SkippableFact]`):
  same three tests `SqlKeysetSourceTests` has (all pages in order/no gaps/no dupes, typed column
  materialization) plus a health-check test (healthy/missing-property/unreachable, matching
  `SqlSourceHealthCheckTests`).
- No test project needed for `Sources.Common` alone — it's exercised transitively by every
  provider's integration tests; a unit test for `AdoKeysetSource<T>`'s cursor encode/decode logic
  against a fake `DbConnection` is optional, not required (the SQL Server suite already proves the
  algorithm; duplicating it as a fake-based unit test is diminishing returns).

## G2 — MySQL/MariaDB (`NeoReports.Sources.MySql`, `MySqlConnector`)

Same shape as G1's Postgres package, built on the now-shared `AdoKeysetSource<T>`:
`Source.MySql(...)`/`Source.MySqlNamed(...)`, `MySqlConfigSourceProvider` (`type: "mysql"`),
`MySqlSourceHealthCheck`, `AddMySqlConfigSource()`. Tests: `Testcontainers.MySql`, same suite shape
as G1.

## G3 — Oracle (`NeoReports.Sources.Oracle`, `Oracle.ManagedDataAccess.Core`)

Same shape again: `Source.Oracle(...)`/`Source.OracleNamed(...)`, `OracleConfigSourceProvider`
(`type: "oracle"`), `OracleSourceHealthCheck`, `AddOracleConfigSource()`. Watch for Oracle-specific
SQL dialect quirks in the test query (`ROWNUM`/bind variable syntax differs from `@cursor` — Oracle
uses `:cursor`; `AdoParameter` naming needs to stay provider-agnostic, so confirm `AddParameter`'s
`"@" + name` token-detection still works for Oracle's `:name` style, or special-case it). Tests:
`Testcontainers.Oracle` (the `gvenzl/oracle-free` image is the common choice — slower container
startup than SQL Server/Postgres, still `[SkippableFact]`-gated).

## G4 — MongoDB (`NeoReports.Sources.MongoDb`, `MongoDB.Driver`)

Standalone design (D44) — no shared engine with G1-G3.

- `Source.MongoDb(connectionString, database, collection)` → `.Keyset<T,TKey>(keySelector,
  pageSize)`. Cursor is the last document's key field, serialized via
  `BsonValue`→string (mirrors the SQL sources' `string?` cursor contract exactly — opaque to the
  pipeline).
- Page read: `collection.Find(cursor is null ? FilterDefinition.Empty : Builders<T>.Filter.Gt(keyField,
  decodedCursor)).Sort(Builders<T>.Sort.Ascending(keyField)).Limit(pageSize).ToListAsync()`.
- `MongoDbConfigSourceProvider` (`type: "mongodb"`), reading `connectionString`/`database`/
  `collection` from properties.
- `MongoDbSourceHealthCheck` via `RunCommandAsync(new BsonDocument("ping", 1))`.
- No filter translation in this pass (D44) — the preview UI (G6) must detect "this source type has
  no filter translator" and show the sample read-only with an explanatory note, not silently drop
  filters.

### G4 tests

`Testcontainers.MongoDb`, `[SkippableFact]`: pages in order/no gaps/no dupes (seed ~2500 documents
like the SQL fixture), typed materialization, health check.

## G5 — Core + AspNetCore: preview endpoint + structured filters

- Core: `PreviewFilter(string Column, PreviewFilterOperator Operator, object? Value)` +
  `enum PreviewFilterOperator { Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan,
  LessThanOrEqual, Contains, StartsWith }` — lives in `NeoReports.Core` (not `Abstractions`; not a
  frozen concept, and previews are an engine-hosting-layer feature like job events/D38).
- A new per-source-type translation seam: `IFilterTranslator` (Core interface, resolved from DI by
  provider type exactly like `IConfigSourceProvider`/`ISourceHealthCheck`) with
  `bool TryApply(DbCommand command, string sql, IReadOnlyList<PreviewFilter> filters, out string
  translatedSql)` — implemented once in `NeoReports.Sources.Common` (shared across Sql/Postgres/
  MySql/Oracle, since the WHERE-fragment-append logic is identical ADO.NET regardless of dialect)
  and never implemented for MongoDB in this pass.
- `NeoReports.AspNetCore`: `POST /reports/{name}/preview` — body `{ filters: [...], pageSize }`
  (`pageSize` capped server-side, e.g. max 200); 404 unknown report; runs one page through the
  report's compiled source (reusing `TypedBatchReader`/`RefBatchSource`'s existing machinery — a
  preview is functionally "run one page and don't write outputs"); response carries the rows
  (schema-projected, same `object?[]` shape writers already consume), the schema, whether filters
  were applied or ignored (source type has no translator), and whether more pages exist.
  `RunReportRequest` gains an additive `Filters` field, applied the same way for a real run — one
  job, filters not persisted anywhere.
- Typed (code-first) reports: preview works (unfiltered sample only); `POST .../preview` with a
  non-empty `filters` array on a typed report returns 400 (filters need a structured source
  representation typed reports don't have).

### G5 tests

Core unit tests for `PreviewFilter`→SQL WHERE translation (parametrized, never string-concatenated
— assert the built `DbCommand.Parameters`, not just the SQL text) using a fake `DbConnection`;
AspNetCore integration tests: preview happy path (rows come back, capped page size honored),
filters applied/ignored-honestly per source type, typed-report 400 on non-empty filters, unknown
report 404.

## G6 — UI: report preview screen

- New route, e.g. `/reports/{name}/preview`, linked from `ReportDetail.razor`.
- Paginated grid of the sample rows (schema-driven columns, reusing `DataGrid`); "Load next page"
  calls `POST .../preview` again with the same filters and an updated cursor/page marker.
- Filter editor: rows of (column dropdown from the report's declared columns, operator dropdown
  from the closed `PreviewFilterOperator` list, value input) — "Add filter"/remove, "Apply" re-runs
  the preview. Hidden entirely for typed reports or sources with no filter translator, replaced by
  an honest note (D36 pattern) rather than a disabled-but-visible control.
- "Run with these filters" button → `POST /reports/{name}/run` with the same `Filters` payload,
  navigates to the resulting job like the existing "Run now" button.

---

## Task order

G1 → G2 → G3 → G4 (independent of each other after G1 lands the shared engine; sequential PRs by
convention, not a hard dependency) → G5 (depends on G1's `IFilterTranslator` seam) → G6 (depends on
G5's endpoint). One PR per item; G1 may combine "shared engine" and "Postgres" as it isn't
independently shippable/testable on its own.
