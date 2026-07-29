# NeoReports.QueryBuilder.Pro

> **Requires a NeoReports Pro license at run time** (ADR D70). Supply it by setting the
> `NEOREPORTS_LICENSE_KEY` environment variable, or explicitly at startup:
> `services.AddNeoReportsProLicense(key)` (dependency injection) or
> `NeoReports.Licensing.ProLicenseGate.Register(key)` (code-first, no container). Without a valid key
> the package throws `NeoReportsLicenseException` the first time it is used.

**Commercial (source-available) — PolyForm Small Business 1.0.0. Not MIT.** See `LICENSE.txt`.

The Pro half of the interactive query builder (ADR D49, Epic K): a structured query model and a
keyset-safe SQL generator. It turns a visually-composed query — a source table, inner/left joins,
selected columns (with aggregation), WHERE conditions, GROUP BY, and a keyset key — into the SQL a
NeoReports report runs, with:

- **Injection safety by construction.** Every identifier (table/column) comes from the model and is
  quoted per dialect; every WHERE value is emitted as a bind-parameter placeholder (`@qbfilterN`),
  never concatenated into the SQL text. The generator returns the parameter values separately.
- **A valid keyset query, always.** The generator appends the `WHERE (@cursor IS NULL OR key > @cursor)
  ORDER BY key` wrapper from the chosen key column — the report can't be built with an invalid keyset.
- **Derived output schema.** The selected columns produce the report's `ReportSchema` (types mapped
  from the catalog's declared DB types).

The `ISchemaExplorer` catalog capability (Core) and the schema-explorer HTTP endpoints (AspNetCore)
that feed this builder are MIT; only the query-model → SQL generator is Pro.
