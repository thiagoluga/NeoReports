# 05 — dynamic config → SQL Server → CSV

The config-driven path (A2) reading from a real **SQL Server** source (A3). The report is defined
entirely in [`report.json`](report.json) with a `"sql"` source — no typed POCO.

```bash
dotnet run --project samples/05-dynamic-config-sql -- "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True"
# writes ./out/monthly-sales-<date>.csv
```

Expects a `Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2)` table (same schema
as sample 01). The connection string passed on the command line is injected into the config's
`__CONNECTION_STRING__` placeholder, so no secret lives in `report.json`.

How it works:

- `services.AddReportFromConfig(...)` registers the config report; `services.AddSqlConfigSource()`
  registers the `"sql"` source provider. The report is then **run by name** through the standard
  runner.
- The SQL source materializes positional `ReportRecord`s by matching each schema column to the
  result-set column by name, reusing the v1 keyset engine (connection-per-page, opaque cursor).

This is sample 04 with one change: the `source` section is `"sql"` instead of `"inmemory"`.
