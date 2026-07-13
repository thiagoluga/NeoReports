# 12 — Aspire + SQL Server: a wide, large report

A **wide** (51 columns spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-row) report, read from a real **SQL Server** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/12-aspire-sqlserver-wide/AppHost
```

Open the printed dashboard URL and click into the **`web`** resource's endpoint — Aspire's only
job here is standing up SQL Server (including creating the `widetransactions` database) and
starting that page. It's the full NeoReports UI:

1. On startup, `Web` creates the `wide_transactions` table if it doesn't exist and seeds it with
   500,000 rows via `NeoReports.Samples.Shared`'s `WideTransactionGenerator`, bulk loaded through
   `SqlBulkCopy` in batches of 5,000 rows — never more than one batch buffered in memory at a time.
2. It registers `wide-transactions` — `Source.Sql(...).Keyset<WideTransaction, Guid>(...)`, the
   same constant-memory keyset pagination every other SQL sample in this repo uses — and mounts
   the NeoReports UI so you can click **Run**, watch live progress, and download
   `wide-transactions-<date>.csv` / `.xlsx` from the Reports screen.

Re-running the sample skips seeding (the table already has rows) — seeding is idempotent, not
"drop and recreate."

## Running the pieces separately

`Web` also runs standalone against any SQL Server connection string, for example one from
`docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
— note that a plain container (unlike Aspire's own orchestration) doesn't create the target
database for you, so run `CREATE DATABASE widetransactions` once first:

```bash
dotnet run --project samples/12-aspire-sqlserver-wide/Web -- "Server=localhost,1433;User Id=sa;Password=...;Database=widetransactions;TrustServerCertificate=True"
```

## Notable implementation details

- **`UNIQUEIDENTIFIER` sorts differently than every other provider's Guid/UUID type.** SQL Server
  orders GUIDs by specific byte groups in its own particular sequence, not the left-to-right binary
  comparison Postgres/MySQL/MongoDB use — so `ORDER BY TransactionId` here returns the 500,000 rows
  in a genuinely different order than the same report against Postgres/MySQL/MongoDB, even though
  every row is still read exactly once (keyset correctness doesn't promise a specific cross-database
  order, only that a fixed order is followed consistently within one database).
