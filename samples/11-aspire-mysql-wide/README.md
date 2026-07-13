# 11 — Aspire + MySQL: a wide, large report

A **wide** (51 columns spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-row) report, read from a real **MySQL** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/11-aspire-mysql-wide/AppHost
```

Open the printed dashboard URL. Aspire pulls the `mysql` image it defaults to, starts the
container, and injects its connection string into the `report-runner` project. On first run,
`report-runner`:

1. Creates the `wide_transactions` table if it doesn't exist.
2. Seeds it with 500,000 rows via `NeoReports.Samples.Shared`'s `WideTransactionGenerator`,
   streamed in batches of 200 parameterized rows per `INSERT` (MySQL has no ADO.NET-level bulk-copy
   protocol comparable to Postgres's binary `COPY` or SQL Server's `SqlBulkCopy`) — never more than
   one batch buffered in memory at a time.
3. Runs a report over the seeded table with `Source.MySql(...).Keyset<WideTransaction, Guid>(...)`
   — the same constant-memory keyset pagination every other SQL sample in this repo uses — and
   writes `./out/wide-transactions-<date>.csv` and `.xlsx` under `ReportRunner/`.

Re-running the sample skips seeding (the table already has rows) and just re-runs the report —
seeding is idempotent, not "drop and recreate."

## Running the pieces separately

`ReportRunner` also runs standalone against any MySQL connection string, for example one from
`docker run -p 3306:3306 -e MYSQL_ROOT_PASSWORD=... mysql:9`:

```bash
dotnet run --project samples/11-aspire-mysql-wide/ReportRunner -- "Server=localhost;Port=3306;User=root;Password=...;Database=widetransactions;AllowPublicKeyRetrieval=True"
```

## Notable implementation details

- **`GuidFormat=Char36` is required on the connection string** (set automatically by
  `ReportRunner`, both for seeding and for the report source). MySQL has no native UUID column
  type — `TransactionId`/`SessionId` are stored as `CHAR(36)`. Without `GuidFormat=Char36`,
  MySqlConnector reads that column back as a plain `string`, and the engine's `RecordMaterializer`
  can't convert a `string` to `Guid` (`Convert.ChangeType` requires `IConvertible`, which `Guid`
  doesn't implement) — every row would fail to materialize. With it, MySqlConnector maps the
  column to a native `Guid` at the ADO.NET level, both directions.
- No cursor cast needed: unlike Postgres, `WHERE (@cursor IS NULL OR TransactionId > @cursor)`
  works as-is — MySQL implicitly converts the bound `Guid`/`CHAR(36)` comparison correctly.
