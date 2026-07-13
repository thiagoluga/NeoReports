# 13 — Aspire + MongoDB: a wide, large report

A **wide** (51 fields spanning `string`/`long`/`decimal`/`bool`/`DateTime`/`Guid`) and **large**
(500,000-document) report, read from a real **MongoDB** database that [.NET
Aspire](https://learn.microsoft.com/dotnet/aspire/) provisions and seeds automatically — no manual
Docker setup, no connection string to configure by hand.

```bash
dotnet run --project samples/13-aspire-mongodb-wide/AppHost
```

Open the printed dashboard URL and click into the **`web`** resource's endpoint — Aspire's only
job here is standing up MongoDB and starting that page. It's the full NeoReports UI:

1. On startup, `Web` seeds `wide_transactions` with 500,000 documents via
   `NeoReports.Samples.Shared`'s `WideTransactionGenerator`, in batches of 5,000 unordered
   `InsertMany` calls — MongoDB has no schema to declare up front, unlike the three relational
   samples.
2. It registers `wide-transactions` — `Source.MongoDb(...).Keyset<WideTransaction, Guid>(...)`,
   the same constant-memory keyset pagination pattern the relational sources use, adapted to
   Mongo's own range-query/no-offset-drift design (D44) — and mounts the NeoReports UI so you can
   click **Run**, watch live progress, and download `wide-transactions-<date>.csv` / `.xlsx` from
   the Reports screen.

Re-running the sample skips seeding (the collection already has documents) — seeding is
idempotent, not "drop and recreate."

## Running the pieces separately

`Web` also runs standalone against any MongoDB connection string, for example one from
`docker run -p 27017:27017 mongo:7`:

```bash
dotnet run --project samples/13-aspire-mongodb-wide/Web -- "mongodb://localhost:27017"
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
