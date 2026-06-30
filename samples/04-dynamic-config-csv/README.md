# 04 — dynamic config → CSV (the dynamic path)

Defines a report entirely in [`report.json`](report.json) — **no typed POCO**. The JSON is parsed
into a `ReportConfig` and compiled into the same runnable report the fluent builder produces; rows
flow through the existing pipeline as positional `ReportRecord`s.

```bash
dotnet run --project samples/04-dynamic-config-csv
# writes ./out/monthly-sales-<date>.csv
```

What the JSON drives today:

- the report **name** and **page size**;
- the **source** selection by id (`"type": "inmemory"`) and its properties (`rows`);
- the **columns / schema**: name, semantic type, header, format and culture;
- the **outputs** and **destinations** selection by id (`"csv"`, `"local"`).

This sample keeps an in-memory source so it runs with no database. The real **SQL** config source
now exists (**A3**): register it with `services.AddSqlConfigSource()` and change the `source` section
to `{ "type": "sql", "properties": { "connectionString": "...", "sql": "...", "key": "Id" } }` to read
from SQL Server instead — nothing else in the config changes.

Still standing in: binding format/destination **options** from config arrives later (**A5**), so the
CSV and Local factories are pre-wired in DI — the JSON's `properties` under `outputs`/`destinations`
are illustrative for now.
