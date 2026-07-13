# 01 — SQL Server → CSV → local filesystem

The first end-to-end report: the typed (code-first) path, reading from **SQL Server** and writing
**CSV** to the **local filesystem**.

```bash
dotnet run --project samples/01-sql-to-csv-local -- "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True"
# writes ./out/monthly-sales-<date>.csv
```

Expects a `Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2)` table. Falls back to
`Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True` if no
connection string is passed.

How it works:

- `Source.Sql(...).Keyset<Sale, long>(...)` reads pages of `Sale` records via keyset pagination
  (opaque `@cursor`, connection opened/closed per page — constant memory regardless of table size).
- `.Filter(...)` and `.Columns(...)` declare the report shape; `.To(Format.Csv(...))` and
  `.UploadTo(Destination.Local(...))` write and publish the output.
- `Sale` is the shared row type from `NeoReports.Samples.Shared`, reused across every typed-path
  sample so it isn't redefined per sample.
