# 10 — Aspire + PostgreSQL: a wide, large report

A **wide** (51 columns spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-row) report, read from a real **PostgreSQL** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/10-aspire-postgres-wide/AppHost
```

Open the printed dashboard URL and click into the **`web`** resource's endpoint — Aspire's only
job here is standing up Postgres and starting that page. It's the full NeoReports UI:

1. On startup, `Web` creates the `wide_transactions` table if it doesn't exist and seeds it with
   500,000 rows via `NeoReports.Samples.Shared`'s `WideTransactionGenerator`, streamed through
   Npgsql's binary `COPY` protocol — no batching, no intermediate buffering of more than one row
   at a time, and fast (well under a minute on a typical machine).
2. It registers `wide-transactions` — `Source.Postgres(...).Keyset<WideTransaction, Guid>(...)`,
   the same constant-memory keyset pagination every other SQL sample in this repo uses — and
   mounts the NeoReports UI so you can click **Run**, watch live progress, and download
   `wide-transactions-<date>.csv` / `.xlsx` from the Reports screen.

Re-running the sample skips seeding (the table already has rows) — seeding is idempotent, not
"drop and recreate."

## Running the pieces separately

`Web` also runs standalone against any PostgreSQL connection string, for example one from
`docker run -p 5432:5432 -e POSTGRES_PASSWORD=... postgres:17`:

```bash
dotnet run --project samples/10-aspire-postgres-wide/Web -- "Host=localhost;Port=5432;Username=postgres;Password=...;Database=widetransactions"
```

## Notable implementation details

- **Postgres needs an explicit cursor cast**: `WHERE (@cursor IS NULL OR TransactionId > @cursor::uuid)`
  — Postgres has no implicit `text`→`uuid` conversion (the same class of gap D43/D45 hit for other
  column types).
- **`TIMESTAMP` columns reject `DateTimeKind.Utc` values outright** — Npgsql validates the CLR
  `DateTime.Kind` against the target column's time-zone-awareness. The generator's rows are all
  UTC-based, so the seeding step strips the `Utc` tag (`DateTime.SpecifyKind(..., Unspecified)`)
  before writing to a `TIMESTAMP` (without time zone) column — the value itself is unchanged, only
  the tag that Npgsql validates.
