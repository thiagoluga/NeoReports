# 02 — SQL Server → XLSX → S3

Sample 01 with two changes: the output format is **XLSX** instead of CSV, and the destination is
**Amazon S3** instead of the local filesystem.

```bash
dotnet run --project samples/02-sql-to-xlsx-s3 -- "Server=localhost;Database=Sales;Trusted_Connection=True;TrustServerCertificate=True"
# uploads monthly-sales-<date>.xlsx to the configured S3 bucket
```

Expects the same `Sales(Id BIGINT, Customer NVARCHAR, Amount DECIMAL, Date DATETIME2)` table as
sample 01, plus AWS credentials resolved the standard way (environment, shared credentials file, or
an attached role) and an S3 bucket configured via `Destination.S3(...)`.

How it works:

- Same `Source.Sql(...).Keyset<Sale, long>(...)` read path as sample 01.
- `.To(Format.Xlsx(...))` writes a native-typed XLSX workbook instead of CSV.
- `.UploadTo(Destination.S3(...))` publishes the finished file all-or-nothing (D2/D15): the upload
  either succeeds completely or the run reports failure, never a partial object in the bucket.
- `Sale` is the shared row type from `NeoReports.Samples.Shared`.
