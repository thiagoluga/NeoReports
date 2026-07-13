# 11 — Aspire + MySQL: a wide, large report

A **wide** (51 columns spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-row) report, read from a real **MySQL** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/11-aspire-mysql-wide/AppHost
```

Open the printed dashboard URL and click into the **`web`** resource's endpoint — Aspire's only
job here is standing up MySQL and starting that page. It's the full NeoReports UI:

1. On startup, `Web` creates the `wide_transactions` table if it doesn't exist and seeds it with
   500,000 rows via `NeoReports.Samples.Shared`'s `WideTransactionGenerator`, streamed in batches
   of 200 parameterized rows per `INSERT` (MySQL has no ADO.NET-level bulk-copy protocol comparable
   to Postgres's binary `COPY` or SQL Server's `SqlBulkCopy`) — never more than one batch buffered
   in memory at a time.
2. It registers `wide-transactions` — `Source.MySql(...).Keyset<WideTransaction, Guid>(...)`, the
   same constant-memory keyset pagination every other SQL sample in this repo uses — and mounts
   the NeoReports UI so you can click **Run**, watch live progress, and download
   `wide-transactions-<date>.csv` / `.xlsx` from the Reports screen.

Re-running the sample skips seeding (the table already has rows) — seeding is idempotent, not
"drop and recreate."

## Running the pieces separately

`Web` also runs standalone against any MySQL connection string, for example one from
`docker run -p 3306:3306 -e MYSQL_ROOT_PASSWORD=... mysql:9`:

```bash
dotnet run --project samples/11-aspire-mysql-wide/Web -- "Server=localhost;Port=3306;User=root;Password=...;Database=widetransactions;AllowPublicKeyRetrieval=True"
```

## Notable implementation details

- **`GuidFormat=Char36` is required on the connection string** (set automatically by
  `Web`, both for seeding and for the report source). MySQL has no native UUID column
  type — `TransactionId`/`SessionId` are stored as `CHAR(36)`. Without `GuidFormat=Char36`,
  MySqlConnector reads that column back as a plain `string`, and the engine's `RecordMaterializer`
  can't convert a `string` to `Guid` (`Convert.ChangeType` requires `IConvertible`, which `Guid`
  doesn't implement) — every row would fail to materialize. With it, MySqlConnector maps the
  column to a native `Guid` at the ADO.NET level, both directions.
- No cursor cast needed: unlike Postgres, `WHERE (@cursor IS NULL OR TransactionId > @cursor)`
  works as-is — MySQL implicitly converts the bound `Guid`/`CHAR(36)` comparison correctly.
