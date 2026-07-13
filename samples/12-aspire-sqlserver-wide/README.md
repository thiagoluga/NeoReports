# 12 — Aspire + SQL Server: a wide, large report

A **wide** (51 columns spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-row) report, read from a real **SQL Server** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/12-aspire-sqlserver-wide/AppHost
```

Open the printed dashboard URL. Aspire pulls the SQL Server image it defaults to, starts the
container, creates the `widetransactions` database, and injects its connection string into the
`report-runner` project. On first run, `report-runner`:

1. Creates the `wide_transactions` table if it doesn't exist.
2. Seeds it with 500,000 rows via `NeoReports.Samples.Shared`'s `WideTransactionGenerator`, bulk
   loaded through `SqlBulkCopy` in batches of 5,000 rows — never more than one batch buffered in
   memory at a time.
3. Runs a report over the seeded table with `Source.Sql(...).Keyset<WideTransaction, Guid>(...)`
   — the same constant-memory keyset pagination every other SQL sample in this repo uses — and
   writes `./out/wide-transactions-<date>.csv` and `.xlsx` under `ReportRunner/`.

Re-running the sample skips seeding (the table already has rows) and just re-runs the report —
seeding is idempotent, not "drop and recreate."

## Running the pieces separately

`ReportRunner` also runs standalone against any SQL Server connection string, for example one from
`docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=... -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest`
— note that a plain container (unlike Aspire's own orchestration) doesn't create the target
database for you, so run `CREATE DATABASE widetransactions` once first:

```bash
dotnet run --project samples/12-aspire-sqlserver-wide/ReportRunner -- "Server=localhost,1433;User Id=sa;Password=...;Database=widetransactions;TrustServerCertificate=True"
```

## Notable implementation details

- **`UNIQUEIDENTIFIER` sorts differently than every other provider's Guid/UUID type.** SQL Server
  orders GUIDs by specific byte groups in its own particular sequence, not the left-to-right binary
  comparison Postgres/MySQL/MongoDB use — so `ORDER BY TransactionId` here returns the 500,000 rows
  in a genuinely different order than the same report against Postgres/MySQL/MongoDB, even though
  every row is still read exactly once (keyset correctness doesn't promise a specific cross-database
  order, only that a fixed order is followed consistently within one database).
