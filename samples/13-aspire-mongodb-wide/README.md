# 13 — Aspire + MongoDB: a wide, large report

A **wide** (51 fields spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-document) report, read from a real **MongoDB** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/13-aspire-mongodb-wide/AppHost
```

Open the printed dashboard URL. Aspire pulls the MongoDB image it defaults to, starts the
container, and injects its connection string into the `report-runner` project. On first run,
`report-runner`:

1. Seeds `wide_transactions` with 500,000 documents via `NeoReports.Samples.Shared`'s
   `WideTransactionGenerator`, in batches of 5,000 unordered `InsertMany` calls — MongoDB has no
   schema to declare up front, unlike the three relational samples.
2. Runs a report over the seeded collection with
   `Source.MongoDb(...).Keyset<WideTransaction, Guid>(...)` — the same constant-memory keyset
   pagination pattern the relational sources use, adapted to Mongo's own range-query/no-offset-drift
   design (D44) — and writes `./out/wide-transactions-<date>.csv` and `.xlsx` under `ReportRunner/`.

Re-running the sample skips seeding (the collection already has documents) and just re-runs the
report — seeding is idempotent, not "drop and recreate."

## Running the pieces separately

`ReportRunner` also runs standalone against any MongoDB connection string, for example one from
`docker run -p 27017:27017 mongo:7`:

```bash
dotnet run --project samples/13-aspire-mongodb-wide/ReportRunner -- "mongodb://localhost:27017"
```

## Notable implementation details

- **`GuidRepresentation` must be registered explicitly before any write.** MongoDB.Driver's
  `GuidSerializer` throws (`"cannot serialize a Guid when GuidRepresentation is Unspecified"`)
  unless a representation is registered process-wide:
  `BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))`. This only
  affects the *seeding* step, which uses the driver's own typed `IMongoCollection<WideTransaction>`
  serializer — reads go through NeoReports' own `BsonDocumentMaterializer` (D44), which reads raw
  BSON values directly and needs no such registration.
- No DDL, no cursor cast — MongoDB has no schema and no SQL text; the filter/sort is built directly
  from the key selector.
