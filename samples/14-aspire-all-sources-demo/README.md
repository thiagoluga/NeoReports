# 14 — Aspire + all sources: a 100% functional demo

A single combined demo that provisions **all four** supported relational/document databases at
once — **PostgreSQL**, **MySQL**, **SQL Server**, **MongoDB** — via [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/), and mounts one NeoReports UI in front of all
of them. Unlike samples `10`-`13` (one database each, deliberately kept lean), this sample exists
to let you exercise everything the engine can do from a single running app — no manual Docker
setup, and, critically, **no "Demo mode" fallback anywhere**:

```bash
dotnet run --project samples/14-aspire-all-sources-demo/AppHost
```

Open the printed dashboard URL and click into the **`web`** resource's endpoint.

## What's already working when it opens

- **Four ready-to-run reports** — `wide-transactions-postgres`/`-mysql`/`-sqlserver`/`-mongodb`,
  each the same 51-column `WideTransaction` shape (`NeoReports.Samples.Shared`), seeded with
  15,000 rows per database (in parallel, on startup). Click **Run** on any of them immediately.
- **Four registered engine source types** — `AddSqlConfigSource`/`AddPostgresConfigSource`/
  `AddMySqlConfigSource`/`AddMongoDbConfigSource` are all called, so `GET /api/capabilities` is
  never empty. The Builder wizard never shows "Demo mode" and "Save" is never disabled.
- **Four named sources** — `postgres-demo`/`mysql-demo`/`sqlserver-demo`/`mongodb-demo`, pre­
  registered in the Source Registry (D42) once seeding finishes. Open the Builder and pick "Use a
  registered source" to build a brand-new report against any of them by name.
- **Dynamic reports and scheduling** (`AddDynamicReports`/`AddScheduling`) are both registered, so
  reports built through the Builder — and recurring schedules on any report — work end to end.

Re-running the sample skips seeding for any database that already has rows (idempotent, not "drop
and recreate") — a `WithDataVolume()` Postgres/MySQL/SQL Server/MongoDB container keeps its data
across restarts.

## Notable implementation details

See `10`-`13`'s READMEs for the per-provider gotchas (Postgres cursor cast + UTC-Kind stripping,
MySQL `GuidFormat=Char36`, SQL Server `SqlBulkCopy` + `OFFSET 0 ROWS`, MongoDB's explicit
`GuidRepresentation` registration) — this sample reuses the exact same seeding logic for each
database, just run in parallel and at a smaller row count (four databases seeding at once, instead
of one). Connection-string resource names are disambiguated per database
(`postgres-db`/`mysql-db`/`sqlserver-db`/`mongodb-db`) since one `AppHost` now references all four
`AddDatabase(...)` calls, which would otherwise collide on the shared default name each of
`10`-`13` uses alone. See ADR **D48** in `DECISIONS.md` for the full rationale.
